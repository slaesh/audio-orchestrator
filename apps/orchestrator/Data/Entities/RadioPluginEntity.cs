using System.ComponentModel.DataAnnotations;

namespace AudioOrchestrator.Data.Entities;

public class RadioPluginEntity
{
    [Key]
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string DockerImage { get; set; } = string.Empty;

    /// <summary>
    /// JSON array of regex strings to match stream URLs against.
    /// </summary>
    public string UrlPatternsJson { get; set; } = "[]";

    public bool Enabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<string> GetUrlPatterns()
    {
        if (string.IsNullOrWhiteSpace(UrlPatternsJson)) return new();
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(UrlPatternsJson) ?? new();
        }
        catch
        {
            return new();
        }
    }

    public void SetUrlPatterns(IEnumerable<string> patterns)
    {
        UrlPatternsJson = System.Text.Json.JsonSerializer.Serialize(patterns ?? Array.Empty<string>());
    }
}
