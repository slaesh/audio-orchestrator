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
    private readonly IHomeAssistantMqttService _mqttService;
    private readonly ILogger<SettingsController> _logger;

    public SettingsController(
        OrchestratorDbContext db,
        VolumeOrchestratorService volumeOrchestrator,
        FullStateBuilder stateBuilder,
        IHubContext<AudioHub, IAudioClient> hub,
        IHomeAssistantMqttService mqttService,
        ILogger<SettingsController> logger)
    {
        _db = db;
        _volumeOrchestrator = volumeOrchestrator;
        _stateBuilder = stateBuilder;
        _hub = hub;
        _mqttService = mqttService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<SettingsDto>> GetSettings()
    {
        var settings = await _db.Settings.FirstOrDefaultAsync();
        var profile = settings?.VolumeProfile ?? VolumeProfile.Cubic;
        var haDto = new HomeAssistantSettingsDto(
            Enabled: settings?.HomeAssistantEnabled ?? false,
            Host: settings?.MqttHost,
            Port: settings?.MqttPort ?? 1883,
            Username: settings?.MqttUsername,
            Password: null,
            HasPassword: !string.IsNullOrEmpty(settings?.MqttPassword),
            UseTls: settings?.MqttUseTls ?? false,
            DiscoveryPrefix: settings?.MqttDiscoveryPrefix ?? "homeassistant",
            BaseTopic: settings?.MqttBaseTopic ?? "audio_orchestrator",
            IsConnected: _mqttService.IsConnected
        );

        return Ok(new SettingsDto(profile, haDto));
    }

    [HttpPut]
    public async Task<ActionResult<SettingsDto>> UpdateSettings([FromBody] UpdateSettingsRequest req)
    {
        var settings = await _db.Settings.FirstOrDefaultAsync();
        if (settings == null)
        {
            settings = new OrchestratorSettingsEntity { Id = "default" };
            _db.Settings.Add(settings);
        }

        settings.VolumeProfile = req.VolumeProfile;

        if (req.HomeAssistant != null)
        {
            var port = req.HomeAssistant.Port == 0 ? 1883 : req.HomeAssistant.Port;
            if (port is < 1 or > 65535)
            {
                return BadRequest("Ungültiger Port (1-65535).");
            }

            var discoveryPrefix = string.IsNullOrWhiteSpace(req.HomeAssistant.DiscoveryPrefix) ? "homeassistant" : req.HomeAssistant.DiscoveryPrefix.Trim().TrimEnd('/');
            var baseTopic = string.IsNullOrWhiteSpace(req.HomeAssistant.BaseTopic) ? "audio_orchestrator" : req.HomeAssistant.BaseTopic.Trim().TrimEnd('/');

            if (!IsValidMqttTopic(discoveryPrefix))
            {
                return BadRequest("Ungültiger Discovery-Präfix: Wildcards (+, #) oder ungültige Zeichen sind nicht erlaubt.");
            }

            if (!IsValidMqttTopic(baseTopic))
            {
                return BadRequest("Ungültiges Basis-Topic: Wildcards (+, #) oder ungültige Zeichen sind nicht erlaubt.");
            }

            // Prevent saved-password reuse when connection settings change
            bool connectionChanged = !string.Equals(settings.MqttHost, req.HomeAssistant.Host, StringComparison.OrdinalIgnoreCase)
                || settings.MqttPort != port
                || !string.Equals(settings.MqttUsername ?? string.Empty, req.HomeAssistant.Username ?? string.Empty, StringComparison.Ordinal)
                || settings.MqttUseTls != req.HomeAssistant.UseTls;

            if (connectionChanged && req.HomeAssistant.Password == null && !string.IsNullOrEmpty(settings.MqttPassword))
            {
                return BadRequest("Verbindungsdaten geändert: Bei Änderung von Host, Port, Benutzername oder TLS muss das Passwort neu angegeben oder explizit gelöscht werden.");
            }

            settings.HomeAssistantEnabled = req.HomeAssistant.Enabled;
            settings.MqttHost = req.HomeAssistant.Host;
            settings.MqttPort = port;
            settings.MqttUsername = req.HomeAssistant.Username;
            if (req.HomeAssistant.Password != null)
            {
                settings.MqttPassword = req.HomeAssistant.Password;
            }
            settings.MqttUseTls = req.HomeAssistant.UseTls;
            settings.MqttDiscoveryPrefix = discoveryPrefix;
            settings.MqttBaseTopic = baseTopic;
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation("Settings updated. VolumeProfile: {Profile}, HA MQTT Enabled: {HaEnabled}", req.VolumeProfile, settings.HomeAssistantEnabled);

        // Recalculate hardware output for all active groups and unassigned rooms
        await _volumeOrchestrator.RecalculateAllAsync();

        // Reload MQTT connection in background
        _ = _mqttService.ReloadConfigurationAsync();

        // Broadcast full state update to SignalR clients
        var fullState = await _stateBuilder.BuildAsync();
        await _hub.Clients.All.OnFullStateUpdate(fullState);
        await _mqttService.NotifyFullStateAsync(fullState);

        var haDto = new HomeAssistantSettingsDto(
            Enabled: settings.HomeAssistantEnabled,
            Host: settings.MqttHost,
            Port: settings.MqttPort,
            Username: settings.MqttUsername,
            Password: null,
            HasPassword: !string.IsNullOrEmpty(settings.MqttPassword),
            UseTls: settings.MqttUseTls,
            DiscoveryPrefix: settings.MqttDiscoveryPrefix,
            BaseTopic: settings.MqttBaseTopic,
            IsConnected: _mqttService.IsConnected
        );

        return Ok(new SettingsDto(settings.VolumeProfile, haDto));
    }

    [HttpPost("mqtt/test")]
    public async Task<ActionResult<TestMqttConnectionResponse>> TestMqttConnection([FromBody] TestMqttConnectionRequest req)
    {
        var requestToUse = req;
        if (req.Password == null)
        {
            var settings = await _db.Settings.FirstOrDefaultAsync();
            if (settings != null
                && !string.IsNullOrEmpty(settings.MqttPassword)
                && string.Equals(settings.MqttHost, req.Host, StringComparison.OrdinalIgnoreCase)
                && settings.MqttPort == req.Port
                && string.Equals(settings.MqttUsername ?? string.Empty, req.Username ?? string.Empty, StringComparison.Ordinal)
                && settings.MqttUseTls == req.UseTls)
            {
                requestToUse = req with { Password = settings.MqttPassword };
            }
        }
        var result = await _mqttService.TestConnectionAsync(requestToUse);
        return Ok(result);
    }

    private static bool IsValidMqttTopic(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic)) return false;
        if (topic.Contains('+') || topic.Contains('#') || topic.Contains('\0')) return false;
        if (topic.Any(char.IsWhiteSpace)) return false;
        return true;
    }
}
