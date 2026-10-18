using Microsoft.AspNetCore.SignalR;
using AudioOrchestrator.Contracts;
using TypedSignalR.Client;

namespace AudioOrchestrator.Hubs;

public class AudioHub : Hub<IAudioClient>
{
}
