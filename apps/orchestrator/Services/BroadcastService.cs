using System.Diagnostics;
using AudioOrchestrator.Contracts;
using AudioOrchestrator.Data;
using AudioOrchestrator.Data.Entities;
using AudioOrchestrator.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AudioOrchestrator.Services;

public class DefaultAudioPlayerProcessRunner : IAudioPlayerProcessRunner
{
    private readonly ILogger<DefaultAudioPlayerProcessRunner> _logger;

    public DefaultAudioPlayerProcessRunner(ILogger<DefaultAudioPlayerProcessRunner> logger)
    {
        _logger = logger;
    }

    public async Task<ProcessRunResult> StreamToFifoAsync(string inputSource, string fifoPath, Stream? stdinStream, CancellationToken ct)
    {
        var inputArg = stdinStream != null ? "pipe:0" : $"\"{inputSource}\"";
        var arguments = $"-hide_banner -loglevel error -i {inputArg} -f s16le -acodec pcm_s16le -ac 2 -ar 44100 -y \"{fifoPath}\"";

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = arguments,
            RedirectStandardInput = stdinStream != null,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        _logger.LogInformation("Starting ffmpeg for broadcast: ffmpeg {Arguments}", arguments);

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start ffmpeg process.");
        }

        using var reg = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    _logger.LogInformation("Cancellation requested. Terminating ffmpeg broadcast process.");
                    process.Kill(entireProcessTree: true);
                }
            }
            catch { /* Process may already have terminated */ }
        });

        Task? stdinTask = null;
        if (stdinStream != null)
        {
            stdinTask = Task.Run(async () =>
            {
                try
                {
                    await stdinStream.CopyToAsync(process.StandardInput.BaseStream, ct);
                    await process.StandardInput.BaseStream.FlushAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Error piping audio into ffmpeg stdin");
                }
                finally
                {
                    process.StandardInput.Close();
                }
            }, ct);
        }

        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);
        if (stdinTask != null)
        {
            try { await stdinTask; } catch { /* Ignore stdin task completion exceptions */ }
        }

        var stderr = await stderrTask;
        if (process.ExitCode != 0 && !ct.IsCancellationRequested)
        {
            _logger.LogWarning("ffmpeg exited with code {ExitCode}. Stderr: {Stderr}", process.ExitCode, stderr);
        }

        return new ProcessRunResult(process.ExitCode, stderr?.Trim() ?? string.Empty);
    }
}

public class BroadcastService : IBroadcastService
{
    private readonly ILogger<BroadcastService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ISnapcastService _snapcast;
    private readonly IFifoService _fifoService;
    private readonly ILibrespotApiClient _librespotApi;
    private readonly IAudioPlayerProcessRunner _processRunner;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private CancellationTokenSource? _activeBroadcastCts;
    private string? _currentAnnouncementId;
    private readonly List<string> _activeTargetGroupIds = new();

