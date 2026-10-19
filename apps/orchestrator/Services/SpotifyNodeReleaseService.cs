using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AudioOrchestrator.Contracts;
using Microsoft.Extensions.Caching.Memory;

namespace AudioOrchestrator.Services;

public interface ISpotifyNodeReleaseService
{
    Task<IReadOnlyList<SpotifyReleaseDto>> GetReleasesAsync(string? currentVersion = null, bool forceRefresh = false);
    Task<string?> GetLatestCompatibleVersionAsync(string currentVersion);
    Task<IReadOnlyList<SpotifyReleaseDto>> GetReleaseHistoryAsync(string currentVersion, string? targetVersion = null);
}

public class SpotifyNodeReleaseService : ISpotifyNodeReleaseService
{
    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SpotifyNodeReleaseService> _logger;
    private const string CacheKey = "SpotifyNodeReleases";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

    public SpotifyNodeReleaseService(HttpClient httpClient, IMemoryCache cache, ILogger<SpotifyNodeReleaseService> logger)
    {
        _httpClient = httpClient;
        _cache = cache;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SpotifyReleaseDto>> GetReleasesAsync(string? currentVersion = null, bool forceRefresh = false)
    {
        if (!forceRefresh && _cache.TryGetValue(CacheKey, out List<GitHubReleaseRaw>? cachedReleases) && cachedReleases != null)
        {
            return MapToDtos(cachedReleases, currentVersion);
        }

        try
        {
            var rawList = await _httpClient.GetFromJsonAsync<List<GitHubReleaseRaw>>("https://api.github.com/repos/slaesh/librespot-server/releases");
            if (rawList != null)
            {
                _cache.Set(CacheKey, rawList, CacheDuration);
                return MapToDtos(rawList, currentVersion);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query releases from GitHub API for slaesh/librespot-server");
        }

        return Array.Empty<SpotifyReleaseDto>();
    }

    public async Task<string?> GetLatestCompatibleVersionAsync(string currentVersion)
    {
        var releases = await GetReleasesAsync(currentVersion);
        var compatible = releases
            .Where(r => r.IsCompatible)
            .OrderByDescending(r => ParseSemVer(r.Version))
            .FirstOrDefault();

        return compatible?.Version;
    }

    public async Task<IReadOnlyList<SpotifyReleaseDto>> GetReleaseHistoryAsync(string currentVersion, string? targetVersion = null)
    {
        var releases = await GetReleasesAsync(currentVersion);
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

    private static IReadOnlyList<SpotifyReleaseDto> MapToDtos(IEnumerable<GitHubReleaseRaw> rawReleases, string? currentVersion)
    {
        var list = new List<SpotifyReleaseDto>();
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

            list.Add(new SpotifyReleaseDto(
                TagName: rawTag,
                Version: cleanVersion,
                Name: string.IsNullOrWhiteSpace(r.Name) ? rawTag : r.Name,
                Body: r.Body ?? string.Empty,
                PublishedAt: r.PublishedAt ?? DateTime.UtcNow,
                IsCompatible: isCompatible
            ));
        }

        return list;
    }

    public static bool IsNonBreakingUpgrade(string currentVersion, string targetVersion)
    {
        var current = ParseSemVer(currentVersion);
        var target = ParseSemVer(targetVersion);

        if (current == null || target == null) return false;

        // Target must be strictly newer
        if (target.CompareTo(current) <= 0) return false;

        // For 0.x.y: breaking changes occur on minor bumps (0.3 -> 0.4).
        // Only patch bumps within the same minor (0.3.0 -> 0.3.1) are non-breaking.
        if (current.Major == 0)
        {
            return target.Major == 0 && target.Minor == current.Minor;
        }

        // For >= 1.0.0: Minor and Patch bumps within the same Major are non-breaking.
        return target.Major == current.Major;
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

    public class GitHubReleaseRaw
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("published_at")]
        public DateTime? PublishedAt { get; set; }
    }
}
