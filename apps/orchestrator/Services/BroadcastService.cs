using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
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
        var arguments = $"-hide_banner -loglevel error -i {inputArg} -af \"loudnorm=I=-16:TP=-1.5:LRA=11\" -f s16le -acodec pcm_s16le -ac 2 -ar 44100 -y \"{fifoPath}\"";

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

        return new ProcessRunResult(process.ExitCode, stderr);
    }
}

public class BroadcastService : IHostedService, IBroadcastService
{
    private readonly ILogger<BroadcastService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ISnapcastService _snapcast;
    private readonly IFifoService _fifoService;
    private readonly ILibrespotApiClient _librespotApi;
    private readonly IAudioPlayerProcessRunner _processRunner;

    private sealed record QueuedBroadcastItem(
        string JobId,
        BroadcastPlayRequest Request,
        string InputSource,
        string? TempFilePath,
        TaskCompletionSource<BroadcastPlayResult> Tcs
    );

    private readonly Channel<QueuedBroadcastItem> _queue = Channel.CreateUnbounded<QueuedBroadcastItem>(new UnboundedChannelOptions
    {
        SingleReader = true
    });

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeCtsMap = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BroadcastPlayResult>> _waiters = new();

    private CancellationTokenSource? _workerCts;
    private Task? _workerTask;
    private string? _currentJobId;
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

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting BroadcastService background queue worker.");
        _workerCts = new CancellationTokenSource();
        _workerTask = Task.Run(() => ProcessQueueAsync(_workerCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping BroadcastService and cancelling active broadcasts.");
        await CancelActiveBroadcastInternalAsync();

        if (_workerCts != null)
        {
            try
            {
                _workerCts.Cancel();
            }
            catch (ObjectDisposedException) { }
        }

        if (_workerTask != null)
        {
            try
            {
                await Task.WhenAny(_workerTask, Task.Delay(2000, cancellationToken));
            }
            catch { /* Ignore cancellation */ }
        }
    }

    public BroadcastStatusDto GetStatus()
    {
        lock (_activeTargetGroupIds)
        {
            return new BroadcastStatusDto(
                IsPlaying: _currentJobId != null,
                CurrentAnnouncementId: _currentJobId,
                ActiveTargetGroupIds: _activeTargetGroupIds.ToList()
            );
        }
    }

    public async Task<BroadcastJobDto> EnqueuePlayUrlAsync(BroadcastPlayRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.AudioUrl))
        {
            throw new ArgumentException("AudioUrl darf nicht leer sein.", nameof(request));
        }

