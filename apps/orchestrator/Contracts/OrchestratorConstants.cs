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
    
    // Persistence
    public const string LibrespotCacheVolume = "librespot_cache";

    // Docker Images & Repositories
    public const string SpotifyRepository = "slaesh/librespot-server";
    public const string SpotifyNodeVersion = "0.3.0";
    public const string DefaultSpotifyNodeImage = "ghcr.io/slaesh/librespot-server:" + SpotifyNodeVersion;
    public const string DefaultFfmpegImage = "jrottenberg/ffmpeg:4.1-alpine";
    public const string DefaultAudalaxyPluginImage = "ghcr.io/slaesh/radio-node-audalaxy:latest";
}
