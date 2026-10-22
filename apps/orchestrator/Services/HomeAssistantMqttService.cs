using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AudioOrchestrator.Contracts;
using AudioOrchestrator.Data;
using AudioOrchestrator.Data.Entities;
using Microsoft.EntityFrameworkCore;
using MQTTnet;

namespace AudioOrchestrator.Services;

public class HomeAssistantMqttService : BackgroundService, IHomeAssistantMqttService
{
    private const string OffSourceLabel = "Aus (Stille)";
    private readonly ILogger<HomeAssistantMqttService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly MqttClientFactory _mqttFactory = new();

    private IMqttClient? _mqttClient;
    private OrchestratorSettingsEntity? _currentSettings;
    private FullStateDto? _lastFullState;
    private readonly HashSet<string> _discoveredGroupIds = new();
    private string _lastDiscoveryHash = string.Empty;
    private bool _forceDiscovery = true;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly SemaphoreSlim _publishLock = new(1, 1);

    public bool IsConnected => _mqttClient?.IsConnected ?? false;

    public HomeAssistantMqttService(
        ILogger<HomeAssistantMqttService> logger,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("HomeAssistantMqttService starting up.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unexpected error in HomeAssistantMqttService connection loop.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        await DisconnectAsync();
    }

    public async Task<TestMqttConnectionResponse> TestConnectionAsync(TestMqttConnectionRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Host))
        {
            return new TestMqttConnectionResponse(false, "Host darf nicht leer sein.");
        }

        if (request.Port <= 0 || request.Port > 65535)
        {
            return new TestMqttConnectionResponse(false, "Ungültiger Port (1-65535).");
        }

        var client = _mqttFactory.CreateMqttClient();
        try
        {
            var optionsBuilder = new MqttClientOptionsBuilder()
                .WithTcpServer(request.Host, request.Port)
                .WithTimeout(TimeSpan.FromSeconds(5))
                .WithClientId($"AudioOrchestrator_Test_{Guid.NewGuid():N}");

            if (!string.IsNullOrWhiteSpace(request.Username))
            {
                optionsBuilder.WithCredentials(request.Username, request.Password ?? string.Empty);
            }

            if (request.UseTls)
            {
                optionsBuilder.WithTlsOptions(o => o.UseTls());
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var result = await client.ConnectAsync(optionsBuilder.Build(), cts.Token);
            if (result.ResultCode == MqttClientConnectResultCode.Success)
            {
                await client.DisconnectAsync(cancellationToken: ct);
                return new TestMqttConnectionResponse(true, "Verbindung zum MQTT Broker erfolgreich hergestellt!");
            }

            return new TestMqttConnectionResponse(false, $"Verbindungsaufbau abgelehnt: {result.ResultCode} ({result.ReasonString})");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MQTT connection test failed for {Host}:{Port}", request.Host, request.Port);
            return new TestMqttConnectionResponse(false, $"Verbindung fehlgeschlagen: {ex.Message}");
        }
        finally
        {
            client.Dispose();
        }
    }

