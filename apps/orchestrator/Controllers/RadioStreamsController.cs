using Microsoft.AspNetCore.Mvc;
using AudioOrchestrator.Services;
using AudioOrchestrator.Data;
using Microsoft.EntityFrameworkCore;
using AudioOrchestrator.Data.Entities;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using AudioOrchestrator.Hubs;
using AudioOrchestrator.Contracts;

namespace AudioOrchestrator.Controllers;

[ApiController]
[Route("api/streams/radio")]
public class RadioStreamsController : ControllerBase
{
    private readonly IContainerLifecycleService _containerLifecycle;
    private readonly ISnapcastService _snapcast;
    private readonly OrchestratorDbContext _db;
    private readonly ILogger<RadioStreamsController> _logger;
    private readonly IHubContext<AudioHub, IAudioClient> _hub;
    private readonly FullStateBuilder _stateBuilder;

    public RadioStreamsController(
        IContainerLifecycleService containerLifecycle, 
        ISnapcastService snapcast, 
        OrchestratorDbContext db, 
        ILogger<RadioStreamsController> logger,
        IHubContext<AudioHub, IAudioClient> hub,
        FullStateBuilder stateBuilder)
    {
        _containerLifecycle = containerLifecycle;
        _snapcast = snapcast;
        _db = db;
        _logger = logger;
        _hub = hub;
        _stateBuilder = stateBuilder;
    }

    [HttpGet]
    public async Task<IActionResult> GetStreams()
    {
        return Ok(await _db.RadioStreams.AsNoTracking().ToListAsync());
    }

    [HttpPost]
    public async Task<IActionResult> CreateStream([FromBody] CreateRadioStreamRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Name) || string.IsNullOrWhiteSpace(request?.Url))
        {
            return BadRequest("Name and Url are required.");
        }

        var radio = new RadioStreamEntity 
        { 
            Name = request.Name.Trim(), 
            StreamUrl = request.Url.Trim()
        };
        _db.RadioStreams.Add(radio);
        await _db.SaveChangesAsync();

        var fifoPath = $"{OrchestratorConstants.FifoBaseDir}/{OrchestratorConstants.RadioPrefix}{radio.Id}";
        await _containerLifecycle.SpawnRadioStreamAsync(radio, fifoPath);

        var streamUri = $"pipe://{fifoPath}?name={OrchestratorConstants.RadioPrefix}{radio.Id}&sampleformat=44100:16:2&idle_threshold_ms=5000";
        await _snapcast.AddStreamAsync(streamUri);

        await _hub.Clients.All.OnFullStateUpdate(await _stateBuilder.BuildAsync());
        return Ok(radio);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteStream(string id)
    {
        var guidId = id.StartsWith(OrchestratorConstants.RadioPrefix) ? id.Substring(OrchestratorConstants.RadioPrefix.Length) : id;
        var radio = await _db.RadioStreams.FindAsync(guidId);
        if (radio == null) return NotFound();

        await _containerLifecycle.StopAndRemoveContainerAsync($"{OrchestratorConstants.RadioContainerPrefix}{guidId}");
        
        try {
            var status = await _snapcast.GetStatusAsync();
            var streams = status?["server"]?["streams"]?.AsArray();
            var targetStreamId = $"{OrchestratorConstants.RadioPrefix}{radio.Id}";
            var existingStream = streams?.FirstOrDefault(s => s?["id"]?.GetValue<string>() == targetStreamId);
            if (existingStream != null) {
                _logger.LogInformation($"Removing Snapserver stream {targetStreamId} for deleted Radio {radio.Name}");
                await _snapcast.RemoveStreamAsync(targetStreamId);
            }
        } catch (Exception ex) {
            _logger.LogWarning($"Failed to remove Snapserver stream during radio deletion: {ex.Message}");
        }

        var fifoPath = $"{OrchestratorConstants.FifoBaseDir}/{OrchestratorConstants.RadioPrefix}{guidId}";
        if (System.IO.File.Exists(fifoPath)) System.IO.File.Delete(fifoPath);

        _db.RadioStreams.Remove(radio);
        await _db.SaveChangesAsync();
        await _hub.Clients.All.OnFullStateUpdate(await _stateBuilder.BuildAsync());
        return NoContent();
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateStream(string id, [FromBody] CreateRadioStreamRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Name) || string.IsNullOrWhiteSpace(request?.Url))
        {
            return BadRequest("Name and Url are required.");
        }

        var guidId = id.StartsWith(OrchestratorConstants.RadioPrefix) ? id.Substring(OrchestratorConstants.RadioPrefix.Length) : id;
        var radio = await _db.RadioStreams.FindAsync(guidId);
        if (radio == null) return NotFound();

        var trimmedName = request.Name.Trim();
        var trimmedUrl = request.Url.Trim();
        bool urlChanged = radio.StreamUrl != trimmedUrl;
        
        radio.Name = trimmedName;
        radio.StreamUrl = trimmedUrl;
        await _db.SaveChangesAsync();

        if (urlChanged)
        {
            _logger.LogInformation($"Radio {id} URL changed. Restarting container...");
            var fifoPath = $"{OrchestratorConstants.FifoBaseDir}/{OrchestratorConstants.RadioPrefix}{radio.Id}";
            await _containerLifecycle.StopAndRemoveContainerAsync($"{OrchestratorConstants.RadioContainerPrefix}{guidId}");
            await _containerLifecycle.SpawnRadioStreamAsync(radio, fifoPath);
        }
        await _hub.Clients.All.OnFullStateUpdate(await _stateBuilder.BuildAsync());
        return Ok(radio);
    }

    [HttpPost("{id}/restart")]
    public async Task<IActionResult> RestartStream(string id)
    {
        var guidId = id.StartsWith(OrchestratorConstants.RadioPrefix) ? id.Substring(OrchestratorConstants.RadioPrefix.Length) : id;
        var radio = await _db.RadioStreams.FindAsync(guidId);
        if (radio == null) return NotFound();

        _logger.LogInformation($"Forced restart of Radio {radio.Name} ({id}) requested");
        var fifoPath = $"{OrchestratorConstants.FifoBaseDir}/{OrchestratorConstants.RadioPrefix}{radio.Id}";
        await _containerLifecycle.StopAndRemoveContainerAsync($"{OrchestratorConstants.RadioContainerPrefix}{guidId}");
        radio.ErrorMessage = null;
        await _db.SaveChangesAsync();

        await _containerLifecycle.SpawnRadioStreamAsync(radio, fifoPath);

        await _hub.Clients.All.OnFullStateUpdate(await _stateBuilder.BuildAsync());
        return Ok();
    }
}

public class CreateRadioStreamRequest 
{ 
    public string Name { get; set; } = string.Empty; 
    public string Url { get; set; } = string.Empty; 
}
