using AudioOrchestrator.Contracts;
using AudioOrchestrator.Services;
using Microsoft.AspNetCore.Mvc;

namespace AudioOrchestrator.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BroadcastController : ControllerBase
{
    private readonly IBroadcastService _broadcastService;
    private readonly ILogger<BroadcastController> _logger;

    public BroadcastController(
        IBroadcastService broadcastService,
        ILogger<BroadcastController> logger)
    {
        _broadcastService = broadcastService;
        _logger = logger;
    }

    [HttpGet("status")]
    public ActionResult<BroadcastStatusDto> GetStatus()
    {
        return Ok(_broadcastService.GetStatus());
    }

    [HttpPost("play")]
    public async Task<ActionResult<BroadcastPlayResult>> Play([FromBody] BroadcastPlayRequest request)
    {
        if (request == null)
        {
            return BadRequest(new BroadcastPlayResult(false, "Ungültige Anfrage."));
        }

        var result = await _broadcastService.PlayUrlAsync(request, HttpContext?.RequestAborted ?? default);
        if (!result.Success)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }

    [HttpPost("play-file")]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<BroadcastPlayResult>> PlayFile(
        [FromForm] IFormFile? file,
        [FromForm] string? groupIds = null,
        [FromForm] string? roomIds = null,
        [FromForm] int? volume = null,
        [FromForm] int? fadeDurationMs = null,
        [FromForm] bool pausePlayback = false,
        [FromForm] int? timeoutSeconds = 60,
        [FromForm] bool interrupt = true)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new BroadcastPlayResult(false, "Keine Audiodatei hochgeladen."));
        }

        var parsedGroupIds = !string.IsNullOrWhiteSpace(groupIds)
            ? groupIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : null;

        var parsedRoomIds = !string.IsNullOrWhiteSpace(roomIds)
            ? roomIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : null;

        var request = new BroadcastPlayRequest(
            AudioUrl: null,
            GroupIds: parsedGroupIds,
            RoomIds: parsedRoomIds,
            Volume: volume,
            FadeDurationMs: fadeDurationMs,
            PausePlayback: pausePlayback,
            TimeoutSeconds: timeoutSeconds,
            Interrupt: interrupt
        );

        var ext = Path.GetExtension(file.FileName);
        using var stream = file.OpenReadStream();
        var result = await _broadcastService.PlayFileAsync(stream, ext, request, HttpContext?.RequestAborted ?? default);

        if (!result.Success)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }

    [HttpPost("stop")]
    public async Task<IActionResult> Stop()
    {
        await _broadcastService.StopAsync(HttpContext?.RequestAborted ?? default);
        return Ok();
    }
}
