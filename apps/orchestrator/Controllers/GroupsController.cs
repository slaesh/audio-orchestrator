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
public class GroupsController : ControllerBase
{
    private readonly IReconciliationService _reconciliation;
    private readonly ISnapcastService _snapcast;
    private readonly OrchestratorDbContext _db;
    private readonly ILogger<GroupsController> _logger;
    private readonly IHubContext<AudioHub, IAudioClient> _hub;
    private readonly FullStateBuilder _stateBuilder;
    private readonly VolumeOrchestratorService _volumeOrchestrator;
    private readonly IHomeAssistantMqttService? _mqttService;

    public GroupsController(
        IReconciliationService reconciliation,
        ISnapcastService snapcast, 
        OrchestratorDbContext db, 
        ILogger<GroupsController> logger,
        IHubContext<AudioHub, IAudioClient> hub,
        FullStateBuilder stateBuilder,
        VolumeOrchestratorService volumeOrchestrator,
        IHomeAssistantMqttService? mqttService = null)
    {
        _reconciliation = reconciliation;
        _snapcast = snapcast;
        _db = db;
        _logger = logger;
        _hub = hub;
        _stateBuilder = stateBuilder;
        _volumeOrchestrator = volumeOrchestrator;
        _mqttService = mqttService;
    }

