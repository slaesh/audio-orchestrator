using System.Runtime.InteropServices;
using Docker.DotNet;
using Docker.DotNet.Models;
using AudioOrchestrator.Data;
using AudioOrchestrator.Data.Entities;
using AudioOrchestrator.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using AudioOrchestrator.Hubs;
using Microsoft.Extensions.DependencyInjection;

namespace AudioOrchestrator.Services;

public interface IContainerLifecycleService
{
    Task SpawnLibreSpotAsync(string sourceId, string sourceName, string fifoPath, int maxVolume = 50, IList<ContainerListResponse>? existingContainers = null, string? version = null);
    Task UpgradeSpotifyNodeAsync(string sourceId, string targetVersion);
    Task SpawnRadioStreamAsync(RadioStreamEntity stream, string fifoPath, IList<ContainerListResponse>? existingContainers = null);
    Task StopAndRemoveContainerAsync(string containerName);
}

public class DockerLifecycleService : IContainerLifecycleService
{
    private readonly IDockerClient _dockerClient;
    private readonly ILogger<DockerLifecycleService> _logger;
    private readonly IFifoService _fifoService;
    private readonly RadioMetadataService _radioMetadataService;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;

    public DockerLifecycleService(
        IDockerClient dockerClient,
        ILogger<DockerLifecycleService> logger,
        IFifoService fifoService,
        RadioMetadataService radioMetadataService,
        IServiceProvider serviceProvider,
        IConfiguration configuration)
    {
        _dockerClient = dockerClient;
        _logger = logger;
        _fifoService = fifoService;
        _radioMetadataService = radioMetadataService;
        _serviceProvider = serviceProvider;
        _configuration = configuration;
    }

    public async Task SpawnLibreSpotAsync(string sourceId, string sourceName, string fifoPath, int maxVolume = 50, IList<ContainerListResponse>? existingContainers = null, string? version = null)
    {
        bool fifoWasDirectory = false;
        if (System.IO.Directory.Exists(fifoPath))
        {
            _logger.LogWarning("FIFO path {FifoPath} is a directory (likely created by Docker on reboot). Recreating spotify-node container.", fifoPath);
            fifoWasDirectory = true;
        }
        await _fifoService.EnsureFifoAsync(fifoPath);
        
        var containerName = $"{OrchestratorConstants.SpotifyContainerPrefix}{sourceId}";
        var apiPort = LibrespotApiClient.GetApiPortForSource(sourceId);

        var existing = existingContainers?.FirstOrDefault(c => c.Names.Any(n => n == $"/{containerName}"))
                       ?? (await _dockerClient.Containers.ListContainersAsync(new ContainersListParameters { All = true }))
                          .FirstOrDefault(c => c.Names.Any(n => n == $"/{containerName}"));

        var baseImage = _configuration["SPOTIFY_NODE_IMAGE"] ?? OrchestratorConstants.DefaultSpotifyNodeImage;
        var spotifyImage = baseImage;
        if (!string.IsNullOrWhiteSpace(version))
        {
            var repo = baseImage.Contains(':') ? baseImage[..baseImage.LastIndexOf(':')] : baseImage;
            spotifyImage = $"{repo}:{version}";
        }
        var internalUrl = (_configuration["ORCHESTRATOR_INTERNAL_URL"] ?? "http://localhost:5000").TrimEnd('/');
        var parameters = new CreateContainerParameters
        {
            Image = spotifyImage,
            Env = new List<string>
            {
                $"GROUP_ID={sourceId}",
                $"DEVICE_NAME={sourceName}",
                $"PIPE_PATH={fifoPath}",
                $"API_PORT={apiPort}",
                $"WEBHOOK_URL={internalUrl}/api/internal/events",
                $"INITIAL_VOLUME={maxVolume}"
            },
            Name = containerName,
            HostConfig = new HostConfig
            {
                NetworkMode = "host",
                ExtraHosts = new List<string> { "apresolve.spotify.com:0.0.0.0" },
                Binds = new List<string>
                {
                    $"{OrchestratorConstants.FifoBaseDir}:{OrchestratorConstants.FifoBaseDir}",
                    $"{OrchestratorConstants.LibrespotCacheVolume}:/var/cache/librespot"
                },
                RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped }
            }
        };

