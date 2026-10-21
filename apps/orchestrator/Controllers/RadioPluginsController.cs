using AudioOrchestrator.Contracts;
using AudioOrchestrator.Data;
using AudioOrchestrator.Data.Entities;
using AudioOrchestrator.Hubs;
using AudioOrchestrator.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AudioOrchestrator.Controllers;

[ApiController]
[Route("api/radio-plugins")]
public class RadioPluginsController : ControllerBase
{
    private readonly OrchestratorDbContext _db;
    private readonly IHubContext<AudioHub, IAudioClient> _hub;
    private readonly FullStateBuilder _stateBuilder;
    private readonly INodeReleaseService _releaseService;
    private readonly IContainerLifecycleService _containerLifecycle;
    private readonly ILogger<RadioPluginsController> _logger;

    public RadioPluginsController(
        OrchestratorDbContext db,
        IHubContext<AudioHub, IAudioClient> hub,
        FullStateBuilder stateBuilder,
        INodeReleaseService releaseService,
        IContainerLifecycleService containerLifecycle,
        ILogger<RadioPluginsController> logger)
    {
        _db = db;
        _hub = hub;
        _stateBuilder = stateBuilder;
        _releaseService = releaseService;
        _containerLifecycle = containerLifecycle;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<List<RadioPluginDto>>> GetPlugins()
    {
        var entities = await _db.RadioPlugins.AsNoTracking().OrderBy(p => p.Name).ToListAsync();
        var dtos = new List<RadioPluginDto>();
        foreach (var p in entities)
        {
            var curVer = p.GetEffectiveVersion();
            string? latest = null;
            bool hasUpdate = false;
            var repo = p.GetGitHubRepository();
            if (!string.IsNullOrEmpty(repo))
            {
                latest = await _releaseService.GetLatestCompatibleVersionAsync(repo, curVer);
                hasUpdate = !string.IsNullOrEmpty(latest) && latest != curVer;
            }

            dtos.Add(new RadioPluginDto(
                p.Id,
                p.Name,
                p.DockerImage,
                p.GetUrlPatterns(),
                p.Enabled,
                p.CreatedAt,
                curVer,
                latest,
                hasUpdate,
                p.GitHubRepository
            ));
        }
        return Ok(dtos);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<RadioPluginDto>> GetPlugin(string id)
    {
        var p = await _db.RadioPlugins.FindAsync(id);
        if (p == null) return NotFound();

        var curVer = p.GetEffectiveVersion();
        string? latest = null;
        bool hasUpdate = false;
        var repo = p.GetGitHubRepository();
        if (!string.IsNullOrEmpty(repo))
        {
            latest = await _releaseService.GetLatestCompatibleVersionAsync(repo, curVer);
            hasUpdate = !string.IsNullOrEmpty(latest) && latest != curVer;
        }

        return Ok(new RadioPluginDto(
            p.Id,
            p.Name,
            p.DockerImage,
            p.GetUrlPatterns(),
            p.Enabled,
            p.CreatedAt,
            curVer,
            latest,
            hasUpdate,
            p.GitHubRepository
        ));
    }

    [HttpPost]
    public async Task<ActionResult<RadioPluginDto>> CreatePlugin([FromBody] RadioPluginCreateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.DockerImage))
        {
            return BadRequest("Id, Name and DockerImage are required.");
        }

        foreach (var pattern in request.UrlPatterns ?? [])
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            try
            {
                _ = new System.Text.RegularExpressions.Regex(pattern);
            }
            catch (ArgumentException ex)
            {
                return BadRequest($"Invalid regex pattern '{pattern}': {ex.Message}");
            }
        }

        var existing = await _db.RadioPlugins.FindAsync(request.Id);
        if (existing != null)
        {
            return Conflict($"Plugin with ID '{request.Id}' already exists.");
        }

        var entity = new RadioPluginEntity
        {
            Id = request.Id.Trim().ToLowerInvariant(),
            Name = request.Name.Trim(),
            DockerImage = request.DockerImage.Trim(),
            GitHubRepository = string.IsNullOrWhiteSpace(request.GitHubRepository) ? null : request.GitHubRepository.Trim(),
            Enabled = request.Enabled,
            CreatedAt = DateTime.UtcNow
        };
        entity.SetUrlPatterns(request.UrlPatterns ?? new List<string>());

        _db.RadioPlugins.Add(entity);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Created radio plugin {PluginId} ({Name}) with image {Image}", entity.Id, entity.Name, entity.DockerImage);

