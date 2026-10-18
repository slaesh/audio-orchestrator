using Microsoft.AspNetCore.Mvc;
using AudioOrchestrator.Services;
using AudioOrchestrator.Data;
using AudioOrchestrator.Data.Entities;
using AudioOrchestrator.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using AudioOrchestrator.Hubs;

namespace AudioOrchestrator.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SpotifySourcesController : ControllerBase
{
    private readonly IContainerLifecycleService _containerLifecycle;
    private readonly ILibrespotApiClient _librespotApi;
    private readonly ISnapcastService _snapcast;
    private readonly OrchestratorDbContext _db;
    private readonly ILogger<SpotifySourcesController> _logger;
    private readonly IHubContext<AudioHub, IAudioClient> _hub;
    private readonly FullStateBuilder _stateBuilder;

    public SpotifySourcesController(
        IContainerLifecycleService containerLifecycle,
        ILibrespotApiClient librespotApi,
        ISnapcastService snapcast,
        OrchestratorDbContext db,
        ILogger<SpotifySourcesController> logger,
        IHubContext<AudioHub, IAudioClient> hub,
        FullStateBuilder stateBuilder)
    {
        _containerLifecycle = containerLifecycle;
        _librespotApi = librespotApi;
        _snapcast = snapcast;
        _db = db;
        _logger = logger;
        _hub = hub;
        _stateBuilder = stateBuilder;
    }

    [HttpGet]
    public async Task<IActionResult> GetSources()
    {
        return Ok(await _db.SpotifySources.AsNoTracking().ToListAsync());
    }

    [HttpGet("metadata/{trackId}")]
    public async Task<ActionResult<SpotifyMetadataDto>> GetMetadata(string trackId)
    {
        var meta = await _db.SpotifyMetadata.FindAsync(trackId);
        if (meta == null) return NotFound();

        Response.Headers.CacheControl = "public, max-age=259200";
        return new SpotifyMetadataDto(
            meta.TrackId,
            meta.Title,
            meta.Artist,
            meta.Album,
            meta.DurationMs,
            meta.CoverUrl
        );
    }

