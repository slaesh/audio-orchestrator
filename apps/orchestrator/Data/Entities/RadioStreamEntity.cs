namespace AudioOrchestrator.Data.Entities;

public class RadioStreamEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string StreamUrl { get; set; } = string.Empty;
    

    // Current Track Info (Cached/Polled)
    public string? CurrentTitle { get; set; }
    public string? CurrentArtist { get; set; }
    public string? CurrentCoverUrl { get; set; }
    public string? ErrorMessage { get; set; }
}
