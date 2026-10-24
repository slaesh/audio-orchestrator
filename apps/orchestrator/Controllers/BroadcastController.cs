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

    [HttpGet("jobs")]
    public async Task<ActionResult<List<BroadcastJobDto>>> GetRecentJobs([FromQuery] int limit = 50)
    {
        var jobs = await _broadcastService.GetRecentJobsAsync(limit, HttpContext?.RequestAborted ?? default);
        return Ok(jobs);
    }

    [HttpGet("jobs/{id}")]
    public async Task<ActionResult<BroadcastJobDto>> GetJob([FromRoute] string id)
    {
        var job = await _broadcastService.GetJobAsync(id, HttpContext?.RequestAborted ?? default);
        if (job == null)
        {
            return NotFound();
        }

        return Ok(job);
    }

    [HttpPost("jobs/{id}/cancel")]
    public async Task<ActionResult<bool>> CancelJob([FromRoute] string id)
    {
        var cancelled = await _broadcastService.CancelJobAsync(id, HttpContext?.RequestAborted ?? default);
        if (!cancelled)
        {
            return NotFound();
        }

        return Ok(true);
    }

    [HttpPost("play")]
    [ProducesResponseType(typeof(BroadcastJobDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(BroadcastJobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(BroadcastJobDto), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<BroadcastJobDto>> Play(
        [FromBody] BroadcastPlayRequest request,
        [FromQuery] bool wait = false)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.AudioUrl))
        {
            return BadRequest("Ungültige Anfrage oder leere AudioUrl.");
        }

        if (!Uri.TryCreate(request.AudioUrl, UriKind.Absolute, out var parsedUri)
            || (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps))
        {
            return BadRequest("AudioUrl muss eine absolute HTTP- oder HTTPS-URL sein.");
        }

        BroadcastJobDto job;
        try
        {
            job = await _broadcastService.EnqueuePlayUrlAsync(request, HttpContext?.RequestAborted ?? default);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }

        if (wait)
        {
            await _broadcastService.WaitForJobAsync(job.Id, HttpContext?.RequestAborted ?? default);
            var finishedJob = await _broadcastService.GetJobAsync(job.Id, HttpContext?.RequestAborted ?? default);
            if (finishedJob?.Status == BroadcastJobStatus.Failed)
            {
                return BadRequest(finishedJob);
            }

            return Ok(finishedJob ?? job);
        }

        return AcceptedAtAction(nameof(GetJob), new { id = job.Id }, job);
    }

    [HttpPost("play-file")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(BroadcastJobDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(BroadcastJobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(BroadcastJobDto), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<BroadcastJobDto>> PlayFile(
        IFormFile file,
        [FromForm] string? groupIds = null,
        [FromForm] string? roomIds = null,
        [FromForm] int? volume = null,
        [FromForm] int? fadeDurationMs = null,
        [FromForm] bool pausePlayback = false,
        [FromForm] int? timeoutSeconds = 60,
        [FromForm] bool interrupt = true,
        [FromQuery] bool wait = false)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest("Keine Audiodatei hochgeladen.");
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
        BroadcastJobDto job;
        try
        {
            job = await _broadcastService.EnqueuePlayFileAsync(stream, ext, request, HttpContext?.RequestAborted ?? default);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }

        if (wait)
        {
            await _broadcastService.WaitForJobAsync(job.Id, HttpContext?.RequestAborted ?? default);
            var finishedJob = await _broadcastService.GetJobAsync(job.Id, HttpContext?.RequestAborted ?? default);
            if (finishedJob?.Status == BroadcastJobStatus.Failed)
            {
                return BadRequest(finishedJob);
            }

            return Ok(finishedJob ?? job);
        }

        return AcceptedAtAction(nameof(GetJob), new { id = job.Id }, job);
    }

    [HttpPost("stop")]
    public async Task<IActionResult> Stop()
    {
        await _broadcastService.StopAsync(HttpContext?.RequestAborted ?? default);
        return Ok();
    }
}
