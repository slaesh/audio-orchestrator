using Tapper;

namespace AudioOrchestrator.Contracts;

[TranspilationSource]
public class SpotifyEventDto
{
    public string GroupId { get; set; } = string.Empty;
    public string Event { get; set; } = string.Empty;
    public int? Volume { get; set; }
    public string? TrackId { get; set; }
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public int? DurationMs { get; set; }
    public string? CoverUrl { get; set; }
    public int? PositionMs { get; set; }
    public int? IntervalMs { get; set; }
    public bool? IsConnected { get; set; }
    public bool? IsPlaying { get; set; }
    public string? ContextUri { get; set; }
    public string? ContextName { get; set; }
    public List<SpotifyRecentContextDto>? RecentContexts { get; set; }
}
