using System.ComponentModel.DataAnnotations;
using AudioOrchestrator.Contracts;

namespace AudioOrchestrator.Data.Entities;

public class OrchestratorSettingsEntity
{
    [Key]
    public string Id { get; set; } = "default";
    public VolumeProfile VolumeProfile { get; set; } = VolumeProfile.Cubic;

    public bool HomeAssistantEnabled { get; set; } = false;
    public string? MqttHost { get; set; }
    public int MqttPort { get; set; } = 1883;
    public string? MqttUsername { get; set; }
    public string? MqttPassword { get; set; }
    public bool MqttUseTls { get; set; } = false;
    public string MqttDiscoveryPrefix { get; set; } = "homeassistant";
    public string MqttBaseTopic { get; set; } = "audio_orchestrator";
}
