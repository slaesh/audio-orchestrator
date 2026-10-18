using System.ComponentModel.DataAnnotations;

namespace AudioOrchestrator.Data.Entities;

public class SpotifySourceEntity
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    
    [Required]
    public string Name { get; set; } = string.Empty;
    
    public int MasterVolume { get; set; } = 50;
    
    public bool IsActive { get; set; } = false;
    public bool IsConnected { get; set; } = false;

    public int MaxVolume { get; set; } = 30;

    // Metadata
    public string? CurrentTrackId { get; set; }
    public string? CurrentTitle { get; set; }
    public string? CurrentArtist { get; set; }
    public string? CurrentAlbum { get; set; }
    public int? CurrentDurationMs { get; set; }
    public string? CurrentCoverUrl { get; set; }
    public int? CurrentPositionMs { get; set; }
    public int? HeartbeatIntervalMs { get; set; }
    public long? LastUpdateTimestamp { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ContextUri { get; set; }
    public string? ContextName { get; set; }
    public string? RecentContextsJson { get; set; }

    public void ResetPlaybackState()
    {
        IsConnected = false;
        IsActive = false;
        CurrentTitle = null;
        CurrentArtist = null;
        CurrentCoverUrl = null;
        CurrentAlbum = null;
        CurrentTrackId = null;
        CurrentPositionMs = 0;
        ContextUri = null;
        ContextName = null;
    }
}
