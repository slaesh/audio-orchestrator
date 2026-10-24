using Microsoft.AspNetCore.Mvc;
using AudioOrchestrator.Services;
using AudioOrchestrator.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using AudioOrchestrator.Hubs;
using AudioOrchestrator.Contracts;
using Microsoft.Extensions.Logging;
using AudioOrchestrator.Data.Entities;

namespace AudioOrchestrator.Controllers;

[ApiController]
[Route("api/internal/[controller]")]
public class EventsController : ControllerBase
{
    private readonly ISnapcastService _snapcast;
    private readonly OrchestratorDbContext _db;
    private readonly ILogger<EventsController> _logger;
    private readonly IHubContext<AudioHub, IAudioClient> _hub;
    private readonly FullStateBuilder _stateBuilder;
    private readonly VolumeOrchestratorService _volumeOrchestrator;
    private readonly ILibrespotApiClient _librespotApi;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHomeAssistantMqttService _mqttService;
    private readonly IBroadcastService? _broadcastService;

    public EventsController(
        ISnapcastService snapcast, 
        OrchestratorDbContext db, 
        ILogger<EventsController> logger,
        IHubContext<AudioHub, IAudioClient> hub,
        FullStateBuilder stateBuilder,
        VolumeOrchestratorService volumeOrchestrator,
        ILibrespotApiClient librespotApi,
        IServiceScopeFactory scopeFactory,
        IHomeAssistantMqttService mqttService,
        IBroadcastService? broadcastService = null)
    {
        _snapcast = snapcast;
        _db = db;
        _logger = logger;
        _hub = hub;
        _stateBuilder = stateBuilder;
        _volumeOrchestrator = volumeOrchestrator;
        _librespotApi = librespotApi;
        _scopeFactory = scopeFactory;
        _mqttService = mqttService;
        _broadcastService = broadcastService;
    }

    private async Task SyncMetadataToSource(SpotifySourceEntity source, WebhookEventPayload payload, OrchestratorDbContext db)
    {
        if (payload.TrackId != null) source.CurrentTrackId = payload.TrackId;
        if (payload.PositionMs.HasValue) source.CurrentPositionMs = payload.PositionMs.Value;
        if (payload.IntervalMs.HasValue) source.HeartbeatIntervalMs = payload.IntervalMs.Value;
        source.LastUpdateTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (string.IsNullOrEmpty(source.CurrentTrackId))
        {
            source.CurrentTitle = null;
            source.CurrentArtist = null;
            source.CurrentAlbum = null;
            source.CurrentDurationMs = 0;
            source.CurrentCoverUrl = null;
            return;
        }

        var meta = await db.SpotifyMetadata.FindAsync(source.CurrentTrackId);
        if (meta != null)
        {
            source.CurrentTitle = meta.Title;
            source.CurrentArtist = meta.Artist;
            source.CurrentAlbum = meta.Album;
            source.CurrentDurationMs = meta.DurationMs;
            source.CurrentCoverUrl = meta.CoverUrl;
        }
    }