        if (existing != null)
        {
            var inspect = await _dockerClient.Containers.InspectContainerAsync(existing.ID);
            
            bool imageMatch = inspect.Config.Image == parameters.Image || inspect.Config.Image == $"{parameters.Image}:latest" || $"{inspect.Config.Image}:latest" == parameters.Image;
            bool nameEnvMatch = inspect.Config.Env.Contains($"DEVICE_NAME={sourceName}");
            bool portEnvMatch = inspect.Config.Env.Contains($"API_PORT={apiPort}");

            if (imageMatch && nameEnvMatch && portEnvMatch && !fifoWasDirectory)
            {
                if (inspect.State.Status != "running")
                {
                    await _dockerClient.Containers.StartContainerAsync(existing.ID, new ContainerStartParameters());
                    _logger.LogInformation("Restarted existing spotify-node container {ContainerName}", containerName);

                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                        var source = await db.SpotifySources.FindAsync(sourceId);
                        if (source != null)
                        {
                            source.IsConnected = false;
                            source.IsActive = false;
                            source.CurrentTitle = null;
                            source.CurrentArtist = null;
                            source.CurrentCoverUrl = null;
                            source.CurrentAlbum = null;
                            source.ContextUri = null;
                            source.ContextName = null;
                            source.ErrorMessage = null;
                            await db.SaveChangesAsync();

                            var hub = scope.ServiceProvider.GetRequiredService<IHubContext<AudioHub, IAudioClient>>();
                            var stateBuilder = scope.ServiceProvider.GetRequiredService<FullStateBuilder>();
                            await hub.Clients.All.OnFullStateUpdate(await stateBuilder.BuildAsync());
                        }
                    }
                }
                return;
            }

