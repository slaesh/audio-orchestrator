using AudioOrchestrator.Data;
using System.Collections.Concurrent;
using AudioOrchestrator.Data.Entities;
using Microsoft.EntityFrameworkCore;
using AudioOrchestrator.Hubs;
using AudioOrchestrator.Contracts;
using AudioOrchestrator.Services.Generated;
using Microsoft.AspNetCore.SignalR;

namespace AudioOrchestrator.Services;

public class VolumeOrchestratorService
{
    private readonly OrchestratorDbContext _db;
    private readonly ISnapcastService _snapcast;
    private readonly ILibrespotApiClient _librespotApi;
    private readonly IHubContext<AudioHub, IAudioClient> _hub;
    private readonly IServiceProvider? _serviceProvider;
    private readonly ILogger<VolumeOrchestratorService> _logger;

    public VolumeOrchestratorService(
        OrchestratorDbContext db,
        ISnapcastService snapcast,
        ILibrespotApiClient librespotApi,
        IHubContext<AudioHub, IAudioClient> hub,
        ILogger<VolumeOrchestratorService> logger)
        : this(db, snapcast, librespotApi, hub, null, logger)
    {
    }

    public VolumeOrchestratorService(
        OrchestratorDbContext db,
        ISnapcastService snapcast,
        ILibrespotApiClient librespotApi,
        IHubContext<AudioHub, IAudioClient> hub,
        IServiceProvider? serviceProvider,
        ILogger<VolumeOrchestratorService> logger)
    {
        _db = db;
        _snapcast = snapcast;
        _librespotApi = librespotApi;
        _hub = hub;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public virtual async Task SetGroupVolumeAsync(string groupId, int masterVolume, bool? isMuted = null, bool fromDocker = false, bool syncStreamSource = true, bool force = false)
    {
        var group = await _db.Groups.FindAsync(groupId);
        if (group == null) return;

        var broadcastService = _serviceProvider?.GetService<IBroadcastService>();
        var gate = broadcastService?.GetGroupGate(group.Id);
        if (gate != null)
        {
            await gate.WaitAsync();
        }

        List<GroupEntity>? siblingGroups = null;
        string? spotifySourceIdToSync = null;

        try
        {
            if (fromDocker && broadcastService?.IsGroupActiveInBroadcast(group.Id, group.SnapcastGroupId) == true)
            {
                return;
            }

            group.MasterVolume = masterVolume;
            if (isMuted.HasValue) group.IsMuted = isMuted.Value;

            await _db.SaveChangesAsync();
            await _hub.Clients.All.OnGroupVolumeChanged(group.Id, group.MasterVolume, group.IsMuted);

            // If the group is listening to a Spotify source, sync with that source and all sibling groups
            // (skipped for transient, group-isolated changes such as broadcasts or groups currently in a broadcast)
            bool isBroadcasting = broadcastService?.IsGroupActiveInBroadcast(group.Id, group.SnapcastGroupId) == true;

            if (syncStreamSource && !isBroadcasting && !string.IsNullOrEmpty(group.CurrentStreamId) && group.CurrentStreamId.StartsWith(OrchestratorConstants.SpotifyPrefix))
            {
                var sourceId = group.CurrentStreamId.Replace(OrchestratorConstants.SpotifyPrefix, "");
                
                // Sync Spotify Source master volume in DB
                var source = await _db.SpotifySources.FindAsync(sourceId);
                if (source != null && source.MasterVolume != masterVolume)
                {
                    source.MasterVolume = masterVolume;
                }

                // Query sibling groups listening to the same Spotify source (excluding groups in an active broadcast)
                siblingGroups = await _db.Groups
                    .Where(g => g.Id != group.Id && g.CurrentStreamId == group.CurrentStreamId)
                    .ToListAsync();

                if (broadcastService != null)
                {
                    siblingGroups = siblingGroups.Where(g => !broadcastService.IsGroupActiveInBroadcast(g.Id, g.SnapcastGroupId)).ToList();
                }

                foreach (var sibling in siblingGroups)
                {
                    sibling.MasterVolume = masterVolume;
                }
                await _db.SaveChangesAsync();

                if (!fromDocker)
                {
                    spotifySourceIdToSync = sourceId;
                }
            }

            if (_serviceProvider != null)
            {
                var mqtt = _serviceProvider.GetService<IHomeAssistantMqttService>();
                if (mqtt != null)
                {
                    _ = mqtt.NotifyGroupVolumeChangedAsync(group.Id, group.MasterVolume, group.IsMuted);
                }
            }

            await RecalculateAndPushGroupCoreAsync(group, force, broadcastService);
        }
        finally
        {
            gate?.Release();
        }

        // Independently update sibling groups AFTER releasing the current group's gate to prevent deadlocks
        if (siblingGroups != null && siblingGroups.Count > 0)
        {
            foreach (var sibling in siblingGroups)
            {
                await _hub.Clients.All.OnGroupVolumeChanged(sibling.Id, sibling.MasterVolume, sibling.IsMuted);
                await RecalculateAndPushGroupAsync(sibling.Id, force: force);
            }
        }

        // Sync back to Spotify (LibreSpot) outside the group gate
        if (spotifySourceIdToSync != null)
        {
            _logger.LogInformation($"Pushing volume {masterVolume}% from Group {group.Name} to Spotify Source {spotifySourceIdToSync}");
            try
            {
                await _librespotApi.SetVolumeAsync(spotifySourceIdToSync, masterVolume);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is ApiException)
            {
                _logger.LogWarning("Could not push volume to Spotify source {SourceId} (node might still be starting or offline): {Message}", spotifySourceIdToSync, ex.Message);
            }
        }
    }

    public virtual async Task SetRoomMixVolumeAsync(string roomMac, int mixVolume)
    {
        var room = await _db.Rooms.FindAsync(roomMac);
        if (room == null)
        {
            room = new RoomEntity { MacAddress = roomMac, FriendlyName = roomMac, MixVolume = mixVolume };
            _db.Rooms.Add(room);
        }
        else
        {
            room.MixVolume = mixVolume;
        }

        var broadcastService = _serviceProvider?.GetService<IBroadcastService>();

        for (int attempt = 0; attempt < 5; attempt++)
        {
            // Find which group this room belongs to (from Snapcast status)
            var status = await _snapcast.GetStatusAsync();
            var snapGroups = status?["server"]?["groups"]?.AsArray();
            var snapGroup = snapGroups?.FirstOrDefault(g => 
                g?["clients"]?.AsArray()?.Any(c => c?["id"]?.GetValue<string>() == roomMac) ?? false);
            var snapGroupId = snapGroup?["id"]?.GetValue<string>();
            var dbGroup = !string.IsNullOrEmpty(snapGroupId)
                ? await _db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.SnapcastGroupId == snapGroupId)
                : null;

            var involvedGateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(snapGroupId)) involvedGateIds.Add(snapGroupId);
            if (dbGroup != null) involvedGateIds.Add(dbGroup.Id);

            var sortedGateIds = involvedGateIds.OrderBy(gid => gid, StringComparer.Ordinal).ToList();
            var acquiredGates = new List<SemaphoreSlim>();

            if (broadcastService != null)
            {
                foreach (var gid in sortedGateIds)
                {
                    var g = broadcastService.GetGroupGate(gid);
                    if (g != null)
                    {
                        await g.WaitAsync();
                        acquiredGates.Add(g);
                    }
                }
            }

            try
            {
                // Revalidate membership under the gate
                var freshStatus = await _snapcast.GetStatusAsync();
                var freshSnapGroups = freshStatus?["server"]?["groups"]?.AsArray();
                var freshSnapGroup = freshSnapGroups?.FirstOrDefault(g => 
                    g?["clients"]?.AsArray()?.Any(c => c?["id"]?.GetValue<string>() == roomMac) ?? false);
                var freshSnapGroupId = freshSnapGroup?["id"]?.GetValue<string>();
                var freshDbGroup = !string.IsNullOrEmpty(freshSnapGroupId)
                    ? await _db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.SnapcastGroupId == freshSnapGroupId)
                    : null;

                if (!string.Equals(freshSnapGroupId, snapGroupId, StringComparison.OrdinalIgnoreCase) ||
                    freshDbGroup?.Id != dbGroup?.Id)
                {
                    continue; // Retry with new owner's gates
                }

                await _db.SaveChangesAsync();

                if (freshDbGroup != null)
                {
                    var trackedDbGroup = await _db.Groups.FindAsync(freshDbGroup.Id);
                    await RecalculateAndPushGroupCoreAsync(trackedDbGroup ?? freshDbGroup, force: false, broadcastService);
                    return;
                }

                if (freshSnapGroupId != null && broadcastService?.IsGroupActiveInBroadcast(null, freshSnapGroupId) == true)
                {
                    broadcastService.MarkDeferredVolumeUpdate(null, freshSnapGroupId);
                    return;
                }

                // If not in a group, just push absolute volume (1:1 with mix) and preserve room mute
                var settings = await _db.Settings.FirstOrDefaultAsync();
                var profile = settings?.VolumeProfile ?? VolumeProfile.Cubic;
                int snapcastVolume = CalculateSnapcastVolume(mixVolume, 100, profile);

                await _snapcast.SetClientVolumeAsync(roomMac, snapcastVolume, room.IsMuted);
                await _hub.Clients.All.OnRoomVolumeChanged(roomMac, snapcastVolume, room.IsMuted);
                return;
            }
            finally
            {
                foreach (var g in acquiredGates)
                {
                    g.Release();
                }
            }
        }

        throw new InvalidOperationException($"Could not update mix volume for room {roomMac} due to concurrent membership changes.");
    }

    public static double CalculateAudioGain(double normalizedValue, VolumeProfile profile)
    {
        double x = Math.Clamp(normalizedValue, 0.0, 1.0);
        return profile switch
        {
            VolumeProfile.Linear => x,
            _ => x * x // Default: Natural / Quadratic (f(x) = x²)
        };
    }

    public static int CalculateSnapcastVolume(int groupMasterPercent, int roomMixPercent, VolumeProfile profile)
    {
        if (groupMasterPercent <= 0 || roomMixPercent <= 0) return 0;

        double groupSlider = Math.Clamp(groupMasterPercent, 0, 100) / 100.0;
        double roomSlider = Math.Clamp(roomMixPercent, 0, 100) / 100.0;

        // 1. Group master slider applies the psychoacoustic curve (x² or linear)
        double groupGain = CalculateAudioGain(groupSlider, profile);

        // 2. Room mix slider distributes proportionally (linear trim of the group gain)
        double finalGain = groupGain * roomSlider;

        // 3. Convert to Snapcast percentage (0 - 100), with midpoint rounding away from zero
        int snapcastVolume = (int)Math.Round(finalGain * 100.0, MidpointRounding.AwayFromZero);

        // 4. Never-0 protection: only true 0 when a slider is 0
        return Math.Clamp(Math.Max(1, snapcastVolume), 0, 100);
    }

    public virtual async Task RecalculateAllAsync(bool markDeferred = true)
    {
        var settings = await _db.Settings.FirstOrDefaultAsync();
        var profile = settings?.VolumeProfile ?? VolumeProfile.Cubic;

        // 1. Recalculate all orchestrated groups
        var groups = await _db.Groups.Where(g => !string.IsNullOrEmpty(g.SnapcastGroupId)).ToListAsync();
        foreach (var group in groups)
        {
            await RecalculateAndPushGroupAsync(group.Id, markDeferred: markDeferred);
        }

        // 2. Recalculate unassigned rooms
        var status = await _snapcast.GetStatusAsync();
        var snapGroups = status?["server"]?["groups"]?.AsArray();
        if (snapGroups == null) return;

        var orchestratedSnapIds = groups.Select(g => g.SnapcastGroupId).ToHashSet();
        var roomsFromDb = await _db.Rooms.ToListAsync();
        var broadcastService = _serviceProvider?.GetService<IBroadcastService>();

        foreach (var sg in snapGroups)
        {
            var sid = sg?["id"]?.GetValue<string>();
            if (sid == null || orchestratedSnapIds.Contains(sid)) continue;

            var gate = broadcastService?.GetGroupGate(sid);
            if (gate != null) await gate.WaitAsync();

            try
            {
                var freshStatus = await _snapcast.GetStatusAsync();
                var freshSnapGroups = freshStatus?["server"]?["groups"]?.AsArray();
                var freshGroup = freshSnapGroups?.FirstOrDefault(g => g?["id"]?.GetValue<string>() == sid);
                if (freshGroup == null) continue;

                var freshOrchestrated = await _db.Groups.Where(g => !string.IsNullOrEmpty(g.SnapcastGroupId))
                    .Select(g => g.SnapcastGroupId!)
                    .ToListAsync();
                if (freshOrchestrated.Contains(sid)) continue;

                if (broadcastService?.IsGroupActiveInBroadcast(null, sid) == true)
                {
                    if (markDeferred) broadcastService.MarkDeferredVolumeUpdate(null, sid);
                    continue;
                }

                var clients = freshGroup?["clients"]?.AsArray();
                if (clients == null) continue;

                foreach (var client in clients)
                {
                    var mac = client?["id"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(mac)) continue;

                    var roomEntity = roomsFromDb.FirstOrDefault(r => r.MacAddress == mac);
                    var mixVolume = roomEntity?.MixVolume ?? 100;
                    var isMuted = roomEntity?.IsMuted ?? false;

                    int snapcastVolume = CalculateSnapcastVolume(mixVolume, 100, profile);
                    await _snapcast.SetClientVolumeAsync(mac, snapcastVolume, isMuted);
                    await _hub.Clients.All.OnRoomVolumeChanged(mac, snapcastVolume, isMuted);
                }
            }
            finally
            {
                gate?.Release();
            }
        }
    }

    public virtual async Task RecalculateAndPushGroupAsync(string groupId, bool force = false, bool markDeferred = true)
    {
        var broadcastService = _serviceProvider?.GetService<IBroadcastService>();

        for (int attempt = 0; attempt < 5; attempt++)
        {
            var initialGroup = await _db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId);
            if (initialGroup == null || string.IsNullOrEmpty(initialGroup.SnapcastGroupId)) return;

            var involvedGateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { initialGroup.Id, initialGroup.SnapcastGroupId };
            var sortedGateIds = involvedGateIds.OrderBy(x => x, StringComparer.Ordinal).ToList();
            var acquiredGates = new List<SemaphoreSlim>();

            if (broadcastService != null)
            {
                foreach (var gid in sortedGateIds)
                {
                    var g = broadcastService.GetGroupGate(gid);
                    if (g != null)
                    {
                        await g.WaitAsync();
                        acquiredGates.Add(g);
                    }
                }
            }

            try
            {
                // Reload group state fresh from DB under acquired gates
                var freshGroup = await _db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId);
                if (freshGroup == null || string.IsNullOrEmpty(freshGroup.SnapcastGroupId)) return;

                if (!string.Equals(freshGroup.SnapcastGroupId, initialGroup.SnapcastGroupId, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // Retry with new snapcast group ID's gate
                }

                await RecalculateAndPushGroupCoreAsync(freshGroup, force, broadcastService, markDeferred);
                return;
            }
            finally
            {
                foreach (var g in acquiredGates)
                {
                    g.Release();
                }
            }
        }

        throw new InvalidOperationException($"Could not recalculate group {groupId} due to concurrent Snapcast mapping changes.");
    }

    public virtual async Task ReplayGroupVolumeUnderGateAsync(GroupEntity group)
    {
        var broadcastService = _serviceProvider?.GetService<IBroadcastService>();
        await RecalculateAndPushGroupCoreAsync(group, force: true, broadcastService);
    }

    public virtual async Task ReplayNativeSnapcastGroupVolumeUnderGateAsync(string snapcastGroupId)
    {
        var status = await _snapcast.GetStatusAsync();
        var snapGroups = status?["server"]?["groups"]?.AsArray();
        var snapGroup = snapGroups?.FirstOrDefault(g => g?["id"]?.GetValue<string>() == snapcastGroupId);
        if (snapGroup == null) return;

        var clients = snapGroup["clients"]?.AsArray();
        if (clients == null) return;

        var settings = await _db.Settings.FirstOrDefaultAsync();
        var profile = settings?.VolumeProfile ?? VolumeProfile.Cubic;
        var roomsFromDb = await _db.Rooms.ToListAsync();

        foreach (var client in clients)
        {
            var mac = client?["id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(mac)) continue;

            var roomEntity = roomsFromDb.FirstOrDefault(r => r.MacAddress == mac);
            var mixVolume = roomEntity?.MixVolume ?? 100;
            var isRoomMuted = roomEntity?.IsMuted ?? false;

            int snapcastVolume = CalculateSnapcastVolume(mixVolume, 100, profile);
            await _snapcast.SetClientVolumeAsync(mac, snapcastVolume, isRoomMuted);
            await _hub.Clients.All.OnRoomVolumeChanged(mac, snapcastVolume, isRoomMuted);
        }
    }

    public virtual async Task SyncSpotifySourceAndSiblingsAsync(string groupId, string currentStreamId, int masterVolume)
    {
        if (string.IsNullOrEmpty(currentStreamId) || !currentStreamId.StartsWith(OrchestratorConstants.SpotifyPrefix)) return;
        var sourceId = currentStreamId.Replace(OrchestratorConstants.SpotifyPrefix, "");

        var broadcastService = _serviceProvider?.GetService<IBroadcastService>();
        var gate = broadcastService?.GetGroupGate(groupId);
        if (gate != null) await gate.WaitAsync();

        List<GroupEntity> siblingGroups = new();
        int finalVolume;
        try
        {
            var originGroup = await _db.Groups.FindAsync(groupId);
            if (originGroup == null || originGroup.CurrentStreamId != currentStreamId)
            {
                return;
            }

            // Revalidate volume: If a newer user volume update completed on the originating group
            // since broadcast captured masterVolume, do not overwrite newer user changes with the stale restoration replay.
            if (originGroup.MasterVolume != masterVolume)
            {
                return;
            }

            finalVolume = originGroup.MasterVolume;

            var source = await _db.SpotifySources.FindAsync(sourceId);
            if (source != null && source.MasterVolume != finalVolume)
            {
                source.MasterVolume = finalVolume;
            }

            siblingGroups = await _db.Groups
                .Where(g => g.Id != originGroup.Id && g.CurrentStreamId == currentStreamId)
                .ToListAsync();

            if (broadcastService != null)
            {
                siblingGroups = siblingGroups.Where(g => !broadcastService.IsGroupActiveInBroadcast(g.Id, g.SnapcastGroupId)).ToList();
            }

            foreach (var grp in siblingGroups)
            {
                grp.MasterVolume = finalVolume;
            }

            await _db.SaveChangesAsync();
        }
        finally
        {
            gate?.Release();
        }

        foreach (var grp in siblingGroups)
        {
            await _hub.Clients.All.OnGroupVolumeChanged(grp.Id, grp.MasterVolume, grp.IsMuted);
            await RecalculateAndPushGroupAsync(grp.Id);
        }

        try
        {
            await _librespotApi.SetVolumeAsync(sourceId, finalVolume);
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is ApiException)
        {
            _logger.LogWarning("Could not push volume to Spotify source {SourceId}: {Message}", sourceId, ex.Message);
        }
    }

    private async Task RecalculateAndPushGroupCoreAsync(GroupEntity group, bool force, IBroadcastService? broadcastService, bool markDeferred = true)
    {
        if (string.IsNullOrEmpty(group.SnapcastGroupId)) return;

        if (!force && broadcastService?.IsGroupActiveInBroadcast(group.Id, group.SnapcastGroupId) == true)
        {
            if (markDeferred) broadcastService.MarkDeferredVolumeUpdate(group.Id, group.SnapcastGroupId);
            return;
        }

        var status = await _snapcast.GetStatusAsync();
        var snapGroups = status?["server"]?["groups"]?.AsArray();
        var snapGroup = snapGroups?.FirstOrDefault(g => g?["id"]?.GetValue<string>() == group.SnapcastGroupId);

        if (snapGroup == null) return;

        var clients = snapGroup["clients"]?.AsArray();
        if (clients == null) return;

        var settings = await _db.Settings.FirstOrDefaultAsync();
        var profile = settings?.VolumeProfile ?? VolumeProfile.Cubic;
        var roomsFromDb = await _db.Rooms.ToListAsync();

        foreach (var client in clients)
        {
            var mac = client?["id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(mac)) continue;

            var roomEntity = roomsFromDb.FirstOrDefault(r => r.MacAddress == mac);
            var mixVolume = roomEntity?.MixVolume ?? 100;
            var isRoomMuted = roomEntity?.IsMuted ?? false;

            // Effective mute: Group mute OR individual room mute
            bool effectiveMuted = group.IsMuted || isRoomMuted;

            int finalVolume = CalculateSnapcastVolume(group.MasterVolume, mixVolume, profile);

            await _snapcast.SetClientVolumeAsync(mac, finalVolume, effectiveMuted);
            await _hub.Clients.All.OnRoomVolumeChanged(mac, finalVolume, effectiveMuted);
        }
    }
}