    public async Task ReloadConfigurationAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Reloading Home Assistant MQTT configuration...");
        await _connectionLock.WaitAsync(ct);
        try
        {
            await DisconnectInternalAsync();
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            _currentSettings = await db.Settings.FirstOrDefaultAsync(ct);
            await ConnectInternalAsync(ct);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task NotifyFullStateAsync(FullStateDto state, CancellationToken ct = default)
    {
        _lastFullState = state;
        if (!IsConnected || _currentSettings == null || !_currentSettings.HomeAssistantEnabled) return;

        await PublishDiscoveryAndStatesAsync(state, ct);
    }

    public async Task NotifyRadioMetadataAsync(string streamId, string? title, string? artist, string? coverUrl, CancellationToken ct = default)
    {
        if (!IsConnected || _lastFullState == null) return;
        await RefreshAndPublishAsync(ct);
    }

    public async Task NotifySpotifyEventAsync(SpotifyEventDto spotifyEvent, CancellationToken ct = default)
    {
        if (!IsConnected || _lastFullState == null) return;
        await RefreshAndPublishAsync(ct);
    }

    public async Task NotifyGroupVolumeChangedAsync(string groupId, int volume, bool isMuted, CancellationToken ct = default)
    {
        if (!IsConnected || _lastFullState == null) return;
        await RefreshAndPublishAsync(ct);
    }

    private async Task RefreshAndPublishAsync(CancellationToken ct, bool forceDiscovery = false)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var stateBuilder = scope.ServiceProvider.GetRequiredService<FullStateBuilder>();
            var freshState = await stateBuilder.BuildAsync();
            _lastFullState = freshState;
            await PublishDiscoveryAndStatesAsync(freshState, ct, forceDiscovery);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh state and publish to MQTT");
        }
    }

    private async Task EnsureConnectionAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var settings = await db.Settings.FirstOrDefaultAsync(ct);
        var stateBuilder = scope.ServiceProvider.GetRequiredService<FullStateBuilder>();

        if (settings == null || !settings.HomeAssistantEnabled || string.IsNullOrWhiteSpace(settings.MqttHost))
        {
            if (IsConnected)
            {
                await _connectionLock.WaitAsync(ct);
                try { await DisconnectInternalAsync(); }
                finally { _connectionLock.Release(); }
            }
            return;
        }

        if (IsConnected && HasSameConfig(_currentSettings, settings))
        {
            // Heartbeat: refresh snapshot to pick up structural or external updates, then publish
            var freshState = await stateBuilder.BuildAsync();
            _lastFullState = freshState;
            await PublishDiscoveryAndStatesAsync(freshState, ct);
            return;
        }

        await _connectionLock.WaitAsync(ct);
        try
        {
            await DisconnectInternalAsync();
            _currentSettings = settings;
            await ConnectInternalAsync(ct);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private static bool HasSameConfig(OrchestratorSettingsEntity? a, OrchestratorSettingsEntity? b)
    {
        if (a == null || b == null) return false;
        return a.HomeAssistantEnabled == b.HomeAssistantEnabled
               && a.MqttHost == b.MqttHost
               && a.MqttPort == b.MqttPort
               && a.MqttUsername == b.MqttUsername
               && a.MqttPassword == b.MqttPassword
               && a.MqttUseTls == b.MqttUseTls
               && a.MqttDiscoveryPrefix == b.MqttDiscoveryPrefix
               && a.MqttBaseTopic == b.MqttBaseTopic;
    }

    private async Task ConnectInternalAsync(CancellationToken ct)
    {
        if (_currentSettings == null || !_currentSettings.HomeAssistantEnabled || string.IsNullOrWhiteSpace(_currentSettings.MqttHost))
        {
            return;
        }

        try
        {
            _mqttClient = _mqttFactory.CreateMqttClient();

            var baseTopic = string.IsNullOrWhiteSpace(_currentSettings.MqttBaseTopic) ? "audio_orchestrator" : _currentSettings.MqttBaseTopic.TrimEnd('/');
            var statusTopic = $"{baseTopic}/bridge/status";

            var optionsBuilder = new MqttClientOptionsBuilder()
                .WithTcpServer(_currentSettings.MqttHost, _currentSettings.MqttPort)
                .WithClientId($"AudioOrchestrator_{Guid.NewGuid():N}")
                .WithWillTopic(statusTopic)
                .WithWillPayload("offline")
                .WithWillRetain(true);

            if (!string.IsNullOrWhiteSpace(_currentSettings.MqttUsername))
            {
                optionsBuilder.WithCredentials(_currentSettings.MqttUsername, _currentSettings.MqttPassword ?? string.Empty);
            }

            if (_currentSettings.MqttUseTls)
            {
                optionsBuilder.WithTlsOptions(o => o.UseTls());
            }

            _mqttClient.ApplicationMessageReceivedAsync += HandleIncomingMessageAsync;

            _logger.LogInformation("Connecting to MQTT broker at {Host}:{Port}...", _currentSettings.MqttHost, _currentSettings.MqttPort);
            var connectResult = await _mqttClient.ConnectAsync(optionsBuilder.Build(), ct);

            if (connectResult.ResultCode != MqttClientConnectResultCode.Success)
            {
                _logger.LogWarning("MQTT connect failed with result: {ResultCode}", connectResult.ResultCode);
                return;
            }

            _logger.LogInformation("MQTT connected successfully. Publishing bridge online status.");

            _forceDiscovery = true;

            // 1. Publish birth message
            await PublishAsync(statusTopic, "online", retain: true, ct);

            // 2. Subscribe to command topics & HA status topic
            var subOptions = _mqttFactory.CreateSubscribeOptionsBuilder()
                .WithTopicFilter($"{baseTopic}/groups/+/set/#")
                .WithTopicFilter($"{_currentSettings.MqttDiscoveryPrefix}/status")
                .Build();

            await _mqttClient.SubscribeAsync(subOptions, ct);

            // 3. Initial sync
            using var scope = _serviceProvider.CreateScope();
            var stateBuilder = scope.ServiceProvider.GetRequiredService<FullStateBuilder>();
            _lastFullState = await stateBuilder.BuildAsync();
            await PublishDiscoveryAndStatesAsync(_lastFullState, ct, forceDiscovery: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to MQTT broker");
        }
    }

    private async Task DisconnectInternalAsync()
    {
        if (_mqttClient != null)
        {
            try
            {
                if (_mqttClient.IsConnected && _currentSettings != null)
                {
                    var baseTopic = string.IsNullOrWhiteSpace(_currentSettings.MqttBaseTopic) ? "audio_orchestrator" : _currentSettings.MqttBaseTopic.TrimEnd('/');
                    await PublishAsync($"{baseTopic}/bridge/status", "offline", retain: true, CancellationToken.None);
                }
                await _mqttClient.DisconnectAsync(cancellationToken: CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error during MQTT disconnect");
            }
            finally
            {
                _mqttClient.Dispose();
                _mqttClient = null;
                _lastDiscoveryHash = string.Empty;
                _forceDiscovery = true;
            }
        }
    }

    private async Task DisconnectAsync()
    {
        await _connectionLock.WaitAsync();
        try
        {
            await DisconnectInternalAsync();
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async Task PublishAsync(string topic, string payload, bool retain = false, CancellationToken ct = default)
    {
        if (!IsConnected || _mqttClient == null) return;

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(retain)
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await _mqttClient.PublishAsync(message, ct);
    }

    private async Task PublishDiscoveryAndStatesAsync(FullStateDto state, CancellationToken ct, bool forceDiscovery = false)
    {
        if (!IsConnected || _currentSettings == null) return;

        await _publishLock.WaitAsync(ct);
        try
        {
            var baseTopic = string.IsNullOrWhiteSpace(_currentSettings.MqttBaseTopic) ? "audio_orchestrator" : _currentSettings.MqttBaseTopic.TrimEnd('/');
            var discPrefix = string.IsNullOrWhiteSpace(_currentSettings.MqttDiscoveryPrefix) ? "homeassistant" : _currentSettings.MqttDiscoveryPrefix.TrimEnd('/');
            var bridgeStatusTopic = $"{baseTopic}/bridge/status";

            // Source list formatted consistently with HACS integration
            var sourceList = new List<string> { OffSourceLabel };
            foreach (var r in state.RadioStreams)
            {
                if (!string.IsNullOrWhiteSpace(r.Name)) sourceList.Add($"Radio: {r.Name}");
            }
            foreach (var s in state.SpotifySources)
            {
                if (!string.IsNullOrWhiteSpace(s.Name)) sourceList.Add($"Spotify: {s.Name}");
            }

            var currentGroupIds = state.Groups.Select(g => g.Id).ToHashSet();

            // 1. Remove discovery & state for groups that were deleted
            var removedGroupIds = _discoveredGroupIds.Except(currentGroupIds).ToList();
            foreach (var removedId in removedGroupIds)
            {
                await UnpublishGroupDiscoveryAsync(discPrefix, baseTopic, removedId, ct);
                _discoveredGroupIds.Remove(removedId);
            }

            // 2. Check if discovery needs re-publishing (group structure, names, or source list changed, or forced)
            var discoveryHash = $"{discPrefix}|{baseTopic}|{string.Join(",", sourceList)}|{string.Join(";", state.Groups.Select(g => $"{g.Id}:{g.Name}"))}";
            bool discoveryNeedsUpdate = forceDiscovery || _forceDiscovery || discoveryHash != _lastDiscoveryHash;
            _forceDiscovery = false;

            foreach (var group in state.Groups)
            {
                var groupId = group.Id;
                var groupName = string.IsNullOrWhiteSpace(group.Name) ? "Audio Gruppe" : group.Name;
                var groupStateTopic = $"{baseTopic}/groups/{groupId}/state";
                var groupCmdTopic = $"{baseTopic}/groups/{groupId}/set";

                // Determine active source info
                var activeStreamId = group.StreamId ?? "default";
                var isOff = string.IsNullOrEmpty(activeStreamId) || activeStreamId == "default";
                var isSpotify = activeStreamId.StartsWith("spotify_");
                var isRadio = activeStreamId.StartsWith("radio_");

                string currentSourceName = OffSourceLabel;
                string? mediaTitle = null;
                string? mediaArtist = null;
                string? mediaAlbum = null;
                string? mediaChannel = null;
                string? mediaPlaylist = null;
                string? contextUri = null;
                string? currentCoverUrl = null;
                string sourceType = "none";
                string playbackState = "idle";

                if (isRadio)
                {
                    sourceType = "radio";
                    var r = state.RadioStreams.FirstOrDefault(x => x.Id == activeStreamId);
                    if (r != null)
                    {
                        currentSourceName = $"Radio: {r.Name}";
                        mediaChannel = r.Name;
                        mediaTitle = r.CurrentTitle;
                        mediaArtist = r.CurrentArtist;
                        currentCoverUrl = r.CurrentCoverUrl;
                        playbackState = r.Status == StreamStatus.Playing ? "playing" : "paused";
                    }
                }
                else if (isSpotify)
                {
                    sourceType = "spotify";
                    var pureId = activeStreamId.Replace("spotify_", "");
                    var s = state.SpotifySources.FirstOrDefault(x => x.Id == pureId || $"spotify_{x.Id}" == activeStreamId);
                    if (s != null)
                    {
                        currentSourceName = $"Spotify: {s.Name}";
                        mediaTitle = s.CurrentTitle;
                        mediaArtist = s.CurrentArtist;
                        mediaAlbum = s.CurrentAlbum;
                        mediaPlaylist = s.ContextName;
                        contextUri = s.ContextUri;
                        currentCoverUrl = s.CurrentCoverUrl;
                        playbackState = s.Status == StreamStatus.Playing ? "playing" : "paused";
                    }
                }

                if (isOff)
                {
                    playbackState = "idle";
                }

                // Publish discovery only when changed or new
                if (discoveryNeedsUpdate || !_discoveredGroupIds.Contains(groupId))
                {
                    await PublishGroupDiscoveryAsync(discPrefix, baseTopic, groupId, groupName, sourceList, ct);
                    _discoveredGroupIds.Add(groupId);
                }

                // Publish transport availability topic (for play/pause/stop/next/prev buttons)
                var transportAvailabilityTopic = $"{baseTopic}/groups/{groupId}/transport_available";
                await PublishAsync(transportAvailabilityTopic, isSpotify ? "online" : "offline", retain: true, ct);

                // Publish Group State JSON
                var roomNames = group.Rooms.Select(r => r.FriendlyName ?? r.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
                var statePayload = new JsonObject
                {
                    ["state"] = playbackState,
                    ["master_volume"] = group.MasterVolume,
                    ["volume_level"] = Math.Round(Math.Clamp(group.MasterVolume / 100.0, 0.0, 1.0), 2),
                    ["is_muted"] = group.IsMuted,
                    ["source"] = currentSourceName,
                    ["stream_id"] = activeStreamId,
                    ["source_type"] = sourceType,
                    ["source_list"] = JsonSerializer.SerializeToNode(sourceList),
                    ["media_title"] = mediaTitle,
                    ["media_artist"] = mediaArtist,
                    ["media_album_name"] = mediaAlbum,
                    ["media_channel"] = mediaChannel,
                    ["station_name"] = mediaChannel,
                    ["media_playlist"] = mediaPlaylist,
                    ["playlist"] = mediaPlaylist,
                    ["context_uri"] = contextUri,
                    ["entity_picture"] = currentCoverUrl,
                    ["rooms"] = JsonSerializer.SerializeToNode(roomNames),
                    ["track_info"] = string.IsNullOrWhiteSpace(mediaTitle)
                        ? (string.IsNullOrWhiteSpace(mediaChannel) ? currentSourceName : mediaChannel)
                        : (string.IsNullOrWhiteSpace(mediaArtist) ? mediaTitle : $"{mediaArtist} - {mediaTitle}")
                };

                await PublishAsync(groupStateTopic, statePayload.ToJsonString(), retain: true, ct);
            }

            if (discoveryNeedsUpdate)
            {
                _lastDiscoveryHash = discoveryHash;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish discovery and states to MQTT");
        }
        finally
        {
            _publishLock.Release();
        }
    }

    private async Task UnpublishGroupDiscoveryAsync(string discPrefix, string baseTopic, string groupId, CancellationToken ct)
    {
        var discoveryEntities = new[]
        {
            $"select/audio_orchestrator_{groupId}/source",
            $"number/audio_orchestrator_{groupId}/volume",
            $"switch/audio_orchestrator_{groupId}/mute",
            $"sensor/audio_orchestrator_{groupId}/state",
            $"sensor/audio_orchestrator_{groupId}/track",
            $"button/audio_orchestrator_{groupId}/play",
            $"button/audio_orchestrator_{groupId}/pause",
            $"button/audio_orchestrator_{groupId}/stop",
            $"button/audio_orchestrator_{groupId}/next",
            $"button/audio_orchestrator_{groupId}/previous"
        };

        foreach (var entity in discoveryEntities)
        {
            // Empty payload removes discovery in Home Assistant
            await PublishAsync($"{discPrefix}/{entity}/config", string.Empty, retain: true, ct);
        }

        // Clear retained state & availability
        await PublishAsync($"{baseTopic}/groups/{groupId}/state", string.Empty, retain: true, ct);
        await PublishAsync($"{baseTopic}/groups/{groupId}/transport_available", string.Empty, retain: true, ct);
    }

    private static JsonObject CreateDeviceObject(string groupId, string groupName) => new()
    {
        ["identifiers"] = new JsonArray { $"audio_orchestrator_group_{groupId}" },
        ["name"] = groupName,
        ["manufacturer"] = "Audio Orchestrator",
        ["model"] = "Synchronized Audio Group",
        ["sw_version"] = "1.0.0"
    };

    private async Task PublishGroupDiscoveryAsync(
        string discPrefix,
        string baseTopic,
        string groupId,
        string groupName,
        List<string> sourceList,
        CancellationToken ct)
    {
        var groupStateTopic = $"{baseTopic}/groups/{groupId}/state";
        var groupCmdTopic = $"{baseTopic}/groups/{groupId}/set";
        var bridgeStatusTopic = $"{baseTopic}/bridge/status";
        var transportAvailTopic = $"{baseTopic}/groups/{groupId}/transport_available";

        // Entity 1: select.group_source (Source Selection)
        var selectConfig = new JsonObject
        {
            ["name"] = "Quelle",
            ["unique_id"] = $"audio_orchestrator_{groupId}_source",
            ["icon"] = "mdi:radio",
            ["state_topic"] = groupStateTopic,
            ["value_template"] = "{{ value_json.source }}",
            ["command_topic"] = $"{groupCmdTopic}/source",
            ["options"] = JsonSerializer.SerializeToNode(sourceList),
            ["availability_topic"] = bridgeStatusTopic,
            ["device"] = CreateDeviceObject(groupId, groupName)
        };
        await PublishAsync($"{discPrefix}/select/audio_orchestrator_{groupId}/source/config", selectConfig.ToJsonString(), retain: true, ct);

        // Entity 2: number.group_volume (Master Volume Slider)
        var volumeConfig = new JsonObject
        {
            ["name"] = "Lautstärke",
            ["unique_id"] = $"audio_orchestrator_{groupId}_volume",
            ["icon"] = "mdi:volume-high",
            ["state_topic"] = groupStateTopic,
            ["value_template"] = "{{ value_json.master_volume }}",
            ["command_topic"] = $"{groupCmdTopic}/volume",
            ["min"] = 0,
            ["max"] = 100,
            ["step"] = 1,
            ["mode"] = "slider",
            ["availability_topic"] = bridgeStatusTopic,
            ["device"] = CreateDeviceObject(groupId, groupName)
        };
        await PublishAsync($"{discPrefix}/number/audio_orchestrator_{groupId}/volume/config", volumeConfig.ToJsonString(), retain: true, ct);

        // Entity 3: switch.group_mute (Mute Toggle)
        var muteConfig = new JsonObject
        {
            ["name"] = "Stummschaltung",
            ["unique_id"] = $"audio_orchestrator_{groupId}_mute",
            ["icon"] = "mdi:volume-mute",
            ["state_topic"] = groupStateTopic,
            ["value_template"] = "{{ 'ON' if value_json.is_muted else 'OFF' }}",
            ["command_topic"] = $"{groupCmdTopic}/mute",
            ["payload_on"] = "ON",
            ["payload_off"] = "OFF",
            ["availability_topic"] = bridgeStatusTopic,
            ["device"] = CreateDeviceObject(groupId, groupName)
        };
        await PublishAsync($"{discPrefix}/switch/audio_orchestrator_{groupId}/mute/config", muteConfig.ToJsonString(), retain: true, ct);

        // Entity 4: sensor.group_state (Playback State: playing/paused/idle + rich attributes)
        var stateSensorConfig = new JsonObject
        {
            ["name"] = "Status",
            ["unique_id"] = $"audio_orchestrator_{groupId}_state",
            ["icon"] = "mdi:music",
            ["state_topic"] = groupStateTopic,
            ["value_template"] = "{{ value_json.state }}",
            ["json_attributes_topic"] = groupStateTopic,
            ["availability_topic"] = bridgeStatusTopic,
            ["device"] = CreateDeviceObject(groupId, groupName)
        };
        await PublishAsync($"{discPrefix}/sensor/audio_orchestrator_{groupId}/state/config", stateSensorConfig.ToJsonString(), retain: true, ct);

        // Entity 5: sensor.group_track (Current Track Text)
        var trackSensorConfig = new JsonObject
        {
            ["name"] = "Titel",
            ["unique_id"] = $"audio_orchestrator_{groupId}_track",
            ["icon"] = "mdi:music-note",
            ["state_topic"] = groupStateTopic,
            ["value_template"] = "{{ value_json.track_info }}",
            ["availability_topic"] = bridgeStatusTopic,
            ["device"] = CreateDeviceObject(groupId, groupName)
        };
        await PublishAsync($"{discPrefix}/sensor/audio_orchestrator_{groupId}/track/config", trackSensorConfig.ToJsonString(), retain: true, ct);

        // Entity 6: button.group_play (Play button, active only on Spotify)
        var playBtnConfig = new JsonObject
        {
            ["name"] = "Wiedergabe",
            ["unique_id"] = $"audio_orchestrator_{groupId}_play",
            ["icon"] = "mdi:play",
            ["command_topic"] = $"{groupCmdTopic}/command",
            ["payload_press"] = "play",
            ["availability"] = new JsonArray
            {
                new JsonObject { ["topic"] = bridgeStatusTopic },
                new JsonObject { ["topic"] = transportAvailTopic }
            },
            ["availability_mode"] = "all",
            ["device"] = CreateDeviceObject(groupId, groupName)
        };
        await PublishAsync($"{discPrefix}/button/audio_orchestrator_{groupId}/play/config", playBtnConfig.ToJsonString(), retain: true, ct);

        // Entity 7: button.group_pause (Pause button, active only on Spotify)
        var pauseBtnConfig = new JsonObject
        {
            ["name"] = "Pause",
            ["unique_id"] = $"audio_orchestrator_{groupId}_pause",
            ["icon"] = "mdi:pause",
            ["command_topic"] = $"{groupCmdTopic}/command",
            ["payload_press"] = "pause",
            ["availability"] = new JsonArray
            {
                new JsonObject { ["topic"] = bridgeStatusTopic },
                new JsonObject { ["topic"] = transportAvailTopic }
            },
            ["availability_mode"] = "all",
            ["device"] = CreateDeviceObject(groupId, groupName)
        };
        await PublishAsync($"{discPrefix}/button/audio_orchestrator_{groupId}/pause/config", pauseBtnConfig.ToJsonString(), retain: true, ct);

        // Entity 8: button.group_stop (Stop button: active only on Spotify)
        var stopBtnConfig = new JsonObject
        {
            ["name"] = "Stopp",
            ["unique_id"] = $"audio_orchestrator_{groupId}_stop",
            ["icon"] = "mdi:stop",
            ["command_topic"] = $"{groupCmdTopic}/command",
            ["payload_press"] = "stop",
            ["availability"] = new JsonArray
            {
                new JsonObject { ["topic"] = bridgeStatusTopic },
                new JsonObject { ["topic"] = transportAvailTopic }
            },
            ["availability_mode"] = "all",
            ["device"] = CreateDeviceObject(groupId, groupName)
        };
        await PublishAsync($"{discPrefix}/button/audio_orchestrator_{groupId}/stop/config", stopBtnConfig.ToJsonString(), retain: true, ct);

        // Entity 9: button.group_next (Next Track button, active only on Spotify)
        var nextBtnConfig = new JsonObject
        {
            ["name"] = "Nächster Titel",
            ["unique_id"] = $"audio_orchestrator_{groupId}_next",
            ["icon"] = "mdi:skip-next",
            ["command_topic"] = $"{groupCmdTopic}/command",
            ["payload_press"] = "next",
            ["availability"] = new JsonArray
            {
                new JsonObject { ["topic"] = bridgeStatusTopic },
                new JsonObject { ["topic"] = transportAvailTopic }
            },
            ["availability_mode"] = "all",
            ["device"] = CreateDeviceObject(groupId, groupName)
        };
        await PublishAsync($"{discPrefix}/button/audio_orchestrator_{groupId}/next/config", nextBtnConfig.ToJsonString(), retain: true, ct);

        // Entity 10: button.group_prev (Previous Track button, active only on Spotify)
        var prevBtnConfig = new JsonObject
        {
            ["name"] = "Vorheriger Titel",
            ["unique_id"] = $"audio_orchestrator_{groupId}_previous",
            ["icon"] = "mdi:skip-previous",
            ["command_topic"] = $"{groupCmdTopic}/command",
            ["payload_press"] = "previous",
            ["availability"] = new JsonArray
            {
                new JsonObject { ["topic"] = bridgeStatusTopic },
                new JsonObject { ["topic"] = transportAvailTopic }
            },
            ["availability_mode"] = "all",
            ["device"] = CreateDeviceObject(groupId, groupName)
        };
        await PublishAsync($"{discPrefix}/button/audio_orchestrator_{groupId}/previous/config", prevBtnConfig.ToJsonString(), retain: true, ct);
    }

    private async Task HandleIncomingMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var payload = e.ApplicationMessage.ConvertPayloadToString().Trim();

        _logger.LogDebug("MQTT Message received: {Topic} => {Payload}", topic, payload);

        // Check for HA birth/status message
        if (_currentSettings != null && topic.Equals($"{_currentSettings.MqttDiscoveryPrefix}/status", StringComparison.OrdinalIgnoreCase))
        {
            if (payload.Equals("online", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Home Assistant reported online. Re-publishing all discovery configs and states.");
                _forceDiscovery = true;
                await RefreshAndPublishAsync(CancellationToken.None, forceDiscovery: true);
            }
            return;
        }

        // Expected format: <baseTopic>/groups/<groupId>/set/<action>
        var baseTopic = string.IsNullOrWhiteSpace(_currentSettings?.MqttBaseTopic) ? "audio_orchestrator" : _currentSettings.MqttBaseTopic.TrimEnd('/');
        var prefix = $"{baseTopic}/groups/";
        if (!topic.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;

        var sub = topic.Substring(prefix.Length); // e.g. "<groupId>/set/volume"
        var parts = sub.Split('/');
        if (parts.Length < 3 || parts[1] != "set") return;

        var groupId = parts[0];
        var action = parts[2].ToLowerInvariant();

        try
        {
            await ProcessGroupCommandAsync(groupId, action, payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process MQTT command {Action} for group {GroupId} (Payload: {Payload})", action, groupId, payload);
        }
    }

    private async Task ProcessGroupCommandAsync(string groupId, string action, string payload)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var volumeOrchestrator = scope.ServiceProvider.GetRequiredService<VolumeOrchestratorService>();
        var snapcast = scope.ServiceProvider.GetRequiredService<ISnapcastService>();
        var librespotApi = scope.ServiceProvider.GetRequiredService<ILibrespotApiClient>();
        var stateBuilder = scope.ServiceProvider.GetRequiredService<FullStateBuilder>();
        var hubContext = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<Hubs.AudioHub, IAudioClient>>();

        var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == groupId || g.SnapcastGroupId == groupId);
        if (group == null)
        {
            _logger.LogWarning("MQTT Command target group {GroupId} not found.", groupId);
            return;
        }

        switch (action)
        {
            case "volume":
                if (double.TryParse(payload, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var volVal))
                {
                    int percent = (int)Math.Round(volVal);
                    percent = Math.Clamp(percent, 0, 100);
                    await volumeOrchestrator.SetGroupVolumeAsync(group.Id, percent, group.IsMuted);
                }
                break;

            case "mute":
                bool mute = payload.Equals("ON", StringComparison.OrdinalIgnoreCase)
                    || payload.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || payload == "1";
                await volumeOrchestrator.SetGroupVolumeAsync(group.Id, group.MasterVolume, mute);
                break;

            case "source":
                var targetStreamId = ResolveSourceToStreamId(payload, await db.RadioStreams.ToListAsync(), await db.SpotifySources.ToListAsync());
                await SetGroupStreamInternalAsync(group, targetStreamId, db, snapcast, volumeOrchestrator);
                break;

            case "command":
                await HandleMediaCommandAsync(group, payload.ToLowerInvariant(), db, snapcast, librespotApi, volumeOrchestrator);
                break;

            case "play_media":
                await HandlePlayMediaAsync(group, payload, db, librespotApi);
                break;

            default:
                _logger.LogWarning("Unknown MQTT action {Action} for group {GroupId}", action, groupId);
                break;
        }

        // Broadcast updated state to SignalR and MQTT
        var freshState = await stateBuilder.BuildAsync();
        _lastFullState = freshState;
        await hubContext.Clients.All.OnFullStateUpdate(freshState);
        await PublishDiscoveryAndStatesAsync(freshState, CancellationToken.None);
    }

    private static string ResolveSourceToStreamId(string sourceInput, List<RadioStreamEntity> radios, List<SpotifySourceEntity> spotifies)
    {
        if (string.IsNullOrWhiteSpace(sourceInput) || sourceInput == OffSourceLabel || sourceInput.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return "default";
        }

        if (sourceInput.StartsWith("Radio: ", StringComparison.OrdinalIgnoreCase))
        {
            var clean = sourceInput.Substring("Radio: ".Length).Trim();
            var radio = radios.FirstOrDefault(r => r.Name.Equals(clean, StringComparison.OrdinalIgnoreCase) || r.Id.Equals(clean, StringComparison.OrdinalIgnoreCase));
            if (radio != null) return $"{OrchestratorConstants.RadioPrefix}{radio.Id}";
            return "default";
        }

        if (sourceInput.StartsWith("Spotify: ", StringComparison.OrdinalIgnoreCase))
        {
            var clean = sourceInput.Substring("Spotify: ".Length).Trim();
            var spot = spotifies.FirstOrDefault(s => s.Name.Equals(clean, StringComparison.OrdinalIgnoreCase) || s.Id.Equals(clean, StringComparison.OrdinalIgnoreCase));
            if (spot != null) return $"{OrchestratorConstants.SpotifyPrefix}{spot.Id}";
            return "default";
        }

        if (sourceInput.StartsWith(OrchestratorConstants.RadioPrefix))
        {
            return sourceInput;
        }

        if (sourceInput.StartsWith(OrchestratorConstants.SpotifyPrefix))
        {
            return sourceInput;
        }

        return "default";
    }

    private async Task SetGroupStreamInternalAsync(
        GroupEntity group,
        string effectiveStreamId,
        OrchestratorDbContext db,
        ISnapcastService snapcast,
        VolumeOrchestratorService volumeOrchestrator)
    {
        group.CurrentStreamId = effectiveStreamId;
        await db.SaveChangesAsync();

        if (!string.IsNullOrEmpty(group.SnapcastGroupId))
        {
            await snapcast.SetGroupStreamAsync(group.SnapcastGroupId, effectiveStreamId);
        }

        if (effectiveStreamId.StartsWith(OrchestratorConstants.SpotifyPrefix))
        {
            var sourceId = effectiveStreamId.Replace(OrchestratorConstants.SpotifyPrefix, "");
            var source = await db.SpotifySources.FindAsync(sourceId);
            var activeSibling = await db.Groups.FirstOrDefaultAsync(g => g.Id != group.Id && g.CurrentStreamId == effectiveStreamId);
            if (activeSibling != null)
            {
                await volumeOrchestrator.SetGroupVolumeAsync(group.Id, activeSibling.MasterVolume, group.IsMuted, fromDocker: true);
            }
            else if (source != null && source.IsActive)
            {
                await volumeOrchestrator.SetGroupVolumeAsync(group.Id, source.MasterVolume, group.IsMuted, fromDocker: true);
            }
            else
            {
                await volumeOrchestrator.SetGroupVolumeAsync(group.Id, group.MasterVolume, group.IsMuted);
            }
        }
    }

    private async Task HandleMediaCommandAsync(
        GroupEntity group,
        string cmd,
        OrchestratorDbContext db,
        ISnapcastService snapcast,
        ILibrespotApiClient librespotApi,
        VolumeOrchestratorService volumeOrchestrator)
    {
        var streamId = group.CurrentStreamId ?? "default";
        var isSpotify = streamId.StartsWith(OrchestratorConstants.SpotifyPrefix);
        var sourceId = isSpotify ? streamId.Replace(OrchestratorConstants.SpotifyPrefix, "") : null;

        switch (cmd)
        {
            case "play":
                if (sourceId != null)
                {
                    await librespotApi.SendCommandAsync(sourceId, "resume");
                }
                break;

            case "pause":
                if (sourceId != null)
                {
                    await librespotApi.SendCommandAsync(sourceId, "pause");
                }
                else
                {
                    // Radio pause switches stream to default silence (matching HACS media_player.py)
                    await SetGroupStreamInternalAsync(group, "default", db, snapcast, volumeOrchestrator);
                }
                break;

            case "play_pause":
                if (sourceId != null)
                {
                    var source = await db.SpotifySources.FindAsync(sourceId);
                    if (source != null && source.IsActive)
                    {
                        await librespotApi.SendCommandAsync(sourceId, "pause");
                    }
                    else
                    {
                        await librespotApi.SendCommandAsync(sourceId, "resume");
                    }
                }
                break;

            case "stop":
                await SetGroupStreamInternalAsync(group, "default", db, snapcast, volumeOrchestrator);
                break;

            case "next":
                if (sourceId != null)
                {
                    await librespotApi.SendCommandAsync(sourceId, "next");
                }
                break;

            case "previous":
                if (sourceId != null)
                {
                    await librespotApi.SendCommandAsync(sourceId, "previous");
                }
                break;
        }
    }

    private async Task HandlePlayMediaAsync(GroupEntity group, string payload, OrchestratorDbContext db, ILibrespotApiClient librespotApi)
    {
        var streamId = group.CurrentStreamId ?? "";
        if (!streamId.StartsWith(OrchestratorConstants.SpotifyPrefix)) return;

        var sourceId = streamId.Replace(OrchestratorConstants.SpotifyPrefix, "");
        string? contextUri = null;

        try
        {
            var node = JsonNode.Parse(payload);
            contextUri = node?["context_uri"]?.GetValue<string>() ?? node?["media_content_id"]?.GetValue<string>();
        }
        catch
        {
            if (payload.StartsWith("spotify:")) contextUri = payload;
        }

        if (!string.IsNullOrWhiteSpace(contextUri))
        {
            await librespotApi.PlayContextAsync(sourceId, contextUri);
        }
    }
}
