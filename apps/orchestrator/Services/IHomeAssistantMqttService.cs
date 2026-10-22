using AudioOrchestrator.Contracts;

namespace AudioOrchestrator.Services;

public interface IHomeAssistantMqttService
{
    bool IsConnected { get; }
    Task<TestMqttConnectionResponse> TestConnectionAsync(TestMqttConnectionRequest request, CancellationToken ct = default);
    Task ReloadConfigurationAsync(CancellationToken ct = default);
    Task NotifyFullStateAsync(FullStateDto state, CancellationToken ct = default);
    Task NotifyRadioMetadataAsync(string streamId, string? title, string? artist, string? coverUrl, CancellationToken ct = default);
    Task NotifySpotifyEventAsync(SpotifyEventDto spotifyEvent, CancellationToken ct = default);
    Task NotifyGroupVolumeChangedAsync(string groupId, int volume, bool isMuted, CancellationToken ct = default);
}
