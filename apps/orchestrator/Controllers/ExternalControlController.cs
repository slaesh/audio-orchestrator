using Microsoft.AspNetCore.Mvc;
using AudioOrchestrator.Services;
using AudioOrchestrator.Data;
using Microsoft.EntityFrameworkCore;
using AudioOrchestrator.Data.Entities;
using AudioOrchestrator.Contracts;

namespace AudioOrchestrator.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ExternalControlController : ControllerBase
{
    private readonly VolumeOrchestratorService _volumeOrchestrator;
    private readonly OrchestratorDbContext _db;
    private readonly FullStateBuilder _stateBuilder;

    public ExternalControlController(
        VolumeOrchestratorService volumeOrchestrator,
        OrchestratorDbContext db,
        FullStateBuilder stateBuilder)
    {
        _volumeOrchestrator = volumeOrchestrator;
        _db = db;
        _stateBuilder = stateBuilder;
    }

    /// <summary>
    /// Set the master volume for a group.
    /// </summary>
    [HttpPut("groups/{id}/volume")]
    public async Task<IActionResult> SetGroupVolume(string id, [FromBody] SetVolumeRequest req)
    {
        await _volumeOrchestrator.SetGroupVolumeAsync(id, req.Value);
        return Ok(new { status = "success", groupId = id, volume = req.Value });
    }

    /// <summary>
    /// Set the mix volume for a room.
    /// </summary>
    [HttpPut("rooms/{id}/mix")]
    public async Task<IActionResult> SetRoomMix(string id, [FromBody] SetVolumeRequest req)
    {
        await _volumeOrchestrator.SetRoomMixVolumeAsync(id, req.Value);
        return Ok(new { status = "success", roomId = id, mix = req.Value });
    }

    /// <summary>
    /// Toggle mute for a group.
    /// </summary>
    [HttpPut("groups/{id}/mute")]
    public async Task<IActionResult> ToggleMute(string id, [FromBody] SetMuteRequest req)
    {
        var group = await _db.Groups.FindAsync(id);
        if (group == null) return NotFound();

        await _volumeOrchestrator.SetGroupVolumeAsync(id, group.MasterVolume, req.Value);
        return Ok(new { status = "success", groupId = id, muted = req.Value });
    }

    /// <summary>
    /// Get simplified status for external controllers.
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        var state = await _stateBuilder.BuildAsync();
        // Return a simplified version if needed, or just the full state
        return Ok(state);
    }
}

public class SetVolumeRequest { public int Value { get; set; } }
public class SetMuteRequest { public bool Value { get; set; } }
