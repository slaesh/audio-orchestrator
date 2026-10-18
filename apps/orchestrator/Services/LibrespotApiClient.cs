using AudioOrchestrator.Services.Generated;

namespace AudioOrchestrator.Services;

public interface ILibrespotApiClient
{
    Task SetVolumeAsync(string sourceId, int percent);
    Task SendCommandAsync(string sourceId, string command);
    Task PlayContextAsync(string sourceId, string contextUri);
    Task<NodeStatusResponse?> GetStatusAsync(string sourceId);
    Task<HealthResponse?> GetHealthAsync(string sourceId);
}

public class LibrespotApiClient : ILibrespotApiClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<LibrespotApiClient> _logger;

    public LibrespotApiClient(HttpClient httpClient, ILogger<LibrespotApiClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public static int GetApiPortForSource(string sourceId)
    {
        uint hash = 5381;
        foreach (char c in sourceId)
        {
            hash = ((hash << 5) + hash) + (uint)c;
        }
        return 25400 + (int)(hash % 2000);
    }

    private SpotifyNodeOpenApiClient CreateGeneratedClient(int apiPort)
    {
        return new SpotifyNodeOpenApiClient($"http://localhost:{apiPort}/", _httpClient);
    }

    public async Task SetVolumeAsync(string sourceId, int percent)
    {
        var apiPort = GetApiPortForSource(sourceId);
        var client = CreateGeneratedClient(apiPort);
        await client.Set_volumeAsync(new VolumeRequest { Volume = percent });
        _logger.LogInformation("Pushed volume {Percent}% to spotify-node on port {ApiPort} via OpenAPI client", percent, apiPort);
    }

    public async Task SendCommandAsync(string sourceId, string command)
    {
        var apiPort = GetApiPortForSource(sourceId);
        var client = CreateGeneratedClient(apiPort);

        switch (command.ToLowerInvariant())
        {
            case "pause":
                await client.PauseAsync();
                break;
            case "resume":
                await client.ResumeAsync();
                break;
            case "next":
                await client.NextAsync();
                break;
            case "previous":
                await client.PreviousAsync();
                break;
            default:
                throw new ArgumentException($"Unsupported spotify-node command: {command}", nameof(command));
        }

        _logger.LogInformation("Sent command {Command} to spotify-node on port {ApiPort} via OpenAPI client", command, apiPort);
    }

    public async Task PlayContextAsync(string sourceId, string contextUri)
    {
        var apiPort = GetApiPortForSource(sourceId);
        var client = CreateGeneratedClient(apiPort);
        await client.Play_contextAsync(new PlayContextRequest { Context_uri = contextUri });
        _logger.LogInformation("Sent play-context {ContextUri} to spotify-node on port {ApiPort} via OpenAPI client", contextUri, apiPort);
    }

    public async Task<NodeStatusResponse?> GetStatusAsync(string sourceId)
    {
        var apiPort = GetApiPortForSource(sourceId);
        var client = CreateGeneratedClient(apiPort);
        return await client.Get_statusAsync();
    }

    public async Task<HealthResponse?> GetHealthAsync(string sourceId)
    {
        var apiPort = GetApiPortForSource(sourceId);
        var client = CreateGeneratedClient(apiPort);
        return await client.Get_healthAsync();
    }
}