        return await EnqueueJobInternalAsync(request, inputSource: request.AudioUrl, tempFilePath: null, sourceType: "url", ct);
    }

    public async Task<BroadcastJobDto> EnqueuePlayFileAsync(Stream fileStream, string fileExtension, BroadcastPlayRequest request, CancellationToken ct = default)
    {
        if (fileStream == null || fileStream.Length == 0)
        {
            throw new ArgumentException("Dateistream ist leer.", nameof(fileStream));
        }

        var tempFileName = $"/tmp/audio_broadcast_{Guid.NewGuid():N}{fileExtension}";
        using (var fs = new FileStream(tempFileName, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await fileStream.CopyToAsync(fs, ct);
        }

        return await EnqueueJobInternalAsync(request, inputSource: tempFileName, tempFilePath: tempFileName, sourceType: "upload", ct);
    }

    public async Task<BroadcastPlayResult> WaitForJobAsync(string jobId, CancellationToken ct = default)
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var job = await db.BroadcastJobs.FindAsync(new object[] { jobId }, ct);
            if (job != null && job.Status != BroadcastJobStatus.Queued && job.Status != BroadcastJobStatus.Playing)
            {
                return new BroadcastPlayResult(
                    Success: job.Status == BroadcastJobStatus.Completed,
                    Message: job.ErrorMessage ?? (job.Status == BroadcastJobStatus.Completed ? "Durchsage erfolgreich abgespielt." : $"Status: {job.Status}"),
                    DurationMs: job.DurationMs,
                    JobId: job.Id
                );
            }
        }

        var tcs = _waiters.GetOrAdd(jobId, _ => new TaskCompletionSource<BroadcastPlayResult>(TaskCreationOptions.RunContinuationsAsynchronously));

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        try
        {
            return await tcs.Task;
        }
        finally
        {
            _waiters.TryRemove(jobId, out _);
        }
    }

    public async Task<BroadcastPlayResult> PlayUrlAsync(BroadcastPlayRequest request, CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.AudioUrl))
        {
            return new BroadcastPlayResult(false, "AudioUrl darf nicht leer sein.");
        }

        var job = await EnqueuePlayUrlAsync(request, ct);
        return await WaitForJobAsync(job.Id, ct);
    }

    public async Task<BroadcastPlayResult> PlayFileAsync(Stream fileStream, string fileExtension, BroadcastPlayRequest request, CancellationToken ct = default)
    {
        if (fileStream == null || fileStream.Length == 0)
        {
            return new BroadcastPlayResult(false, "Dateistream ist leer.");
        }

        var job = await EnqueuePlayFileAsync(fileStream, fileExtension, request, ct);
        return await WaitForJobAsync(job.Id, ct);
    }

    public async Task<BroadcastJobDto?> GetJobAsync(string jobId, CancellationToken ct = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var job = await db.BroadcastJobs.FindAsync(new object[] { jobId }, ct);
        return job != null ? MapToDto(job) : null;
    }

    public async Task<List<BroadcastJobDto>> GetRecentJobsAsync(int limit = 50, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var jobs = await db.BroadcastJobs
            .AsNoTracking()
            .OrderByDescending(j => j.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);

        return jobs.Select(MapToDto).ToList();
    }

    public async Task<bool> CancelJobAsync(string jobId, CancellationToken ct = default)
    {
        if (_activeCtsMap.TryGetValue(jobId, out var cts))
        {
            try
            {
                cts.Cancel();
                return true;
            }
            catch (ObjectDisposedException) { }
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var job = await db.BroadcastJobs.FindAsync(new object[] { jobId }, ct);
        if (job != null && job.Status == BroadcastJobStatus.Queued)
        {
            job.Status = BroadcastJobStatus.Cancelled;
            job.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return true;
        }

        return false;
    }

    private async Task<BroadcastJobDto> EnqueueJobInternalAsync(
        BroadcastPlayRequest request,
        string inputSource,
        string? tempFilePath,
        string sourceType,
        CancellationToken ct)
    {
        if (request.Interrupt)
        {
            await CancelActiveBroadcastInternalAsync();
        }

        var jobId = Guid.NewGuid().ToString("N");
        var jobEntity = new BroadcastJobEntity
        {
            Id = jobId,
            CreatedAt = DateTime.UtcNow,
            AudioUrl = request.AudioUrl,
            SourceType = sourceType,
            TargetGroupIdsJson = request.GroupIds != null && request.GroupIds.Count > 0 ? JsonSerializer.Serialize(request.GroupIds) : null,
            TargetRoomIdsJson = request.RoomIds != null && request.RoomIds.Count > 0 ? JsonSerializer.Serialize(request.RoomIds) : null,
            Volume = request.Volume,
            FadeDurationMs = request.FadeDurationMs,
            PausePlayback = request.PausePlayback,
            TimeoutSeconds = request.TimeoutSeconds ?? 60,
            Interrupt = request.Interrupt,
            Status = BroadcastJobStatus.Queued
        };

        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            db.BroadcastJobs.Add(jobEntity);
            await db.SaveChangesAsync(ct);
        }

        var tcs = new TaskCompletionSource<BroadcastPlayResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters[jobId] = tcs;

        var queuedItem = new QueuedBroadcastItem(jobId, request, inputSource, tempFilePath, tcs);
        await _queue.Writer.WriteAsync(queuedItem, ct);

        // Ensure background worker is running (in case StartAsync was not triggered by host)
        EnsureWorkerRunning();

        return MapToDto(jobEntity);
    }

    private void EnsureWorkerRunning()
    {
        if (_workerTask == null || _workerTask.IsCompleted)
        {
            _workerCts ??= new CancellationTokenSource();
            _workerTask = Task.Run(() => ProcessQueueAsync(_workerCts.Token), CancellationToken.None);
        }
    }

    private async Task CancelActiveBroadcastInternalAsync()
    {
        foreach (var kvp in _activeCtsMap.ToArray())
        {
            try
            {
                kvp.Value.Cancel();
            }
            catch (ObjectDisposedException) { }
        }
        await Task.Yield();
    }

    private async Task ProcessQueueAsync(CancellationToken workerStoppingToken)
    {
        _logger.LogInformation("BroadcastQueue worker loop started.");

        while (!workerStoppingToken.IsCancellationRequested)
        {
            QueuedBroadcastItem item;
            try
            {
                item = await _queue.Reader.ReadAsync(workerStoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var timeoutSec = Math.Clamp(item.Request.TimeoutSeconds ?? 60, 1, 600);
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(workerStoppingToken, timeoutCts.Token);

            _activeCtsMap[item.JobId] = linkedCts;
            lock (_activeTargetGroupIds)
            {
                _currentJobId = item.JobId;
            }

            var result = await ExecuteSingleBroadcastAsync(item, linkedCts.Token);

            _activeCtsMap.TryRemove(item.JobId, out _);
            lock (_activeTargetGroupIds)
            {
                _currentJobId = null;
                _activeTargetGroupIds.Clear();
            }

            // Cleanup temp file if upload
            if (!string.IsNullOrEmpty(item.TempFilePath) && File.Exists(item.TempFilePath))
            {
                try { File.Delete(item.TempFilePath); } catch { /* Ignore */ }
            }

            item.Tcs.TrySetResult(result);
        }

        _logger.LogInformation("BroadcastQueue worker loop stopped.");
    }

    private async Task<BroadcastPlayResult> ExecuteSingleBroadcastAsync(QueuedBroadcastItem item, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var snapshots = new List<GroupSnapshot>();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var volumeOrchestrator = scope.ServiceProvider.GetRequiredService<VolumeOrchestratorService>();
        var hub = scope.ServiceProvider.GetRequiredService<IHubContext<AudioHub, IAudioClient>>();
        var stateBuilder = scope.ServiceProvider.GetRequiredService<FullStateBuilder>();
        var mqtt = scope.ServiceProvider.GetService<IHomeAssistantMqttService>();

        var jobEntity = await db.BroadcastJobs.FindAsync(item.JobId);
        if (jobEntity == null)
        {
            return new BroadcastPlayResult(false, "Job nicht in Datenbank gefunden.", 0, item.JobId);
        }

        if (jobEntity.Status == BroadcastJobStatus.Cancelled)
        {
            return new BroadcastPlayResult(false, "Job wurde vor der Ausführung abgebrochen.", 0, item.JobId);
        }

        jobEntity.Status = BroadcastJobStatus.Playing;
        jobEntity.StartedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);

        try
        {
            await _fifoService.EnsureFifoAsync(OrchestratorConstants.AnnouncementFifoPath);

            // 1. Resolve Targets
            snapshots = await ResolveTargetSnapshotsAsync(item.Request, db);
            if (snapshots.Count == 0)
            {
                throw new InvalidOperationException("Keine Zielgruppen für die Durchsage gefunden.");
            }

            lock (_activeTargetGroupIds)
            {
                _activeTargetGroupIds.AddRange(snapshots.Select(s => s.GroupId));
            }

            // 2. Optional Pause active Spotify playback
            if (item.Request.PausePlayback)
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
            var fadeMs = Math.Clamp(item.Request.FadeDurationMs ?? 0, 0, 5000);
            if (fadeMs > 0)
            {
                await FadeGroupsVolumeAsync(snapshots, targetZero: true, fadeMs, volumeOrchestrator, ct);
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
                var targetVol = item.Request.Volume.HasValue ? Math.Clamp(item.Request.Volume.Value, 0, 100) : s.OriginalVolume;
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
                item.InputSource,
                OrchestratorConstants.AnnouncementFifoPath,
                stdinStream: null,
                ct
            );

            // 8. Snapcast audio buffer delay (~500ms) to ensure audio finishes playback on physical clients
            await Task.Delay(500, ct);

            var duration = (int)stopwatch.ElapsedMilliseconds;
            if (runResult.ExitCode != 0)
            {
                var firstErrLine = runResult.Stderr.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                var detail = !string.IsNullOrWhiteSpace(firstErrLine) ? $": {firstErrLine}" : "";
                var msg = $"ffmpeg beendet mit Code {runResult.ExitCode}{detail}";

                jobEntity.Status = BroadcastJobStatus.Failed;
                jobEntity.ErrorMessage = msg;
                jobEntity.CompletedAt = DateTime.UtcNow;
                jobEntity.DurationMs = duration;
                await db.SaveChangesAsync(CancellationToken.None);

                return new BroadcastPlayResult(false, msg, duration, item.JobId);
            }

            jobEntity.Status = BroadcastJobStatus.Completed;
            jobEntity.CompletedAt = DateTime.UtcNow;
            jobEntity.DurationMs = duration;
            await db.SaveChangesAsync(CancellationToken.None);

            return new BroadcastPlayResult(true, "Durchsage erfolgreich abgespielt.", duration, item.JobId);
        }
        catch (OperationCanceledException)
        {
            var duration = (int)stopwatch.ElapsedMilliseconds;
            jobEntity.Status = BroadcastJobStatus.Cancelled;
            jobEntity.CompletedAt = DateTime.UtcNow;
            jobEntity.DurationMs = duration;
            jobEntity.ErrorMessage = "Durchsage abgebrochen oder Zeitüberschreitung.";
            await db.SaveChangesAsync(CancellationToken.None);

            return new BroadcastPlayResult(false, "Durchsage abgebrochen oder Zeitüberschreitung.", duration, item.JobId);
        }
        catch (Exception ex)
        {
            var duration = (int)stopwatch.ElapsedMilliseconds;
            jobEntity.Status = BroadcastJobStatus.Failed;
            jobEntity.CompletedAt = DateTime.UtcNow;
            jobEntity.DurationMs = duration;
            jobEntity.ErrorMessage = $"Fehler bei Durchsage: {ex.Message}";
            await db.SaveChangesAsync(CancellationToken.None);

            return new BroadcastPlayResult(false, jobEntity.ErrorMessage, duration, item.JobId);
        }
        finally
        {
            try
            {
                await RestoreSnapshotsAsync(snapshots, item.Request.FadeDurationMs ?? 0, item.Request.PausePlayback, volumeOrchestrator);
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
        }
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

        if (fadeDurationMs > 0)
        {
            await FadeGroupsVolumeAsync(snapshots, targetZero: false, fadeDurationMs, volumeOrchestrator, CancellationToken.None);
        }

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

    private static BroadcastJobDto MapToDto(BroadcastJobEntity j)
    {
        List<string>? groupIds = null;
        if (!string.IsNullOrEmpty(j.TargetGroupIdsJson))
        {
            try { groupIds = JsonSerializer.Deserialize<List<string>>(j.TargetGroupIdsJson); } catch { }
        }

        List<string>? roomIds = null;
        if (!string.IsNullOrEmpty(j.TargetRoomIdsJson))
        {
            try { roomIds = JsonSerializer.Deserialize<List<string>>(j.TargetRoomIdsJson); } catch { }
        }

        return new BroadcastJobDto(
            Id: j.Id,
            CreatedAt: j.CreatedAt,
            StartedAt: j.StartedAt,
            CompletedAt: j.CompletedAt,
            AudioUrl: j.AudioUrl,
            SourceType: j.SourceType,
            TargetGroupIds: groupIds,
            TargetRoomIds: roomIds,
            Volume: j.Volume,
            FadeDurationMs: j.FadeDurationMs,
            PausePlayback: j.PausePlayback,
            Status: j.Status,
            DurationMs: j.DurationMs,
            ErrorMessage: j.ErrorMessage
        );
    }
}
