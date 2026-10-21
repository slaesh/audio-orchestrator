using Tapper;

namespace AudioOrchestrator.Contracts;

[TranspilationSource]
public record RoomDto(string Id, string Name, string? FriendlyName, string? HostName, bool Connected, int VolumePercent, bool VolumeMuted, int MixVolume);

[TranspilationSource]
public record StreamDto(
    string Id, 
    string Name, 
    string Status, 
    string? Url,
    string? CurrentTitle = null,
    string? CurrentArtist = null,
    string? CurrentCoverUrl = null,
    string? ErrorMessage = null,
    string? CurrentVersion = null,
    string? LatestCompatibleVersion = null,
    bool HasUpdate = false,
    string? PluginId = null,
    bool ContainerRunning = true
);

[TranspilationSource]
public record SpotifyRecentContextDto(string Uri, string Name);

[TranspilationSource]
public record NodeReleaseDto(
    string TagName,
    string Version,
    string Name,
    string Body,
    DateTime PublishedAt,
    bool IsCompatible,
    string? Url = null
);

[TranspilationSource]
public record SpotifyReleaseDto(
    string TagName,
    string Version,
    string Name,
    string Body,
    DateTime PublishedAt,
    bool IsCompatible,
    string? Url = null
);

[TranspilationSource]
public record UpdateSettingsRequest(string VolumeProfile);

[TranspilationSource]
public record NodeUpgradeRequest(string? TargetVersion = null);

[TranspilationSource]
public record UpgradeSpotifySourceRequest(string? TargetVersion = null);

[TranspilationSource]
public record SpotifySourceDto(
    string Id, 
    string Name, 
    bool IsActive, 
    bool IsConnected,
    string Status, 
    int MasterVolume, 
    int MaxVolume,
    string? CurrentTrackId = null,
    string? CurrentTitle = null,
    string? CurrentArtist = null,
    string? CurrentAlbum = null,
    int? CurrentDurationMs = null,
    string? CurrentCoverUrl = null,
    int? CurrentPositionMs = null,
    int? HeartbeatIntervalMs = null,
    long? LastUpdateTimestamp = null,
    string? ErrorMessage = null,
    string? ContextUri = null,
    string? ContextName = null,
    IReadOnlyList<SpotifyRecentContextDto>? RecentContexts = null,
    string CurrentVersion = OrchestratorConstants.SpotifyNodeVersion,
    string? LatestCompatibleVersion = null,
    bool HasUpdate = false,
    bool ContainerRunning = true
);

[TranspilationSource]
public record GroupDto(string Id, string Name, string? StreamId, List<RoomDto> Rooms, int MasterVolume, bool IsMuted);

[TranspilationSource]
public record RadioPluginDto(
    string Id,
    string Name,
    string DockerImage,
    List<string> UrlPatterns,
    bool Enabled,
    DateTime CreatedAt,
    string CurrentVersion = "0.1.0",
    string? LatestCompatibleVersion = null,
    bool HasUpdate = false,
    string? GitHubRepository = null
);

[TranspilationSource]
public record RadioPluginCreateRequest(
    string Id,
    string Name,
    string DockerImage,
    List<string> UrlPatterns,
    bool Enabled = true,
    string? GitHubRepository = null
);

[TranspilationSource]
public record RadioPluginUpdateRequest(
    string Name,
    string DockerImage,
    List<string> UrlPatterns,
    bool Enabled,
    string? GitHubRepository = null
);

[TranspilationSource]
public record RadioEventPayload(
    string StreamId,
    string? Title,
    string? Artist,
    string? CoverUrl,
    string? Version = null
);

[TranspilationSource]
public record FullStateDto(
    List<GroupDto> Groups, 
    List<RoomDto> UnassignedRooms, 
    List<StreamDto> RadioStreams, 
    List<SpotifySourceDto> SpotifySources,
    List<StreamDto> SnapcastStreams,
    List<RadioPluginDto>? RadioPlugins = null,
    string VolumeProfile = "cubic");

[TranspilationSource]
public record SpotifyMetadataDto(
    string TrackId,
    string? Title,
    string? Artist,
    string? Album,
    int? DurationMs,
    string? CoverUrl
);

