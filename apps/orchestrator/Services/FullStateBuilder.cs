using System.Text.Json;
using System.Text.Json.Nodes;
using AudioOrchestrator.Contracts;
using AudioOrchestrator.Data;
using AudioOrchestrator.Data.Entities;
using AudioOrchestrator.Services;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.EntityFrameworkCore;

namespace AudioOrchestrator.Services;

public class FullStateBuilder
{
    private readonly OrchestratorDbContext _db;
    private readonly ISnapcastService _snapcast;
    private readonly INodeReleaseService? _nodeReleaseService;
    private readonly ISpotifyNodeReleaseService? _spotifyReleaseService;
    private readonly IDockerClient? _dockerClient;

    public FullStateBuilder(OrchestratorDbContext db, ISnapcastService snapcast)
        : this(db, snapcast, null, null, null)
    {
    }

    public FullStateBuilder(OrchestratorDbContext db, ISnapcastService snapcast, ISpotifyNodeReleaseService? releaseService)
        : this(db, snapcast, releaseService as INodeReleaseService, releaseService, null)
    {
    }

    public FullStateBuilder(OrchestratorDbContext db, ISnapcastService snapcast, INodeReleaseService? nodeReleaseService)
        : this(db, snapcast, nodeReleaseService, nodeReleaseService as ISpotifyNodeReleaseService, null)
    {
    }

    public FullStateBuilder(
        OrchestratorDbContext db, 
        ISnapcastService snapcast, 
        INodeReleaseService? nodeReleaseService, 
        ISpotifyNodeReleaseService? spotifyReleaseService,
        IDockerClient? dockerClient = null)
    {
        _db = db;
        _snapcast = snapcast;
        _nodeReleaseService = nodeReleaseService ?? (spotifyReleaseService as INodeReleaseService);
        _spotifyReleaseService = spotifyReleaseService ?? (_nodeReleaseService as ISpotifyNodeReleaseService);
        _dockerClient = dockerClient;
    }

    public virtual async Task<FullStateDto> BuildAsync()
    {
        var groups = await _db.Groups.AsNoTracking().ToListAsync();
        var roomsFromDb = await _db.Rooms.AsNoTracking().ToListAsync();
        var radios = await _db.RadioStreams.AsNoTracking().ToListAsync();
        var spotifySources = await _db.SpotifySources.AsNoTracking().ToListAsync();
        var status = await _snapcast.GetStatusAsync();

        IList<ContainerListResponse>? containers = null;
        if (_dockerClient != null)
        {
            try
            {
                containers = await _dockerClient.Containers.ListContainersAsync(new ContainersListParameters { All = true });
            }
            catch
            {
                // Ignore docker connection issues in test/restricted environments
            }
        }

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

            var currentVersion = string.IsNullOrWhiteSpace(source.CurrentVersion)
                ? OrchestratorConstants.SpotifyNodeVersion
                : source.CurrentVersion;

            string? latestCompatible = null;
            bool hasUpdate = false;
            if (_spotifyReleaseService != null)
            {
                latestCompatible = await _spotifyReleaseService.GetLatestCompatibleVersionAsync(currentVersion);
                hasUpdate = !string.IsNullOrEmpty(latestCompatible) && latestCompatible != currentVersion;
            }

            var containerName = $"/{OrchestratorConstants.SpotifyContainerPrefix}{source.Id}";
            var c = containers?.FirstOrDefault(ct => ct.Names != null && ct.Names.Contains(containerName));
            bool containerRunning = containers == null || (c != null && c.State == "running");

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
                currentVersion,
                latestCompatible,
                hasUpdate,
                containerRunning
            ));
        }

        // Build Radio Plugins
        var pluginsFromDb = await _db.RadioPlugins.AsNoTracking().ToListAsync();
        var pluginDtos = new List<RadioPluginDto>();
        foreach (var p in pluginsFromDb)
        {
            var curVer = p.GetEffectiveVersion();
            string? latest = null;
            bool hasUpdate = false;
            var repo = p.GetGitHubRepository();
            if (_nodeReleaseService != null && !string.IsNullOrEmpty(repo))
            {
                latest = await _nodeReleaseService.GetLatestCompatibleVersionAsync(repo, curVer);
                hasUpdate = !string.IsNullOrEmpty(latest) && latest != curVer;
            }

            pluginDtos.Add(new RadioPluginDto(
                p.Id,
                p.Name,
                p.DockerImage,
                p.GetUrlPatterns(),
                p.Enabled,
                p.CreatedAt,
                curVer,
                latest,
                hasUpdate,
                p.GitHubRepository
            ));
        }

        // Build Radio Streams (from DB)
        foreach (var radio in radios)
        {
            // We find the corresponding snapcast stream for status
            var ss = snapStreams?.FirstOrDefault(s => s?["id"]?.GetValue<string>() == $"{OrchestratorConstants.RadioPrefix}{radio.Id}");
            var st = ss?["status"]?.GetValue<string>() ?? "idle";

            var matchedPlugin = pluginsFromDb.FirstOrDefault(p => p.Enabled && p.GetUrlPatterns().Any(pattern =>
            {
                if (string.IsNullOrWhiteSpace(pattern)) return false;
                try
                {
                    return System.Text.RegularExpressions.Regex.IsMatch(radio.StreamUrl, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
                }
                catch
                {
                    return false;
                }
            }));

            string? pluginVer = matchedPlugin?.GetEffectiveVersion();
            string? latestComp = null;
            bool hasUpdate = false;
            string? pluginId = matchedPlugin?.Id;

            if (matchedPlugin != null)
            {
                var pDto = pluginDtos.FirstOrDefault(p => p.Id == matchedPlugin.Id);
                if (pDto != null)
                {
                    latestComp = pDto.LatestCompatibleVersion;
                    hasUpdate = pDto.HasUpdate;
                }
            }

            var radioContainerName = $"/{OrchestratorConstants.RadioContainerPrefix}{radio.Id}";
            var rc = containers?.FirstOrDefault(ct => ct.Names != null && ct.Names.Contains(radioContainerName));
            bool radioContainerRunning = containers == null || (rc != null && rc.State == "running");

            radioDtos.Add(new StreamDto(
                $"{OrchestratorConstants.RadioPrefix}{radio.Id}", 
                radio.Name, 
                st, 
                radio.StreamUrl,
                radio.CurrentTitle,
                radio.CurrentArtist,
                radio.CurrentCoverUrl,
                radio.ErrorMessage,
                pluginVer,
                latestComp,
                hasUpdate,
                pluginId,
                radioContainerRunning
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
        var settings = await _db.Settings.FirstOrDefaultAsync();
        var volumeProfile = settings?.VolumeProfile ?? "cubic";

        return new FullStateDto(groupDtos, unassignedRooms, radioDtos, spotifySourceDtos, snapcastStreamDtos, pluginDtos, volumeProfile);
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
        var volumeMuted = (roomEntity?.IsMuted ?? false) || (clientNode["config"]?["volume"]?["muted"]?.GetValue<bool>() ?? false);
        var mixVolume = roomEntity?.MixVolume ?? 100;

        return new RoomDto(id, name, friendlyName, hostName, connected, volumePercent, volumeMuted, mixVolume);
    }
}
