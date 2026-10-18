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
[Route("api/[controller]")]
public class RoomsController : ControllerBase
{
    private readonly ISnapcastService _snapcast;
    private readonly OrchestratorDbContext _db;
    private readonly IHubContext<AudioHub, IAudioClient> _hub;
    private readonly FullStateBuilder _stateBuilder;
    private readonly VolumeOrchestratorService _volumeOrchestrator;

    public RoomsController(
        ISnapcastService snapcast, 
        OrchestratorDbContext db,
        IHubContext<AudioHub, IAudioClient> hub,
        FullStateBuilder stateBuilder,
        VolumeOrchestratorService volumeOrchestrator)
    {
        _snapcast = snapcast;
        _db = db;
        _hub = hub;
        _stateBuilder = stateBuilder;
        _volumeOrchestrator = volumeOrchestrator;
    }

    [HttpGet]
    public async Task<IActionResult> GetRooms()
    {
        var status = await _snapcast.GetStatusAsync();
        var rooms = await _db.Rooms.AsNoTracking().ToListAsync();
        
        return Ok(new { SnapcastStatus = status, Rooms = rooms });
    }

    [HttpPut("{id}/name")]
    public async Task<IActionResult> RenameRoom(string id, [FromBody] RenameRoomRequest req)
    {
        var room = await _db.Rooms.FindAsync(id);
        if (room == null)
        {
            room = new RoomEntity { MacAddress = id, FriendlyName = req.Name };
            _db.Rooms.Add(room);
        }
        else
        {
            room.FriendlyName = req.Name;
        }

        await _db.SaveChangesAsync();
        try {
            await _snapcast.SetClientNameAsync(id, req.Name);
        } catch {} 
        await _hub.Clients.All.OnFullStateUpdate(await _stateBuilder.BuildAsync());
        return Ok(room);
    }

    [HttpPut("{id}/volume")]
    public async Task<IActionResult> SetVolume(string id, [FromBody] VolumeRequest req)
    {
        await _snapcast.SetClientVolumeAsync(id, req.Percent, req.Muted);
        await _hub.Clients.All.OnRoomVolumeChanged(id, req.Percent, req.Muted);
        return Ok();
    }

    [HttpPut("{id}/mix")]
    public async Task<IActionResult> SetMix(string id, [FromBody] SetMixRequest req)
    {
        await _volumeOrchestrator.SetRoomMixVolumeAsync(id, req.Percent);
        await _hub.Clients.All.OnFullStateUpdate(await _stateBuilder.BuildAsync());
        return Ok();
    }

    [HttpPut("{id}/unassign")]
    public async Task<IActionResult> Unassign(string id)
    {
        // 1. Find the current physical group for this client
        var status = await _snapcast.GetStatusAsync();
        var snapGroups = status?["server"]?["groups"]?.AsArray();
        if (snapGroups == null) return Ok();

        var currentSnapGroup = snapGroups.FirstOrDefault(g => 
            g?["clients"]?.AsArray()?.Any(c => c?["id"]?.GetValue<string>() == id) ?? false);

        if (currentSnapGroup == null) return Ok();
        var snapGroupId = currentSnapGroup["id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(snapGroupId)) return Ok();

        // 3. Find if this Snapcast group is currently linked to an Orchestrator DB group
        var dbGroup = await _db.Groups.FirstOrDefaultAsync(g => g.SnapcastGroupId == snapGroupId);
        if (dbGroup == null) return Ok();

        var otherClients = currentSnapGroup["clients"]?.AsArray()
            ?.Select(c => c?["id"]?.GetValue<string>())
            .Where(cId => !string.IsNullOrEmpty(cId) && cId != id)
            .ToList();

        if (otherClients == null || otherClients.Count == 0)
        {
            // If this was the LAST room, we "dissolve" the orchestration link for this snapcast group.
            // It stays alive on Snapserver, but the Orchestrator now treats it as Unassigned.
            dbGroup.SnapcastGroupId = null;
            await _db.SaveChangesAsync();

            // IMPORTANT: Reset the name and stream so the self-healing reconciliation 
            // doesn't 'claim' it back immediately via Name-matching.
            try { await _snapcast.SetGroupNameAsync(snapGroupId, ""); } catch {}
            try { await _snapcast.SetGroupStreamAsync(snapGroupId, "default"); } catch {}
            await _hub.Clients.All.OnFullStateUpdate(await _stateBuilder.BuildAsync());
            return Ok();
        }

        // If there are other rooms, prune this ID. 
        // Snapserver moves the removed client to its own new group.
        await _snapcast.SetGroupClientsAsync(snapGroupId, otherClients.ToArray()!);
        
        // IMPORTANT: Find the newly created group for this client and silence it
        try {
            // Small delay to let Snapserver settle the move
            await Task.Delay(100);
            var newStatus = await _snapcast.GetStatusAsync();
            var newGroups = newStatus?["server"]?["groups"]?.AsArray();
            var orphanGroup = newGroups?.FirstOrDefault(g => 
                g?["clients"]?.AsArray()?.Any(c => c?["id"]?.GetValue<string>() == id) ?? false);
            
            if (orphanGroup != null) {
                var ogId = orphanGroup["id"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(ogId)) {
                    await _snapcast.SetGroupStreamAsync(ogId, "default");
                    await _snapcast.SetGroupNameAsync(ogId, "");
                }
            }
        } catch {}
        
        await _hub.Clients.All.OnFullStateUpdate(await _stateBuilder.BuildAsync());
        return Ok();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteRoom(string id)
    {
        try
        {
            await _snapcast.DeleteClientAsync(id);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = $"Failed to delete client from Snapserver: {ex.Message}" });
        }

        var room = await _db.Rooms.FindAsync(id);
        if (room != null)
        {
            _db.Rooms.Remove(room);
            await _db.SaveChangesAsync();
        }

        await _hub.Clients.All.OnFullStateUpdate(await _stateBuilder.BuildAsync());
        return NoContent();
    }
}

public class VolumeRequest { public int Percent { get; set; } public bool Muted { get; set; } }
public class RenameRoomRequest { public string Name { get; set; } = string.Empty; }
public class SetMixRequest { public int Percent { get; set; } }
