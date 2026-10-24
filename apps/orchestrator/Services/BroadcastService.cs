using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardInput = stdinStream != null,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(stdinStream != null ? "pipe:0" : inputSource);
        startInfo.ArgumentList.Add("-af");
        startInfo.ArgumentList.Add("loudnorm=I=-16:TP=-1.5:LRA=11");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("s16le");
        startInfo.ArgumentList.Add("-acodec");
        startInfo.ArgumentList.Add("pcm_s16le");
        startInfo.ArgumentList.Add("-ac");
        startInfo.ArgumentList.Add("2");
        startInfo.ArgumentList.Add("-ar");
        startInfo.ArgumentList.Add("44100");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add(fifoPath);

        using var process = new Process { StartInfo = startInfo };
        _logger.LogInformation("Starting ffmpeg for broadcast: input={InputSource}, fifo={FifoPath}", inputSource, fifoPath);

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

    private const int MaxFadeDurationMs = 5000;

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeCtsMap = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BroadcastPlayResult>> _waiters = new();
    private readonly SemaphoreSlim _jobStateLock = new(1, 1);
    private readonly SemaphoreSlim _startupGate = new(1, 1);
    private readonly object _workerLock = new();
    private volatile bool _initialized;
    private volatile bool _stopping;

    private CancellationTokenSource? _workerCts;
    private Task? _workerTask;
    private string? _currentJobId;
    private readonly List<string> _activeTargetGroupIds = new();
    private readonly List<string> _activeTargetSnapcastGroupIds = new();
    private readonly HashSet<string> _deferredVolumeGroupIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _deferredNativeSnapGroupIds = new(StringComparer.OrdinalIgnoreCase);
    private List<GroupSnapshot>? _currentSnapshots;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _groupGates = new();

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

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting BroadcastService background queue worker.");
        await EnsureInitializedAsync(cancellationToken);
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized)
        {
            EnsureWorkerRunning();
            return;
        }

        // Admission (EnqueueJobInternalAsync) and startup share this gate so orphan cleanup runs
        // exactly once before any new job can be persisted or processed.
        await _startupGate.WaitAsync(ct);
        try
        {
            if (!_initialized)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                    var orphanedJobs = await db.BroadcastJobs
                        .Where(j => j.Status == BroadcastJobStatus.Queued || j.Status == BroadcastJobStatus.Playing)
                        .ToListAsync(ct);

                    foreach (var job in orphanedJobs)
                    {
                        job.Status = BroadcastJobStatus.Cancelled;
                        job.CompletedAt = DateTime.UtcNow;
                        job.ErrorMessage = "Abgebrochen durch Neustart des Orchestrators.";
                    }

                    if (orphanedJobs.Count > 0)
                    {
                        await db.SaveChangesAsync(ct);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up orphaned broadcast jobs on startup.");
                }

                _initialized = true;
            }
        }
        finally
        {
            _startupGate.Release();
        }

        EnsureWorkerRunning();
    }

    async Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Shutting down BroadcastService worker.");
        _stopping = true;
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
                await _workerTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) { /* Host shutdown deadline reached */ }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Broadcast worker terminated with an error during shutdown.");
            }
        }

        // Close admission and resolve everything still pending: persist cancellation,
        // complete waiters and delete uploaded files that no worker will ever process.
        _queue.Writer.TryComplete();
        while (_queue.Reader.TryRead(out var pending))
        {
            await DiscardQueuedItemAsync(pending, "Abgebrochen durch Herunterfahren des Orchestrators.");
        }
    }

    private async Task DiscardQueuedItemAsync(QueuedBroadcastItem item, string message)
    {
        await TryMarkJobFailedAsync(item.JobId, message, BroadcastJobStatus.Cancelled);

        if (!string.IsNullOrEmpty(item.TempFilePath) && File.Exists(item.TempFilePath))
        {
            try { File.Delete(item.TempFilePath); } catch { /* Ignore */ }
        }

        item.Tcs.TrySetResult(new BroadcastPlayResult(false, message, 0, item.JobId));
        _waiters.TryRemove(item.JobId, out _);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Stopping active broadcasts on user request.");
        await CancelActiveBroadcastInternalAsync();
        await EnsureInitializedAsync(ct);
    }

    public BroadcastStatusDto GetStatus()
    {
        lock (_activeTargetGroupIds)
        {
            return new BroadcastStatusDto(
                IsPlaying: _currentJobId != null,
                CurrentAnnouncementId: _currentJobId,
                ActiveTargetGroupIds: _activeTargetGroupIds.ToList(),
                ActiveSnapcastGroupIds: _activeTargetSnapcastGroupIds.ToList()
            );
        }
    }

    public bool IsGroupActiveInBroadcast(string? orchestratorGroupId, string? snapcastGroupId = null)
    {
        lock (_activeTargetGroupIds)
        {
            if (orchestratorGroupId != null && _activeTargetGroupIds.Contains(orchestratorGroupId)) return true;
            if (snapcastGroupId != null && _activeTargetSnapcastGroupIds.Contains(snapcastGroupId)) return true;
            return false;
        }
    }

    public void MarkDeferredVolumeUpdate(string? orchestratorGroupId, string? snapcastGroupId = null)
    {
        lock (_activeTargetGroupIds)
        {
            if (!string.IsNullOrEmpty(orchestratorGroupId))
            {
                _deferredVolumeGroupIds.Add(orchestratorGroupId);
            }
            if (!string.IsNullOrEmpty(snapcastGroupId))
            {
                _deferredNativeSnapGroupIds.Add(snapcastGroupId);
            }
        }
    }

    public SemaphoreSlim GetGroupGate(string groupId)
    {
        return _groupGates.GetOrAdd(groupId, _ => new SemaphoreSlim(1, 1));
    }

    public async Task<BroadcastJobDto> EnqueuePlayUrlAsync(BroadcastPlayRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.AudioUrl))
        {
            throw new ArgumentException("AudioUrl darf nicht leer sein.", nameof(request));
        }

        if (!Uri.TryCreate(request.AudioUrl, UriKind.Absolute, out var parsedUri)
            || (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("AudioUrl muss eine absolute HTTP- oder HTTPS-URL sein.", nameof(request));
        }

        return await EnqueueJobInternalAsync(request, inputSource: request.AudioUrl, tempFilePath: null, sourceType: BroadcastSourceType.Url, ct);
    }

    public async Task<BroadcastJobDto> EnqueuePlayFileAsync(Stream fileStream, string fileExtension, BroadcastPlayRequest request, CancellationToken ct = default)
    {
        if (fileStream == null || fileStream.Length == 0)
        {
            throw new ArgumentException("Dateistream ist leer.", nameof(fileStream));
        }

        var tempFileName = $"/tmp/audio_broadcast_{Guid.NewGuid():N}{fileExtension}";
        try
        {
            using (var fs = new FileStream(tempFileName, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await fileStream.CopyToAsync(fs, ct);
            }

            // On success, ownership of the temp file passes to the queue worker.
            return await EnqueueJobInternalAsync(request, inputSource: tempFileName, tempFilePath: tempFileName, sourceType: BroadcastSourceType.Upload, ct);
        }
        catch
        {
            try { File.Delete(tempFileName); } catch { /* Ignore */ }
            throw;
        }
    }

    public async Task<BroadcastPlayResult> WaitForJobAsync(string jobId, CancellationToken ct = default)
    {
        // The worker owns the lifetime of the waiter entry. Cancellation is local to this caller.
        if (_waiters.TryGetValue(jobId, out var tcs))
        {
            return await tcs.Task.WaitAsync(ct);
        }

        // No active waiter: the job already finished (or never existed). The worker persists the
        // terminal state before completing/removing the waiter, so the database is authoritative.
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var job = await db.BroadcastJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job == null)
        {
            return new BroadcastPlayResult(false, "Job nicht gefunden.", 0, jobId);
        }

        return new BroadcastPlayResult(
            Success: job.Status == BroadcastJobStatus.Completed,
            Message: job.ErrorMessage ?? (job.Status == BroadcastJobStatus.Completed ? "Durchsage erfolgreich abgespielt." : $"Status: {job.Status}"),
            DurationMs: job.DurationMs,
            JobId: job.Id
        );
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
        await _jobStateLock.WaitAsync(ct);
        try
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

                // Release callers blocked in WaitForJobAsync right away; the worker still owns
                // eventual queue item and temp file cleanup.
                if (_waiters.TryGetValue(jobId, out var waiter))
                {
                    waiter.TrySetResult(new BroadcastPlayResult(false, "Job wurde vor der Ausführung abgebrochen.", 0, jobId));
                }

                return true;
            }

            return false;
        }
        finally
        {
            _jobStateLock.Release();
        }
    }

    private async Task<BroadcastJobDto> EnqueueJobInternalAsync(
        BroadcastPlayRequest request,
        string inputSource,
        string? tempFilePath,
        BroadcastSourceType sourceType,
        CancellationToken ct)
    {
        if (_stopping)
        {
            throw new InvalidOperationException("Der Orchestrator wird gerade beendet.");
        }

        // Defer admission until startup (orphan cleanup) has completed
        await EnsureInitializedAsync(ct);

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

        // Once persisted, hand the job to the worker independent of caller cancellation.
        var queuedItem = new QueuedBroadcastItem(jobId, request, inputSource, tempFilePath, tcs);
        if (!_queue.Writer.TryWrite(queuedItem))
        {
            _waiters.TryRemove(jobId, out _);
            await TryMarkJobFailedAsync(jobId, "Warteschlange nimmt keine Jobs mehr an.");
            throw new InvalidOperationException("Der Orchestrator wird gerade beendet.");
        }

        // Ensure background worker is running (in case StartAsync was not triggered by host)
        EnsureWorkerRunning();

        return MapToDto(jobEntity);
    }

    private void EnsureWorkerRunning()
    {
        lock (_workerLock)
        {
            if (_stopping) return;
            if (_workerTask != null && !_workerTask.IsCompleted) return;

            _workerCts ??= new CancellationTokenSource();
            var token = _workerCts.Token;
            _workerTask = Task.Run(() => ProcessQueueAsync(token), CancellationToken.None);
        }
    }

    private async Task CancelActiveBroadcastInternalAsync()
    {
        await _jobStateLock.WaitAsync(CancellationToken.None);
        try
        {
            foreach (var kvp in _activeCtsMap.ToArray())
            {
                try
                {
                    kvp.Value.Cancel();
                }
                catch (ObjectDisposedException) { }
            }
        }
        finally
        {
            _jobStateLock.Release();
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
            catch (ChannelClosedException)
            {
                break;
            }

            var timeoutSec = Math.Clamp(item.Request.TimeoutSeconds ?? 60, 1, 600);
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(workerStoppingToken, timeoutCts.Token);

            BroadcastPlayResult result;
            try
            {
                result = await ExecuteSingleBroadcastAsync(item, linkedCts);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error while processing broadcast job {JobId}", item.JobId);
                result = new BroadcastPlayResult(false, $"Fehler bei Durchsage: {ex.Message}", 0, item.JobId);
                await TryMarkJobFailedAsync(item.JobId, $"Fehler bei Durchsage: {ex.Message}");
            }
            finally
            {
                _activeCtsMap.TryRemove(item.JobId, out _);

                // Acquire gates for active target groups before clearing ownership
                // to serialize with any pending ordinary volume push and to apply deferred stream edits.
                var gates = new List<SemaphoreSlim>();
                var spotifySyncs = new List<(string GroupId, string StreamId, int MasterVolume)>();
                try
                {
                    List<string> targetGroupIds;
                    List<string> targetSnapcastGroupIds;
                    List<string> deferredGroupIds;
                    List<string> deferredNativeIds;
                    List<GroupSnapshot>? snapshots;
                    lock (_activeTargetGroupIds)
                    {
                        targetGroupIds = _activeTargetGroupIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        targetSnapcastGroupIds = _activeTargetSnapcastGroupIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        deferredGroupIds = _deferredVolumeGroupIds.ToList();
                        deferredNativeIds = _deferredNativeSnapGroupIds.ToList();
                        snapshots = _currentSnapshots;
                    }

                    var allGateIds = new HashSet<string>(targetGroupIds, StringComparer.OrdinalIgnoreCase);
                    foreach (var sid in targetSnapcastGroupIds)
                    {
                        allGateIds.Add(sid);
                    }

                    var sortedGateIds = allGateIds.OrderBy(x => x, StringComparer.Ordinal).ToList();
                    foreach (var gid in sortedGateIds)
                    {
                        var gate = GetGroupGate(gid);
                        await gate.WaitAsync();
                        gates.Add(gate);
                    }

                    // Replay any deferred stream selections and volume/mute edits saved to SQLite during restoration
                    try
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                        var volumeOrchestrator = scope.ServiceProvider.GetRequiredService<VolumeOrchestratorService>();

                        var dbGroups = targetGroupIds.Count > 0 
                            ? await db.Groups.Where(g => targetGroupIds.Contains(g.Id)).ToListAsync()
                            : new List<GroupEntity>();
                        var dbRooms = await db.Rooms.ToListAsync();
                        var settings = await db.Settings.FirstOrDefaultAsync();
                        var currentProfile = settings?.VolumeProfile ?? VolumeProfile.Cubic;

                        foreach (var g in dbGroups)
                        {
                            if (!string.IsNullOrEmpty(g.SnapcastGroupId) && !string.IsNullOrEmpty(g.CurrentStreamId))
                            {
                                try
                                {
                                    await _snapcast.SetGroupStreamAsync(g.SnapcastGroupId, g.CurrentStreamId);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Failed applying deferred stream {StreamId} to group {GroupId}", g.CurrentStreamId, g.Id);
                                }
                            }

                            var snap = snapshots?.FirstOrDefault(s => s.GroupId == g.Id);
                            bool groupEdited = snap != null && (g.MasterVolume != snap.InitialDbVolume || g.IsMuted != snap.InitialDbIsMuted);
                            bool roomEdited = snap != null && snap.SelectedClients.Concat(snap.UnselectedClients).Any(c =>
                            {
                                var r = dbRooms.FirstOrDefault(room => room.MacAddress == c.ClientId);
                                if (r == null) return false;
                                return (c.InitialDbMixVolume.HasValue && r.MixVolume != c.InitialDbMixVolume.Value)
                                    || (c.InitialDbIsMuted.HasValue && r.IsMuted != c.InitialDbIsMuted.Value);
                            });
                            bool profileChanged = snap != null && currentProfile != snap.InitialVolumeProfile;
                            bool isDeferred = deferredGroupIds.Contains(g.Id);

                            if (groupEdited || roomEdited || profileChanged || isDeferred)
                            {
                                try
                                {
                                    await volumeOrchestrator.ReplayGroupVolumeUnderGateAsync(g);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Failed replaying deferred volume/mute for group {GroupId}", g.Id);
                                }
                            }

                            if (groupEdited && !string.IsNullOrEmpty(g.CurrentStreamId) && g.CurrentStreamId.StartsWith(OrchestratorConstants.SpotifyPrefix))
                            {
                                spotifySyncs.Add((g.Id, g.CurrentStreamId, g.MasterVolume));
                            }
                        }

                        // Replay native Snapcast groups (unassigned rooms) that had deferred edits
                        var nativeSnapshots = snapshots?.Where(s => s.IsNative).ToList() ?? new List<GroupSnapshot>();
                        foreach (var s in nativeSnapshots)
                        {
                            bool nativeRoomEdited = s.NativeClients.Any(c =>
                            {
                                var r = dbRooms.FirstOrDefault(room => room.MacAddress == c.ClientId);
                                if (r == null) return false;
                                return (c.InitialDbMixVolume.HasValue && r.MixVolume != c.InitialDbMixVolume.Value)
                                    || (c.InitialDbIsMuted.HasValue && r.IsMuted != c.InitialDbIsMuted.Value);
                            });
                            bool nativeProfileChanged = currentProfile != s.InitialVolumeProfile;
                            bool nativeIsDeferred = deferredNativeIds.Contains(s.SnapcastGroupId);

                            if (nativeRoomEdited || nativeProfileChanged || nativeIsDeferred)
                            {
                                try
                                {
                                    await volumeOrchestrator.ReplayNativeSnapcastGroupVolumeUnderGateAsync(s.SnapcastGroupId);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Failed replaying deferred volume/mute for native Snapcast group {Sid}", s.SnapcastGroupId);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed querying deferred stream or volume changes on release");
                    }

                    lock (_activeTargetGroupIds)
                    {
                        _currentJobId = null;
                        _activeTargetGroupIds.Clear();
                        _activeTargetSnapcastGroupIds.Clear();
                        _deferredVolumeGroupIds.Clear();
                        _deferredNativeSnapGroupIds.Clear();
                        _currentSnapshots = null;
                    }
                }
                finally
                {
                    foreach (var gate in gates)
                    {
                        gate.Release();
                    }
                }

                if (spotifySyncs.Count > 0)
                {
                    try
                    {
                        using var syncScope = _serviceProvider.CreateScope();
                        var volOrchestrator = syncScope.ServiceProvider.GetRequiredService<VolumeOrchestratorService>();
                        foreach (var sync in spotifySyncs)
                        {
                            try
                            {
                                await volOrchestrator.SyncSpotifySourceAndSiblingsAsync(sync.GroupId, sync.StreamId, sync.MasterVolume);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed syncing deferred Spotify source volume for group {GroupId}", sync.GroupId);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed creating scope for deferred Spotify volume sync");
                    }
                }

                // Cleanup temp file if upload
                if (!string.IsNullOrEmpty(item.TempFilePath) && File.Exists(item.TempFilePath))
                {
                    try { File.Delete(item.TempFilePath); } catch { /* Ignore */ }
                }
            }

            item.Tcs.TrySetResult(result);
            _waiters.TryRemove(item.JobId, out _);
        }

        _logger.LogInformation("BroadcastQueue worker loop stopped.");
    }

    private async Task TryMarkJobFailedAsync(string jobId, string message, BroadcastJobStatus status = BroadcastJobStatus.Failed)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var job = await db.BroadcastJobs.FindAsync(jobId);
            if (job == null) return;
            if (job.Status != BroadcastJobStatus.Queued && job.Status != BroadcastJobStatus.Playing) return;

            job.Status = status;
            job.CompletedAt = DateTime.UtcNow;
            job.ErrorMessage = message;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to mark broadcast job {JobId} as {Status}", jobId, status);
        }
    }

    private async Task<BroadcastPlayResult> ExecuteSingleBroadcastAsync(QueuedBroadcastItem item, CancellationTokenSource linkedCts)
    {
        var ct = linkedCts.Token;
        var stopwatch = Stopwatch.StartNew();
        var snapshots = new List<GroupSnapshot>();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var volumeOrchestrator = scope.ServiceProvider.GetRequiredService<VolumeOrchestratorService>();
        var hub = scope.ServiceProvider.GetRequiredService<IHubContext<AudioHub, IAudioClient>>();
        var stateBuilder = scope.ServiceProvider.GetRequiredService<FullStateBuilder>();
        var mqtt = scope.ServiceProvider.GetService<IHomeAssistantMqttService>();

        // Synchronize the Queued -> Playing transition and active CTS registration with CancelJobAsync
        // so a cancel call either observes the Queued row before playback starts or cancels the live CTS.
        BroadcastJobEntity? jobEntity;
        await _jobStateLock.WaitAsync(CancellationToken.None);
        try
        {
            jobEntity = await db.BroadcastJobs.FindAsync(item.JobId);
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

            _activeCtsMap[item.JobId] = linkedCts;
            lock (_activeTargetGroupIds)
            {
                _currentJobId = item.JobId;
            }
        }
        finally
        {
            _jobStateLock.Release();
        }

        var finalStatus = BroadcastJobStatus.Failed;
        string? errorMessage = null;

        try
        {
            await _fifoService.EnsureFifoAsync(OrchestratorConstants.AnnouncementFifoPath);

            // Ensure Snapcast announcement stream is registered before switching targets
            try
            {
                var snapStatus = await _snapcast.GetStatusAsync();
                var snapStreams = snapStatus?["server"]?["streams"]?.AsArray();
                var annStreamId = OrchestratorConstants.AnnouncementStreamId;
                if (snapStreams == null || !snapStreams.Any(s => s?["id"]?.GetValue<string>() == annStreamId))
                {
                    var fifoPath = OrchestratorConstants.AnnouncementFifoPath;
                    var streamUri = $"pipe://{fifoPath}?name={annStreamId}&sampleformat=44100:16:2&idle_threshold_ms=1000";
                    try { await _snapcast.AddStreamAsync(streamUri); } catch { /* Already exists? */ }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to verify or register announcement stream in Snapcast");
            }

            // 1. Resolve Targets under gate coordination
            var candidateGateIds = await ResolveCandidateGateIdsAsync(item.Request, db);
            var targetGates = new List<SemaphoreSlim>();
            var acquiredGateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var gid in candidateGateIds)
                {
                    var g = GetGroupGate(gid);
                    await g.WaitAsync(ct);
                    targetGates.Add(g);
                    acquiredGateIds.Add(gid);
                }

                snapshots = await ResolveTargetSnapshotsAsync(item.Request, db);
                if (snapshots.Count == 0)
                {
                    throw new InvalidOperationException("Keine Zielgruppen für die Durchsage gefunden.");
                }

                foreach (var s in snapshots)
                {
                    var gid = !s.IsNative ? s.GroupId : s.SnapcastGroupId;
                    if (!acquiredGateIds.Contains(gid))
                    {
                        var g = GetGroupGate(gid);
                        await g.WaitAsync(ct);
                        targetGates.Add(g);
                        acquiredGateIds.Add(gid);
                    }
                }

                lock (_activeTargetGroupIds)
                {
                    _activeTargetGroupIds.AddRange(snapshots.Where(s => !s.IsNative).Select(s => s.GroupId));
                    _activeTargetSnapcastGroupIds.AddRange(snapshots.Select(s => s.SnapcastGroupId));
                    _currentSnapshots = snapshots;
                }
            }
            finally
            {
                foreach (var g in targetGates)
                {
                    g.Release();
                }
            }

            // 2. Fade down current volume (if requested)
            ct.ThrowIfCancellationRequested();
            var fadeMs = Math.Clamp(item.Request.FadeDurationMs ?? 0, 0, MaxFadeDurationMs);
            if (fadeMs > 0)
            {
                await FadeGroupsVolumeAsync(snapshots, targetZero: true, fadeMs, ct);
            }

            // 3. Optional pause of active Spotify playback (after the fade-down, so the fade is audible)
            ct.ThrowIfCancellationRequested();
            if (item.Request.PausePlayback)
            {
                foreach (var s in snapshots.Where(s => !s.IsNative && s.OriginalStreamId.StartsWith(OrchestratorConstants.SpotifyPrefix)))
                {
                    ct.ThrowIfCancellationRequested();
                    var sourceId = s.OriginalStreamId.Replace(OrchestratorConstants.SpotifyPrefix, "");
                    try
                    {
                        var nodeStatus = await _librespotApi.GetStatusAsync(sourceId);
                        if (nodeStatus?.Is_playing != true) continue;

                        await _librespotApi.SendCommandAsync(sourceId, "pause");
                        s.PausedSpotifySourceId = sourceId;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to pause Spotify source {SourceId}", sourceId);
                    }
                }
            }

            // 4. Switch target groups to announcement stream.
            // Failures abort the job: otherwise the group would never hear the announcement
            // while the volume override still raises its original music.
            ct.ThrowIfCancellationRequested();
            foreach (var s in snapshots)
            {
                ct.ThrowIfCancellationRequested();
                await _snapcast.SetGroupStreamAsync(s.SnapcastGroupId, OrchestratorConstants.AnnouncementStreamId);
            }

            // 5. Set announcement playback volume (group-isolated, never touches sibling groups / source volume)
            ct.ThrowIfCancellationRequested();
            var settings = await db.Settings.FirstOrDefaultAsync();
            var profile = settings?.VolumeProfile ?? VolumeProfile.Cubic;
            var roomsFromDb = await db.Rooms.ToListAsync();

            foreach (var s in snapshots)
            {
                ct.ThrowIfCancellationRequested();
                if (s.IsNative)
                {
                    foreach (var c in s.NativeClients)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (item.Request.Volume.HasValue)
                        {
                            var nativeOverrideVol = Math.Clamp(item.Request.Volume.Value, 0, 100);
                            var roomEntity = roomsFromDb.FirstOrDefault(r => r.MacAddress == c.ClientId);
                            var mixVolume = roomEntity?.MixVolume ?? 100;
                            var announcementVol = VolumeOrchestratorService.CalculateSnapcastVolume(nativeOverrideVol, mixVolume, profile);
                            await _snapcast.SetClientVolumeAsync(c.ClientId, announcementVol, muted: false);
                        }
                        else
                        {
                            await _snapcast.SetClientVolumeAsync(c.ClientId, c.Volume, muted: false);
                        }
                    }
                    continue;
                }

                var targetVol = item.Request.Volume.HasValue ? Math.Clamp(item.Request.Volume.Value, 0, 100) : s.OriginalVolume;

                // Explicitly set announcement volume and unmute targeted clients directly on Snapcast.
                // Transient broadcast volumes are not persisted to SQLite to ensure crash resilience.
                // Failures abort the job so un-configured or silent targets trigger restoration.
                foreach (var c in s.SelectedClients)
                {
                    ct.ThrowIfCancellationRequested();
                    var roomEntity = roomsFromDb.FirstOrDefault(r => r.MacAddress == c.ClientId);
                    var mixVolume = roomEntity?.MixVolume ?? 100;
                    var announcementVol = VolumeOrchestratorService.CalculateSnapcastVolume(targetVol, mixVolume, profile);
                    await _snapcast.SetClientVolumeAsync(c.ClientId, announcementVol, muted: false);
                }
            }

            // 6. Temporarily mute unselected rooms within target groups. Must happen after all group
            // volume pushes (they re-apply client mute state from the database). Failures abort the
            // job so excluded rooms never hear the announcement.
            ct.ThrowIfCancellationRequested();
            foreach (var s in snapshots)
            {
                foreach (var c in s.UnselectedClients)
                {
                    ct.ThrowIfCancellationRequested();
                    await _snapcast.SetClientVolumeAsync(c.ClientId, c.Volume, muted: true);
                }
            }

            // 7. Stream audio into FIFO using ffmpeg process runner
            ct.ThrowIfCancellationRequested();
            var runResult = await _processRunner.StreamToFifoAsync(
                item.InputSource,
                OrchestratorConstants.AnnouncementFifoPath,
                stdinStream: null,
                ct
            );

            // 8. Wait for FIFO to drain and client playback buffer (~500ms) to finish
            await WaitForFifoAndBufferDrainAsync(OrchestratorConstants.AnnouncementFifoPath, clientBufferMs: 500, ct);

            await _jobStateLock.WaitAsync(CancellationToken.None);
            try
            {
                _activeCtsMap.TryRemove(item.JobId, out _);
                ct.ThrowIfCancellationRequested();

                if (runResult.ExitCode != 0)
                {
                    var firstErrLine = runResult.Stderr.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    var detail = !string.IsNullOrWhiteSpace(firstErrLine) ? $": {firstErrLine}" : "";
                    errorMessage = $"ffmpeg beendet mit Code {runResult.ExitCode}{detail}";
                }
                else
                {
                    finalStatus = BroadcastJobStatus.Completed;
                }
            }
            finally
            {
                _jobStateLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            finalStatus = BroadcastJobStatus.Cancelled;
            errorMessage = "Durchsage abgebrochen oder Zeitüberschreitung.";
        }
        catch (Exception ex)
        {
            finalStatus = BroadcastJobStatus.Failed;
            errorMessage = $"Fehler bei Durchsage: {ex.Message}";
        }
        finally
        {
            try
            {
                var restorationErrors = await RestoreSnapshotsAsync(snapshots, item.Request.FadeDurationMs ?? 0, item.Request.PausePlayback, volumeOrchestrator);
                if (restorationErrors.Count > 0 && finalStatus == BroadcastJobStatus.Completed)
                {
                    finalStatus = BroadcastJobStatus.Failed;
                    errorMessage = $"Fehler bei der Wiederherstellung: {string.Join("; ", restorationErrors)}";
                }
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
                if (finalStatus == BroadcastJobStatus.Completed)
                {
                    finalStatus = BroadcastJobStatus.Failed;
                    errorMessage = $"Fehler bei der Wiederherstellung: {ex.Message}";
                }
            }
        }

        // The terminal state is persisted only after restoration finished, so polling clients
        // never observe completion while streams/volumes are still being restored.
        var duration = (int)stopwatch.ElapsedMilliseconds;
        jobEntity.Status = finalStatus;
        jobEntity.CompletedAt = DateTime.UtcNow;
        jobEntity.DurationMs = duration;
        jobEntity.ErrorMessage = errorMessage;
        await db.SaveChangesAsync(CancellationToken.None);

        return finalStatus == BroadcastJobStatus.Completed
            ? new BroadcastPlayResult(true, "Durchsage erfolgreich abgespielt.", duration, item.JobId)
            : new BroadcastPlayResult(false, errorMessage ?? "Durchsage fehlgeschlagen.", duration, item.JobId);
    }

    private sealed class ClientSnapshot
    {
        public string ClientId { get; init; } = string.Empty;
        public int Volume { get; init; }
        public bool Muted { get; init; }
        public int? InitialDbMixVolume { get; init; }
        public bool? InitialDbIsMuted { get; init; }
    }

    private sealed class GroupSnapshot
    {
        public string GroupId { get; init; } = string.Empty;
        public string SnapcastGroupId { get; init; } = string.Empty;
        public string OriginalStreamId { get; init; } = "default";
        public int OriginalVolume { get; init; } = 100;
        public bool OriginalIsMuted { get; init; }
        public int InitialDbVolume { get; init; } = 100;
        public bool InitialDbIsMuted { get; init; }
        public VolumeProfile InitialVolumeProfile { get; init; } = VolumeProfile.Cubic;
        public string? PausedSpotifySourceId { get; set; }

        /// <summary>True for native Snapcast groups without an orchestrator group (e.g. unassigned rooms).</summary>
        public bool IsNative { get; init; }
        public List<ClientSnapshot> NativeClients { get; } = new();
        public List<ClientSnapshot> SelectedClients { get; } = new();
        public List<ClientSnapshot> UnselectedClients { get; } = new();
    }

    private async Task<List<string>> ResolveCandidateGateIdsAsync(BroadcastPlayRequest request, OrchestratorDbContext db)
    {
        var gateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool explicitGroups = request.GroupIds != null && request.GroupIds.Count > 0;
        bool explicitRooms = request.RoomIds != null && request.RoomIds.Count > 0;

        var allGroups = await db.Groups.AsNoTracking().ToListAsync();

        if (!explicitGroups && !explicitRooms)
        {
            foreach (var g in allGroups)
            {
                gateIds.Add(g.Id);
                if (!string.IsNullOrEmpty(g.SnapcastGroupId)) gateIds.Add(g.SnapcastGroupId);
            }
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
                    if (match != null)
                    {
                        gateIds.Add(match.Id);
                        if (!string.IsNullOrEmpty(match.SnapcastGroupId)) gateIds.Add(match.SnapcastGroupId);
                    }
                }
            }

            if (explicitRooms)
            {
                var rooms = await db.Rooms.AsNoTracking().ToListAsync();
                var targetedMacs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var rInput in request.RoomIds!)
                {
                    var match = rooms.FirstOrDefault(r =>
                        string.Equals(r.MacAddress, rInput, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(r.FriendlyName, rInput, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        targetedMacs.Add(match.MacAddress);
                    }
                    else
                    {
                        targetedMacs.Add(rInput);
                    }
                }

                if (targetedMacs.Count > 0)
                {
                    try
                    {
                        var snapStatus = await _snapcast.GetStatusAsync();
                        var snapGroups = snapStatus?["server"]?["groups"]?.AsArray();
                        if (snapGroups != null)
                        {
                            foreach (var sg in snapGroups)
                            {
                                var sgId = sg?["id"]?.GetValue<string>();
                                var clients = sg?["clients"]?.AsArray();
                                if (clients != null && clients.Any(c => targetedMacs.Contains(c?["id"]?.GetValue<string>() ?? "")))
                                {
                                    if (!string.IsNullOrEmpty(sgId))
                                    {
                                        gateIds.Add(sgId);
                                        var matchingDbGroup = allGroups.FirstOrDefault(g => g.SnapcastGroupId == sgId);
                                        if (matchingDbGroup != null)
                                        {
                                            gateIds.Add(matchingDbGroup.Id);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed querying Snapcast groups to resolve candidate room gates");
                    }
                }
            }
        }

        return gateIds.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    private async Task<List<GroupSnapshot>> ResolveTargetSnapshotsAsync(BroadcastPlayRequest request, OrchestratorDbContext db)
    {
        var allGroups = await db.Groups.ToListAsync();
        var allRooms = await db.Rooms.ToListAsync();
        var initialSettings = await db.Settings.FirstOrDefaultAsync();
        var initialProfile = initialSettings?.VolumeProfile ?? VolumeProfile.Cubic;
        var snapStatus = await _snapcast.GetStatusAsync();
        var snapGroups = snapStatus?["server"]?["groups"]?.AsArray();

        var liveClientIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (snapGroups != null)
        {
            foreach (var sg in snapGroups)
            {
                var sgClients = sg?["clients"]?.AsArray();
                if (sgClients == null) continue;

                foreach (var c in sgClients)
                {
                    var cid = c?["id"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(cid)) liveClientIds.Add(cid);
                }
            }
        }

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
                    if (match != null)
                    {
                        targetedRoomMacs.Add(match.MacAddress);
                        continue;
                    }

                    // Live Snapcast clients do not necessarily have a persisted Rooms row
                    if (liveClientIds.Contains(rInput)) targetedRoomMacs.Add(rInput);
                }
            }
        }

        var snapshots = new List<GroupSnapshot>();
        var visitedSnapGroupIds = new HashSet<string>();
        bool dbChanged = false;
        foreach (var group in allGroups)
        {
            var snapGroupId = group.SnapcastGroupId;
            bool snapGroupExists = snapGroups != null && !string.IsNullOrEmpty(snapGroupId) &&
                snapGroups.Any(sg => sg?["id"]?.GetValue<string>() == snapGroupId);

            if (!snapGroupExists && snapGroups != null)
            {
                var otherMappedSnapIds = allGroups
                    .Where(g => g.Id != group.Id && !string.IsNullOrEmpty(g.SnapcastGroupId))
                    .Select(g => g.SnapcastGroupId!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var matchingSnapGroup = snapGroups.FirstOrDefault(sg => {
                    var sid = sg?["id"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(sid) || otherMappedSnapIds.Contains(sid)) return false;
                    return sg?["name"]?.GetValue<string>() == group.Name || sid == group.Id;
                });
                var resolvedId = matchingSnapGroup?["id"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(resolvedId))
                {
                    snapGroupId = resolvedId;
                    if (group.SnapcastGroupId != resolvedId)
                    {
                        group.SnapcastGroupId = resolvedId;
                        dbChanged = true;
                    }
                }
            }

            if (string.IsNullOrEmpty(snapGroupId)) continue;

            var sgNode = snapGroups?.FirstOrDefault(sg => sg?["id"]?.GetValue<string>() == snapGroupId);
            if (sgNode == null && snapGroups != null) continue;

            visitedSnapGroupIds.Add(snapGroupId);

            var clients = sgNode?["clients"]?.AsArray();

            bool isGroupExplicitlyTargeted = targetedGroupIds.Contains(group.Id);
            var matchingClientsInGroup = new List<ClientSnapshot>();
            var unselectedClientsInGroup = new List<ClientSnapshot>();

            if (clients != null)
            {
                foreach (var c in clients)
                {
                    var cid = c?["id"]?.GetValue<string>() ?? string.Empty;
                    var cVol = c?["config"]?["volume"]?["percent"]?.GetValue<int>() ?? 100;
                    var cMuted = c?["config"]?["volume"]?["muted"]?.GetValue<bool>() ?? false;
                    var roomEntity = allRooms.FirstOrDefault(r => string.Equals(r.MacAddress, cid, StringComparison.OrdinalIgnoreCase));

                    var clientSnapshot = new ClientSnapshot
                    {
                        ClientId = cid,
                        Volume = cVol,
                        Muted = cMuted,
                        InitialDbMixVolume = roomEntity?.MixVolume,
                        InitialDbIsMuted = roomEntity?.IsMuted
                    };

                    if (isGroupExplicitlyTargeted || targetedRoomMacs.Contains(cid))
                    {
                        matchingClientsInGroup.Add(clientSnapshot);
                    }
                    else
                    {
                        unselectedClientsInGroup.Add(clientSnapshot);
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
                    OriginalIsMuted = group.IsMuted,
                    InitialDbVolume = group.MasterVolume,
                    InitialDbIsMuted = group.IsMuted,
                    InitialVolumeProfile = initialProfile
                };

                snapshot.SelectedClients.AddRange(matchingClientsInGroup);

                if (matchingClientsInGroup.Count > 0 && unselectedClientsInGroup.Count > 0)
                {
                    snapshot.UnselectedClients.AddRange(unselectedClientsInGroup);
                }

                snapshots.Add(snapshot);
            }
        }

        // Native Snapcast groups without an orchestrator group (e.g. unassigned rooms)
        if (snapGroups != null && targetedRoomMacs.Count > 0)
        {
            foreach (var sg in snapGroups)
            {
                var sgId = sg?["id"]?.GetValue<string>();
                if (string.IsNullOrEmpty(sgId) || visitedSnapGroupIds.Contains(sgId)) continue;

                var sgClients = sg?["clients"]?.AsArray();
                if (sgClients == null) continue;

                var targeted = new List<ClientSnapshot>();
                var others = new List<ClientSnapshot>();
                foreach (var c in sgClients)
                {
                    var cid = c?["id"]?.GetValue<string>() ?? string.Empty;
                    var cVol = c?["config"]?["volume"]?["percent"]?.GetValue<int>() ?? 100;
                    var cMuted = c?["config"]?["volume"]?["muted"]?.GetValue<bool>() ?? false;
                    var roomEntity = allRooms.FirstOrDefault(r => string.Equals(r.MacAddress, cid, StringComparison.OrdinalIgnoreCase));

                    var clientSnapshot = new ClientSnapshot
                    {
                        ClientId = cid,
                        Volume = cVol,
                        Muted = cMuted,
                        InitialDbMixVolume = roomEntity?.MixVolume,
                        InitialDbIsMuted = roomEntity?.IsMuted
                    };

                    if (targetedRoomMacs.Contains(cid)) targeted.Add(clientSnapshot);
                    else others.Add(clientSnapshot);
                }

                if (targeted.Count == 0) continue;

                var nativeSnapshot = new GroupSnapshot
                {
                    SnapcastGroupId = sgId,
                    OriginalStreamId = sg?["stream_id"]?.GetValue<string>() ?? "default",
                    IsNative = true,
                    InitialVolumeProfile = initialProfile
                };
                nativeSnapshot.NativeClients.AddRange(targeted);
                nativeSnapshot.UnselectedClients.AddRange(others);
                snapshots.Add(nativeSnapshot);
            }
        }

        if (dbChanged)
        {
            try
            {
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to backfill resolved SnapcastGroupId on groups");
            }
        }

        return snapshots;
    }

    private async Task FadeGroupsVolumeAsync(
        List<GroupSnapshot> snapshots,
        bool targetZero,
        int durationMs,
        CancellationToken ct,
        IReadOnlyDictionary<string, (int TargetVolume, bool TargetMuted)>? restorationTargets = null)
    {
        var steps = Math.Clamp(durationMs / 100, 2, 10);
        var stepDelay = durationMs / steps;

        for (int step = 1; step <= steps; step++)
        {
            double progress = (double)step / steps;
            foreach (var s in snapshots)
            {
                var allClients = s.NativeClients.Concat(s.SelectedClients).Concat(s.UnselectedClients);
                foreach (var c in allClients)
                {
                    if (targetZero)
                    {
                        int clientVol = (int)Math.Round(c.Volume * (1.0 - progress));
                        clientVol = Math.Clamp(clientVol, 0, 100);
                        try
                        {
                            await _snapcast.SetClientVolumeAsync(c.ClientId, clientVol, c.Muted);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Error fading down client {ClientId} volume to {Vol}", c.ClientId, clientVol);
                        }
                    }
                    else
                    {
                        int targetVol = c.Volume;
                        bool targetMuted = c.Muted;
                        if (restorationTargets != null && restorationTargets.TryGetValue(c.ClientId, out var resolvedTarget))
                        {
                            targetVol = resolvedTarget.TargetVolume;
                            targetMuted = resolvedTarget.TargetMuted;
                        }

                        if (targetMuted)
                        {
                            try
                            {
                                await _snapcast.SetClientVolumeAsync(c.ClientId, targetVol, muted: true);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Error maintaining mute during fade for client {ClientId}", c.ClientId);
                            }
                        }
                        else
                        {
                            int clientVol = (int)Math.Round(targetVol * progress);
                            clientVol = Math.Clamp(clientVol, 0, 100);
                            try
                            {
                                await _snapcast.SetClientVolumeAsync(c.ClientId, clientVol, muted: false);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Error fading up client {ClientId} volume to {Vol}", c.ClientId, clientVol);
                            }
                        }
                    }
                }
            }

            if (step < steps)
            {
                await Task.Delay(stepDelay, ct);
            }
        }
    }

    private async Task<List<string>> RestoreSnapshotsAsync(
        List<GroupSnapshot> snapshots,
        int fadeDurationMs,
        bool resumePlayback,
        VolumeOrchestratorService volumeOrchestrator)
    {
        var restorationErrors = new List<string>();
        var fadeMs = Math.Clamp(fadeDurationMs, 0, MaxFadeDurationMs);

        // 1. Establish a safe volume before switching back so the original stream never
        // briefly plays at the announcement volume.
        foreach (var s in snapshots)
        {
            foreach (var c in s.NativeClients.Concat(s.SelectedClients))
            {
                try
                {
                    await _snapcast.SetClientVolumeAsync(c.ClientId, fadeMs > 0 ? 0 : c.Volume, c.Muted);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed preparing volume before restoring stream on client {ClientId}", c.ClientId);
                    restorationErrors.Add($"Client {c.ClientId} volume prep: {ex.Message}");
                }
            }

            // Excluded clients must remain muted until after the stream switch has completed
            foreach (var c in s.UnselectedClients)
            {
                try
                {
                    await _snapcast.SetClientVolumeAsync(c.ClientId, c.Volume, muted: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed keeping unselected client {ClientId} muted before stream switch", c.ClientId);
                    restorationErrors.Add($"Client {c.ClientId} mute hold: {ex.Message}");
                }
            }
        }

        // 2. Switch back to the stream (respecting any user selection made during broadcast)
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var successfullyRestoredSnapshots = new HashSet<string>();

        foreach (var s in snapshots)
        {
            var targetStreamId = s.OriginalStreamId;
            if (!s.IsNative)
            {
                var currentGroup = await db.Groups.FindAsync(s.GroupId);
                if (currentGroup != null && !string.IsNullOrEmpty(currentGroup.CurrentStreamId))
                {
                    targetStreamId = currentGroup.CurrentStreamId;
                }
            }

            bool streamRestored = false;
            for (int retry = 0; retry < 3; retry++)
            {
                try
                {
                    await _snapcast.SetGroupStreamAsync(s.SnapcastGroupId, targetStreamId);
                    streamRestored = true;
                    successfullyRestoredSnapshots.Add(s.SnapcastGroupId);
                    break;
                }
                catch (Exception ex)
                {
                    if (retry == 2)
                    {
                        _logger.LogWarning(ex, "Failed restoring stream on group {GroupId} after retries", s.SnapcastGroupId);
                        restorationErrors.Add($"Group {s.SnapcastGroupId} stream restore: {ex.Message}");
                    }
                    else
                    {
                        await Task.Delay(100);
                    }
                }
            }

            // If stream restoration failed, detach from announcement stream to default silence and mute clients immediately
            if (!streamRestored)
            {
                try
                {
                    await _snapcast.SetGroupStreamAsync(s.SnapcastGroupId, "default");
                }
                catch { /* Ignore */ }

                foreach (var c in s.NativeClients.Concat(s.SelectedClients).Concat(s.UnselectedClients))
                {
                    try
                    {
                        await _snapcast.SetClientVolumeAsync(c.ClientId, c.Volume, muted: true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed emergency muting client {ClientId} for group with failed stream restore", c.ClientId);
                    }
                }
            }
        }

        var successfulSnapshots = snapshots.Where(s => successfullyRestoredSnapshots.Contains(s.SnapcastGroupId)).ToList();

        // 3. Resume Spotify at (near) zero volume, before fading back up
        // Attempt to resume every source recorded as paused, independently of individual group stream-restoration results;
        // any failed groups are already emergency-muted above.
        if (resumePlayback)
        {
            var resumedSources = new HashSet<string>();
            foreach (var s in snapshots)
            {
                if (string.IsNullOrEmpty(s.PausedSpotifySourceId) || !resumedSources.Add(s.PausedSpotifySourceId)) continue;

                try
                {
                    await _librespotApi.SendCommandAsync(s.PausedSpotifySourceId, "resume");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed resuming Spotify source {SourceId}", s.PausedSpotifySourceId);
                    restorationErrors.Add($"Spotify source {s.PausedSpotifySourceId} resume: {ex.Message}");
                }
            }
        }

        // 4. Resolve final original group volume / mute state (replaying Spotify volume reconciliation or respecting deferred DB edits)
        var currentRooms = await db.Rooms.ToDictionaryAsync(r => r.MacAddress);
        var settings = await db.Settings.FirstOrDefaultAsync();
        var profile = settings?.VolumeProfile ?? VolumeProfile.Cubic;
        var resolvedGroupStates = new Dictionary<string, (int TargetVolume, bool TargetMuted)>();

        foreach (var s in successfulSnapshots.Where(s => !s.IsNative))
        {
            try
            {
                var currentGroup = await db.Groups.FindAsync(s.GroupId);
                var targetStreamId = currentGroup?.CurrentStreamId ?? s.OriginalStreamId;

                int targetVolume = currentGroup?.MasterVolume ?? s.OriginalVolume;
                bool targetMuted = currentGroup?.IsMuted ?? s.OriginalIsMuted;

                if (currentGroup != null)
                {
                    if (targetStreamId.StartsWith(OrchestratorConstants.SpotifyPrefix))
                    {
                        if (currentGroup.MasterVolume != s.InitialDbVolume)
                        {
                            // Explicit user edit takes precedence
                            targetVolume = currentGroup.MasterVolume;
                        }
                        else
                        {
                            // Reconcile source/sibling volume changes (for newly selected or unchanged Spotify streams)
                            var sourceId = targetStreamId.Replace(OrchestratorConstants.SpotifyPrefix, "");
                            var source = await db.SpotifySources.FindAsync(sourceId);
                            var broadcastGroupIds = snapshots.Where(snap => !snap.IsNative).Select(snap => snap.GroupId).ToHashSet();
                            var activeSibling = await db.Groups.FirstOrDefaultAsync(g => g.Id != currentGroup.Id && !broadcastGroupIds.Contains(g.Id) && g.CurrentStreamId == targetStreamId);
                            if (activeSibling != null)
                            {
                                currentGroup.MasterVolume = activeSibling.MasterVolume;
                                targetVolume = currentGroup.MasterVolume;
                                await db.SaveChangesAsync();
                            }
                            else if (source != null && source.IsActive)
                            {
                                currentGroup.MasterVolume = source.MasterVolume;
                                targetVolume = currentGroup.MasterVolume;
                                await db.SaveChangesAsync();
                            }
                        }
                    }
                    else if (currentGroup.MasterVolume != s.InitialDbVolume)
                    {
                        targetVolume = currentGroup.MasterVolume;
                    }

                    if (currentGroup.IsMuted != s.InitialDbIsMuted)
                    {
                        targetMuted = currentGroup.IsMuted;
                    }
                }

                resolvedGroupStates[s.GroupId] = (targetVolume, targetMuted);
                if (currentGroup != null && (targetVolume != currentGroup.MasterVolume || targetMuted != currentGroup.IsMuted))
                {
                    // force: false ensures DB persistence and SignalR notification occur without triggering
                    // an uncoordinated Snapcast bulk recalculation while broadcast ownership is active.
                    await volumeOrchestrator.SetGroupVolumeAsync(s.GroupId, targetVolume, isMuted: targetMuted, syncStreamSource: false, force: false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed setting final original volume on group {GroupId}", s.GroupId);
                restorationErrors.Add($"Group {s.GroupId} final volume: {ex.Message}");
            }
        }

        // Calculate resolved client target volume and mute states
        var resolvedClientTargets = new Dictionary<string, (int TargetVolume, bool TargetMuted)>();
        foreach (var s in successfulSnapshots)
        {
            var (resolvedGrpVol, resolvedGrpMuted) = (!s.IsNative && resolvedGroupStates.TryGetValue(s.GroupId, out var st))
                ? st
                : (s.OriginalVolume, s.OriginalIsMuted);

            bool groupSettingsChanged = !s.IsNative && (resolvedGrpVol != s.OriginalVolume || resolvedGrpMuted != s.OriginalIsMuted);
            bool profileChanged = profile != s.InitialVolumeProfile;

            foreach (var c in s.NativeClients.Concat(s.SelectedClients).Concat(s.UnselectedClients))
            {
                int finalVol = c.Volume;
                bool finalMuted = c.Muted;

                var hasRoom = currentRooms.TryGetValue(c.ClientId, out var currentRoom);
                bool roomMixChanged = hasRoom && (c.InitialDbMixVolume.HasValue
                    ? currentRoom!.MixVolume != c.InitialDbMixVolume.Value
                    : true);
                bool roomMuteChanged = hasRoom && (c.InitialDbIsMuted.HasValue
                    ? currentRoom!.IsMuted != c.InitialDbIsMuted.Value
                    : true);

                if (s.IsNative)
                {
                    if (profileChanged || roomMixChanged || roomMuteChanged)
                    {
                        int mixVol = currentRoom?.MixVolume ?? (c.InitialDbMixVolume ?? 100);
                        finalVol = VolumeOrchestratorService.CalculateSnapcastVolume(mixVol, 100, profile);
                        finalMuted = currentRoom?.IsMuted ?? (c.InitialDbIsMuted ?? false);
                    }
                }
                else
                {
                    if (profileChanged || groupSettingsChanged || roomMixChanged || roomMuteChanged)
                    {
                        int mixVol = currentRoom?.MixVolume ?? (c.InitialDbMixVolume ?? 100);
                        bool roomMuted = currentRoom?.IsMuted ?? (c.InitialDbIsMuted ?? false);
                        finalVol = VolumeOrchestratorService.CalculateSnapcastVolume(resolvedGrpVol, mixVol, profile);
                        finalMuted = resolvedGrpMuted || roomMuted;
                    }
                }

                resolvedClientTargets[c.ClientId] = (finalVol, finalMuted);
            }
        }

        // 5. Fade back up using resolved client target volumes and mute states
        if (fadeMs > 0 && successfulSnapshots.Count > 0)
        {
            await FadeGroupsVolumeAsync(successfulSnapshots, targetZero: false, fadeMs, CancellationToken.None, resolvedClientTargets);
        }

        // 6. Saved per-client states must be applied last:
        // Set final exact client state from resolvedClientTargets
        foreach (var s in successfulSnapshots)
        {
            foreach (var c in s.NativeClients.Concat(s.SelectedClients).Concat(s.UnselectedClients))
            {
                try
                {
                    var (finalVol, finalMuted) = resolvedClientTargets.TryGetValue(c.ClientId, out var target)
                        ? target
                        : (c.Volume, c.Muted);

                    await _snapcast.SetClientVolumeAsync(c.ClientId, finalVol, finalMuted);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed restoring client {ClientId} volume/mute state", c.ClientId);
                    restorationErrors.Add($"Client {c.ClientId} final restore: {ex.Message}");
                }
            }
        }

        return restorationErrors;
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

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int LibcOpen(string pathname, int flags);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int LibcIoctl(int fd, ulong request, out int argp);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int LibcClose(int fd);

    private const int LibcO_RDONLY = 0;
    private const int LibcO_NONBLOCK = 0x800;
    private const ulong LibcFIONREAD = 0x541B;

    private static int GetFifoUnreadBytes(string fifoPath)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(fifoPath)) return 0;
        try
        {
            int fd = LibcOpen(fifoPath, LibcO_RDONLY | LibcO_NONBLOCK);
            if (fd < 0) return 0;
            try
            {
                if (LibcIoctl(fd, LibcFIONREAD, out int unread) == 0)
                {
                    return Math.Max(0, unread);
                }
                return 0;
            }
            finally
            {
                LibcClose(fd);
            }
        }
        catch
        {
            return 0;
        }
    }

    private async Task WaitForFifoAndBufferDrainAsync(string fifoPath, int clientBufferMs, CancellationToken ct)
    {
        if (OperatingSystem.IsLinux() && File.Exists(fifoPath))
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 3000)
            {
                ct.ThrowIfCancellationRequested();
                int unread = GetFifoUnreadBytes(fifoPath);
                if (unread <= 0) break;
                await Task.Delay(50, ct);
            }
        }

        if (clientBufferMs > 0)
        {
            await Task.Delay(clientBufferMs, ct);
        }
    }
}
