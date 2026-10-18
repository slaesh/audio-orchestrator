using System.Text.Json.Nodes;

namespace AudioOrchestrator.Services;

public class SnapcastService : ISnapcastService
{
    private readonly SnapcastConnectionService _connection;
    private readonly ILogger<SnapcastService> _logger;

    public SnapcastService(SnapcastConnectionService connection, ILogger<SnapcastService> logger)
    {
        _connection = connection;
        _logger = logger;
    }

    private async Task<JsonNode?> SendRequestAsync(string method, object? @params = null)
    {
        try
        {
            return await _connection.SendRequestAsync(method, @params);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Snapcast RPC Error for {method}: {ex.Message}");
            throw;
        }
    }

    public async Task<JsonNode?> GetStatusAsync()
    {
        return await SendRequestAsync("Server.GetStatus");
    }

    public async Task<string> AddStreamAsync(string streamUri)
    {
        // 1. Check if it already exists to avoid collisions
        var status = await GetStatusAsync();
        var streams = status?["server"]?["streams"]?.AsArray();
        if (streams != null)
        {
            foreach (var s in streams)
            {
                var existingUri = s?["uri"]?["raw"]?.GetValue<string>();
                var existingId = s?["id"]?.GetValue<string>();
                
                // Match by URI
                if (existingUri == streamUri) return existingId!;
                
                // Match by Name (parsed from URI query)
                var newName = GetNameFromUri(streamUri);
                var existingName = s?["uri"]?["query"]?["name"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(newName) && newName == existingName) return existingId!;
            }
        }

        try {
            var result = await SendRequestAsync("Stream.AddStream", new { streamUri });
            return result?["stream_id"]?.GetValue<string>() ?? Guid.NewGuid().ToString();
        } catch (Exception ex) when (ex.Message.Contains("already exists")) {
            // Re-fetch to find the ID of the one that supposedly already exists
            _logger.LogWarning($"Stream already exists in Snapserver but wasn't caught by pre-check. URI: {streamUri}");
            var freshStatus = await GetStatusAsync();
            var freshStreams = freshStatus?["server"]?["streams"]?.AsArray();
            var newName = GetNameFromUri(streamUri);
            var match = freshStreams?.FirstOrDefault(s => s?["uri"]?["query"]?["name"]?.GetValue<string>() == newName);
            return match?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString();
        }
    }

    private string? GetNameFromUri(string uri)
    {
        try {
            if (string.IsNullOrEmpty(uri)) return null;
            var queryIndex = uri.IndexOf('?');
            if (queryIndex == -1) return null;
            
            var query = uri.Substring(queryIndex + 1);
            var parts = query.Split('&');
            var namePart = parts.FirstOrDefault(p => p.StartsWith("name="));
            if (namePart == null || namePart.Length <= 5) return null;
            
            return Uri.UnescapeDataString(namePart.Substring(5));
        } catch { return null; }
    }

    public async Task RemoveStreamAsync(string streamId)
    {
        await SendRequestAsync("Stream.RemoveStream", new { id = streamId });
    }

    public async Task SetGroupStreamAsync(string groupId, string streamId)
    {
        await SendRequestAsync("Group.SetStream", new { id = groupId, stream_id = streamId });
    }

    public async Task SetGroupClientsAsync(string groupId, string[] clientIds)
    {
        await SendRequestAsync("Group.SetClients", new { id = groupId, clients = clientIds });
    }

    public async Task SetClientVolumeAsync(string clientId, int percent, bool muted = false)
    {
        await SendRequestAsync("Client.SetVolume", new { id = clientId, volume = new { percent, muted } });
    }

    public async Task SetGroupNameAsync(string groupId, string name)
    {
        await SendRequestAsync("Group.SetName", new { id = groupId, name });
    }

    public async Task SetClientNameAsync(string clientId, string name)
    {
        await SendRequestAsync("Client.SetName", new { id = clientId, name });
    }

    public async Task DeleteClientAsync(string clientId)
    {
        await SendRequestAsync("Server.DeleteClient", new { id = clientId });
    }
}
