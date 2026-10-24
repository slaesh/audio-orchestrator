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
        var room = await _db.Rooms.FindAsync(id);
        if (room == null)
        {
            room = new RoomEntity { MacAddress = id, FriendlyName = id, MixVolume = req.Percent, IsMuted = req.Muted };
            _db.Rooms.Add(room);
        }
        else
        {
            room.IsMuted = req.Muted;
        }

        var broadcastService = HttpContext?.RequestServices?.GetService<IBroadcastService>();

        for (int attempt = 0; attempt < 5; attempt++)
        {
            // Check if room belongs to an orchestrated group
            var status = await _snapcast.GetStatusAsync();
            var snapGroups = status?["server"]?["groups"]?.AsArray();
            var currentSnapGroup = snapGroups?.FirstOrDefault(g => 
                g?["clients"]?.AsArray()?.Any(c => c?["id"]?.GetValue<string>() == id) ?? false);
            var snapGroupId = currentSnapGroup?["id"]?.GetValue<string>();
            var dbGroup = !string.IsNullOrEmpty(snapGroupId) 
                ? await _db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.SnapcastGroupId == snapGroupId) 
                : null;

            if (dbGroup != null)
            {
                await _db.SaveChangesAsync();
                // In an orchestrated group, recalculate to combine room.IsMuted with group.IsMuted
                await _volumeOrchestrator.RecalculateAndPushGroupAsync(dbGroup.Id);
                return Ok();
            }

            var gate = snapGroupId != null ? broadcastService?.GetGroupGate(snapGroupId) : null;
            if (gate != null) await gate.WaitAsync();

            try
            {
                // Revalidate membership under gate
                var freshStatus = await _snapcast.GetStatusAsync();
                var freshSnapGroups = freshStatus?["server"]?["groups"]?.AsArray();
                var freshSnapGroup = freshSnapGroups?.FirstOrDefault(g => 
                    g?["clients"]?.AsArray()?.Any(c => c?["id"]?.GetValue<string>() == id) ?? false);
                var freshSnapGroupId = freshSnapGroup?["id"]?.GetValue<string>();
                var freshDbGroup = !string.IsNullOrEmpty(freshSnapGroupId) 
                    ? await _db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.SnapcastGroupId == freshSnapGroupId) 
                    : null;

                if (!string.Equals(freshSnapGroupId, snapGroupId, StringComparison.OrdinalIgnoreCase) || freshDbGroup != null)
                {
                    continue; // Room moved while acquiring gate, retry with fresh owner
                }

                // Unassigned room: mix volume is 1:1 with volume
                room.MixVolume = req.Percent;
                await _db.SaveChangesAsync();

                if (freshSnapGroupId != null && broadcastService?.IsGroupActiveInBroadcast(null, freshSnapGroupId) == true)
                {
                    broadcastService?.MarkDeferredVolumeUpdate(null, freshSnapGroupId);
                    return Ok();
                }

                var settings = await _db.Settings.FirstOrDefaultAsync();
                var profile = settings?.VolumeProfile ?? AudioOrchestrator.Contracts.VolumeProfile.Cubic;
                int snapcastVolume = VolumeOrchestratorService.CalculateSnapcastVolume(req.Percent, 100, profile);

                await _snapcast.SetClientVolumeAsync(id, snapcastVolume, req.Muted);
                await _hub.Clients.All.OnRoomVolumeChanged(id, snapcastVolume, req.Muted);
                return Ok();
            }
            finally
            {
                gate?.Release();
            }
        }

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
        var broadcastService = HttpContext?.RequestServices?.GetService<IBroadcastService>();
        JsonNode? currentSnapGroup = null;
        string? snapGroupId = null;
        Data.Entities.GroupEntity? dbGroup = null;
        var acquiredGates = new List<SemaphoreSlim>();

        for (int attempt = 0; attempt < 5; attempt++)
        {
            // 1. Find the current physical group for this client
            var status = await _snapcast.GetStatusAsync();
            var snapGroups = status?["server"]?["groups"]?.AsArray();
            if (snapGroups == null) return Ok();

            currentSnapGroup = snapGroups.FirstOrDefault(g => 
                g?["clients"]?.AsArray()?.Any(c => c?["id"]?.GetValue<string>() == id) ?? false);

            if (currentSnapGroup == null) return Ok();
            snapGroupId = currentSnapGroup["id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(snapGroupId)) return Ok();

            // 2. Find if this Snapcast group is currently linked to an Orchestrator DB group
            dbGroup = await _db.Groups.FirstOrDefaultAsync(g => g.SnapcastGroupId == snapGroupId);

            var involvedGateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { snapGroupId };
            if (dbGroup != null) involvedGateIds.Add(dbGroup.Id);

            var sortedGateIds = involvedGateIds.OrderBy(gid => gid, StringComparer.Ordinal).ToList();
            acquiredGates.Clear();

            if (broadcastService != null)
            {
                foreach (var gid in sortedGateIds)
                {
                    var g = broadcastService.GetGroupGate(gid);
                    if (g != null)
                    {
                        await g.WaitAsync();
                        acquiredGates.Add(g);
                    }
                }
            }

            bool retainGates = false;
            try
            {
                // Refresh room membership and database owner after acquiring the gates
                var freshStatus = await _snapcast.GetStatusAsync();
                var freshSnapGroups = freshStatus?["server"]?["groups"]?.AsArray();
                var freshSnapGroup = freshSnapGroups?.FirstOrDefault(g => 
                    g?["clients"]?.AsArray()?.Any(c => c?["id"]?.GetValue<string>() == id) ?? false);

                if (freshSnapGroup == null)
                {
                    return Ok();
                }

                var freshSnapGroupId = freshSnapGroup["id"]?.GetValue<string>();
                if (string.IsNullOrEmpty(freshSnapGroupId)) return Ok();

                var freshDbGroup = await _db.Groups.FirstOrDefaultAsync(g => g.SnapcastGroupId == freshSnapGroupId);

                // If membership or DB group link shifted while waiting on the gates, retry with updated gate set
                if (freshSnapGroupId != snapGroupId || freshDbGroup?.Id != dbGroup?.Id)
                {
                    continue;
                }

                // Check broadcast ownership under the verified gate
                if (broadcastService?.IsGroupActiveInBroadcast(freshDbGroup?.Id, freshSnapGroupId) == true)
                {
                    return Conflict("Membership changes cannot be performed while the group is actively playing a broadcast.");
                }

                retainGates = true;
                currentSnapGroup = freshSnapGroup;
                snapGroupId = freshSnapGroupId;
                dbGroup = freshDbGroup;
                break;
            }
            finally
            {
                if (!retainGates)
                {
                    foreach (var g in acquiredGates) g.Release();
                    acquiredGates.Clear();
                }
            }
        }

        if (acquiredGates.Count == 0 && broadcastService != null)
        {
            return StatusCode(503, "Could not acquire group locks due to concurrent membership changes");
        }

        try
        {
            if (dbGroup == null) return Ok();

            var otherClients = currentSnapGroup?["clients"]?.AsArray()
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
                try { await _snapcast.SetGroupNameAsync(snapGroupId!, ""); } catch {}
                try { await _snapcast.SetGroupStreamAsync(snapGroupId!, "default"); } catch {}
                await _hub.Clients.All.OnFullStateUpdate(await _stateBuilder.BuildAsync());
                return Ok();
            }

            // If there are other rooms, prune this ID. 
            // Snapserver moves the removed client to its own new group.
            await _snapcast.SetGroupClientsAsync(snapGroupId!, otherClients.ToArray()!);

            // Release original gates before acquiring the orphan gate to avoid lock-order inversion deadlock
            foreach (var g in acquiredGates) g.Release();
            acquiredGates.Clear();
            
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
                        var ogGate = broadcastService?.GetGroupGate(ogId);
                        if (ogGate != null) await ogGate.WaitAsync();
                        try
                        {
                            // Check broadcast status on orphan under gate before resetting stream
                            if (broadcastService?.IsGroupActiveInBroadcast(null, ogId) != true)
                            {
                                await _snapcast.SetGroupStreamAsync(ogId, "default");
                                await _snapcast.SetGroupNameAsync(ogId, "");
                            }
                        }
                        finally
                        {
                            ogGate?.Release();
                        }
                    }
                }
            } catch {}
            
            await _hub.Clients.All.OnFullStateUpdate(await _stateBuilder.BuildAsync());
            return Ok();
        }
        finally
        {
            foreach (var g in acquiredGates)
            {
                g.Release();
            }
        }
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
