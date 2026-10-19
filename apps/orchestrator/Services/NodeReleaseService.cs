using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AudioOrchestrator.Contracts;
using Microsoft.Extensions.Caching.Memory;

namespace AudioOrchestrator.Services;

public interface INodeReleaseService
{
    Task<IReadOnlyList<NodeReleaseDto>> GetReleasesAsync(string repository, string? currentVersion = null, bool forceRefresh = false);
    Task<string?> GetLatestCompatibleVersionAsync(string repository, string currentVersion);
    Task<IReadOnlyList<NodeReleaseDto>> GetReleaseHistoryAsync(string repository, string currentVersion, string? targetVersion = null);
}

public interface ISpotifyNodeReleaseService
{
    Task<IReadOnlyList<SpotifyReleaseDto>> GetReleasesAsync(string? currentVersion = null, bool forceRefresh = false);
    Task<string?> GetLatestCompatibleVersionAsync(string currentVersion);
    Task<IReadOnlyList<SpotifyReleaseDto>> GetReleaseHistoryAsync(string currentVersion, string? targetVersion = null);
}

public class NodeReleaseService : INodeReleaseService, ISpotifyNodeReleaseService
{
    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly ILogger<NodeReleaseService> _logger;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public NodeReleaseService(HttpClient httpClient, IMemoryCache cache, ILogger<NodeReleaseService> logger)
    {
        _httpClient = httpClient;
        _cache = cache;
        _logger = logger;
    }

    // ISpotifyNodeReleaseService backward compatibility
    async Task<IReadOnlyList<SpotifyReleaseDto>> ISpotifyNodeReleaseService.GetReleasesAsync(string? currentVersion, bool forceRefresh)
    {
        var list = await GetReleasesAsync(OrchestratorConstants.SpotifyRepository, currentVersion, forceRefresh);
        return list.Select(r => new SpotifyReleaseDto(r.TagName, r.Version, r.Name, r.Body, r.PublishedAt, r.IsCompatible, r.Url)).ToList();
    }

    Task<string?> ISpotifyNodeReleaseService.GetLatestCompatibleVersionAsync(string currentVersion)
    {
        return GetLatestCompatibleVersionAsync(OrchestratorConstants.SpotifyRepository, currentVersion);
    }

    async Task<IReadOnlyList<SpotifyReleaseDto>> ISpotifyNodeReleaseService.GetReleaseHistoryAsync(string currentVersion, string? targetVersion)
    {
        var list = await GetReleaseHistoryAsync(OrchestratorConstants.SpotifyRepository, currentVersion, targetVersion);
        return list.Select(r => new SpotifyReleaseDto(r.TagName, r.Version, r.Name, r.Body, r.PublishedAt, r.IsCompatible, r.Url)).ToList();
    }

