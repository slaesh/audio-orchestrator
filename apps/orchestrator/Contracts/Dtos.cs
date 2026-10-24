using System.Text.Json.Serialization;
using Tapper;

namespace AudioOrchestrator.Contracts;

[TranspilationSource]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VolumeProfile
{
    [JsonStringEnumMemberName("cubic")]
    Cubic,
    [JsonStringEnumMemberName("linear")]
    Linear
}

[TranspilationSource]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StreamStatus
{
    [JsonStringEnumMemberName("idle")]
    Idle,
    [JsonStringEnumMemberName("playing")]
    Playing
}

[TranspilationSource]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NodeType
{
    [JsonStringEnumMemberName("spotify")]
    Spotify,
    [JsonStringEnumMemberName("radioPlugin")]
    RadioPlugin
}

[TranspilationSource]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UpgradePhase
{
    [JsonStringEnumMemberName("idle")]
    Idle,
    [JsonStringEnumMemberName("pulling")]
    Pulling,
    [JsonStringEnumMemberName("restarting")]
    Restarting,
    [JsonStringEnumMemberName("success")]
    Success,
    [JsonStringEnumMemberName("failed")]
    Failed
}

[TranspilationSource]
public record RoomDto(string Id, string Name, string? FriendlyName, string? HostName, bool Connected, int VolumePercent, bool VolumeMuted, int MixVolume);

[TranspilationSource]
public record StreamDto(
    string Id, 
    string Name, 
    StreamStatus Status, 
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
public record HomeAssistantSettingsDto(
    bool Enabled,
    string? Host,
    int Port,
    string? Username,
    string? Password,
    bool HasPassword,
    bool UseTls,
    string DiscoveryPrefix,
    string BaseTopic,
    bool IsConnected
);

[TranspilationSource]
public record TestMqttConnectionRequest(
    string Host,
    int Port,
    string? Username,
    string? Password,
    bool UseTls
);

[TranspilationSource]
public record TestMqttConnectionResponse(
    bool Success,
    string Message
);

[TranspilationSource]
public record SettingsDto(
    VolumeProfile VolumeProfile,
    HomeAssistantSettingsDto HomeAssistant
);

[TranspilationSource]
public record UpdateSettingsRequest(
    VolumeProfile VolumeProfile,
    HomeAssistantSettingsDto? HomeAssistant = null
);

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
    StreamStatus Status, 
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
    string CurrentVersion = "",
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
    string CurrentVersion = "",
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
    VolumeProfile VolumeProfile = VolumeProfile.Cubic);

[TranspilationSource]
public record SpotifyMetadataDto(
    string TrackId,
    string? Title,
    string? Artist,
    string? Album,
    int? DurationMs,
    string? CoverUrl
);

[TranspilationSource]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BroadcastSourceType
{
    [JsonStringEnumMemberName("url")]
    Url,
    [JsonStringEnumMemberName("upload")]
    Upload
}

[TranspilationSource]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BroadcastJobStatus
{
    [JsonStringEnumMemberName("queued")]
    Queued,
    [JsonStringEnumMemberName("playing")]
    Playing,
    [JsonStringEnumMemberName("completed")]
    Completed,
    [JsonStringEnumMemberName("failed")]
    Failed,
    [JsonStringEnumMemberName("cancelled")]
    Cancelled
}

[TranspilationSource]
public record BroadcastPlayRequest(
    string? AudioUrl = null,
    List<string>? GroupIds = null,
    List<string>? RoomIds = null,
    int? Volume = null,
    int? FadeDurationMs = null,
    bool PausePlayback = false,
    int? TimeoutSeconds = 60,
    bool Interrupt = true
);

[TranspilationSource]
public record BroadcastPlayResult(
    bool Success,
    string? Message = null,
    int DurationMs = 0,
    string? JobId = null
);

[TranspilationSource]
public record BroadcastStatusDto(
    bool IsPlaying,
    string? CurrentAnnouncementId,
    List<string> ActiveTargetGroupIds,
    List<string>? ActiveSnapcastGroupIds = null
);

[TranspilationSource]
public record BroadcastJobDto(
    string Id,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? AudioUrl,
    BroadcastSourceType SourceType,
    List<string>? TargetGroupIds,
    List<string>? TargetRoomIds,
    int? Volume,
    int? FadeDurationMs,
    bool PausePlayback,
    BroadcastJobStatus Status,
    int DurationMs,
    string? ErrorMessage
);

