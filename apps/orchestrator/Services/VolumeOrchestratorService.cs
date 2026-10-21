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
    private readonly ILogger<VolumeOrchestratorService> _logger;

    public VolumeOrchestratorService(
        OrchestratorDbContext db,
        ISnapcastService snapcast,
        ILibrespotApiClient librespotApi,
        IHubContext<AudioHub, IAudioClient> hub,
        ILogger<VolumeOrchestratorService> logger)
    {
        _db = db;
        _snapcast = snapcast;
        _librespotApi = librespotApi;
        _hub = hub;
        _logger = logger;
    }

    public virtual async Task SetGroupVolumeAsync(string groupId, int masterVolume, bool? isMuted = null, bool fromDocker = false)
    {
        var group = await _db.Groups.FindAsync(groupId);
        if (group == null) return;

        group.MasterVolume = masterVolume;
        if (isMuted.HasValue) group.IsMuted = isMuted.Value;

        await _db.SaveChangesAsync();
        await _hub.Clients.All.OnGroupVolumeChanged(group.Id, group.MasterVolume, group.IsMuted);
        
        // If the group is listening to a Spotify source, sync with that source and all sibling groups
        if (!string.IsNullOrEmpty(group.CurrentStreamId) && group.CurrentStreamId.StartsWith(OrchestratorConstants.SpotifyPrefix))
        {
            var sourceId = group.CurrentStreamId.Replace(OrchestratorConstants.SpotifyPrefix, "");
            
            // Sync Spotify Source master volume in DB
            var source = await _db.SpotifySources.FindAsync(sourceId);
            if (source != null && source.MasterVolume != masterVolume)
            {
                source.MasterVolume = masterVolume;
            }

            // Sync all other groups listening to the same Spotify source
            var siblingGroups = await _db.Groups
                .Where(g => g.Id != group.Id && g.CurrentStreamId == group.CurrentStreamId)
                .ToListAsync();

            foreach (var sibling in siblingGroups)
            {
                if (sibling.MasterVolume != masterVolume)
                {
                    sibling.MasterVolume = masterVolume;
                    await _hub.Clients.All.OnGroupVolumeChanged(sibling.Id, sibling.MasterVolume, sibling.IsMuted);
                    await RecalculateAndPushGroupAsync(sibling.Id);
                }
            }
            await _db.SaveChangesAsync();

            // Sync back to Spotify (LibreSpot) only if NOT coming from Docker event
            if (!fromDocker)
            {
                _logger.LogInformation($"Pushing volume {masterVolume}% from Group {group.Name} to Spotify Source {sourceId}");
                try
                {
                    await _librespotApi.SetVolumeAsync(sourceId, masterVolume);
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is ApiException)
                {
                    _logger.LogWarning("Could not push volume to Spotify source {SourceId} (node might still be starting or offline): {Message}", sourceId, ex.Message);
                }
            }
        }

        await RecalculateAndPushGroupAsync(group.Id);
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

        await _db.SaveChangesAsync();

        // Find which group this room belongs to (from Snapcast status)
        var status = await _snapcast.GetStatusAsync();
        var snapGroups = status?["server"]?["groups"]?.AsArray();
        var snapGroup = snapGroups?.FirstOrDefault(g => 
            g?["clients"]?.AsArray()?.Any(c => c?["id"]?.GetValue<string>() == roomMac) ?? false);

        if (snapGroup != null)
        {
            var snapGroupId = snapGroup["id"]?.GetValue<string>();
            var dbGroup = await _db.Groups.FirstOrDefaultAsync(g => g.SnapcastGroupId == snapGroupId);
            if (dbGroup != null)
            {
                await RecalculateAndPushGroupAsync(dbGroup.Id);
                return;
            }
        }

        // If not in a group, just push absolute volume (1:1 with mix) and preserve room mute
        var settings = await _db.Settings.FirstOrDefaultAsync();
        var profile = settings?.VolumeProfile ?? VolumeProfile.Cubic;
        int snapcastVolume = CalculateSnapcastVolume(mixVolume, 100, profile);

        await _snapcast.SetClientVolumeAsync(roomMac, snapcastVolume, room.IsMuted);
        await _hub.Clients.All.OnRoomVolumeChanged(roomMac, snapcastVolume, room.IsMuted);
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

    public virtual async Task RecalculateAllAsync()
    {
        var settings = await _db.Settings.FirstOrDefaultAsync();
        var profile = settings?.VolumeProfile ?? VolumeProfile.Cubic;

        // 1. Recalculate all orchestrated groups
        var groups = await _db.Groups.Where(g => !string.IsNullOrEmpty(g.SnapcastGroupId)).ToListAsync();
        foreach (var group in groups)
        {
            await RecalculateAndPushGroupAsync(group.Id);
        }

        // 2. Recalculate unassigned rooms
        var status = await _snapcast.GetStatusAsync();
        var snapGroups = status?["server"]?["groups"]?.AsArray();
        if (snapGroups == null) return;

        var orchestratedSnapIds = groups.Select(g => g.SnapcastGroupId).ToHashSet();
        var roomsFromDb = await _db.Rooms.ToListAsync();

        foreach (var sg in snapGroups)
        {
            var sid = sg?["id"]?.GetValue<string>();
            if (sid != null && orchestratedSnapIds.Contains(sid)) continue;

            var clients = sg?["clients"]?.AsArray();
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
    }

    public virtual async Task RecalculateAndPushGroupAsync(string groupId)
    {
        var group = await _db.Groups.FindAsync(groupId);
        if (group == null || string.IsNullOrEmpty(group.SnapcastGroupId)) return;

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