    // INodeReleaseService implementation
    public async Task<IReadOnlyList<NodeReleaseDto>> GetReleasesAsync(string repository, string? currentVersion = null, bool forceRefresh = false)
    {
        if (string.IsNullOrWhiteSpace(repository)) return Array.Empty<NodeReleaseDto>();

        var cacheKey = $"NodeReleases:{repository.Trim().ToLowerInvariant()}";
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out List<GitHubReleaseRaw>? cachedReleases) && cachedReleases != null)
        {
            return MapToDtos(cachedReleases, currentVersion);
        }

        try
        {
            using var response = await _httpClient.GetAsync($"https://api.github.com/repos/{repository.Trim()}/releases");
            if (response.IsSuccessStatusCode)
            {
                var rawList = await response.Content.ReadFromJsonAsync<List<GitHubReleaseRaw>>();
                if (rawList != null)
                {
                    _cache.Set(cacheKey, rawList, CacheDuration);
                    return MapToDtos(rawList, currentVersion);
                }
            }
            else
            {
                _logger.LogDebug("GitHub API returned {StatusCode} for {Repository}", response.StatusCode, repository);
                _cache.Set(cacheKey, new List<GitHubReleaseRaw>(), CacheDuration);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query releases from GitHub API for {Repository}", repository);
            _cache.Set(cacheKey, new List<GitHubReleaseRaw>(), CacheDuration);
        }

        return Array.Empty<NodeReleaseDto>();
    }

    public async Task<string?> GetLatestCompatibleVersionAsync(string repository, string currentVersion)
    {
        var releases = await GetReleasesAsync(repository, currentVersion);
        var compatible = releases
            .Where(r => r.IsCompatible)
            .OrderByDescending(r => ParseSemVer(r.Version))
            .FirstOrDefault();

        return compatible?.Version;
    }

    public async Task<IReadOnlyList<NodeReleaseDto>> GetReleaseHistoryAsync(string repository, string currentVersion, string? targetVersion = null)
    {
        var releases = await GetReleasesAsync(repository, currentVersion);
        var currentSemVer = ParseSemVer(currentVersion);
        var targetSemVer = string.IsNullOrWhiteSpace(targetVersion) ? null : ParseSemVer(targetVersion);

        return releases
            .Where(r => {
                var releaseSemVer = ParseSemVer(r.Version);
                if (releaseSemVer == null || currentSemVer == null) return false;
                
                // Release must be strictly newer than current version
                if (releaseSemVer.CompareTo(currentSemVer) <= 0) return false;

                // If target version is specified, must be <= target version
                if (targetSemVer != null && releaseSemVer.CompareTo(targetSemVer) > 0) return false;

                return true;
            })
            .OrderByDescending(r => ParseSemVer(r.Version))
            .ToList();
    }

    private static IReadOnlyList<NodeReleaseDto> MapToDtos(IEnumerable<GitHubReleaseRaw> rawReleases, string? currentVersion)
    {
        var list = new List<NodeReleaseDto>();
        foreach (var r in rawReleases)
        {
            if (r.Draft) continue;

            var rawTag = r.TagName ?? string.Empty;
            var cleanVersion = rawTag.TrimStart('v', 'V').Trim();
            if (string.IsNullOrWhiteSpace(cleanVersion)) continue;

            bool isCompatible = false;
            if (!string.IsNullOrWhiteSpace(currentVersion))
            {
                isCompatible = IsNonBreakingUpgrade(currentVersion, cleanVersion);
            }

            list.Add(new NodeReleaseDto(
                TagName: rawTag,
                Version: cleanVersion,
                Name: string.IsNullOrWhiteSpace(r.Name) ? rawTag : r.Name,
                Body: r.Body ?? string.Empty,
                PublishedAt: r.PublishedAt ?? DateTime.UtcNow,
                IsCompatible: isCompatible,
                Url: r.HtmlUrl
            ));
        }

        return list;
    }

    public static bool IsNonBreakingUpgrade(string currentVersionStr, string targetVersionStr)
    {
        var current = ParseSemVer(currentVersionStr);
        var target = ParseSemVer(targetVersionStr);

        if (current == null || target == null) return false;

        // Target must be strictly newer
        if (target.CompareTo(current) <= 0) return false;

        // SemVer 2.0 rules:
        // For Major == 0 (0.x.y): Minor bumps are breaking changes!
        // Only patch bumps (0.x.y -> 0.x.z) are non-breaking.
        if (current.Major == 0)
        {
            return target.Major == 0 && target.Minor == current.Minor && target.Patch > current.Patch;
        }

        // For Major >= 1: Major version must match, minor or patch bump is non-breaking.
        if (target.Major != current.Major) return false;

        return true;
    }

    public static SemVer? ParseSemVer(string? versionStr)
    {
        if (string.IsNullOrWhiteSpace(versionStr)) return null;

        var clean = versionStr.Trim().TrimStart('v', 'V');
        var dashIdx = clean.IndexOf('-');
        string? prerelease = null;
        if (dashIdx >= 0)
        {
            prerelease = clean[(dashIdx + 1)..];
            clean = clean[..dashIdx];
        }

        var plusIdx = clean.IndexOf('+');
        if (plusIdx >= 0) clean = clean[..plusIdx];

        var parts = clean.Split('.');
        if (parts.Length < 1) return null;

        if (!int.TryParse(parts[0], out int major)) return null;
        int minor = parts.Length > 1 && int.TryParse(parts[1], out int m) ? m : 0;
        int patch = parts.Length > 2 && int.TryParse(parts[2], out int p) ? p : 0;

        return new SemVer(major, minor, patch, prerelease);
    }

    public record SemVer(int Major, int Minor, int Patch, string? Prerelease) : IComparable<SemVer>
    {
        public int CompareTo(SemVer? other)
        {
            if (other == null) return 1;
            if (Major != other.Major) return Major.CompareTo(other.Major);
            if (Minor != other.Minor) return Minor.CompareTo(other.Minor);
            if (Patch != other.Patch) return Patch.CompareTo(other.Patch);

            // Prerelease versions have lower precedence than normal versions
            if (Prerelease == null && other.Prerelease != null) return 1;
            if (Prerelease != null && other.Prerelease == null) return -1;
            if (Prerelease != null && other.Prerelease != null) return string.Compare(Prerelease, other.Prerelease, StringComparison.Ordinal);

            return 0;
        }

        public override string ToString() => $"{Major}.{Minor}.{Patch}{(string.IsNullOrEmpty(Prerelease) ? "" : $"-{Prerelease}")}";
    }

    private sealed class GitHubReleaseRaw
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("published_at")]
        public DateTime? PublishedAt { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }
    }
}
