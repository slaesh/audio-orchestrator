namespace AudioOrchestrator.Contracts;

public static class OrchestratorConstants
{
    public const string FifoBaseDir = "/tmp/audio_fifos";
    
    // Container name prefixes
    public const string ContainerPrefix = "orch_";
    public const string RadioContainerPrefix = "orch_radio_";
    public const string SpotifyContainerPrefix = "orch_spotify_";
    
    // Stream/FIFO path prefixes
    public const string RadioPrefix = "radio_";
    public const string SpotifyPrefix = "spotify_";
    public const string AnnouncementStreamId = "announcement";
    public const string AnnouncementFifoPath = $"{FifoBaseDir}/announcement";
    
    // Persistence
    public const string LibrespotCacheVolume = "librespot_cache";

    // Docker Images & Repositories
    public const string SpotifyRepository = "slaesh/librespot-server";
    public const string DefaultSpotifyNodeImage = "ghcr.io/slaesh/librespot-server:0.3.1";
    public const string DefaultFfmpegImage = "jrottenberg/ffmpeg:4.1-alpine";
    public const string DefaultAudalaxyPluginImage = "ghcr.io/slaesh/radio-node-audalaxy:latest";

    public static string ExtractImageTag(string? image, string defaultVersion = "")
    {
        if (string.IsNullOrWhiteSpace(image)) return defaultVersion;
        var lastColon = image.LastIndexOf(':');
        if (lastColon >= 0 && !image[lastColon..].Contains('/'))
        {
            var tag = image[(lastColon + 1)..].Trim().TrimStart('v', 'V');
            if (!string.IsNullOrWhiteSpace(tag))
            {
                return tag;
            }
        }
        return defaultVersion;
    }
}
