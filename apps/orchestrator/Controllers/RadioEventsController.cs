using AudioOrchestrator.Contracts;
using AudioOrchestrator.Services;
using Microsoft.AspNetCore.Mvc;

namespace AudioOrchestrator.Controllers;

[ApiController]
[Route("api/internal/radio-events")]
public class RadioEventsController : ControllerBase
{
    private readonly RadioMetadataService _metadataService;
    private readonly ILogger<RadioEventsController> _logger;

    public RadioEventsController(RadioMetadataService metadataService, ILogger<RadioEventsController> logger)
    {
        _metadataService = metadataService;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> HandleEvent([FromBody] RadioEventPayload payload)
    {
        if (payload == null || string.IsNullOrWhiteSpace(payload.StreamId))
        {
            return BadRequest("StreamId is required");
        }

        _logger.LogDebug("Received radio event for stream {StreamId}: {Artist} - {Title}", payload.StreamId, payload.Artist, payload.Title);
        await _metadataService.ProcessRadioEventAsync(payload);
        return Ok();
    }
}