    public BroadcastService(
        ILogger<BroadcastService> logger,
        IServiceProvider serviceProvider,
        ISnapcastService snapcast,
        IFifoService fifoService,
        ILibrespotApiClient librespotApi,
        IAudioPlayerProcessRunner? processRunner = null)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _snapcast = snapcast;
        _fifoService = fifoService;
        _librespotApi = librespotApi;
        _processRunner = processRunner ?? new DefaultAudioPlayerProcessRunner(
            logger: serviceProvider.GetService<ILogger<DefaultAudioPlayerProcessRunner>>() 
                    ?? LoggerFactory.Create(b => b.AddConsole()).CreateLogger<DefaultAudioPlayerProcessRunner>());
    }

    public BroadcastStatusDto GetStatus()
    {
        lock (_activeTargetGroupIds)
        {
            return new BroadcastStatusDto(
                IsPlaying: _currentAnnouncementId != null,
                CurrentAnnouncementId: _currentAnnouncementId,
                ActiveTargetGroupIds: _activeTargetGroupIds.ToList()
            );
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Stop broadcast requested.");
        CancellationTokenSource? ctsToCancel;
        lock (_activeTargetGroupIds)
        {
            ctsToCancel = _activeBroadcastCts;
        }

        if (ctsToCancel != null)
        {
            try
            {
                ctsToCancel.Cancel();
            }
            catch (ObjectDisposedException) { }
        }

        // Wait briefly for the lock to be released (indicating cleanup completed)
        var acquired = await _lock.WaitAsync(TimeSpan.FromSeconds(3), ct);
        if (acquired)
        {
            _lock.Release();
        }
    }

    public async Task<BroadcastPlayResult> PlayUrlAsync(BroadcastPlayRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.AudioUrl))
        {
            return new BroadcastPlayResult(false, "AudioUrl darf nicht leer sein.");
        }

        return await ExecuteBroadcastAsync(request.AudioUrl, stdinStream: null, request, ct);
    }

    public async Task<BroadcastPlayResult> PlayFileAsync(Stream fileStream, string fileExtension, BroadcastPlayRequest request, CancellationToken ct = default)
    {
        if (fileStream == null || fileStream.Length == 0)
        {
            return new BroadcastPlayResult(false, "Dateistream ist leer.");
        }

        return await ExecuteBroadcastAsync(inputSource: "-", stdinStream: fileStream, request, ct);
    }

    private sealed class GroupSnapshot
    {
        public string GroupId { get; init; } = string.Empty;
        public string SnapcastGroupId { get; init; } = string.Empty;
        public string OriginalStreamId { get; init; } = "default";
        public int OriginalVolume { get; init; } = 100;
        public bool OriginalIsMuted { get; init; }
        public string? PausedSpotifySourceId { get; set; }
        public List<(string ClientId, int Volume, bool Muted)> UnselectedClients { get; } = new();
    }

    private async Task<BroadcastPlayResult> ExecuteBroadcastAsync(
        string inputSource,
        Stream? stdinStream,
        BroadcastPlayRequest request,
        CancellationToken ct)
    {
        if (request.Interrupt)
        {
            await StopAsync(ct);
        }

        var timeoutSec = Math.Clamp(request.TimeoutSeconds ?? 60, 1, 600);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

        var acquired = await _lock.WaitAsync(TimeSpan.FromSeconds(10), linkedCts.Token);
        if (!acquired)
        {
            return new BroadcastPlayResult(false, "Wiedergabe konnte nicht gestartet werden (Kanal blockiert).");
        }

        var announcementId = Guid.NewGuid().ToString("N");
        lock (_activeTargetGroupIds)
        {
            _activeBroadcastCts = linkedCts;
            _currentAnnouncementId = announcementId;
            _activeTargetGroupIds.Clear();
        }

        var stopwatch = Stopwatch.StartNew();
        var snapshots = new List<GroupSnapshot>();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var volumeOrchestrator = scope.ServiceProvider.GetRequiredService<VolumeOrchestratorService>();
        var hub = scope.ServiceProvider.GetRequiredService<IHubContext<AudioHub, IAudioClient>>();
        var stateBuilder = scope.ServiceProvider.GetRequiredService<FullStateBuilder>();
        var mqtt = scope.ServiceProvider.GetService<IHomeAssistantMqttService>();

        try
        {
            await _fifoService.EnsureFifoAsync(OrchestratorConstants.AnnouncementFifoPath);

            // 1. Resolve Targets
            snapshots = await ResolveTargetSnapshotsAsync(request, db);
            if (snapshots.Count == 0)
            {
                return new BroadcastPlayResult(false, "Keine Zielgruppen für die Durchsage gefunden.");
            }

            lock (_activeTargetGroupIds)
            {
                _activeTargetGroupIds.AddRange(snapshots.Select(s => s.GroupId));
            }

            // 2. Optional Pause active Spotify playback
            if (request.PausePlayback)
            {
                foreach (var s in snapshots)
                {
                    if (s.OriginalStreamId.StartsWith(OrchestratorConstants.SpotifyPrefix))
                    {
                        var sourceId = s.OriginalStreamId.Replace(OrchestratorConstants.SpotifyPrefix, "");
                        try
                        {
                            await _librespotApi.SendCommandAsync(sourceId, "pause");
                            s.PausedSpotifySourceId = sourceId;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to pause Spotify source {SourceId}", sourceId);
                        }
                    }
                }
            }

            // 3. Fade down current volume (if requested)
            var fadeMs = Math.Clamp(request.FadeDurationMs ?? 0, 0, 5000);
            if (fadeMs > 0)
            {
                await FadeGroupsVolumeAsync(snapshots, targetZero: true, fadeMs, volumeOrchestrator, linkedCts.Token);
            }

            // 4. Temporarily mute unselected rooms within target groups
            foreach (var s in snapshots)
            {
                foreach (var c in s.UnselectedClients)
                {
                    try
                    {
                        await _snapcast.SetClientVolumeAsync(c.ClientId, c.Volume, muted: true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to mute unselected client {ClientId}", c.ClientId);
                    }
                }
            }

            // 5. Switch target groups to announcement stream
            foreach (var s in snapshots)
            {
                try
                {
                    await _snapcast.SetGroupStreamAsync(s.SnapcastGroupId, OrchestratorConstants.AnnouncementStreamId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to set announcement stream on group {GroupId}", s.SnapcastGroupId);
                }
            }

            // 6. Set announcement playback volume
            foreach (var s in snapshots)
            {
                var targetVol = request.Volume.HasValue ? Math.Clamp(request.Volume.Value, 0, 100) : s.OriginalVolume;
                try
                {
                    await volumeOrchestrator.SetGroupVolumeAsync(s.GroupId, targetVol, isMuted: false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to set announcement volume on group {GroupId}", s.GroupId);
                }
            }

            // 7. Stream audio into FIFO using ffmpeg process runner
            var runResult = await _processRunner.StreamToFifoAsync(
                inputSource,
                OrchestratorConstants.AnnouncementFifoPath,
                stdinStream,
                linkedCts.Token
            );

            // 8. Snapcast audio buffer delay (~500ms) to ensure audio finishes playback on physical clients
            await Task.Delay(500, linkedCts.Token);

            if (runResult.ExitCode != 0)
            {
                var firstErrLine = runResult.Stderr.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                var detail = !string.IsNullOrWhiteSpace(firstErrLine) ? $": {firstErrLine}" : "";
                return new BroadcastPlayResult(
                    Success: false,
                    Message: $"ffmpeg beendet mit Code {runResult.ExitCode}{detail}",
                    DurationMs: (int)stopwatch.ElapsedMilliseconds
                );
            }

            return new BroadcastPlayResult(
                Success: true,
                Message: "Durchsage erfolgreich abgespielt.",
                DurationMs: (int)stopwatch.ElapsedMilliseconds
            );
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
        {
            _logger.LogInformation("Broadcast {Id} was cancelled or timed out.", announcementId);
            return new BroadcastPlayResult(false, "Durchsage abgebrochen oder Zeitüberschreitung.", (int)stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Broadcast {Id} failed unexpectedly", announcementId);
            return new BroadcastPlayResult(false, $"Fehler bei Durchsage: {ex.Message}", (int)stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            // GUARANTEED RESTORATION
            try
            {
                await RestoreSnapshotsAsync(snapshots, request.FadeDurationMs ?? 0, request.PausePlayback, volumeOrchestrator);
                var freshState = await stateBuilder.BuildAsync();
                await hub.Clients.All.OnFullStateUpdate(freshState);
                if (mqtt != null)
                {
                    _ = mqtt.NotifyFullStateAsync(freshState);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fully restore audio states after broadcast");
            }

            lock (_activeTargetGroupIds)
            {
                _activeBroadcastCts = null;
                _currentAnnouncementId = null;
                _activeTargetGroupIds.Clear();
            }

            _lock.Release();
        }
    }

    private async Task<List<GroupSnapshot>> ResolveTargetSnapshotsAsync(BroadcastPlayRequest request, OrchestratorDbContext db)
    {
        var allGroups = await db.Groups.ToListAsync();
        var allRooms = await db.Rooms.ToListAsync();
        var snapStatus = await _snapcast.GetStatusAsync();
        var snapGroups = snapStatus?["server"]?["groups"]?.AsArray();

        var targetedGroupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targetedRoomMacs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool explicitGroups = request.GroupIds != null && request.GroupIds.Count > 0;
        bool explicitRooms = request.RoomIds != null && request.RoomIds.Count > 0;

        if (!explicitGroups && !explicitRooms)
        {
            // Default: All active groups
            foreach (var g in allGroups) targetedGroupIds.Add(g.Id);
        }
        else
        {
            if (explicitGroups)
            {
                foreach (var gInput in request.GroupIds!)
                {
                    var match = allGroups.FirstOrDefault(g => 
                        string.Equals(g.Id, gInput, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(g.Name, gInput, StringComparison.OrdinalIgnoreCase));
                    if (match != null) targetedGroupIds.Add(match.Id);
                }
            }

            if (explicitRooms)
            {
                foreach (var rInput in request.RoomIds!)
                {
                    var match = allRooms.FirstOrDefault(r =>
                        string.Equals(r.MacAddress, rInput, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(r.FriendlyName, rInput, StringComparison.OrdinalIgnoreCase));
                    if (match != null) targetedRoomMacs.Add(match.MacAddress);
                }
            }
        }

        // Build Snapshots
        var snapshots = new List<GroupSnapshot>();
        foreach (var group in allGroups)
        {
            var snapGroupId = group.SnapcastGroupId;
            if (string.IsNullOrEmpty(snapGroupId) && snapGroups != null)
            {
                var matchingSnapGroup = snapGroups.FirstOrDefault(sg => 
                    sg?["name"]?.GetValue<string>() == group.Name
                    || sg?["id"]?.GetValue<string>() == group.Id);
                snapGroupId = matchingSnapGroup?["id"]?.GetValue<string>();
            }

            if (string.IsNullOrEmpty(snapGroupId)) continue;

            var sgNode = snapGroups?.FirstOrDefault(sg => sg?["id"]?.GetValue<string>() == snapGroupId);
            var clients = sgNode?["clients"]?.AsArray();

            bool isGroupExplicitlyTargeted = targetedGroupIds.Contains(group.Id);
            var matchingClientsInGroup = new List<string>();
            var unselectedClientsInGroup = new List<(string ClientId, int Volume, bool Muted)>();

            if (clients != null)
            {
                foreach (var c in clients)
                {
                    var cid = c?["id"]?.GetValue<string>() ?? string.Empty;
                    var cVol = c?["config"]?["volume"]?["percent"]?.GetValue<int>() ?? 100;
                    var cMuted = c?["config"]?["volume"]?["muted"]?.GetValue<bool>() ?? false;

                    if (isGroupExplicitlyTargeted || targetedRoomMacs.Contains(cid))
                    {
                        matchingClientsInGroup.Add(cid);
                    }
                    else
                    {
                        unselectedClientsInGroup.Add((cid, cVol, cMuted));
                    }
                }
            }

            // Include group if either explicitly targeted or at least one of its rooms was targeted
            if (isGroupExplicitlyTargeted || matchingClientsInGroup.Count > 0)
            {
                var snapshot = new GroupSnapshot
                {
                    GroupId = group.Id,
                    SnapcastGroupId = snapGroupId,
                    OriginalStreamId = group.CurrentStreamId ?? "default",
                    OriginalVolume = group.MasterVolume,
                    OriginalIsMuted = group.IsMuted
                };

                // If only a subset of rooms in this group were targeted, record the others to mute them
                if (matchingClientsInGroup.Count > 0 && unselectedClientsInGroup.Count > 0)
                {
                    snapshot.UnselectedClients.AddRange(unselectedClientsInGroup);
                }

                snapshots.Add(snapshot);
            }
        }

        return snapshots;
    }

    private async Task FadeGroupsVolumeAsync(
        List<GroupSnapshot> snapshots,
        bool targetZero,
        int durationMs,
        VolumeOrchestratorService volumeOrchestrator,
        CancellationToken ct)
    {
        var steps = Math.Clamp(durationMs / 100, 2, 10);
        var stepDelay = durationMs / steps;

        for (int step = 1; step <= steps; step++)
        {
            double progress = (double)step / steps;
            foreach (var s in snapshots)
            {
                int currentVol = targetZero
                    ? (int)Math.Round(s.OriginalVolume * (1.0 - progress))
                    : (int)Math.Round(s.OriginalVolume * progress);

                currentVol = Math.Clamp(currentVol, 0, 100);
                try
                {
                    await volumeOrchestrator.SetGroupVolumeAsync(s.GroupId, currentVol, isMuted: false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error fading group {GroupId} volume to {Vol}", s.GroupId, currentVol);
                }
            }

            if (step < steps)
            {
                await Task.Delay(stepDelay, ct);
            }
        }
    }

    private async Task RestoreSnapshotsAsync(
        List<GroupSnapshot> snapshots,
        int fadeDurationMs,
        bool resumePlayback,
        VolumeOrchestratorService volumeOrchestrator)
    {
        // 1. Switch back to original stream
        foreach (var s in snapshots)
        {
            try
            {
                await _snapcast.SetGroupStreamAsync(s.SnapcastGroupId, s.OriginalStreamId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed restoring stream {StreamId} on group {GroupId}", s.OriginalStreamId, s.SnapcastGroupId);
            }
        }

        // 2. Restore unselected clients mute state
        foreach (var s in snapshots)
        {
            foreach (var c in s.UnselectedClients)
            {
                try
                {
                    await _snapcast.SetClientVolumeAsync(c.ClientId, c.Volume, c.Muted);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed restoring client {ClientId} mute state", c.ClientId);
                }
            }
        }

        // 3. Restore volume (with optional fade-up)
        if (fadeDurationMs > 0)
        {
            await FadeGroupsVolumeAsync(snapshots, targetZero: false, fadeDurationMs, volumeOrchestrator, CancellationToken.None);
        }

        // Finalize exact original volume & mute
        foreach (var s in snapshots)
        {
            try
            {
                await volumeOrchestrator.SetGroupVolumeAsync(s.GroupId, s.OriginalVolume, isMuted: s.OriginalIsMuted);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed setting final original volume on group {GroupId}", s.GroupId);
            }
        }

        // 4. Resume Spotify if paused
        if (resumePlayback)
        {
            foreach (var s in snapshots)
            {
                if (!string.IsNullOrEmpty(s.PausedSpotifySourceId))
                {
                    try
                    {
                        await _librespotApi.SendCommandAsync(s.PausedSpotifySourceId, "resume");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed resuming Spotify source {SourceId}", s.PausedSpotifySourceId);
                    }
                }
            }
        }
    }
}
