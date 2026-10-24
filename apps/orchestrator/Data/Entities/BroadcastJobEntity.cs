using System.ComponentModel.DataAnnotations;
using AudioOrchestrator.Contracts;

namespace AudioOrchestrator.Data.Entities;

public class BroadcastJobEntity
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public string? AudioUrl { get; set; }
    public BroadcastSourceType SourceType { get; set; } = BroadcastSourceType.Url;
    public string? TargetGroupIdsJson { get; set; }
    public string? TargetRoomIdsJson { get; set; }
    public int? Volume { get; set; }
    public int? FadeDurationMs { get; set; }
    public bool PausePlayback { get; set; }
    public int TimeoutSeconds { get; set; } = 60;
    public bool Interrupt { get; set; } = true;

    public BroadcastJobStatus Status { get; set; } = BroadcastJobStatus.Queued;
    public int DurationMs { get; set; } = 0;
    public string? ErrorMessage { get; set; }
}
