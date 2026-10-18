using Docker.DotNet;
using Docker.DotNet.Models;
using AudioOrchestrator.Data;
using AudioOrchestrator.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using AudioOrchestrator.Hubs;

namespace AudioOrchestrator.Services;

/// <summary>
/// Periodically inspects orchestrated containers and detects crash loops.
/// When a container has restarted too many times in a short window, the watchdog
/// kills it, marks the source as disconnected, and logs a clear error.
/// </summary>
public class ContainerHealthWatchdog : BackgroundService
{
    private readonly ILogger<ContainerHealthWatchdog> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IDockerClient _dockerClient;

    // How often to check container health
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);

    // A container restarting more than this many times within the window is considered crash-looping
    private const int MaxRestartsBeforeKill = 5;
    private static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(3);

    public ContainerHealthWatchdog(
        IDockerClient dockerClient,
        ILogger<ContainerHealthWatchdog> logger,
        IServiceProvider serviceProvider)
    {
        _dockerClient = dockerClient;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Give the system time to boot up before we start watching
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            _logger.LogInformation("ContainerHealthWatchdog started. Checking every {Interval}s.", CheckInterval.TotalSeconds);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckContainerHealthAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ContainerHealthWatchdog encountered an error during health check.");
                }

                await Task.Delay(CheckInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown
        }
    }

    private async Task CheckContainerHealthAsync(CancellationToken ct)
    {
        var containers = await _dockerClient.Containers.ListContainersAsync(
            new ContainersListParameters { All = true }, ct);

        var orchContainers = containers.Where(c =>
            c.Names.Any(n => n.StartsWith("/" + OrchestratorConstants.ContainerPrefix)));

        foreach (var container in orchContainers)
        {
            var name = container.Names.FirstOrDefault()?.TrimStart('/') ?? container.ID[..12];

            // Check for "Restarting" state
            if (container.State == "restarting")
            {
                _logger.LogWarning("[Watchdog] Container {Name} is in 'restarting' state.", name);
                await InspectAndHandleCrashLoop(container.ID, name, ct);
                continue;
            }

            // Check for exited containers (Docker "unless-stopped" will restart them, but we want to detect if they are stuck in a loop)
            if (container.State == "exited")
            {
                await InspectAndHandleCrashLoop(container.ID, name, ct);
                continue;
            }

            // For running containers, check restart count via inspect
            if (container.State == "running")
            {
                await InspectAndHandleCrashLoop(container.ID, name, ct);
            }
        }
    }

    private async Task InspectAndHandleCrashLoop(string containerId, string containerName, CancellationToken ct)
    {
        ContainerInspectResponse inspect;
        try
        {
            inspect = await _dockerClient.Containers.InspectContainerAsync(containerId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Watchdog] Could not inspect container {Name}.", containerName);
            return;
        }

        var restartCount = inspect.RestartCount;
        var startedAt = inspect.State.StartedAt;
        var finishedAt = inspect.State.FinishedAt;

        // If restart count exceeds threshold, check how quickly it's cycling
        if (restartCount < MaxRestartsBeforeKill)
        {
            return;
        }

        // The container has restarted many times. Check if it's recent.
        // Docker's StartedAt gives us the last start time.
        if (!DateTime.TryParse(startedAt, out var lastStart))
        {
            return;
        }

        var timeSinceLastStart = DateTime.UtcNow - lastStart.ToUniversalTime();

        // If the container keeps restarting and hasn't been running for long, it's crash-looping
        if (timeSinceLastStart > RestartWindow)
        {
            // It's been running long enough since last restart — probably recovered
            return;
        }

        _logger.LogError(
            "[Watchdog] Container {Name} is crash-looping ({RestartCount} restarts). Killing and marking as broken.",
            containerName, restartCount);

        await KillCrashLoopingContainer(containerId, containerName, restartCount, ct);
    }

    private async Task KillCrashLoopingContainer(string containerId, string containerName, long restartCount, CancellationToken ct)
    {
        try
        {
            try
            {
                using var logsStream = await _dockerClient.Containers.GetContainerLogsAsync(containerId, false, new ContainerLogsParameters
                {
                    ShowStdout = true,
                    ShowStderr = true,
                    Tail = "100"
                }, ct);

                var (stdout, stderr) = await logsStream.ReadOutputToEndAsync(ct);
                var inspect = await _dockerClient.Containers.InspectContainerAsync(containerId, ct);
                var exitCode = inspect.State.ExitCode;
                var oomKilled = inspect.State.OOMKilled;
                var errorMsg = inspect.State.Error;

                _logger.LogError("[Watchdog] Last logs for crash-looping container {Name} (ExitCode: {ExitCode}, OOMKilled: {OOMKilled}, Error: {Error}):\nSTDOUT:\n{Stdout}\nSTDERR:\n{Stderr}", 
                    containerName, exitCode, oomKilled, errorMsg, stdout, stderr);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Watchdog] Could not retrieve logs or inspect state for container {Name} before removal.", containerName);
            }

            // Stop with no restart (we override the policy by removing the container)
            await _dockerClient.Containers.StopContainerAsync(containerId,
                new ContainerStopParameters { WaitBeforeKillSeconds = 2 }, ct);
            await _dockerClient.Containers.RemoveContainerAsync(containerId,
                new ContainerRemoveParameters { Force = true }, ct);

            _logger.LogWarning("[Watchdog] Removed crash-looping container {Name}.", containerName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Watchdog] Failed to remove crash-looping container {Name}.", containerName);
            return;
        }

        // Mark the associated source as disconnected in the DB and notify the UI
        await MarkSourceDisconnectedAsync(containerName, restartCount);
    }

    private async Task MarkSourceDisconnectedAsync(string containerName, long restartCount)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var hub = scope.ServiceProvider.GetRequiredService<IHubContext<AudioHub, IAudioClient>>();
        var stateBuilder = scope.ServiceProvider.GetRequiredService<FullStateBuilder>();

        bool changed = false;

        // Check if it's a Spotify source
        if (containerName.StartsWith(OrchestratorConstants.SpotifyContainerPrefix))
        {
            var sourceId = containerName[OrchestratorConstants.SpotifyContainerPrefix.Length..];
            var source = await db.SpotifySources.FindAsync(sourceId);
            if (source != null)
            {
                source.IsConnected = false;
                source.IsActive = false;
                source.ErrorMessage = $"⚠ Crash loop ({restartCount}x) — container removed";
                changed = true;
                _logger.LogError("[Watchdog] Spotify source '{Name}' ({Id}) marked as broken.", source.Name, sourceId);
            }
        }
        // Check if it's a Radio stream
        else if (containerName.StartsWith(OrchestratorConstants.RadioContainerPrefix))
        {
            var streamId = containerName[OrchestratorConstants.RadioContainerPrefix.Length..];
            var radio = await db.RadioStreams.FindAsync(streamId);
            if (radio != null)
            {
                radio.ErrorMessage = $"⚠ Crash loop ({restartCount}x) — container removed";
                changed = true;
                _logger.LogError("[Watchdog] Radio stream '{Name}' ({Id}) marked as broken.", radio.Name, streamId);
            }
        }

        if (changed)
        {
            await db.SaveChangesAsync();
            await hub.Clients.All.OnFullStateUpdate(await stateBuilder.BuildAsync());
        }
    }
}
