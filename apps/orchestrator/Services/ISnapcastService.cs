using System.Text.Json.Nodes;

namespace AudioOrchestrator.Services;

public interface ISnapcastService
{
    bool IsConnected { get; }
    Task<JsonNode?> GetStatusAsync();
    Task<string> AddStreamAsync(string streamUri);
    Task RemoveStreamAsync(string streamId);
    Task SetGroupStreamAsync(string groupId, string streamId);
    Task SetGroupClientsAsync(string groupId, string[] clientIds);
    Task SetClientVolumeAsync(string clientId, int percent, bool muted = false);
    Task SetGroupNameAsync(string groupId, string name);
    Task SetClientNameAsync(string clientId, string name);
    Task DeleteClientAsync(string clientId);
}
