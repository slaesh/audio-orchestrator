using Docker.DotNet;
using Docker.DotNet.Models;
using AudioOrchestrator.Data;
using AudioOrchestrator.Data.Entities;
using AudioOrchestrator.Contracts;
using Microsoft.EntityFrameworkCore;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using AudioOrchestrator.Hubs;
using Microsoft.Extensions.DependencyInjection;
using System.Threading;

namespace AudioOrchestrator.Services;

public interface IReconciliationService
{
    Task ReconcileAsync(CancellationToken ct = default);
    Task ReconcileDockerOnStartupAsync();
    Task ReconcileSnapcastAsync(List<GroupEntity> groups, List<RadioStreamEntity> radios, List<SpotifySourceEntity> spotifySources, JsonNode? snapcastStatus);
}

public class ReconciliationService : IReconciliationService, IDisposable
{
    private readonly IDockerClient _dockerClient;
    private readonly ILogger<ReconciliationService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IContainerLifecycleService _containerLifecycle;
    private readonly IFifoService _fifoService;
    private readonly ISnapcastService _snapcast;
    private readonly IConfiguration? _configuration;
    private readonly SemaphoreSlim _reconcileLock = new(1, 1);

    public ReconciliationService(
        IDockerClient dockerClient,
        ILogger<ReconciliationService> logger,
        IServiceProvider serviceProvider,
        IContainerLifecycleService containerLifecycle,
        IFifoService fifoService,
        ISnapcastService snapcast,
        IConfiguration? configuration = null)
    {
        _dockerClient = dockerClient;
        _logger = logger;
        _serviceProvider = serviceProvider;
        _containerLifecycle = containerLifecycle;
        _fifoService = fifoService;
        _snapcast = snapcast;
        _configuration = configuration;
    }

    public void Dispose()
    {
        _reconcileLock.Dispose();
    }

    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        if (!await _reconcileLock.WaitAsync(0, ct))
        {
            _logger.LogInformation("Reconciliation already in progress, skipping.");
            return;
        }

