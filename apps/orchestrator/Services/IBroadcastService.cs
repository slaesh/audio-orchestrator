using AudioOrchestrator.Contracts;

namespace AudioOrchestrator.Services;

public record ProcessRunResult(int ExitCode, string Stderr);

public interface IAudioPlayerProcessRunner
{
    Task<ProcessRunResult> StreamToFifoAsync(string inputSource, string fifoPath, Stream? stdinStream, CancellationToken ct);
}

public interface IBroadcastService
{
    Task<BroadcastJobDto> EnqueuePlayUrlAsync(BroadcastPlayRequest request, CancellationToken ct = default);
    Task<BroadcastJobDto> EnqueuePlayFileAsync(Stream fileStream, string fileExtension, BroadcastPlayRequest request, CancellationToken ct = default);
    Task<BroadcastPlayResult> WaitForJobAsync(string jobId, CancellationToken ct = default);
    Task<BroadcastPlayResult> PlayUrlAsync(BroadcastPlayRequest request, CancellationToken ct = default);
    Task<BroadcastPlayResult> PlayFileAsync(Stream fileStream, string fileExtension, BroadcastPlayRequest request, CancellationToken ct = default);
    Task<BroadcastJobDto?> GetJobAsync(string jobId, CancellationToken ct = default);
    Task<List<BroadcastJobDto>> GetRecentJobsAsync(int limit = 50, CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task<bool> CancelJobAsync(string jobId, CancellationToken ct = default);
    BroadcastStatusDto GetStatus();
    bool IsGroupActiveInBroadcast(string? orchestratorGroupId, string? snapcastGroupId = null);
    SemaphoreSlim GetGroupGate(string groupId);
}
