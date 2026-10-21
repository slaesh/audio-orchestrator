using System.ComponentModel.DataAnnotations;
using AudioOrchestrator.Contracts;

namespace AudioOrchestrator.Data.Entities;

public class OrchestratorSettingsEntity
{
    [Key]
    public string Id { get; set; } = "default";
    public VolumeProfile VolumeProfile { get; set; } = VolumeProfile.Cubic;
}
