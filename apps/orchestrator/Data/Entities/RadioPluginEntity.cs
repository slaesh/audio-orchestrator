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

    public string CurrentVersion { get; set; } = "";

    public string? GitHubRepository { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string GetEffectiveVersion()
    {
        if (!string.IsNullOrWhiteSpace(CurrentVersion)) return CurrentVersion;
        if (!string.IsNullOrWhiteSpace(DockerImage))
        {
            var parts = DockerImage.Split(':');
            if (parts.Length > 1 && parts[^1] != "latest")
            {
                return parts[^1].TrimStart('v');
            }
        }
        return "0.1.0";
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
