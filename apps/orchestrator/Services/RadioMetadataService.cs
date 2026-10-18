using AudioOrchestrator.Contracts;
using AudioOrchestrator.Data;
using AudioOrchestrator.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

namespace AudioOrchestrator.Services;

public class RadioMetadataService : BackgroundService
{
    private readonly ILogger<RadioMetadataService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IHubContext<AudioHub, IAudioClient> _hub;
    private readonly ConcurrentDictionary<string, DateTime> _lastMetadataUpdate = new();

    public RadioMetadataService(
        ILogger<RadioMetadataService> logger,
        IServiceProvider serviceProvider,
        IHubContext<AudioHub, IAudioClient> hub)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _hub = hub;
    }

    public async Task ProcessRadioEventAsync(RadioEventPayload payload, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(payload.StreamId)) return;

        var streamDbId = payload.StreamId.StartsWith(OrchestratorConstants.RadioPrefix)
            ? payload.StreamId.Substring(OrchestratorConstants.RadioPrefix.Length)
            : payload.StreamId;

        _lastMetadataUpdate[streamDbId] = DateTime.UtcNow;

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var stream = await db.RadioStreams.FindAsync(new object[] { streamDbId }, ct);
        if (stream == null) return;

        bool changed = stream.CurrentTitle != payload.Title ||
                       stream.CurrentArtist != payload.Artist ||
                       stream.CurrentCoverUrl != payload.CoverUrl;

        if (changed)
        {
            stream.CurrentTitle = payload.Title;
            stream.CurrentArtist = payload.Artist;
            stream.CurrentCoverUrl = payload.CoverUrl;
            await db.SaveChangesAsync(ct);

            var signalrId = $"{OrchestratorConstants.RadioPrefix}{streamDbId}";
            await _hub.Clients.All.OnRadioMetadataChanged(signalrId, payload.Title, payload.Artist, payload.CoverUrl);
            _logger.LogInformation("Radio {StreamId} metadata updated: {Artist} - {Title}", streamDbId, payload.Artist, payload.Title);
        }
    }

    public async Task ClearMetadataAsync(string streamId, CancellationToken ct = default)
    {
        var streamDbId = streamId.StartsWith(OrchestratorConstants.RadioPrefix)
            ? streamId.Substring(OrchestratorConstants.RadioPrefix.Length)
            : streamId;

        _lastMetadataUpdate.TryRemove(streamDbId, out _);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var stream = await db.RadioStreams.FindAsync(new object[] { streamDbId }, ct);
        if (stream == null) return;

        if (!string.IsNullOrEmpty(stream.CurrentTitle) || !string.IsNullOrEmpty(stream.CurrentArtist) || !string.IsNullOrEmpty(stream.CurrentCoverUrl))
        {
            stream.CurrentTitle = null;
            stream.CurrentArtist = null;
            stream.CurrentCoverUrl = null;
            await db.SaveChangesAsync(ct);

            var signalrId = $"{OrchestratorConstants.RadioPrefix}{streamDbId}";
            await _hub.Clients.All.OnRadioMetadataChanged(signalrId, null, null, null);
            _logger.LogInformation("Radio {StreamId} metadata cleared", streamDbId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

                var now = DateTime.UtcNow;
                foreach (var (streamId, lastUpdate) in _lastMetadataUpdate)
                {
                    if (now - lastUpdate > TimeSpan.FromMinutes(8))
                    {
                        _logger.LogInformation("Radio {StreamId} metadata timeout (8 min), clearing...", streamId);
                        await ClearMetadataAsync(streamId, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in RadioMetadataService inactivity loop");
            }
        }
    }
}
