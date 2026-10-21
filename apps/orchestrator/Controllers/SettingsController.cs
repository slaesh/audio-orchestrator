using Microsoft.AspNetCore.Mvc;
using AudioOrchestrator.Data;
using AudioOrchestrator.Data.Entities;
using AudioOrchestrator.Contracts;
using AudioOrchestrator.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using AudioOrchestrator.Hubs;

namespace AudioOrchestrator.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SettingsController : ControllerBase
{
    private readonly OrchestratorDbContext _db;
    private readonly VolumeOrchestratorService _volumeOrchestrator;
    private readonly FullStateBuilder _stateBuilder;
    private readonly IHubContext<AudioHub, IAudioClient> _hub;
    private readonly ILogger<SettingsController> _logger;

    public SettingsController(
        OrchestratorDbContext db,
        VolumeOrchestratorService volumeOrchestrator,
        FullStateBuilder stateBuilder,
        IHubContext<AudioHub, IAudioClient> hub,
        ILogger<SettingsController> logger)
    {
        _db = db;
        _volumeOrchestrator = volumeOrchestrator;
        _stateBuilder = stateBuilder;
        _hub = hub;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetSettings()
    {
        var settings = await _db.Settings.FirstOrDefaultAsync();
        var profile = settings?.VolumeProfile ?? VolumeProfile.Cubic;
        return Ok(new { volumeProfile = profile });
    }

    [HttpPut]
    public async Task<IActionResult> UpdateSettings([FromBody] UpdateSettingsRequest req)
    {
        var settings = await _db.Settings.FirstOrDefaultAsync();
        if (settings == null)
        {
            settings = new OrchestratorSettingsEntity { Id = "default", VolumeProfile = req.VolumeProfile };
            _db.Settings.Add(settings);
        }
        else
        {
            settings.VolumeProfile = req.VolumeProfile;
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation("Volume profile updated to {Profile}. Recalculating all volumes...", req.VolumeProfile);

        // Recalculate hardware output for all active groups and unassigned rooms
        await _volumeOrchestrator.RecalculateAllAsync();

        // Broadcast full state update to SignalR clients
        await _hub.Clients.All.OnFullStateUpdate(await _stateBuilder.BuildAsync());

        return Ok(new { volumeProfile = req.VolumeProfile });
    }
}
