using System.ComponentModel.DataAnnotations;

namespace AudioOrchestrator.Data.Entities;

public class OrchestratorSettingsEntity
{
    [Key]
    public string Id { get; set; } = "default";
    public string VolumeProfile { get; set; } = "cubic"; // "cubic" | "linear"
}