    [HttpPost]
    public async Task<IActionResult> CreateSource([FromBody] CreateSourceRequest req)
    {
        var source = new SpotifySourceEntity { Name = req.Name, MaxVolume = req.MaxVolume };
        _db.SpotifySources.Add(source);
        await _db.SaveChangesAsync();

        try
        {
            var streamId = $"{OrchestratorConstants.SpotifyPrefix}{source.Id}";
            var fifoPath = $"{OrchestratorConstants.FifoBaseDir}/{streamId}";
            
            await _containerLifecycle.SpawnLibreSpotAsync(source.Id, source.Name, fifoPath, source.MaxVolume);

            var streamUri = $"pipe://{fifoPath}?name={streamId}&sampleformat=44100:16:2&idle_threshold_ms=5000&chunk_ms=20";
            await _snapcast.AddStreamAsync(streamUri);

            await _hub.Clients.All.OnFullStateUpdate(await _stateBuilder.BuildAsync());
            return Ok(source);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create Spotify source");
            await _containerLifecycle.StopAndRemoveContainerAsync($"{OrchestratorConstants.SpotifyContainerPrefix}{source.Id}");
            _db.SpotifySources.Remove(source);
            await _db.SaveChangesAsync();
            return StatusCode(500, ex.Message);
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteSource(string id)
    {
        var source = await _db.SpotifySources.FindAsync(id);
        if (source == null) return NotFound();

        var streamId = $"{OrchestratorConstants.SpotifyPrefix}{id}";
        
        await _containerLifecycle.StopAndRemoveContainerAsync($"{OrchestratorConstants.SpotifyContainerPrefix}{id}");
        try { await _snapcast.RemoveStreamAsync(streamId); } catch { }

        // Update any groups listening to this source back to default
        var listeningGroups = await _db.Groups.Where(g => g.CurrentStreamId == streamId).ToListAsync();
        foreach (var g in listeningGroups)
        {
            g.CurrentStreamId = "default";
            if (!string.IsNullOrEmpty(g.SnapcastGroupId))
            {
                try { await _snapcast.SetGroupStreamAsync(g.SnapcastGroupId, "default"); } catch { }
            }
        }

        _db.SpotifySources.Remove(source);
        await _db.SaveChangesAsync();

        var fifoPath = $"{OrchestratorConstants.FifoBaseDir}/{streamId}";
        if (System.IO.File.Exists(fifoPath))
        {
            try { System.IO.File.Delete(fifoPath); } catch { }
        }

        await _hub.Clients.All.OnFullStateUpdate(await _stateBuilder.BuildAsync());
        return NoContent();
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateSource(string id, [FromBody] UpdateSourceRequest req)
    {
        var source = await _db.SpotifySources.FindAsync(id);
        if (source == null) return NotFound();

        source.Name = req.Name;
        source.MaxVolume = req.MaxVolume;
        await _db.SaveChangesAsync();

        // Recreate container to update settings (Connect name, initial volume, etc.)
        var streamId = $"{OrchestratorConstants.SpotifyPrefix}{source.Id}";
        var fifoPath = $"{OrchestratorConstants.FifoBaseDir}/{streamId}";
        await _containerLifecycle.SpawnLibreSpotAsync(source.Id, source.Name, fifoPath, source.MaxVolume);

        await _hub.Clients.All.OnFullStateUpdate(await _stateBuilder.BuildAsync());
        return Ok(source);
    }
    [HttpPost("{id}/restart")]
    public async Task<IActionResult> RestartSource(string id)
    {
        var source = await _db.SpotifySources.FindAsync(id);
        if (source == null) return NotFound();

        _logger.LogInformation($"Forced restart of Spotify source {source.Name} ({id}) requested");
        var streamId = $"{OrchestratorConstants.SpotifyPrefix}{source.Id}";
        var fifoPath = $"{OrchestratorConstants.FifoBaseDir}/{streamId}";
        
        await _containerLifecycle.StopAndRemoveContainerAsync($"{OrchestratorConstants.SpotifyContainerPrefix}{id}");
        await _containerLifecycle.SpawnLibreSpotAsync(source.Id, source.Name, fifoPath, source.MaxVolume);

        await _hub.Clients.All.OnFullStateUpdate(await _stateBuilder.BuildAsync());
        return Ok();
    }

    [HttpGet("{id}/status")]
    public async Task<IActionResult> GetNodeStatus(string id)
    {
        var source = await _db.SpotifySources.FindAsync(id);
        if (source == null) return NotFound();

        try
        {
            var status = await _librespotApi.GetStatusAsync(id);
            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query status from spotify-node for source {SourceId}", id);
            return StatusCode(502, "Failed to communicate with Spotify node.");
        }
    }

    [HttpPost("{id}/pause")]
    public async Task<IActionResult> Pause(string id)
    {
        try
        {
            await _librespotApi.SendCommandAsync(id, "pause");
            return Ok();
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is AudioOrchestrator.Services.Generated.ApiException)
        {
            _logger.LogWarning(ex, "Failed to send pause command to spotify-node for source {SourceId}", id);
            return StatusCode(502, "Failed to communicate with Spotify node.");
        }
    }

    [HttpPost("{id}/resume")]
    public async Task<IActionResult> Resume(string id)
    {
        try
        {
            await _librespotApi.SendCommandAsync(id, "resume");
            return Ok();
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is AudioOrchestrator.Services.Generated.ApiException)
        {
            _logger.LogWarning(ex, "Failed to send resume command to spotify-node for source {SourceId}", id);
            return StatusCode(502, "Failed to communicate with Spotify node.");
        }
    }

    [HttpPost("{id}/next")]
    public async Task<IActionResult> Next(string id)
    {
        try
        {
            await _librespotApi.SendCommandAsync(id, "next");
            return Ok();
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is AudioOrchestrator.Services.Generated.ApiException)
        {
            _logger.LogWarning(ex, "Failed to send next command to spotify-node for source {SourceId}", id);
            return StatusCode(502, "Failed to communicate with Spotify node.");
        }
    }

    [HttpPost("{id}/previous")]
    public async Task<IActionResult> Previous(string id)
    {
        try
        {
            await _librespotApi.SendCommandAsync(id, "previous");
            return Ok();
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is AudioOrchestrator.Services.Generated.ApiException)
        {
            _logger.LogWarning(ex, "Failed to send previous command to spotify-node for source {SourceId}", id);
            return StatusCode(502, "Failed to communicate with Spotify node.");
        }
    }

    [HttpPost("{id}/play-context")]
    public async Task<IActionResult> PlayContext(string id, [FromBody] PlayContextRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.ContextUri)) return BadRequest("ContextUri required");
        try
        {
            await _librespotApi.PlayContextAsync(id, request.ContextUri);
            return Ok();
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is AudioOrchestrator.Services.Generated.ApiException)
        {
            _logger.LogWarning(ex, "Failed to send play-context to spotify-node for source {SourceId}. Node container might need rebuilding.", id);
            return StatusCode(502, "Failed to communicate with Spotify node (please ensure the container image is updated).");
        }
    }
}

public class CreateSourceRequest { public string Name { get; set; } = string.Empty; public int MaxVolume { get; set; } = 30; }
public class UpdateSourceRequest { public string Name { get; set; } = string.Empty; public int MaxVolume { get; set; } = 30; }
public class PlayContextRequest { public string ContextUri { get; set; } = string.Empty; }
