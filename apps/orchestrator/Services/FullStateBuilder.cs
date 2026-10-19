using System.Text.Json;
using System.Text.Json.Nodes;
using AudioOrchestrator.Contracts;
using AudioOrchestrator.Data;
using AudioOrchestrator.Data.Entities;
using AudioOrchestrator.Services;
using Microsoft.EntityFrameworkCore;

namespace AudioOrchestrator.Services;

public class FullStateBuilder
{
    private readonly OrchestratorDbContext _db;
    private readonly ISnapcastService _snapcast;
    private readonly ISpotifyNodeReleaseService? _releaseService;

    public FullStateBuilder(OrchestratorDbContext db, ISnapcastService snapcast)
        : this(db, snapcast, null)
    {
    }

    public FullStateBuilder(OrchestratorDbContext db, ISnapcastService snapcast, ISpotifyNodeReleaseService? releaseService)
    {
        _db = db;
        _snapcast = snapcast;
        _releaseService = releaseService;
    }

    public virtual async Task<FullStateDto> BuildAsync()
    {
        var groups = await _db.Groups.AsNoTracking().ToListAsync();
        var roomsFromDb = await _db.Rooms.AsNoTracking().ToListAsync();
        var radios = await _db.RadioStreams.AsNoTracking().ToListAsync();
        var spotifySources = await _db.SpotifySources.AsNoTracking().ToListAsync();
        var status = await _snapcast.GetStatusAsync();

        var snapGroups = status?["server"]?["groups"]?.AsArray();
        var snapStreams = status?["server"]?["streams"]?.AsArray();

        var groupDtos = new List<GroupDto>();
        var unassignedRooms = new List<RoomDto>();
        var radioDtos = new List<StreamDto>();
        var spotifySourceDtos = new List<SpotifySourceDto>();
        var snapcastStreamDtos = new List<StreamDto>();

        var claimedSnapIds = new HashSet<string>();

        // Build Spotify Sources
        foreach (var source in spotifySources)
        {
            var ss = snapStreams?.FirstOrDefault(s => s?["id"]?.GetValue<string>() == $"{OrchestratorConstants.SpotifyPrefix}{source.Id}");
            var st = ss?["status"]?.GetValue<string>() ?? "idle";
            
            List<SpotifyRecentContextDto>? recents = null;
            if (!string.IsNullOrEmpty(source.RecentContextsJson))
            {
                try
                {
                    recents = JsonSerializer.Deserialize<List<SpotifyRecentContextDto>>(source.RecentContextsJson);
                }
                catch
                {
                    // Ignore JSON parse errors from corrupt/legacy data
                }
            }

            string? latestCompatible = null;
            bool hasUpdate = false;
            if (_releaseService != null && !string.IsNullOrEmpty(source.CurrentVersion))
            {
                latestCompatible = await _releaseService.GetLatestCompatibleVersionAsync(source.CurrentVersion);
                hasUpdate = !string.IsNullOrEmpty(latestCompatible) && latestCompatible != source.CurrentVersion;
            }

            spotifySourceDtos.Add(new SpotifySourceDto(
                source.Id, 
                source.Name, 
                source.IsActive, 
                source.IsConnected,
                st, 
                source.MasterVolume, 
                source.MaxVolume,
                source.CurrentTrackId,
                source.CurrentTitle,
                source.CurrentArtist,
                source.CurrentAlbum,
                source.CurrentDurationMs,
                source.CurrentCoverUrl,
                source.CurrentPositionMs,
                source.HeartbeatIntervalMs,
                source.LastUpdateTimestamp,
                source.ErrorMessage,
                source.ContextUri,
                source.ContextName,
                recents,
                source.CurrentVersion,
                latestCompatible,
                hasUpdate
            ));
        }

        // Build Radio Streams (from DB)
        foreach (var radio in radios)
        {
            // We find the corresponding snapcast stream for status
            var ss = snapStreams?.FirstOrDefault(s => s?["id"]?.GetValue<string>() == $"{OrchestratorConstants.RadioPrefix}{radio.Id}");
            var st = ss?["status"]?.GetValue<string>() ?? "idle";
            radioDtos.Add(new StreamDto(
                $"{OrchestratorConstants.RadioPrefix}{radio.Id}", 
                radio.Name, 
                st, 
                radio.StreamUrl,
                radio.CurrentTitle,
                radio.CurrentArtist,
                radio.CurrentCoverUrl,
                radio.ErrorMessage
            ));
        }

        // Build All Snapcast Streams (for source selection)
        if (snapStreams != null)
        {
            foreach (var stream in snapStreams)
            {
                var id = stream?["id"]?.GetValue<string>() ?? "";
                var st = stream?["status"]?.GetValue<string>() ?? "idle";
                snapcastStreamDtos.Add(new StreamDto(id, id, st, null));
            }
        }

        // Build Orchestrated Groups
        foreach (var group in groups)
        {
            var rooms = new List<RoomDto>();
            JsonNode? sg = null;
            if (!string.IsNullOrEmpty(group.SnapcastGroupId) && snapGroups != null)
            {
                sg = snapGroups.FirstOrDefault(g => g?["id"]?.GetValue<string>() == group.SnapcastGroupId);
                if (sg != null)
                {
                    claimedSnapIds.Add(group.SnapcastGroupId);
                    var clients = sg?["clients"]?.AsArray();
                    if (clients != null)
                    {
                        foreach (var c in clients)
                        {
                            var r = BuildRoomDto(c, roomsFromDb);
                            if (r != null) rooms.Add(r);
                        }
                    }
                }
            }
            groupDtos.Add(new GroupDto(group.Id, group.Name, sg?["stream_id"]?.GetValue<string>() ?? group.CurrentStreamId, rooms, group.MasterVolume, group.IsMuted));
        }

        // Build Unassigned Rooms
        if (snapGroups != null)
        {
            foreach (var sg in snapGroups)
            {
                var sid = sg?["id"]?.GetValue<string>();
                if (sid != null && !claimedSnapIds.Contains(sid))
                {
                    var clients = sg?["clients"]?.AsArray();
                    if (clients != null)
                    {
                        foreach (var c in clients)
                        {
                            var r = BuildRoomDto(c, roomsFromDb);
                            if (r != null) unassignedRooms.Add(r);
                        }
                    }
                }
            }
        }

        // Build Radio Plugins
        var pluginsFromDb = await _db.RadioPlugins.AsNoTracking().ToListAsync();
        var pluginDtos = pluginsFromDb.Select(p => new RadioPluginDto(
            p.Id,
            p.Name,
            p.DockerImage,
            p.GetUrlPatterns(),
            p.Enabled,
            p.CreatedAt
        )).ToList();

        return new FullStateDto(groupDtos, unassignedRooms, radioDtos, spotifySourceDtos, snapcastStreamDtos, pluginDtos);
    }

    private RoomDto? BuildRoomDto(JsonNode? clientNode, List<RoomEntity> rooms)
    {
        if (clientNode == null) return null;
        var id = clientNode["id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(id)) return null;

        var roomEntity = rooms.FirstOrDefault(n => n.MacAddress == id);
        
        // Fix: Ensure we don't return an empty string as name. Fallback to host name, then ID.
        var friendlyName = roomEntity?.FriendlyName;
        var hostName = clientNode["host"]?["name"]?.GetValue<string>();

        var name = string.IsNullOrWhiteSpace(friendlyName) 
            ? (string.IsNullOrWhiteSpace(hostName) ? id : hostName)
            : $"{friendlyName} ({hostName ?? id})";

        var connected = clientNode["connected"]?.GetValue<bool>() ?? false;
        var volumePercent = clientNode["config"]?["volume"]?["percent"]?.GetValue<int>() ?? 0;
        var volumeMuted = clientNode["config"]?["volume"]?["muted"]?.GetValue<bool>() ?? false;
        var mixVolume = roomEntity?.MixVolume ?? 100;

        return new RoomDto(id, name, friendlyName, hostName, connected, volumePercent, volumeMuted, mixVolume);
    }
}