            _logger.LogInformation("Config changed (or broken mount) for {ContainerName}, recreating...", containerName);
            await StopAndRemoveContainerAsync(containerName);
        }

        // Reset connection state in DB since we are (re)starting the container
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var source = await db.SpotifySources.FindAsync(sourceId);
            if (source != null)
            {
                source.IsConnected = false;
                source.IsActive = false;
                source.CurrentTitle = null;
                source.CurrentArtist = null;
                source.CurrentCoverUrl = null;
                source.CurrentAlbum = null;
                source.ContextUri = null;
                source.ContextName = null;
                source.ErrorMessage = null;
                await db.SaveChangesAsync();
                
                // Broadcast state change immediately
                var hub = scope.ServiceProvider.GetRequiredService<IHubContext<AudioHub, IAudioClient>>();
                var stateBuilder = scope.ServiceProvider.GetRequiredService<FullStateBuilder>();
                await hub.Clients.All.OnFullStateUpdate(await stateBuilder.BuildAsync());
            }
        }

        try
        {
            await EnsureImageExistsAsync(spotifyImage);
            var response = await _dockerClient.Containers.CreateContainerAsync(parameters);
            await _dockerClient.Containers.StartContainerAsync(response.ID, new ContainerStartParameters());
            _logger.LogInformation("Started new spotify-node container {ContainerName} with image {Image}", containerName, spotifyImage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to spawn container {ContainerName}.", containerName);
            throw;
        }
    }

    public async Task UpgradeSpotifyNodeAsync(string sourceId, string targetVersion)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var source = await db.SpotifySources.FindAsync(sourceId);
        if (source == null)
        {
            throw new KeyNotFoundException($"Spotify source {sourceId} not found.");
        }

        var baseImage = _configuration["SPOTIFY_NODE_IMAGE"] ?? OrchestratorConstants.DefaultSpotifyNodeImage;
        var repo = baseImage.Contains(':') ? baseImage[..baseImage.LastIndexOf(':')] : baseImage;
        var targetImage = $"{repo}:{targetVersion}";

        _logger.LogInformation("Upgrading Spotify node {SourceName} ({SourceId}) from {CurrentVersion} to {TargetVersion} (image {TargetImage})",
            source.Name, sourceId, source.CurrentVersion, targetVersion, targetImage);

        await EnsureImageExistsAsync(targetImage);

        var containerName = $"{OrchestratorConstants.SpotifyContainerPrefix}{sourceId}";
        await StopAndRemoveContainerAsync(containerName);

        var streamId = $"{OrchestratorConstants.SpotifyPrefix}{source.Id}";
        var fifoPath = $"{OrchestratorConstants.FifoBaseDir}/{streamId}";
        await SpawnLibreSpotAsync(source.Id, source.Name, fifoPath, source.MaxVolume, version: targetVersion);

        source.CurrentVersion = targetVersion;
        await db.SaveChangesAsync();

        var hub = scope.ServiceProvider.GetRequiredService<IHubContext<AudioHub, IAudioClient>>();
        var stateBuilder = scope.ServiceProvider.GetRequiredService<FullStateBuilder>();
        await hub.Clients.All.OnFullStateUpdate(await stateBuilder.BuildAsync());

        _logger.LogInformation("Successfully upgraded Spotify node {SourceName} ({SourceId}) to {TargetVersion}", source.Name, sourceId, targetVersion);
    }

    public async Task SpawnRadioStreamAsync(RadioStreamEntity stream, string fifoPath, IList<ContainerListResponse>? existingContainers = null)
    {
        var streamId = stream.Id;
        var containerName = $"{OrchestratorConstants.RadioContainerPrefix}{streamId}";

        // 1. Check for a matching radio plugin
        RadioPluginEntity? matchedPlugin = null;
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var plugins = await db.RadioPlugins.AsNoTracking().Where(p => p.Enabled).ToListAsync();
            foreach (var p in plugins)
            {
                var patterns = p.GetUrlPatterns();
                foreach (var pattern in patterns)
                {
                    if (string.IsNullOrWhiteSpace(pattern)) continue;
                    try
                    {
                        if (System.Text.RegularExpressions.Regex.IsMatch(stream.StreamUrl, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250)))
                        {
                            matchedPlugin = p;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Invalid regex pattern '{Pattern}' in plugin {PluginId}: {Message}", pattern, p.Id, ex.Message);
                    }
                }
                if (matchedPlugin != null) break;
            }
        }

        bool isPlugin = matchedPlugin != null;
        var defaultFfmpeg = _configuration["DOCKER_FFMPEG_IMAGE"] ?? OrchestratorConstants.DefaultFfmpegImage;
        var desiredImage = isPlugin ? matchedPlugin!.DockerImage : defaultFfmpeg;
        var internalUrl = (_configuration["ORCHESTRATOR_INTERNAL_URL"] ?? "http://localhost:5000").TrimEnd('/');

        var desiredCmdTemplate = new List<string> {
            "-hide_banner",
            "-user_agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36",
            "-reconnect", "1",
            "-reconnect_at_eof", "1",
            "-reconnect_streamed", "1",
            "-reconnect_delay_max", "5",
            "-rw_timeout", "10000000",
            "-thread_queue_size", "4096",
            "-i", "PLACEHOLDER",
            "-f", "s16le",
            "-acodec", "pcm_s16le",
            "-ac", "2",
            "-ar", "44100",
            "-y", fifoPath
        };

        bool fifoWasDirectory = false;
        if (System.IO.Directory.Exists(fifoPath))
        {
            _logger.LogWarning("FIFO path {FifoPath} is a directory (likely created by Docker on reboot). Recreating container.", fifoPath);
            fifoWasDirectory = true;
        }
        await _fifoService.EnsureFifoAsync(fifoPath);

        var existing = existingContainers?.FirstOrDefault(c => c.Names.Any(n => n == $"/{containerName}"))
                       ?? (await _dockerClient.Containers.ListContainersAsync(new ContainersListParameters { All = true }))
                          .FirstOrDefault(c => c.Names.Any(n => n == $"/{containerName}"));

        if (existing != null)
        {
            var inspect = await _dockerClient.Containers.InspectContainerAsync(existing.ID);
            string? existingBaseUrl = null;
            inspect.Config.Labels?.TryGetValue("orch.radio.base_url", out existingBaseUrl);
            
            bool imageMatch = inspect.Config.Image == desiredImage || inspect.Config.Image == $"{desiredImage}:latest" || $"{inspect.Config.Image}:latest" == desiredImage;

            bool configMatch = false;
            if (isPlugin)
            {
                string? existingPluginId = null;
                inspect.Config.Labels?.TryGetValue("orch.radio.plugin_id", out existingPluginId);
                configMatch = existingPluginId == matchedPlugin!.Id;
            }
            else
            {
                var existingCmd = inspect.Config.Cmd?.ToList() ?? new List<string>();
                if (existingCmd.Count == desiredCmdTemplate.Count)
                {
                    configMatch = true;
                    for (int i = 0; i < existingCmd.Count; i++)
                    {
                        if (desiredCmdTemplate[i] == "PLACEHOLDER") continue;
                        if (existingCmd[i] != desiredCmdTemplate[i]) { configMatch = false; break; }
                    }
                }
            }

            if (existingBaseUrl == stream.StreamUrl && imageMatch && configMatch && !fifoWasDirectory)
            {
                if (inspect.State.Status != "running")
                {
                    await _dockerClient.Containers.StartContainerAsync(existing.ID, new ContainerStartParameters());
                }

                // Clear ErrorMessage in DB when restarting
                using (var scope = _serviceProvider.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                    var dbStream = await db.RadioStreams.FindAsync(streamId);
                    if (dbStream != null)
                    {
                        dbStream.ErrorMessage = null;
                        await db.SaveChangesAsync();

                        var hub = scope.ServiceProvider.GetRequiredService<IHubContext<AudioHub, IAudioClient>>();
                        var stateBuilder = scope.ServiceProvider.GetRequiredService<FullStateBuilder>();
                        await hub.Clients.All.OnFullStateUpdate(await stateBuilder.BuildAsync());
                    }
                }
                return;
            }

            await StopAndRemoveContainerAsync(containerName);
        }

        var labels = new Dictionary<string, string> { { "orch.radio.base_url", stream.StreamUrl } };
        if (isPlugin)
        {
            labels["orch.radio.plugin_id"] = matchedPlugin!.Id;
        }

        var parameters = new CreateContainerParameters
        {
            Image = desiredImage,
            Labels = labels,
            Name = containerName,
            HostConfig = new HostConfig
            {
                NetworkMode = isPlugin ? "host" : "bridge",
                Binds = new List<string> { $"{fifoPath}:{fifoPath}" },
                RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped }
            }
        };

        if (isPlugin)
        {
            parameters.Env = new List<string>
            {
                $"STREAM_ID={streamId}",
                $"STREAM_URL={stream.StreamUrl}",
                $"FIFO_PATH={fifoPath}",
                $"WEBHOOK_URL={internalUrl}/api/internal/radio-events"
            };
        }
        else
        {
            parameters.Cmd = desiredCmdTemplate.Select(c => c == "PLACEHOLDER" ? stream.StreamUrl : c).ToList();
        }

        try
        {
            await EnsureImageExistsAsync(desiredImage);

            var response = await _dockerClient.Containers.CreateContainerAsync(parameters);
            await _dockerClient.Containers.StartContainerAsync(response.ID, new ContainerStartParameters());
            
            // Clear ErrorMessage in DB when starting
            using (var scope = _serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                var dbStream = await db.RadioStreams.FindAsync(streamId);
                if (dbStream != null)
                {
                    dbStream.ErrorMessage = null;
                    await db.SaveChangesAsync();

                    var hub = scope.ServiceProvider.GetRequiredService<IHubContext<AudioHub, IAudioClient>>();
                    var stateBuilder = scope.ServiceProvider.GetRequiredService<FullStateBuilder>();
                    await hub.Clients.All.OnFullStateUpdate(await stateBuilder.BuildAsync());
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to spawn container {ContainerName}.", containerName);
        }
    }

    public async Task StopAndRemoveContainerAsync(string containerName)
    {
        var containers = await _dockerClient.Containers.ListContainersAsync(new ContainersListParameters { All = true });
        var targetNormalized = "/" + containerName.TrimStart('/');
        var existing = containers.FirstOrDefault(c => c.Names.Any(n => n == targetNormalized));

        if (existing != null)
        {
            try
            {
                await _dockerClient.Containers.StopContainerAsync(existing.ID, new ContainerStopParameters { WaitBeforeKillSeconds = 2 });
                await _dockerClient.Containers.RemoveContainerAsync(existing.ID, new ContainerRemoveParameters { Force = true });
                _logger.LogInformation("Removed container {ContainerName}", containerName);

                if (containerName.StartsWith(OrchestratorConstants.RadioContainerPrefix))
                {
                    var streamId = containerName.Replace(OrchestratorConstants.RadioContainerPrefix, "");
                    await _radioMetadataService.ClearMetadataAsync(streamId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not seamlessly remove container {ContainerName}", containerName);
            }
        }
    }

    private async Task EnsureImageExistsAsync(string image)
    {
        try
        {
            var images = await _dockerClient.Images.ListImagesAsync(new ImagesListParameters { All = true });
            var normalizedImage = image.Contains(':') ? image : $"{image}:latest";
            var exists = images.Any(img => img.RepoTags != null && img.RepoTags.Any(t => t == normalizedImage || t == image));
            if (!exists)
            {
                _logger.LogInformation("Docker image {Image} not found locally. Pulling from registry...", image);
                var lastColon = image.LastIndexOf(':');
                var repo = lastColon != -1 && !image.Substring(lastColon).Contains('/') ? image.Substring(0, lastColon) : image;
                var tag = lastColon != -1 && !image.Substring(lastColon).Contains('/') ? image.Substring(lastColon + 1) : "latest";
                await _dockerClient.Images.CreateImageAsync(
                    new ImagesCreateParameters { FromImage = repo, Tag = tag },
                    null,
                    new Progress<JSONMessage>());
                _logger.LogInformation("Successfully pulled Docker image {Image}", image);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check or pull Docker image {Image}. Will attempt to proceed if cached locally.", image);
        }
    }
}
