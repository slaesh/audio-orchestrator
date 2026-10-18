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
    private readonly ILogger<RadioPluginsController> _logger;

    public RadioPluginsController(
        OrchestratorDbContext db,
        IHubContext<AudioHub, IAudioClient> hub,
        FullStateBuilder stateBuilder,
        ILogger<RadioPluginsController> logger)
    {
        _db = db;
        _hub = hub;
        _stateBuilder = stateBuilder;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<List<RadioPluginDto>>> GetPlugins()
    {
        var entities = await _db.RadioPlugins.AsNoTracking().OrderBy(p => p.Name).ToListAsync();
        var dtos = entities.Select(p => new RadioPluginDto(
            p.Id,
            p.Name,
            p.DockerImage,
            p.GetUrlPatterns(),
            p.Enabled,
            p.CreatedAt
        )).ToList();
        return Ok(dtos);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<RadioPluginDto>> GetPlugin(string id)
    {
        var p = await _db.RadioPlugins.FindAsync(id);
        if (p == null) return NotFound();

        return Ok(new RadioPluginDto(
            p.Id,
            p.Name,
            p.DockerImage,
            p.GetUrlPatterns(),
            p.Enabled,
            p.CreatedAt
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
            entity.CreatedAt
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
            entity.CreatedAt
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
}
