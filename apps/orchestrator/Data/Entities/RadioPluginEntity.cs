using System.ComponentModel.DataAnnotations;
using AudioOrchestrator.Contracts;

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

    public string CurrentVersion { get; set; } = "";

    public string? GitHubRepository { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string GetEffectiveVersion()
    {
        if (!string.IsNullOrWhiteSpace(CurrentVersion)) return CurrentVersion;
        // Colons before the final slash are registry ports (e.g. localhost:5000/radio), not tags.
        // ExtractImageTag handles this port isolation correctly.
        var tag = OrchestratorConstants.ExtractImageTag(DockerImage, defaultVersion: "0.1.0");
        return tag == "latest" ? "0.1.0" : tag;
    }

    public string? GetGitHubRepository()
    {
        if (!string.IsNullOrWhiteSpace(GitHubRepository))
        {
            return GitHubRepository.Trim();
        }

        if (string.IsNullOrWhiteSpace(DockerImage)) return null;
        var imageWithoutTag = DockerImage.Split(':')[0];
        var segments = imageWithoutTag.Split('/');
        if (segments.Length >= 2)
        {
            var owner = segments[^2];
            var repo = segments[^1];
            return $"{owner}/{repo}";
        }
        return null;
    }

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
