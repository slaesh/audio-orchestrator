using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using AudioOrchestrator.Hubs;
using AudioOrchestrator.Contracts;

namespace AudioOrchestrator.Services;

public class SnapcastTcpListenerService : BackgroundService
{
    private readonly ILogger<SnapcastTcpListenerService> _logger;
    private readonly IHubContext<AudioHub, IAudioClient> _hub;
    private readonly IServiceProvider _serviceProvider;
    private readonly SnapcastConnectionService _connection;

    public SnapcastTcpListenerService(
        ILogger<SnapcastTcpListenerService> logger,
        IHubContext<AudioHub, IAudioClient> hub,
        IServiceProvider serviceProvider,
        SnapcastConnectionService connection)
    {
        _logger = logger;
        _hub = hub;
        _serviceProvider = serviceProvider;
        _connection = connection;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SnapcastTcpListenerService: Subscribing to Snapcast TCP events.");
        
        async void OnEvent(object? s, JsonNode e)
        {
            try
            {
                await OnEventReceived(e);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SnapcastTcpListenerService: Unhandled exception in OnEventReceived.");
            }
        }

        async void OnConnected(object? s, EventArgs e)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var reconciliationService = scope.ServiceProvider.GetRequiredService<IReconciliationService>();
                await reconciliationService.ReconcileAsync(stoppingToken);
                await BroadcastFullState();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SnapcastTcpListenerService: Error during connection reconciliation.");
            }
        }

        _connection.EventReceived += OnEvent;
        _connection.Connected += OnConnected;

        try
        {
            // Initial hydration: trigger a full state broadcast
            await BroadcastFullState();

            // Keep the background service alive while the application is running
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal graceful shutdown
        }
        finally
        {
            _connection.EventReceived -= OnEvent;
            _connection.Connected -= OnConnected;
        }
    }

    private async Task OnEventReceived(JsonNode node)
    {
        try
        {
            var method = node["method"]?.GetValue<string>();
            if (string.IsNullOrEmpty(method)) return;

            await HandleNotification(method, node["params"]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SnapcastTcpListenerService: Failed to handle notification: {ErrorMessage}", ex.Message);
        }
    }

    private async Task HandleNotification(string method, JsonNode? parameters)
    {
        switch (method)
        {
            case "Client.OnVolumeChanged":
                var clientId = parameters?["id"]?.GetValue<string>();
                var percent = parameters?["volume"]?["percent"]?.GetValue<int>();
                var muted = parameters?["volume"]?["muted"]?.GetValue<bool>();
                if (clientId == null || !percent.HasValue || !muted.HasValue) return;

                await _hub.Clients.All.OnRoomVolumeChanged(clientId, percent.Value, muted.Value);
                break;

            case "Client.OnConnect":
            case "Client.OnDisconnect":
            case "Group.OnStreamChanged":
            case "Group.OnNameChanged":
            case "Group.OnMute":
            case "Server.OnUpdate":
            case "Stream.OnUpdate":
            case "Group.OnClientsChanged":
                // Structural changes require a full state rebuild
                await BroadcastFullState();
                break;

            default:
                break;
        }
    }

    private async Task BroadcastFullState()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var stateBuilder = scope.ServiceProvider.GetRequiredService<FullStateBuilder>();
            var state = await stateBuilder.BuildAsync();
            await _hub.Clients.All.OnFullStateUpdate(state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SnapcastTcpListenerService: Failed to broadcast full state: {ErrorMessage}", ex.Message);
        }
    }
}
