using System.ComponentModel.DataAnnotations;

namespace AudioOrchestrator.Data.Entities;

public class RoomEntity
{
    [Key]
    public string MacAddress { get; set; } = string.Empty; // Used as the Primary Key / Snapcast Client ID
    public string FriendlyName { get; set; } = string.Empty;
    public int MixVolume { get; set; } = 100;
    public bool IsMuted { get; set; } = false;
}
