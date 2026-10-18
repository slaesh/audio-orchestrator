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
    string? ErrorMessage = null
);

[TranspilationSource]
public record SpotifyRecentContextDto(string Uri, string Name);

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
    IReadOnlyList<SpotifyRecentContextDto>? RecentContexts = null
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
    DateTime CreatedAt
);

[TranspilationSource]
public record RadioPluginCreateRequest(
    string Id,
    string Name,
    string DockerImage,
    List<string> UrlPatterns,
    bool Enabled = true
);

[TranspilationSource]
public record RadioPluginUpdateRequest(
    string Name,
    string DockerImage,
    List<string> UrlPatterns,
    bool Enabled
);

[TranspilationSource]
public record RadioEventPayload(
    string StreamId,
    string? Title,
    string? Artist,
    string? CoverUrl
);

[TranspilationSource]
public record FullStateDto(
    List<GroupDto> Groups, 
    List<RoomDto> UnassignedRooms, 
    List<StreamDto> RadioStreams, 
    List<SpotifySourceDto> SpotifySources,
    List<StreamDto> SnapcastStreams,
    List<RadioPluginDto>? RadioPlugins = null);

[TranspilationSource]
public record SpotifyMetadataDto(
    string TrackId,
    string? Title,
    string? Artist,
    string? Album,
    int? DurationMs,
    string? CoverUrl
);

