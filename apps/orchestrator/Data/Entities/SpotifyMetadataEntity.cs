using System.ComponentModel.DataAnnotations;
using Tapper;

namespace AudioOrchestrator.Data.Entities;

[TranspilationSource]
public class SpotifyMetadataEntity
{
    [Key]
    public string TrackId { get; set; } = string.Empty;
    
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public int? DurationMs { get; set; }
    public string? CoverUrl { get; set; }
    
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
