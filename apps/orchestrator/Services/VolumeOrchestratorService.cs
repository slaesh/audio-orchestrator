using AudioOrchestrator.Data;
using System.Collections.Concurrent;
using AudioOrchestrator.Data.Entities;
using Microsoft.EntityFrameworkCore;
using AudioOrchestrator.Hubs;
using AudioOrchestrator.Contracts;
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
                catch (HttpRequestException ex)
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

        // If not in a group, just push absolute volume (1:1 with mix)
        await _snapcast.SetClientVolumeAsync(roomMac, mixVolume, false);
        await _hub.Clients.All.OnRoomVolumeChanged(roomMac, mixVolume, false);
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

        var roomsFromDb = await _db.Rooms.ToListAsync();

        foreach (var client in clients)
        {
            var mac = client?["id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(mac)) continue;

            var roomEntity = roomsFromDb.FirstOrDefault(r => r.MacAddress == mac);
            var mixVolume = roomEntity?.MixVolume ?? 100;

            // ActualSnapcastVolume = (MixVolume / 100) * (MasterVolume / 100) * 100
            double calculated = (mixVolume / 100.0) * (group.MasterVolume / 100.0) * 100.0;
            int finalVolume = (int)Math.Round(calculated);

            // Cap at 0-100
            finalVolume = Math.Clamp(finalVolume, 0, 100);

            await _snapcast.SetClientVolumeAsync(mac, finalVolume, group.IsMuted);
            await _hub.Clients.All.OnRoomVolumeChanged(mac, finalVolume, group.IsMuted);
        }
    }
}
