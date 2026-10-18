namespace AudioOrchestrator.Data.Entities;

public class GroupEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string? SnapcastGroupId { get; set; } // Corresponds to Snapcast ID if mapped
    public string? CurrentStreamId { get; set; } // The stream currently assigned (LibreSpot or Radio)

    public int MasterVolume { get; set; } = 100;
    public bool IsMuted { get; set; } = false;
}
