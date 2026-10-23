using AudioOrchestrator.Contracts;

namespace AudioOrchestrator.Services;

public interface IAudioPlayerProcessRunner
{
    Task<int> StreamToFifoAsync(string inputSource, string fifoPath, Stream? stdinStream, CancellationToken ct);
}

public interface IBroadcastService
{
    Task<BroadcastPlayResult> PlayUrlAsync(BroadcastPlayRequest request, CancellationToken ct = default);
    Task<BroadcastPlayResult> PlayFileAsync(Stream fileStream, string fileExtension, BroadcastPlayRequest request, CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    BroadcastStatusDto GetStatus();
}