    [HttpPost]
    public async Task<IActionResult> HandleEvent([FromBody] WebhookEventPayload payload)
    {
        if (payload.Event == "heartbeat")
        {
            _logger.LogDebug("Received webhook event {Event} for Group {GroupId} (Volume: {Volume})", payload.Event, payload.GroupId, payload.Volume);
        }
        else
        {
            _logger.LogInformation("Received webhook event {Event} for Group {GroupId} (Volume: {Volume})", payload.Event, payload.GroupId, payload.Volume);
        }

        if (string.IsNullOrEmpty(payload.GroupId)) return BadRequest("GroupId missing");
        
        string sourceStreamId = $"{OrchestratorConstants.SpotifyPrefix}{payload.GroupId}";
        var source = await _db.SpotifySources.FindAsync(payload.GroupId);
        
        // Groups that are currently owned by a running broadcast must not be switched back to
        // Spotify or have their (announcement) volume overwritten until restoration has finished.
        var broadcastGroupIds = _broadcastService?.GetStatus().ActiveTargetGroupIds ?? new List<string>();

        var listeningGroups = (await _db.Groups
            .AsNoTracking()
            .Where(g => g.CurrentStreamId == sourceStreamId)
            .ToListAsync())
            .Where(g => !broadcastGroupIds.Contains(g.Id))
            .ToList();

        List<SpotifyRecentContextDto>? currentRecentContexts = null;

        if (payload.Event == "heartbeat")
        {
            _logger.LogDebug("[Broadcast] Received {Event} from Spotify source {GroupId} ({SourceName}). Found {GroupCount} listening groups.", payload.Event, payload.GroupId, source?.Name, listeningGroups.Count);
        }
        else
        {
            _logger.LogInformation("[Broadcast] Received {Event} from Spotify source {GroupId} ({SourceName}). Found {GroupCount} listening groups.", payload.Event, payload.GroupId, source?.Name, listeningGroups.Count);
        }

        var isConnected = payload.Event switch
        {
            "connected" => true,
            "session_connected" => true,
            "disconnected" => false,
            "session_disconnected" => false,
            _ => source?.IsConnected
        };

        if (source != null && isConnected.HasValue && source.IsConnected != isConnected.Value)
        {
            source.IsConnected = isConnected.Value;
            if (!isConnected.Value)
            {
                source.ResetPlaybackState();
            }
            _logger.LogInformation("[Connection State] Source {SourceName} IsConnected updated to {IsConnected}", source.Name, source.IsConnected);
        }

        // Pre-emptive connection flag: Any event from the node (except explicit disconnect) means it's alive.
        if (payload.Event != "disconnected" && payload.Event != "session_disconnected" && source != null && !source.IsConnected)
        {
            if (payload.Event != "heartbeat" || payload.IsPlaying == true)
            {
                source.IsConnected = true;
                _logger.LogInformation("[Connection State] Source {SourceName} Pre-emptive connection flag set IsConnected to true", source.Name);
            }
        }

        if (payload.Event == "playing")
        {
            if (source != null)
            {
                source.IsConnected = true;
                source.IsActive = true;
            }

            var groupIds = listeningGroups.Select(g => g.Id).ToList();
            var groupNames = listeningGroups.Select(g => g.Name).ToList();
            var targetVolume = listeningGroups.FirstOrDefault()?.MasterVolume ?? source?.MasterVolume;

            _ = Task.Run(async () => {
                try {
                    using var scope = _scopeFactory.CreateScope();
                    var bgSnapcast = scope.ServiceProvider.GetRequiredService<ISnapcastService>();
                    var bgVolume = scope.ServiceProvider.GetRequiredService<VolumeOrchestratorService>();
                    var bgDb = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                    var bgLibrespot = scope.ServiceProvider.GetRequiredService<ILibrespotApiClient>();

                    if (targetVolume.HasValue)
                    {
                        try
                        {
                            await bgLibrespot.SetVolumeAsync(payload.GroupId, targetVolume.Value);
                            _logger.LogInformation("[Sync-BG] Enforced group volume {TargetVolume}% on Spotify playback start for {GroupId}", targetVolume.Value, payload.GroupId);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to enforce volume on playback start for {GroupId}", payload.GroupId);
                        }
                    }

                    var bgBroadcast = scope.ServiceProvider.GetService<IBroadcastService>();

                    for (int i = 0; i < groupIds.Count; i++)
                    {
                        var id = groupIds[i];
                        var g = await bgDb.Groups.FindAsync(id);
                        if (g == null) continue;
                        var snapcastGroupId = g.SnapcastGroupId ?? g.Id;
                        if (bgBroadcast?.IsGroupActiveInBroadcast(g.Id, snapcastGroupId) == true)
                        {
                            _logger.LogInformation("[Sync-BG] Group {GroupName} is currently active in a broadcast, skipping Spotify playback join", groupNames[i]);
                            continue;
                        }

                        await bgSnapcast.SetGroupStreamAsync(snapcastGroupId, sourceStreamId);
                        _logger.LogInformation("[Sync-BG] Group {GroupName} joined active source stream {StreamId}", groupNames[i], sourceStreamId);
                        
                        await bgVolume.SetGroupVolumeAsync(g.Id, g.MasterVolume, g.IsMuted, fromDocker: true, syncStreamSource: false);
                    }
                } catch (Exception ex) {
                    _logger.LogError(ex, "Failed to perform background stream/volume sync for 'playing' event");
                }
            });
        }
        else if (payload.Event == "connected" || payload.Event == "session_connected")
        {
            _logger.LogInformation("[Sync] Spotify session established for source {GroupId}. Setting connected state.", payload.GroupId);
            if (source != null) {
                source.IsConnected = true;
                source.ErrorMessage = null;
            }

            var targetVolume = listeningGroups.FirstOrDefault()?.MasterVolume ?? source?.MasterVolume;
            if (targetVolume.HasValue)
            {
                var vol = targetVolume.Value;
                _ = Task.Run(async () => {
                    try {
                        using var scope = _scopeFactory.CreateScope();
                        var bgLibrespot = scope.ServiceProvider.GetRequiredService<ILibrespotApiClient>();
                        await bgLibrespot.SetVolumeAsync(payload.GroupId, vol);
                        _logger.LogInformation("[Sync-BG] Pre-emptively enforced volume {Volume}% to newly connected Spotify session {GroupId}", vol, payload.GroupId);
                    } catch (Exception ex) {
                        _logger.LogWarning(ex, "Failed to pre-emptively enforce volume on session connect for {GroupId}", payload.GroupId);
                    }
                });
            }
        }
        else if (payload.Event == "started")
        {
            _logger.LogInformation("[Sync] Spotify track started for source {GroupId}. Setting active state.", payload.GroupId);
            if (source != null) {
                source.IsConnected = true;
                source.IsActive = true;
                await SyncMetadataToSource(source, payload, _db);
            }
        }
        else if (payload.Event == "disconnected")
        {
            _logger.LogWarning("[Sync] Spotify source {GroupId} disconnected.", payload.GroupId);
            if (source != null) {
                source.ResetPlaybackState();
            }
        }
        else if (payload.Event == "volume_set" || payload.Event == "volume_changed")
        {
            if (payload.Volume.HasValue)
            {
                int percent = (int)Math.Round((payload.Volume.Value / 65535.0) * 100);

                // If playback is not active yet and listening groups exist, do not let an initial/startup volume
                // from Docker/Spotify overwrite the group's established volume. Instead, push the group volume back to Spotify.
                if (source != null && !source.IsActive && listeningGroups.Count > 0)
                {
                    var targetVolume = listeningGroups[0].MasterVolume;
                    if (percent != targetVolume)
                    {
                        _logger.LogInformation("[Sync] Inactive Spotify source {GroupId} reported initial volume {Percent}%. Preserving group volume {TargetVolume}% and enforcing to Spotify.", payload.GroupId, percent, targetVolume);
                        _ = Task.Run(async () => {
                            try {
                                using var scope = _scopeFactory.CreateScope();
                                var bgLibrespot = scope.ServiceProvider.GetRequiredService<ILibrespotApiClient>();
                                await bgLibrespot.SetVolumeAsync(payload.GroupId, targetVolume);
                            } catch (Exception ex) {
                                _logger.LogWarning(ex, "Failed to enforce preserved group volume for {GroupId}", payload.GroupId);
                            }
                        });
                    }
                }
                else
                {
                    if (source != null) {
                        source.MasterVolume = percent;
                    }

                    var groupIds = listeningGroups.Select(g => g.Id).ToList();
                    var groupNames = listeningGroups.Select(g => g.Name).ToList();

                    _ = Task.Run(async () => {
                        try {
                            using var scope = _scopeFactory.CreateScope();
                            var bgVolume = scope.ServiceProvider.GetRequiredService<VolumeOrchestratorService>();
                            var bgDb = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

                            var bgBroadcast = scope.ServiceProvider.GetService<IBroadcastService>();

                            for (int i = 0; i < groupIds.Count; i++)
                            {
                                var id = groupIds[i];
                                var g = await bgDb.Groups.FindAsync(id);
                                if (g == null) continue;
                                if (bgBroadcast?.IsGroupActiveInBroadcast(g.Id, g.SnapcastGroupId) == true) continue;
                                await bgVolume.SetGroupVolumeAsync(g.Id, percent, g.IsMuted, fromDocker: true, syncStreamSource: false);
                                _logger.LogInformation("[Broadcast-BG] Synced Spotify volume ({Percent}%) to listening Group {GroupName}", percent, groupNames[i]);
                            }
                        } catch (Exception ex) {
                            _logger.LogError(ex, "Failed to sync volume in background for {Event}", payload.Event);
                        }
                    });
                }
            }
        }
        else if (payload.Event == "stopped" || payload.Event == "inactive" || payload.Event == "paused")
        {
            if (source != null) {
                source.IsActive = false;
                if (payload.Event == "stopped" || payload.Event == "inactive")
                {
                    source.ResetPlaybackState();
                    source.IsConnected = isConnected ?? source.IsConnected;
                }
            }
        }
        else if (payload.Event == "set_queue")
        {
            if (source != null) {
                if (payload.ContextUri != null)
                {
                    source.ContextUri = payload.ContextUri;
                }
                
                if (payload.ContextName != null)
                {
                    source.ContextName = payload.ContextName;
                }
                else if (string.IsNullOrEmpty(payload.ContextUri))
                {
                    source.ContextUri = null;
                    source.ContextName = null;
                }

                if (!string.IsNullOrEmpty(payload.ContextUri) && !string.IsNullOrEmpty(payload.ContextName))
                {
                    var recents = new List<SpotifyRecentContextDto>();
                    if (!string.IsNullOrEmpty(source.RecentContextsJson))
                    {
                        try {
                            recents = JsonSerializer.Deserialize<List<SpotifyRecentContextDto>>(source.RecentContextsJson) ?? new List<SpotifyRecentContextDto>();
                        } catch {}
                    }

                    recents.RemoveAll(r => r.Uri == payload.ContextUri);
                    recents.Insert(0, new SpotifyRecentContextDto(payload.ContextUri, payload.ContextName));
                    if (recents.Count > 10)
                    {
                        recents = recents.Take(10).ToList();
                    }
                    source.RecentContextsJson = JsonSerializer.Serialize(recents);
                    currentRecentContexts = recents;
                }
                else if (!string.IsNullOrEmpty(source.RecentContextsJson))
                {
                    try {
                        currentRecentContexts = JsonSerializer.Deserialize<List<SpotifyRecentContextDto>>(source.RecentContextsJson);
                    } catch {}
                }
            }
        }

        if (source != null && (payload.Event == "heartbeat" || payload.Event == "playing" || payload.Event == "paused" || payload.Event == "seeked" || payload.Event == "position_correction" || payload.Event == "position_changed" || payload.Event == "changed"))
        {
            if (payload.Event == "playing" || payload.Event == "changed") {
                source.IsActive = true;
            } else if (payload.Event == "paused") {
                source.IsActive = false;
            } else if (payload.Event == "heartbeat" && payload.IsPlaying.HasValue) {
                source.IsActive = payload.IsPlaying.Value;
            }
            await SyncMetadataToSource(source, payload, _db);
            
            // Enrich payload with DB metadata if it's missing (for SignalR broadcast)
            if (string.IsNullOrEmpty(payload.Title))
            {
                payload.Title = source.CurrentTitle;
                payload.Artist = source.CurrentArtist;
                payload.Album = source.CurrentAlbum;
                payload.DurationMs = source.CurrentDurationMs;
                payload.CoverUrl = source.CurrentCoverUrl;
            }
        }
        
        if (payload.Event == "metadata_update" && !string.IsNullOrEmpty(payload.TrackId))
        {
            var meta = await _db.SpotifyMetadata.FindAsync(payload.TrackId);
            if (meta == null)
            {
                meta = new SpotifyMetadataEntity { TrackId = payload.TrackId };
                _db.SpotifyMetadata.Add(meta);
            }
            
            meta.Title = payload.Title;
            meta.Artist = payload.Artist;
            meta.Album = payload.Album;
            meta.DurationMs = payload.DurationMs;
            meta.CoverUrl = payload.CoverUrl;
            meta.LastUpdated = DateTime.UtcNow;
            
            _logger.LogInformation("[Metadata] Cached track metadata: {Title} - {Artist} ({TrackId})", payload.Title, payload.Artist, payload.TrackId);

            if (source != null && source.CurrentTrackId == payload.TrackId)
            {
                await SyncMetadataToSource(source, payload, _db);
            }
        }

        // Single coordinated SaveChangesAsync() for the event processing
        await _db.SaveChangesAsync();

        // Map to SpotifyEventDto for SignalR
        var spotifyEvent = new SpotifyEventDto
        {
            GroupId = payload.GroupId,
            Event = payload.Event,
            Volume = payload.Volume,
            TrackId = payload.TrackId,
            Title = payload.Title,
            Artist = payload.Artist,
            Album = payload.Album,
            DurationMs = payload.DurationMs,
            CoverUrl = payload.CoverUrl,
            PositionMs = payload.PositionMs,
            IntervalMs = payload.IntervalMs,
            IsConnected = source?.IsConnected,
            IsPlaying = payload.IsPlaying,
            ContextUri = payload.ContextUri ?? source?.ContextUri,
            ContextName = payload.ContextName ?? source?.ContextName,
            RecentContexts = currentRecentContexts
        };

        // Always send the fine-grained event
        await _hub.Clients.All.OnSpotifyEvent(spotifyEvent);
        _ = _mqttService.NotifySpotifyEventAsync(spotifyEvent);

        // Major state changes still trigger a full sync to ensure all clients are consistent
        var majorEvents = new[] { "playing", "stopped", "paused", "started", "inactive", "connected", "disconnected", "set_queue" };
        if (majorEvents.Contains(payload.Event))
        {
            var freshState = await _stateBuilder.BuildAsync();
            await _hub.Clients.All.OnFullStateUpdate(freshState);
            _ = _mqttService.NotifyFullStateAsync(freshState);
        }

        return Ok();
    }
}

public class WebhookEventPayload : AudioOrchestrator.Services.Generated.WebhookPayload
{
    // Metadata fields (present when Event == "metadata_update", see Generated.MetadataPayload)
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? CoverUrl { get; set; }
}