    private async Task BroadcastStateAsync()
    {
        var state = await _stateBuilder.BuildAsync();
        await _hub.Clients.All.OnFullStateUpdate(state);
        if (_mqttService != null)
        {
            _ = _mqttService.NotifyFullStateAsync(state);
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetGroups()
    {
        var groups = await _db.Groups.ToListAsync();
        var radios = await _db.RadioStreams.ToListAsync();
        var spotifySources = await _db.SpotifySources.ToListAsync();
        var status = await _snapcast.GetStatusAsync();
        
        await _reconciliation.ReconcileSnapcastAsync(groups, radios, spotifySources, status);

        return Ok(new { DbGroups = groups, SnapcastStatus = status });
    }

    [HttpPost]
    public async Task<IActionResult> CreateGroup([FromBody] CreateGroupRequest request)
    {
        var group = new GroupEntity { Name = request.Name };
        _db.Groups.Add(group);
        await _db.SaveChangesAsync();
        await BroadcastStateAsync();
        return Ok(group);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteGroup(string id)
    {
        var group = await _db.Groups.FirstOrDefaultAsync(g => g.Id == id || g.SnapcastGroupId == id);
        if (group == null) return NotFound();

        // Note: We no longer stop containers here because groups don't 'own' them anymore.
        // The Sources hub manages their lifecycle.
        
        _db.Groups.Remove(group);
        await _db.SaveChangesAsync();
        await BroadcastStateAsync();
        return NoContent();
    }

    [HttpPut("{id}/name")]
    public async Task<IActionResult> RenameGroup(string id, [FromBody] RenameGroupRequest req)
    {
        var group = await _db.Groups.FirstOrDefaultAsync(g => g.Id == id || g.SnapcastGroupId == id);
        if (group == null) return NotFound();

        group.Name = req.Name;
        await _db.SaveChangesAsync();

        if (!string.IsNullOrEmpty(group.SnapcastGroupId)) {
            await _snapcast.SetGroupNameAsync(group.SnapcastGroupId, req.Name);
        }

        // Technically we might want to recreate the LibreSpot container to change its name, 
        // but for MVP updating DB and Snapcast is sufficient.
        await BroadcastStateAsync();
        return Ok(group);
    }

    [HttpPut("{id}/volume")]
    public async Task<IActionResult> SetGroupVolume(string id, [FromBody] VolumeRequest req)
    {
        await _volumeOrchestrator.SetGroupVolumeAsync(id, req.Percent, req.Muted);
        await BroadcastStateAsync();
        return Ok();
    }

    [HttpPut("{id}/clients")]
    public async Task<IActionResult> AssignClients(string id, [FromBody] string[] clientIds)
    {
        var group = await _db.Groups.FindAsync(id);
        if (group == null) return NotFound();
        if (clientIds.Length == 0) return BadRequest("No clients to assign");

        var broadcastService = HttpContext?.RequestServices?.GetService<IBroadcastService>();

        JsonNode? status = null;
        JsonArray? snapGroups = null;
        var sourceSnapGroupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<GroupEntity> sourceDbGroups = new();
        var acquiredGates = new List<SemaphoreSlim>();

        for (int attempt = 0; attempt < 5; attempt++)
        {
            status = await _snapcast.GetStatusAsync();
            snapGroups = status?["server"]?["groups"]?.AsArray();
            if (snapGroups == null) return StatusCode(500, "Snapserver unreachable");

            sourceSnapGroupIds.Clear();
            foreach (var cId in clientIds)
            {
                var sourceSnapGroup = snapGroups.FirstOrDefault(sg => 
                    sg?["clients"]?.AsArray()?.Any(c => c?["id"]?.GetValue<string>() == cId) == true);
                var sGroupId = sourceSnapGroup?["id"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(sGroupId))
                {
                    sourceSnapGroupIds.Add(sGroupId);
                }
            }

            sourceDbGroups = await _db.Groups
                .Where(g => g.SnapcastGroupId != null && sourceSnapGroupIds.Contains(g.SnapcastGroupId))
                .ToListAsync();

            var involvedGateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { group.Id };
            if (!string.IsNullOrEmpty(group.SnapcastGroupId)) involvedGateIds.Add(group.SnapcastGroupId);
            foreach (var sgId in sourceSnapGroupIds)
            {
                var matchingDb = sourceDbGroups.FirstOrDefault(g => g.SnapcastGroupId == sgId);
                if (matchingDb != null)
                {
                    involvedGateIds.Add(matchingDb.Id);
                }
                involvedGateIds.Add(sgId);
            }

            var sortedGateIds = involvedGateIds.OrderBy(gid => gid, StringComparer.Ordinal).ToList();
            acquiredGates.Clear();
            if (broadcastService != null)
            {
                foreach (var gid in sortedGateIds)
                {
                    var g = broadcastService.GetGroupGate(gid);
                    await g.WaitAsync();
                    acquiredGates.Add(g);
                }
            }

            bool gateRetained = false;
            try
            {
                // Verify membership did not shift before gates were acquired
                var freshStatus = await _snapcast.GetStatusAsync();
                var freshSnapGroups = freshStatus?["server"]?["groups"]?.AsArray();
                if (freshSnapGroups == null)
                {
                    return StatusCode(500, "Snapserver unreachable");
                }

                var freshSourceSnapGroupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var cId in clientIds)
                {
                    var sourceSnapGroup = freshSnapGroups.FirstOrDefault(sg => 
                        sg?["clients"]?.AsArray()?.Any(c => c?["id"]?.GetValue<string>() == cId) == true);
                    var sGroupId = sourceSnapGroup?["id"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(sGroupId))
                    {
                        freshSourceSnapGroupIds.Add(sGroupId);
                    }
                }

                if (!freshSourceSnapGroupIds.SetEquals(sourceSnapGroupIds))
                {
                    continue;
                }

                status = freshStatus;
                snapGroups = freshSnapGroups;
                gateRetained = true;
                break;
            }
            finally
            {
                if (!gateRetained)
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

            if (broadcastService?.IsGroupActiveInBroadcast(group.Id, group.SnapcastGroupId) == true)
            {
                return Conflict("Membership changes cannot be performed while the group is actively playing a broadcast.");
            }

            foreach (var sgId in sourceSnapGroupIds)
            {
                var matchingDb = sourceDbGroups.FirstOrDefault(g => g.SnapcastGroupId == sgId);
                if (broadcastService?.IsGroupActiveInBroadcast(matchingDb?.Id, sgId) == true)
                {
                    return Conflict("Membership changes cannot be performed while a source group is actively playing a broadcast.");
                }
            }

            // 1. PRUNING PHASE: Extract moving clients from any existing sibling groups
            // This ensures that dragging ONE client doesn't hijack its roommates.
            if (snapGroups == null) return StatusCode(500, "Snapserver unreachable");

            var otherOrchestratedSnapIds = await _db.Groups.Where(g => g.Id != group.Id && g.SnapcastGroupId != null)
                .Select(g => g.SnapcastGroupId!)
                .ToListAsync();

            var processedSourceGroupIds = new HashSet<string>();
            foreach (var cId in clientIds)
            {
                var sourceSnapGroup = snapGroups.FirstOrDefault(sg => 
                    sg?["clients"]?.AsArray()?.Any(c => c?["id"]?.GetValue<string>() == cId) == true);
                
                if (sourceSnapGroup == null) continue;
                var sGroupId = sourceSnapGroup["id"]?.GetValue<string>();
                if (string.IsNullOrEmpty(sGroupId) || !processedSourceGroupIds.Add(sGroupId)) continue;

                // Find all clients in this source group that are NOT part of the moving clientIds
                var remainingClients = sourceSnapGroup["clients"]?.AsArray()
                    ?.Select(c => c?["id"]?.GetValue<string>())
                    .Where(cid => !string.IsNullOrEmpty(cid) && !clientIds.Contains(cid!))
                    .ToList();

                // If there are other clients staying in the source group, keep only them
                if (remainingClients != null && remainingClients.Count > 0)
                {
                    await _snapcast.SetGroupClientsAsync(sGroupId, remainingClients.ToArray()!);
                }
            }

            // 2. RESOLUTION PHASE: Always dynamically resolve - Snapcast group IDs are transient
            // This intermediate refresh is now semi-resilient. If it fails, we keep going with the previous status
            // to prevent orphaning rooms in the void.
            try {
                status = await _snapcast.GetStatusAsync();
                if (status != null) snapGroups = status["server"]?["groups"]?.AsArray();
            } catch (Exception ex) {
                _logger.LogWarning($"Intermediate status refresh failed during assignment: {ex.Message}. Proceeding with cached info.");
            }

            if (snapGroups == null) return StatusCode(500, "Snapserver unreachable");

            var snapcastGroupId = ResolveSnapcastGroupId(group, snapGroups, clientIds[0], otherOrchestratedSnapIds);
            if (string.IsNullOrEmpty(snapcastGroupId))
                return BadRequest("Could not resolve a Snapcast group for this assignment");

            // Persist the resolved mapping if it changed
            if (group.SnapcastGroupId != snapcastGroupId)
            {
                // EXCLUSIVITY ENFORCEMENT: If another group was using this Snapcast ID, they must let it go.
                var otherGroupsWithSameId = await _db.Groups.Where(g => g.Id != group.Id && g.SnapcastGroupId == snapcastGroupId).ToListAsync();
                foreach (var og in otherGroupsWithSameId) {
                    og.SnapcastGroupId = null;
                }

                group.SnapcastGroupId = snapcastGroupId;
                await _db.SaveChangesAsync();
            }

            // 3. MERGE PHASE: get existing clients in target group, add new ones
            var targetSnapGroup = snapGroups.FirstOrDefault(g => g?["id"]?.GetValue<string>() == snapcastGroupId);
            var mergedClients = targetSnapGroup?["clients"]?.AsArray()
                ?.Select(c => c?["id"]?.GetValue<string>())
                .Where(cid => !string.IsNullOrEmpty(cid))
                .ToList() ?? new List<string?>();

            foreach (var cId in clientIds)
            {
                if (!mergedClients.Contains(cId))
                    mergedClients.Add(cId);
            }

            await _snapcast.SetGroupClientsAsync(snapcastGroupId, mergedClients.ToArray()!);

            // 4. RE-RESOLUTION PHASE: Snapserver is dynamic. 
            // Adding a client might have caused the group ID to change or rotate!
            // This refresh is now NON-FATAL. If it glitches, we allow the request to succeed
            // and let the 2s background reconciliation handle the final wiring.
            try {
                status = await _snapcast.GetStatusAsync();
                snapGroups = status?["server"]?["groups"]?.AsArray();
                if (snapGroups != null) {
                    var freshId = ResolveSnapcastGroupId(group, snapGroups, clientIds[0], otherOrchestratedSnapIds);
                    if (!string.IsNullOrEmpty(freshId)) {
                        if (snapcastGroupId != freshId) {
                            _logger.LogInformation($"Group ID rotated from {snapcastGroupId} to {freshId} after client assignment.");
                            snapcastGroupId = freshId;
                            group.SnapcastGroupId = snapcastGroupId;
                            await _db.SaveChangesAsync();
                        }
                    }
                }

                // 5. CONFIGURATION PHASE: Configure group identity and stream
                // Failure here is also non-fatal since reconciliation will fix it.
                try {
                    await _snapcast.SetGroupNameAsync(snapcastGroupId, group.Name);
                } catch (Exception ex) {
                    _logger.LogWarning($"Non-critical failure setting group name for {snapcastGroupId}: {ex.Message}");
                }

                bool isBroadcasting = broadcastService?.IsGroupActiveInBroadcast(group.Id, snapcastGroupId) == true;
                if (!isBroadcasting && !string.IsNullOrEmpty(group.CurrentStreamId))
                {
                    try { 
                        await _snapcast.SetGroupStreamAsync(snapcastGroupId, group.CurrentStreamId); 
                    }
                    catch (Exception ex) { 
                        _logger.LogWarning($"Failure setting stream for {snapcastGroupId}: {ex.Message}. It will be auto-corrected by background reconciliation.");
                    }
                }

            } catch (Exception ex) {
                _logger.LogWarning($"Assignment confirmation check failed: {ex.Message}. The room likely moved successfully; background reconciliation will verify.");
            }
        }
        finally
        {
            foreach (var g in acquiredGates)
            {
                g.Release();
            }
        }

        // 6. VOLUME PHASE: Recalculate and push group volume to the newly assigned clients
        // Performed AFTER releasing acquired gates because RecalculateAndPushGroupAsync
        // internally acquires the group gate (avoiding non-reentrant SemaphoreSlim deadlock).
        try {
            await _volumeOrchestrator.RecalculateAndPushGroupAsync(group.Id);
        } catch (Exception ex) {
            _logger.LogWarning($"Failure recalculating group volume for {group.Id}: {ex.Message}");
        }

        await BroadcastStateAsync();
        return Ok();
    }

    /// <summary>
    /// Resolves a valid current Snapcast group ID for an orchestrator group.
    /// Priority: stored ID (if still valid) → group playing our stream → first client's current group.
    /// </summary>
    private static string? ResolveSnapcastGroupId(GroupEntity group, System.Text.Json.Nodes.JsonArray snapGroups, string firstClientId, List<string> occupiedSnapIds)
    {
        // 1. Check if the stored ID still exists and is not currently hijacked by someone else
        if (!string.IsNullOrEmpty(group.SnapcastGroupId) && !occupiedSnapIds.Contains(group.SnapcastGroupId!))
        {
            if (snapGroups.Any(g => g?["id"]?.GetValue<string>() == group.SnapcastGroupId))
                return group.SnapcastGroupId;
        }

        // 2. Find a group already playing our stream that isn't occupied
        if (!string.IsNullOrEmpty(group.CurrentStreamId) && group.CurrentStreamId != "default")
        {
            foreach (var sg in snapGroups)
            {
                var sid = sg?["id"]?.GetValue<string>();
                if (sid != null && !occupiedSnapIds.Contains(sid) && sg?["stream_id"]?.GetValue<string>() == group.CurrentStreamId)
                    return sid;
            }
        }

        // 3. Fall back to the first client's current group.
        // HIJACK ALLOWED: If we're moving a room, it's always legal to take its current hardware link,
        // even if another orchestrated group 'owns' it (the loop at line 296 handles the cleanup).
        foreach (var sg in snapGroups)
        {
            var sid = sg?["id"]?.GetValue<string>();
            var clients = sg?["clients"]?.AsArray();
            if (sid == null || clients == null) continue;
            
            if (clients.Any(c => c?["id"]?.GetValue<string>() == firstClientId))
                return sid;
        }

        // 4. If all else fails, pick any unoccupied orphan group
        foreach (var sg in snapGroups)
        {
            var sid = sg?["id"]?.GetValue<string>();
            if (sid != null && !occupiedSnapIds.Contains(sid)) return sid;
        }

        // 5. DESPERATION PASS: Pick any group serving 'default' (silence), even if earmarked
        foreach (var sg in snapGroups)
        {
            if (sg?["stream_id"]?.GetValue<string>() == "default")
                return sg?["id"]?.GetValue<string>();
        }

        return null;
    }

    [HttpPut("{id}/stream")]
    public async Task<IActionResult> SetStream(string id, [FromBody] SetStreamRequest req)
    {
        var group = await _db.Groups.FirstOrDefaultAsync(g => g.Id == id || g.SnapcastGroupId == id);
        if (group == null) return NotFound();

        // If 'No active stream' is selected (empty string), we fall back to the system 'default' (silence).
        var effectiveStreamId = string.IsNullOrEmpty(req.StreamId) ? "default" : req.StreamId;

        var broadcastService = HttpContext?.RequestServices?.GetService<IBroadcastService>();
        var gate = broadcastService?.GetGroupGate(group.Id);
        if (gate != null) await gate.WaitAsync();

        bool isBroadcasting = false;
        try
        {
            group.CurrentStreamId = effectiveStreamId;
            await _db.SaveChangesAsync();

            isBroadcasting = broadcastService?.IsGroupActiveInBroadcast(group.Id, group.SnapcastGroupId) == true;

            if (!string.IsNullOrEmpty(group.SnapcastGroupId) && !isBroadcasting) {
                await _snapcast.SetGroupStreamAsync(group.SnapcastGroupId, effectiveStreamId);
            }
        }
        finally
        {
            gate?.Release();
        }

        // If we switched TO a Spotify stream, adopt existing volume if active or push current group volume
        if (effectiveStreamId.StartsWith(OrchestratorConstants.SpotifyPrefix))
        {
            var sourceId = effectiveStreamId.Replace(OrchestratorConstants.SpotifyPrefix, "");
            var source = await _db.SpotifySources.FindAsync(sourceId);
            var activeSibling = await _db.Groups.FirstOrDefaultAsync(g => g.Id != group.Id && g.CurrentStreamId == effectiveStreamId);
            int targetVolume = activeSibling?.MasterVolume ?? (source != null && source.IsActive ? source.MasterVolume : group.MasterVolume);

            if (isBroadcasting)
            {
                if (group.MasterVolume != targetVolume)
                {
                    group.MasterVolume = targetVolume;
                    await _db.SaveChangesAsync();
                    await _hub.Clients.All.OnGroupVolumeChanged(group.Id, group.MasterVolume, group.IsMuted);
                }
                broadcastService?.MarkDeferredVolumeUpdate(group.Id, group.SnapcastGroupId);
            }
            else
            {
                if (activeSibling != null)
                {
                    await _volumeOrchestrator.SetGroupVolumeAsync(group.Id, activeSibling.MasterVolume, group.IsMuted, fromDocker: true);
                }
                else if (source != null && source.IsActive)
                {
                    await _volumeOrchestrator.SetGroupVolumeAsync(group.Id, source.MasterVolume, group.IsMuted, fromDocker: true);
                }
                else
                {
                    await _volumeOrchestrator.SetGroupVolumeAsync(group.Id, group.MasterVolume, group.IsMuted);
                }
            }
        }
        
        await BroadcastStateAsync();
        return Ok();
    }
}

public class CreateGroupRequest { public string Name { get; set; } = string.Empty; }
public class SetStreamRequest { public string StreamId { get; set; } = string.Empty; }
public class RenameGroupRequest { public string Name { get; set; } = string.Empty; }
