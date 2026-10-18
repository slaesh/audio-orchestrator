using TypedSignalR.Client;
using Tapper;

namespace AudioOrchestrator.Contracts;

[Receiver]
public interface IAudioClient
{
    Task OnFullStateUpdate(FullStateDto state);
    Task OnRoomVolumeChanged(string roomId, int percent, bool muted);
    Task OnGroupVolumeChanged(string groupId, int masterVolume, bool muted);
    Task OnSpotifyEvent(SpotifyEventDto spotifyEvent);
    Task OnRadioMetadataChanged(string streamId, string? title, string? artist, string? coverUrl);
}

[Hub]
public interface IAudioHub
{
}