        try
        {
            await ReconcileDockerOnStartupAsync();

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            
            var groups = await db.Groups.ToListAsync(ct);
            var radios = await db.RadioStreams.ToListAsync(ct);
            var spotifySources = await db.SpotifySources.ToListAsync(ct);
            var status = await _snapcast.GetStatusAsync();
            
            await ReconcileSnapcastAsync(groups, radios, spotifySources, status);
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    public async Task ReconcileDockerOnStartupAsync()
    {
        _logger.LogInformation("Reconciling orchestrated containers...");

        var containers = await _dockerClient.Containers.ListContainersAsync(new ContainersListParameters { All = true });
        
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var radios = await db.RadioStreams.ToListAsync();
        var spotifySources = await db.SpotifySources.ToListAsync();

        var validContainerNames = new HashSet<string>();
        
        // 1. Reconcile Spotify Sources (Ensure FIFOs exist and containers are up)
        foreach (var s in spotifySources)
        {
            var containerName = $"{OrchestratorConstants.SpotifyContainerPrefix}{s.Id}";
            validContainerNames.Add($"/{containerName}");
            
            // Reset connection/activity state on startup reconciliation
            s.IsConnected = false;
            s.IsActive = false;
            s.CurrentTitle = null;
            s.CurrentArtist = null;
            s.CurrentCoverUrl = null;
            s.CurrentAlbum = null;
            s.ContextUri = null;
            s.ContextName = null;
            s.ErrorMessage = null;

            var streamId = $"{OrchestratorConstants.SpotifyPrefix}{s.Id}";
            var fifoPath = $"{OrchestratorConstants.FifoBaseDir}/{streamId}";
            await _containerLifecycle.SpawnLibreSpotAsync(s.Id, s.Name, fifoPath, s.MaxVolume, containers, string.IsNullOrWhiteSpace(s.CurrentVersion) ? null : s.CurrentVersion);
        }

        // 2. Reconcile Radio Streams (Ensure all are running and FIFOs exist)
        foreach (var r in radios)
        {
            var containerName = $"{OrchestratorConstants.RadioContainerPrefix}{r.Id}";
            validContainerNames.Add($"/{containerName}");
            
            // Clear stale metadata on startup
            r.CurrentTitle = null;
            r.CurrentArtist = null;
            r.CurrentCoverUrl = null;
            r.ErrorMessage = null;

            var streamId = $"{OrchestratorConstants.RadioPrefix}{r.Id}";
            var fifoPath = $"{OrchestratorConstants.FifoBaseDir}/{streamId}";
            await _containerLifecycle.SpawnRadioStreamAsync(r, fifoPath, containers);
        }
        await db.SaveChangesAsync();

        // Broadcast the cleared metadata/reset state to all clients
        var hub = scope.ServiceProvider.GetRequiredService<IHubContext<AudioHub, IAudioClient>>();
        var stateBuilder = scope.ServiceProvider.GetRequiredService<FullStateBuilder>();
        await hub.Clients.All.OnFullStateUpdate(await stateBuilder.BuildAsync());

        foreach (var c in containers)
        {
            var name = c.Names.FirstOrDefault();
            if (name == null || !name.StartsWith("/" + OrchestratorConstants.ContainerPrefix)) continue;
            if (validContainerNames.Contains(name)) continue;

            _logger.LogInformation("Found orphaned container {Name}, removing...", name);
            await _containerLifecycle.StopAndRemoveContainerAsync(name.TrimStart('/'));
        }

        var validFifoPaths = new List<string>();
        foreach (var s in spotifySources) validFifoPaths.Add($"{OrchestratorConstants.FifoBaseDir}/{OrchestratorConstants.SpotifyPrefix}{s.Id}");
        foreach (var r in radios) validFifoPaths.Add($"{OrchestratorConstants.FifoBaseDir}/{OrchestratorConstants.RadioPrefix}{r.Id}");
        
        await _fifoService.CleanupStaleFifosAsync(validFifoPaths);
    }

    public async Task ReconcileSnapcastAsync(List<GroupEntity> groups, List<RadioStreamEntity> radios, List<SpotifySourceEntity> spotifySources, JsonNode? snapcastStatus)
    {
        var snapGroups = snapcastStatus?["server"]?["groups"]?.AsArray();
        var snapStreams = snapcastStatus?["server"]?["streams"]?.AsArray();

        if (snapStreams != null)
        {
            // 1. Reconcile Radio Streams
            foreach (var radio in radios)
            {
                var streamId = $"{OrchestratorConstants.RadioPrefix}{radio.Id}";
                if (!snapStreams.Any(s => s?["id"]?.GetValue<string>() == streamId))
                {
                    var fifoPath = $"{OrchestratorConstants.FifoBaseDir}/{streamId}";
                    var streamUri = $"pipe://{fifoPath}?name={streamId}&sampleformat=44100:16:2&idle_threshold_ms=5000";
                    try { await _snapcast.AddStreamAsync(streamUri); } catch { /* Already exists? */ }
                }
            }

            // 1b. Reconcile Spotify Sources
            foreach (var spotify in spotifySources)
            {
                var streamId = $"{OrchestratorConstants.SpotifyPrefix}{spotify.Id}";
                if (!snapStreams.Any(s => s?["id"]?.GetValue<string>() == streamId))
                {
                    var fifoPath = $"{OrchestratorConstants.FifoBaseDir}/{streamId}";
                    var streamUri = $"pipe://{fifoPath}?name={streamId}&sampleformat=44100:16:2&idle_threshold_ms=5000&chunk_ms=20";
                    try { await _snapcast.AddStreamAsync(streamUri); } catch { /* Already exists? */ }
                }
            }
        }

        if (snapGroups == null) return;

        var claimedSnapIds = new HashSet<string>();
        bool dbChanged = false;
        
        // First pass: Match by SnapcastGroupId
        foreach (var group in groups)
        {
            if (string.IsNullOrEmpty(group.SnapcastGroupId)) continue;
            var match = snapGroups.FirstOrDefault(sg => 
                sg?["id"]?.GetValue<string>() == group.SnapcastGroupId && 
                !claimedSnapIds.Contains(group.SnapcastGroupId!));

            if (match != null) {
                claimedSnapIds.Add(group.SnapcastGroupId!);
                if (match?["name"]?.GetValue<string>() != group.Name) {
                    await _snapcast.SetGroupNameAsync(group.SnapcastGroupId!, group.Name);
                }
                if (!string.IsNullOrEmpty(group.CurrentStreamId) && match?["stream_id"]?.GetValue<string>() != group.CurrentStreamId) {
                    try { await _snapcast.SetGroupStreamAsync(group.SnapcastGroupId!, group.CurrentStreamId); } catch {}
                }
            } else {
                group.SnapcastGroupId = null;
                dbChanged = true;
            }
        }

        // Second pass: Match orphaned DB groups by Name or Stream (Self-Healing)
        foreach (var group in groups.Where(g => string.IsNullOrEmpty(g.SnapcastGroupId) || !claimedSnapIds.Contains(g.SnapcastGroupId!)))
        {
            var matchingSnapGroup = snapGroups.FirstOrDefault(sg => {
                var sid = sg?["id"]?.GetValue<string>();
                if (sid == null || claimedSnapIds.Contains(sid)) return false;
                if (!string.IsNullOrEmpty(group.Name) && sg?["name"]?.GetValue<string>() == group.Name) return true;
                if (!string.IsNullOrEmpty(group.CurrentStreamId) && 
                    group.CurrentStreamId != "default" && 
                    sg?["stream_id"]?.GetValue<string>() == group.CurrentStreamId) 
                {
                    var otherPossible = snapGroups.Count(s => s?["stream_id"]?.GetValue<string>() == group.CurrentStreamId);
                    return otherPossible == 1; 
                }
                return false;
            });

            if (matchingSnapGroup != null)
            {
                var actualSnapId = matchingSnapGroup["id"]?.GetValue<string>();
                if (actualSnapId != null)
                {
                    group.SnapcastGroupId = actualSnapId;
                    claimedSnapIds.Add(actualSnapId);
                    dbChanged = true;
                    
                    await _snapcast.SetGroupNameAsync(actualSnapId, group.Name);
                    if (!string.IsNullOrEmpty(group.CurrentStreamId)) {
                         try { await _snapcast.SetGroupStreamAsync(actualSnapId, group.CurrentStreamId); } catch {}
                    }
                }
            }
        }

        // Third pass: Silence Orphans
        foreach (var sg in snapGroups)
        {
            var sid = sg?["id"]?.GetValue<string>();
            if (sid == null || claimedSnapIds.Contains(sid)) continue;

            if (sg?["stream_id"]?.GetValue<string>() != "default" || !string.IsNullOrEmpty(sg?["name"]?.GetValue<string>()))
            {
                try {
                    await _snapcast.SetGroupNameAsync(sid, "");
                    await _snapcast.SetGroupStreamAsync(sid, "default");
                } catch (Exception ex) {
                    _logger.LogWarning("Failed to silence orphan group {Sid}: {Message}", sid, ex.Message);
                }
            }
        }

        if (!dbChanged) return;

        using var saveScope = _serviceProvider.CreateScope();
        var saveDb = saveScope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var groupIds = groups.Select(g => g.Id).ToList();
        var dbGroups = await saveDb.Groups.Where(g => groupIds.Contains(g.Id)).ToListAsync();
        foreach (var g in groups)
        {
            var target = dbGroups.FirstOrDefault(x => x.Id == g.Id);
            if (target != null && target.SnapcastGroupId != g.SnapcastGroupId)
            {
                target.SnapcastGroupId = g.SnapcastGroupId;
            }
        }
        await saveDb.SaveChangesAsync();
    }
}
