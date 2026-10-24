using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace AudioOrchestrator.Services;

public class SnapcastConnectionService : IDisposable
{
    private readonly ILogger<SnapcastConnectionService> _logger;
    private readonly string _host;
    private readonly int _port;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private int _requestId = 0;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pendingRequests = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readTask;

    public event EventHandler<JsonNode>? EventReceived;
    public event EventHandler? Connected;
    public bool IsConnected => _client?.Connected == true && _stream != null;

    public SnapcastConnectionService(ILogger<SnapcastConnectionService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _host = configuration["SNAPCAST_HOST"] ?? "localhost";
        _port = int.TryParse(configuration["SNAPCAST_PORT"] ?? configuration["SNAPCAST_RPC_PORT"], out var p) ? p : 1705;
        _readTask = Task.Run(ConnectionLoop);
    }

    private async Task ConnectionLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                _client = new TcpClient();
                _logger.LogInformation("SnapcastConnectionService: Connecting to {Host}:{Port}...", _host, _port);
                await _client.ConnectAsync(_host, _port, _cts.Token);
                _stream = _client.GetStream();
                _logger.LogInformation("SnapcastConnectionService: Connected.");
                Connected?.Invoke(this, EventArgs.Empty);

                using var reader = new StreamReader(_stream, Encoding.UTF8);
                while (!_cts.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(_cts.Token);
                    if (line == null) break;

                    try
                    {
                        var node = JsonNode.Parse(line);
                        if (node == null) continue;

                        if (node["id"] != null)
                        {
                            var id = node["id"]!.GetValue<int>();
                            if (_pendingRequests.TryRemove(id, out var tcs))
                            {
                                if (node["error"] != null)
                                {
                                    var errMsg = node["error"]?["message"]?.GetValue<string>() ?? "Snapcast RPC error";
                                    tcs.TrySetException(new Exception(errMsg));
                                }
                                else
                                {
                                    tcs.TrySetResult(node["result"]);
                                }
                            }
                        }
                        else if (node["method"] != null)
                        {
                            EventReceived?.Invoke(this, node);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "SnapcastConnectionService: Error parsing line: {ErrorMessage}", ex.Message);
                    }
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!_cts.IsCancellationRequested)
                {
                    _logger.LogWarning("SnapcastConnectionService: Connection lost: {ErrorMessage}. Reconnecting in 5s...", ex.Message);
                    try
                    {
                        await Task.Delay(5000, _cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            finally
            {
                _client?.Dispose();
                _client = null;
                _stream = null;

                // Fail any pending requests immediately upon disconnect
                foreach (var kvp in _pendingRequests)
                {
                    if (_pendingRequests.TryRemove(kvp.Key, out var pendingTcs))
                    {
                        pendingTcs.TrySetException(new IOException("Snapcast TCP connection closed."));
                    }
                }
            }
        }
    }

    public async Task<JsonNode?> SendRequestAsync(string method, object? @params = null)
    {
        var id = Interlocked.Increment(ref _requestId);
        var request = new
        {
            id,
            jsonrpc = "2.0",
            method,
            @params
        };

        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[id] = tcs;

        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(request);
            var buffer = Encoding.UTF8.GetBytes(json + "\n");
            
            await _writeLock.WaitAsync(_cts.Token);
            try
            {
                if (_stream == null) throw new IOException("Snapcast is not connected via TCP.");
                await _stream.WriteAsync(buffer, _cts.Token);
            }
            finally
            {
                _writeLock.Release();
            }
            
            // Wait for response with timeout
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), _cts.Token);
        }
        catch (TimeoutException)
        {
            _pendingRequests.TryRemove(id, out _);
            throw new TimeoutException($"Snapcast RPC timed out for method {method}");
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            _pendingRequests.TryRemove(id, out _);
            throw new OperationCanceledException($"Snapcast RPC cancelled for method {method}");
        }
        catch (Exception ex)
        {
            _pendingRequests.TryRemove(id, out _);
            _logger.LogError("SnapcastConnectionService: Error sending request {Method}: {ErrorMessage}", method, ex.Message);
            throw;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _client?.Dispose();
    }
}