        await _hub.Clients.All.OnFullStateUpdate(await _stateBuilder.BuildAsync());

        return CreatedAtAction(nameof(GetPlugin), new { id = entity.Id }, new RadioPluginDto(
            entity.Id,
            entity.Name,
            entity.DockerImage,
            entity.GetUrlPatterns(),
            entity.Enabled,
            entity.CreatedAt,
            CurrentVersion: entity.GetEffectiveVersion(),
            GitHubRepository: entity.GitHubRepository
        ));
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<RadioPluginDto>> UpdatePlugin(string id, [FromBody] RadioPluginUpdateRequest request)
    {
        var entity = await _db.RadioPlugins.FindAsync(id);
        if (entity == null) return NotFound();

        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.DockerImage))
        {
            return BadRequest("Name and DockerImage are required.");
        }

        foreach (var pattern in request.UrlPatterns ?? [])
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            try
            {
                _ = new System.Text.RegularExpressions.Regex(pattern);
            }
            catch (ArgumentException ex)
            {
                return BadRequest($"Invalid regex pattern '{pattern}': {ex.Message}");
            }
        }

        entity.Name = request.Name.Trim();
        entity.DockerImage = request.DockerImage.Trim();
        entity.GitHubRepository = string.IsNullOrWhiteSpace(request.GitHubRepository) ? null : request.GitHubRepository.Trim();
        entity.Enabled = request.Enabled;
        entity.SetUrlPatterns(request.UrlPatterns ?? new List<string>());

        await _db.SaveChangesAsync();

        _logger.LogInformation("Updated radio plugin {PluginId} ({Name}) with image {Image}", entity.Id, entity.Name, entity.DockerImage);

        await _hub.Clients.All.OnFullStateUpdate(await _stateBuilder.BuildAsync());

        return Ok(new RadioPluginDto(
            entity.Id,
            entity.Name,
            entity.DockerImage,
            entity.GetUrlPatterns(),
            entity.Enabled,
            entity.CreatedAt,
            CurrentVersion: entity.GetEffectiveVersion(),
            GitHubRepository: entity.GitHubRepository
        ));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeletePlugin(string id)
    {
        var entity = await _db.RadioPlugins.FindAsync(id);
        if (entity == null) return NotFound();

        _db.RadioPlugins.Remove(entity);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Deleted radio plugin {PluginId}", id);

        await _hub.Clients.All.OnFullStateUpdate(await _stateBuilder.BuildAsync());

        return NoContent();
    }

    [HttpGet("{id}/releases")]
    public async Task<ActionResult<IReadOnlyList<NodeReleaseDto>>> GetPluginReleases(string id, [FromQuery] string? targetVersion = null)
    {
        var plugin = await _db.RadioPlugins.FindAsync(id);
        if (plugin == null) return NotFound();

        var repo = plugin.GetGitHubRepository();
        if (string.IsNullOrWhiteSpace(repo))
        {
            return Ok(Array.Empty<NodeReleaseDto>());
        }

        var history = await _releaseService.GetReleaseHistoryAsync(repo, plugin.GetEffectiveVersion(), targetVersion);
        return Ok(history);
    }

    [HttpPost("{id}/upgrade")]
    public async Task<IActionResult> UpgradePlugin(string id, [FromBody] NodeUpgradeRequest? req = null)
    {
        var plugin = await _db.RadioPlugins.FindAsync(id);
        if (plugin == null) return NotFound();

        var targetVersion = req?.TargetVersion;
        if (string.IsNullOrWhiteSpace(targetVersion))
        {
            var repo = plugin.GetGitHubRepository();
            if (string.IsNullOrWhiteSpace(repo))
            {
                return BadRequest("No GitHub repository could be inferred for this plugin.");
            }

            targetVersion = await _releaseService.GetLatestCompatibleVersionAsync(repo, plugin.GetEffectiveVersion());
            if (string.IsNullOrWhiteSpace(targetVersion))
            {
                return BadRequest("No compatible upgrade version available for this plugin.");
            }
        }

        try
        {
            await _containerLifecycle.UpgradeRadioPluginAsync(id, targetVersion);
            return Ok(new { message = $"Plugin upgraded to {targetVersion}", version = targetVersion });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upgrade Radio plugin {PluginId} to {TargetVersion}", id, targetVersion);
            return StatusCode(500, $"Failed to upgrade Radio plugin: {ex.Message}");
        }
    }
}
