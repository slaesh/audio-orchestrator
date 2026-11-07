using System.Collections.Concurrent;
using AudioOrchestrator.Services.Generated;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AudioOrchestrator.Services;

public interface ISpotifyVolumeSyncService
{
    void SchedulePushVolume(string sourceId, int percent, TimeSpan? delay = null);
    Task PushVolumeImmediateAsync(string sourceId, int percent);
    bool ShouldAcceptIncomingVolume(string sourceId, int incomingPercent, IEnumerable<int> currentGroupVolumes);
}

public class SpotifyVolumeSyncService : ISpotifyVolumeSyncService, IDisposable
{
    private class SourceSyncState
    {
        public readonly object Lock = new();
        public int? PendingVolume { get; set; }
        public CancellationTokenSource? DebounceCts { get; set; }
        public DateTime LastLocalChangeTime { get; set; } = DateTime.MinValue;
        public readonly ConcurrentQueue<(int volume, DateTime timestamp)> RecentPushes = new();
        public readonly SemaphoreSlim WriteGate = new(1, 1);
        public long LatestVersion { get; set; }
    }

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SpotifyVolumeSyncService> _logger;
    private readonly ILibrespotApiClient? _directClient;
    private readonly ConcurrentDictionary<string, SourceSyncState> _states = new(StringComparer.OrdinalIgnoreCase);

    public TimeSpan DebounceDelay { get; set; } = TimeSpan.FromMilliseconds(350);
    public TimeSpan CooldownWindow { get; set; } = TimeSpan.FromSeconds(2.5);
    public TimeSpan HistoryWindow { get; set; } = TimeSpan.FromSeconds(10.0);

    public SpotifyVolumeSyncService(
        IServiceScopeFactory scopeFactory,
        ILogger<SpotifyVolumeSyncService> logger,
        ILibrespotApiClient? directClient = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _directClient = directClient;
    }

    public void SchedulePushVolume(string sourceId, int percent, TimeSpan? delay = null)
    {
        var state = _states.GetOrAdd(sourceId, _ => new SourceSyncState());
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        long version;

        lock (state.Lock)
        {
            state.LastLocalChangeTime = DateTime.UtcNow;
            state.PendingVolume = percent;
            version = ++state.LatestVersion;

            state.DebounceCts?.Cancel();
            state.DebounceCts = cts;
        }

        var effectiveDelay = delay ?? DebounceDelay;

        _ = Task.Run(async () =>
        {
            try
            {
                if (effectiveDelay > TimeSpan.Zero)
                {
                    await Task.Delay(effectiveDelay, token);
                }

                if (token.IsCancellationRequested) return;

                await state.WriteGate.WaitAsync(token);
                try
                {
                    int targetVolume;
                    lock (state.Lock)
                    {
                        if (token.IsCancellationRequested || !state.PendingVolume.HasValue || version < state.LatestVersion)
                        {
                            return;
                        }

                        targetVolume = state.PendingVolume.Value;
                        state.PendingVolume = null;
                        state.LastLocalChangeTime = DateTime.UtcNow;
                        state.RecentPushes.Enqueue((targetVolume, DateTime.UtcNow));
                        PruneRecentPushes(state);
                    }

                    await ExecuteSetVolumeAsync(sourceId, targetVolume);
                    _logger.LogInformation("[SpotifySync] Debounced pushed volume {Percent}% to Spotify source {SourceId}", targetVolume, sourceId);
                }
                finally
                {
                    state.WriteGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // Debounce superseded by a newer volume
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is ApiException)
            {
                _logger.LogWarning("Could not push debounced volume to Spotify source {SourceId}: {Message}", sourceId, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error pushing debounced volume to Spotify source {SourceId}", sourceId);
            }
            finally
            {
                lock (state.Lock)
                {
                    if (ReferenceEquals(state.DebounceCts, cts))
                    {
                        state.DebounceCts = null;
                    }
                }
                cts.Dispose();
            }
        });
    }

    public async Task PushVolumeImmediateAsync(string sourceId, int percent)
    {
        var state = _states.GetOrAdd(sourceId, _ => new SourceSyncState());
        long version;
        lock (state.Lock)
        {
            state.DebounceCts?.Cancel();
            state.DebounceCts = null;
            state.PendingVolume = percent;
            state.LastLocalChangeTime = DateTime.UtcNow;
            version = ++state.LatestVersion;
        }

        await state.WriteGate.WaitAsync();
        try
        {
            lock (state.Lock)
            {
                if (version < state.LatestVersion)
                {
                    // A newer write was already requested while waiting for the gate
                    return;
                }

                state.PendingVolume = null;
                state.LastLocalChangeTime = DateTime.UtcNow;
                state.RecentPushes.Enqueue((percent, DateTime.UtcNow));
                PruneRecentPushes(state);
            }

            await ExecuteSetVolumeAsync(sourceId, percent);
            _logger.LogInformation("[SpotifySync] Immediately pushed volume {Percent}% to Spotify source {SourceId}", percent, sourceId);
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is ApiException)
        {
            _logger.LogWarning("Could not push immediate volume to Spotify source {SourceId}: {Message}", sourceId, ex.Message);
        }
        finally
        {
            state.WriteGate.Release();
        }
    }

    public bool ShouldAcceptIncomingVolume(string sourceId, int incomingPercent, IEnumerable<int> currentGroupVolumes)
    {
        var groupVolumesList = currentGroupVolumes as IList<int> ?? currentGroupVolumes.ToList();

        // 1. If all listening groups already have this exact volume, ignore (redundant processing)
        if (groupVolumesList.Count > 0 && groupVolumesList.All(v => v == incomingPercent))
        {
            return false;
        }

        if (!_states.TryGetValue(sourceId, out var state))
        {
            return true;
        }

        lock (state.Lock)
        {
            PruneRecentPushes(state);

            // 2. If it matches what is currently pending to be pushed, ignore
            if (state.PendingVolume.HasValue && state.PendingVolume.Value == incomingPercent)
            {
                return false;
            }

            // 3. If it matches any volume we pushed recently within the history window, ignore (stale echo)
            if (state.RecentPushes.Any(p => p.volume == incomingPercent))
            {
                return false;
            }

            // 4. If local volume was changed within CooldownWindow, ignore (local user is actively controlling)
            if ((DateTime.UtcNow - state.LastLocalChangeTime) < CooldownWindow)
            {
                return false;
            }

            return true;
        }
    }

    private async Task ExecuteSetVolumeAsync(string sourceId, int targetVolume)
    {
        if (_directClient != null)
        {
            await _directClient.SetVolumeAsync(sourceId, targetVolume);
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<ILibrespotApiClient>();
        await client.SetVolumeAsync(sourceId, targetVolume);
    }

    private void PruneRecentPushes(SourceSyncState state)
    {
        var cutoff = DateTime.UtcNow - HistoryWindow;
        while (state.RecentPushes.TryPeek(out var item) && item.timestamp < cutoff)
        {
            state.RecentPushes.TryDequeue(out _);
        }
    }

    public void Dispose()
    {
        foreach (var kvp in _states)
        {
            lock (kvp.Value.Lock)
            {
                kvp.Value.DebounceCts?.Cancel();
                kvp.Value.DebounceCts = null;
            }
        }
        _states.Clear();
    }
}
