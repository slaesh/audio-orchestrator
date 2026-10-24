using System.Text.Json;
using AudioOrchestrator.Contracts;
using AudioOrchestrator.Controllers;
using AudioOrchestrator.Data;
using AudioOrchestrator.Data.Entities;
using AudioOrchestrator.Hubs;
using AudioOrchestrator.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using MQTTnet;
using MQTTnet.Packets;
using Xunit;

namespace AudioOrchestrator.OrchestratorTests;

public class HomeAssistantMqttTests
{
    private readonly DbContextOptions<OrchestratorDbContext> _dbOptions;

    public HomeAssistantMqttTests()
    {
        _dbOptions = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(databaseName: $"Orchestrator_Mqtt_Test_{Guid.NewGuid()}")
            .Options;
    }

    [Fact]
    public async Task GetSettings_ShouldReturnDefaultMqttSettings_WhenNoneExist()
    {
        await using var db = new OrchestratorDbContext(_dbOptions);
        var mockVolume = new Mock<VolumeOrchestratorService>(db, Mock.Of<ISnapcastService>(), Mock.Of<ILibrespotApiClient>(), Mock.Of<IHubContext<AudioHub, IAudioClient>>(), Mock.Of<ILogger<VolumeOrchestratorService>>());
        var mockStateBuilder = new Mock<FullStateBuilder>(db, Mock.Of<ISnapcastService>(), null, null, null, null);
        var mockHub = new Mock<IHubContext<AudioHub, IAudioClient>>();
        var mockMqtt = new Mock<IHomeAssistantMqttService>();
        mockMqtt.Setup(m => m.IsConnected).Returns(false);
        var mockLogger = new Mock<ILogger<SettingsController>>();

        var controller = new SettingsController(db, mockVolume.Object, mockStateBuilder.Object, mockHub.Object, mockMqtt.Object, mockLogger.Object);

        var result = await controller.GetSettings();
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var settingsDto = Assert.IsType<SettingsDto>(okResult.Value);

        Assert.Equal(VolumeProfile.Cubic, settingsDto.VolumeProfile);
        Assert.False(settingsDto.HomeAssistant.Enabled);
        Assert.Equal(1883, settingsDto.HomeAssistant.Port);
        Assert.Equal("homeassistant", settingsDto.HomeAssistant.DiscoveryPrefix);
        Assert.Equal("audio_orchestrator", settingsDto.HomeAssistant.BaseTopic);
        Assert.False(settingsDto.HomeAssistant.IsConnected);
    }

    [Fact]
    public async Task UpdateSettings_ShouldSaveMqttConfiguration_AndTriggerReload()
    {
        await using var db = new OrchestratorDbContext(_dbOptions);
        var mockVolume = new Mock<VolumeOrchestratorService>(db, Mock.Of<ISnapcastService>(), Mock.Of<ILibrespotApiClient>(), Mock.Of<IHubContext<AudioHub, IAudioClient>>(), Mock.Of<ILogger<VolumeOrchestratorService>>());
        var mockStateBuilder = new Mock<FullStateBuilder>(db, Mock.Of<ISnapcastService>(), null, null, null, null);
        mockStateBuilder.Setup(s => s.BuildAsync()).ReturnsAsync(new FullStateDto(new List<GroupDto>(), new List<RoomDto>(), new List<StreamDto>(), new List<SpotifySourceDto>(), new List<StreamDto>(), null, VolumeProfile.Linear));

        var mockClients = new Mock<IHubClients<IAudioClient>>();
        var mockClientProxy = new Mock<IAudioClient>();
        mockClients.Setup(c => c.All).Returns(mockClientProxy.Object);

        var mockHub = new Mock<IHubContext<AudioHub, IAudioClient>>();
        mockHub.Setup(h => h.Clients).Returns(mockClients.Object);

        var mockMqtt = new Mock<IHomeAssistantMqttService>();
        mockMqtt.Setup(m => m.IsConnected).Returns(true);
        var mockLogger = new Mock<ILogger<SettingsController>>();

        var controller = new SettingsController(db, mockVolume.Object, mockStateBuilder.Object, mockHub.Object, mockMqtt.Object, mockLogger.Object);

        var req = new UpdateSettingsRequest(
            VolumeProfile: VolumeProfile.Linear,
            HomeAssistant: new HomeAssistantSettingsDto(
                Enabled: true,
                Host: "192.168.178.50",
                Port: 1883,
                Username: "test-user",
                Password: "secret-password",
                HasPassword: true,
                UseTls: false,
                DiscoveryPrefix: "homeassistant",
                BaseTopic: "audio_orchestrator",
                IsConnected: false
            )
        );

        var result = await controller.UpdateSettings(req);
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var settingsDto = Assert.IsType<SettingsDto>(okResult.Value);

        Assert.Equal(VolumeProfile.Linear, settingsDto.VolumeProfile);
        Assert.True(settingsDto.HomeAssistant.Enabled);
        Assert.Equal("192.168.178.50", settingsDto.HomeAssistant.Host);
        Assert.Equal("test-user", settingsDto.HomeAssistant.Username);
        Assert.Null(settingsDto.HomeAssistant.Password);
        Assert.True(settingsDto.HomeAssistant.HasPassword);

        var savedEntity = await db.Settings.FirstOrDefaultAsync();
        Assert.NotNull(savedEntity);
        Assert.True(savedEntity.HomeAssistantEnabled);
        Assert.Equal("192.168.178.50", savedEntity.MqttHost);
        Assert.Equal("secret-password", savedEntity.MqttPassword);

        mockMqtt.Verify(m => m.ReloadConfigurationAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateSettings_ShouldSucceed_WhenMqttNotificationFails()
    {
        await using var db = new OrchestratorDbContext(_dbOptions);
        var mockVolume = new Mock<VolumeOrchestratorService>(db, Mock.Of<ISnapcastService>(), Mock.Of<ILibrespotApiClient>(), Mock.Of<IHubContext<AudioHub, IAudioClient>>(), Mock.Of<ILogger<VolumeOrchestratorService>>());
        var mockStateBuilder = new Mock<FullStateBuilder>(db, Mock.Of<ISnapcastService>(), null, null, null, null);
        mockStateBuilder.Setup(s => s.BuildAsync()).ReturnsAsync(new FullStateDto(new List<GroupDto>(), new List<RoomDto>(), new List<StreamDto>(), new List<SpotifySourceDto>(), new List<StreamDto>(), null, VolumeProfile.Linear));

        var mockClients = new Mock<IHubClients<IAudioClient>>();
        var mockClientProxy = new Mock<IAudioClient>();
        mockClients.Setup(c => c.All).Returns(mockClientProxy.Object);

        var mockHub = new Mock<IHubContext<AudioHub, IAudioClient>>();
        mockHub.Setup(h => h.Clients).Returns(mockClients.Object);

        var mockMqtt = new Mock<IHomeAssistantMqttService>();
        mockMqtt.Setup(m => m.IsConnected).Returns(true);
        // Simulate broker connection drop / rejection during notification
        mockMqtt.Setup(m => m.NotifyFullStateAsync(It.IsAny<FullStateDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Broker rejected publication"));

        var mockLogger = new Mock<ILogger<SettingsController>>();

        var controller = new SettingsController(db, mockVolume.Object, mockStateBuilder.Object, mockHub.Object, mockMqtt.Object, mockLogger.Object);

        var req = new UpdateSettingsRequest(
            VolumeProfile: VolumeProfile.Linear,
            HomeAssistant: new HomeAssistantSettingsDto(
                Enabled: true,
                Host: "192.168.178.50",
                Port: 1883,
                Username: null,
                Password: null,
                HasPassword: false,
                UseTls: false,
                DiscoveryPrefix: "homeassistant",
                BaseTopic: "audio_orchestrator",
                IsConnected: true
            )
        );

        // Act - should not throw 500 error
        var result = await controller.UpdateSettings(req);
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var settingsDto = Assert.IsType<SettingsDto>(okResult.Value);

        Assert.Equal(VolumeProfile.Linear, settingsDto.VolumeProfile);
        Assert.True(settingsDto.HomeAssistant.Enabled);

        var savedEntity = await db.Settings.FirstOrDefaultAsync();
        Assert.NotNull(savedEntity);
        Assert.True(savedEntity.HomeAssistantEnabled);
    }

    [Fact]
    public async Task TestMqttConnection_ShouldDelegateToMqttService()
    {
        await using var db = new OrchestratorDbContext(_dbOptions);
        var mockVolume = new Mock<VolumeOrchestratorService>(db, Mock.Of<ISnapcastService>(), Mock.Of<ILibrespotApiClient>(), Mock.Of<IHubContext<AudioHub, IAudioClient>>(), Mock.Of<ILogger<VolumeOrchestratorService>>());
        var mockStateBuilder = new Mock<FullStateBuilder>(db, Mock.Of<ISnapcastService>(), null, null, null, null);
        var mockHub = new Mock<IHubContext<AudioHub, IAudioClient>>();
        var mockMqtt = new Mock<IHomeAssistantMqttService>();
        var mockLogger = new Mock<ILogger<SettingsController>>();

        mockMqtt.Setup(m => m.TestConnectionAsync(It.IsAny<TestMqttConnectionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestMqttConnectionResponse(true, "Connected successfully"));

        var controller = new SettingsController(db, mockVolume.Object, mockStateBuilder.Object, mockHub.Object, mockMqtt.Object, mockLogger.Object);

        var testReq = new TestMqttConnectionRequest("192.168.1.100", 1883, null, null, false);
        var result = await controller.TestMqttConnection(testReq);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var resp = Assert.IsType<TestMqttConnectionResponse>(okResult.Value);

        Assert.True(resp.Success);
        Assert.Equal("Connected successfully", resp.Message);
    }

    [Fact]
    public async Task UpdateSettings_ShouldAllowClearingSavedPassword()
    {
        await using var db = new OrchestratorDbContext(_dbOptions);
        db.Settings.Add(new OrchestratorSettingsEntity
        {
            Id = "default",
            HomeAssistantEnabled = true,
            MqttHost = "192.168.178.50",
            MqttPassword = "old-secret"
        });
        await db.SaveChangesAsync();

        var mockVolume = new Mock<VolumeOrchestratorService>(db, Mock.Of<ISnapcastService>(), Mock.Of<ILibrespotApiClient>(), Mock.Of<IHubContext<AudioHub, IAudioClient>>(), Mock.Of<ILogger<VolumeOrchestratorService>>());
        var mockStateBuilder = new Mock<FullStateBuilder>(db, Mock.Of<ISnapcastService>(), null, null, null, null);
        mockStateBuilder.Setup(s => s.BuildAsync()).ReturnsAsync(new FullStateDto(new List<GroupDto>(), new List<RoomDto>(), new List<StreamDto>(), new List<SpotifySourceDto>(), new List<StreamDto>(), null, VolumeProfile.Linear));

        var mockClients = new Mock<IHubClients<IAudioClient>>();
        mockClients.Setup(c => c.All).Returns(Mock.Of<IAudioClient>());
        var mockHub = new Mock<IHubContext<AudioHub, IAudioClient>>();
        mockHub.Setup(h => h.Clients).Returns(mockClients.Object);

        var mockMqtt = new Mock<IHomeAssistantMqttService>();
        var controller = new SettingsController(db, mockVolume.Object, mockStateBuilder.Object, mockHub.Object, mockMqtt.Object, Mock.Of<ILogger<SettingsController>>());

        var req = new UpdateSettingsRequest(
            VolumeProfile: VolumeProfile.Linear,
            HomeAssistant: new HomeAssistantSettingsDto(
                Enabled: true,
                Host: "192.168.178.50",
                Port: 1883,
                Username: "test-user",
                Password: "", // Explicit clear
                HasPassword: false,
                UseTls: false,
                DiscoveryPrefix: "homeassistant/", // Trailing slash to test normalization
                BaseTopic: "audio_orchestrator/",
                IsConnected: false
            )
        );

        var result = await controller.UpdateSettings(req);
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var settingsDto = Assert.IsType<SettingsDto>(okResult.Value);

        Assert.False(settingsDto.HomeAssistant.HasPassword);
        Assert.Equal("homeassistant", settingsDto.HomeAssistant.DiscoveryPrefix);
        Assert.Equal("audio_orchestrator", settingsDto.HomeAssistant.BaseTopic);

        var saved = await db.Settings.FirstOrDefaultAsync();
        Assert.NotNull(saved);
        Assert.Equal("", saved.MqttPassword);
        Assert.Equal("homeassistant", saved.MqttDiscoveryPrefix);
        Assert.Equal("audio_orchestrator", saved.MqttBaseTopic);
    }

    [Fact]
    public async Task UpdateSettings_ShouldAutomaticallyClearPassword_WhenUsernameIsRemoved()
    {
        await using var db = new OrchestratorDbContext(_dbOptions);
        db.Settings.Add(new OrchestratorSettingsEntity
        {
            Id = "default",
            HomeAssistantEnabled = true,
            MqttHost = "192.168.178.50",
            MqttPort = 1883,
            MqttUsername = "old-user",
            MqttPassword = "old-password"
        });
        await db.SaveChangesAsync();

        var mockVolume = new Mock<VolumeOrchestratorService>(db, Mock.Of<ISnapcastService>(), Mock.Of<ILibrespotApiClient>(), Mock.Of<IHubContext<AudioHub, IAudioClient>>(), Mock.Of<ILogger<VolumeOrchestratorService>>());
        var mockStateBuilder = new Mock<FullStateBuilder>(db, Mock.Of<ISnapcastService>(), null, null, null, null);
        mockStateBuilder.Setup(s => s.BuildAsync()).ReturnsAsync(new FullStateDto(new List<GroupDto>(), new List<RoomDto>(), new List<StreamDto>(), new List<SpotifySourceDto>(), new List<StreamDto>(), null, VolumeProfile.Linear));

        var mockClients = new Mock<IHubClients<IAudioClient>>();
        mockClients.Setup(c => c.All).Returns(Mock.Of<IAudioClient>());
        var mockHub = new Mock<IHubContext<AudioHub, IAudioClient>>();
        mockHub.Setup(h => h.Clients).Returns(mockClients.Object);

        var mockMqtt = new Mock<IHomeAssistantMqttService>();
        var controller = new SettingsController(db, mockVolume.Object, mockStateBuilder.Object, mockHub.Object, mockMqtt.Object, Mock.Of<ILogger<SettingsController>>());

        // User removed the username to connect anonymously without explicitly deleting the password first
        var req = new UpdateSettingsRequest(
            VolumeProfile: VolumeProfile.Linear,
            HomeAssistant: new HomeAssistantSettingsDto(
                Enabled: true,
                Host: "192.168.178.50",
                Port: 1883,
                Username: "",
                Password: null,
                HasPassword: false,
                UseTls: false,
                DiscoveryPrefix: "homeassistant",
                BaseTopic: "audio_orchestrator",
                IsConnected: false
            )
        );

        var result = await controller.UpdateSettings(req);
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var settingsDto = Assert.IsType<SettingsDto>(okResult.Value);

        Assert.Null(settingsDto.HomeAssistant.Username);
        Assert.False(settingsDto.HomeAssistant.HasPassword);

        var saved = await db.Settings.FirstOrDefaultAsync();
        Assert.NotNull(saved);
        Assert.Null(saved.MqttUsername);
        Assert.Null(saved.MqttPassword);
    }

    [Fact]
    public void BuildSourceMapping_ShouldDisambiguateDuplicateNamesWithId()
    {
        var radios = new List<(string Id, string Name)>
        {
            ("r1", "Rock"),
            ("r2", "Rock"),
            ("r3", "Jazz")
        };

        var spotifies = new List<(string Id, string Name)>
        {
            ("s1", "Office"),
            ("s2", "Office"),
            ("s3", "Living Room")
        };

        var result = HomeAssistantMqttService.BuildSourceMapping(radios, spotifies);

        Assert.Contains("Radio: Rock (r1)", result.SourceList);
        Assert.Contains("Radio: Rock (r2)", result.SourceList);
        Assert.Contains("Radio: Jazz", result.SourceList);

        Assert.Contains("Spotify: Office (s1)", result.SourceList);
        Assert.Contains("Spotify: Office (s2)", result.SourceList);
        Assert.Contains("Spotify: Living Room", result.SourceList);

        Assert.Equal("radio_r1", result.LabelToStreamId["Radio: Rock (r1)"]);
        Assert.Equal("radio_r2", result.LabelToStreamId["Radio: Rock (r2)"]);
        Assert.Equal("radio_r3", result.LabelToStreamId["Radio: Jazz"]);

        Assert.Equal("spotify_s1", result.LabelToStreamId["Spotify: Office (s1)"]);
        Assert.Equal("spotify_s2", result.LabelToStreamId["Spotify: Office (s2)"]);
        Assert.Equal("spotify_s3", result.LabelToStreamId["Spotify: Living Room"]);
    }

    [Fact]
    public void BuildSourceMapping_ShouldHandlePrefixedDtoIdsCorrectly()
    {
        var radios = new List<(string Id, string Name)>
        {
            ("radio_r1", "Rock"),
            ("radio_r2", "Rock")
        };

        var spotifies = new List<(string Id, string Name)>
        {
            ("spotify_s1", "Office"),
            ("spotify_s2", "Office")
        };

        var result = HomeAssistantMqttService.BuildSourceMapping(radios, spotifies);

        Assert.Contains("Radio: Rock (r1)", result.SourceList);
        Assert.Contains("Radio: Rock (r2)", result.SourceList);
        Assert.Equal("radio_r1", result.LabelToStreamId["Radio: Rock (r1)"]);
        Assert.Equal("Radio: Rock (r1)", result.StreamIdToLabel["radio_r1"]);
        Assert.Equal("Radio: Rock (r1)", result.StreamIdToLabel["r1"]);

        Assert.Contains("Spotify: Office (s1)", result.SourceList);
        Assert.Contains("Spotify: Office (s2)", result.SourceList);
        Assert.Equal("spotify_s1", result.LabelToStreamId["Spotify: Office (s1)"]);
        Assert.Equal("Spotify: Office (s1)", result.StreamIdToLabel["spotify_s1"]);
        Assert.Equal("Spotify: Office (s1)", result.StreamIdToLabel["s1"]);
    }

    [Theory]
    [InlineData(65536)]
    [InlineData(-1)]
    public async Task UpdateSettings_ShouldRejectInvalidPort(int invalidPort)
    {
        await using var db = new OrchestratorDbContext(_dbOptions);
        var mockVolume = new Mock<VolumeOrchestratorService>(db, Mock.Of<ISnapcastService>(), Mock.Of<ILibrespotApiClient>(), Mock.Of<IHubContext<AudioHub, IAudioClient>>(), Mock.Of<ILogger<VolumeOrchestratorService>>());
        var mockStateBuilder = new Mock<FullStateBuilder>(db, Mock.Of<ISnapcastService>(), null, null, null, null);
        var mockHub = new Mock<IHubContext<AudioHub, IAudioClient>>();
        var mockMqtt = new Mock<IHomeAssistantMqttService>();

        var controller = new SettingsController(db, mockVolume.Object, mockStateBuilder.Object, mockHub.Object, mockMqtt.Object, Mock.Of<ILogger<SettingsController>>());

        var req = new UpdateSettingsRequest(
            VolumeProfile: VolumeProfile.Linear,
            HomeAssistant: new HomeAssistantSettingsDto(
                Enabled: true,
                Host: "192.168.178.50",
                Port: invalidPort,
                Username: null,
                Password: null,
                HasPassword: false,
                UseTls: false,
                DiscoveryPrefix: "homeassistant",
                BaseTopic: "audio_orchestrator",
                IsConnected: false
            )
        );

        var result = await controller.UpdateSettings(req);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Ungültiger Port (1-65535).", badRequest.Value);
    }

    [Theory]
    [InlineData("homeassistant/#", "audio_orchestrator")]
    [InlineData("homeassistant", "audio+orchestrator")]
    [InlineData("home assistant", "audio_orchestrator")]
    public async Task UpdateSettings_ShouldRejectInvalidTopics(string discoveryPrefix, string baseTopic)
    {
        await using var db = new OrchestratorDbContext(_dbOptions);
        var mockVolume = new Mock<VolumeOrchestratorService>(db, Mock.Of<ISnapcastService>(), Mock.Of<ILibrespotApiClient>(), Mock.Of<IHubContext<AudioHub, IAudioClient>>(), Mock.Of<ILogger<VolumeOrchestratorService>>());
        var mockStateBuilder = new Mock<FullStateBuilder>(db, Mock.Of<ISnapcastService>(), null, null, null, null);
        var mockHub = new Mock<IHubContext<AudioHub, IAudioClient>>();
        var mockMqtt = new Mock<IHomeAssistantMqttService>();

        var controller = new SettingsController(db, mockVolume.Object, mockStateBuilder.Object, mockHub.Object, mockMqtt.Object, Mock.Of<ILogger<SettingsController>>());

        var req = new UpdateSettingsRequest(
            VolumeProfile: VolumeProfile.Linear,
            HomeAssistant: new HomeAssistantSettingsDto(
                Enabled: true,
                Host: "192.168.178.50",
                Port: 1883,
                Username: null,
                Password: null,
                HasPassword: false,
                UseTls: false,
                DiscoveryPrefix: discoveryPrefix,
                BaseTopic: baseTopic,
                IsConnected: false
            )
        );

        var result = await controller.UpdateSettings(req);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateSettings_ShouldRejectConnectionChange_WhenSavedPasswordReusedWithoutExplicitPassword()
    {
        await using var db = new OrchestratorDbContext(_dbOptions);
        db.Settings.Add(new OrchestratorSettingsEntity
        {
            Id = "default",
            HomeAssistantEnabled = true,
            MqttHost = "192.168.178.50",
            MqttPort = 1883,
            MqttPassword = "super-secret-password"
        });
        await db.SaveChangesAsync();

        var mockVolume = new Mock<VolumeOrchestratorService>(db, Mock.Of<ISnapcastService>(), Mock.Of<ILibrespotApiClient>(), Mock.Of<IHubContext<AudioHub, IAudioClient>>(), Mock.Of<ILogger<VolumeOrchestratorService>>());
        var mockStateBuilder = new Mock<FullStateBuilder>(db, Mock.Of<ISnapcastService>(), null, null, null, null);
        var mockHub = new Mock<IHubContext<AudioHub, IAudioClient>>();
        var mockMqtt = new Mock<IHomeAssistantMqttService>();

        var controller = new SettingsController(db, mockVolume.Object, mockStateBuilder.Object, mockHub.Object, mockMqtt.Object, Mock.Of<ILogger<SettingsController>>());

        // Attempt to redirect host to an attacker IP while omitting Password (hoping to steal old password)
        var req = new UpdateSettingsRequest(
            VolumeProfile: VolumeProfile.Linear,
            HomeAssistant: new HomeAssistantSettingsDto(
                Enabled: true,
                Host: "attacker.com",
                Port: 1883,
                Username: null,
                Password: null, // Omitted
                HasPassword: true,
                UseTls: false,
                DiscoveryPrefix: "homeassistant",
                BaseTopic: "audio_orchestrator",
                IsConnected: false
            )
        );

        var result = await controller.UpdateSettings(req);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("Verbindungsdaten geändert", badRequest.Value?.ToString());
    }

    [Fact]
    public async Task TestMqttConnection_ShouldNotReuseSavedPassword_WhenConnectionSettingsMismatch()
    {
        await using var db = new OrchestratorDbContext(_dbOptions);
        db.Settings.Add(new OrchestratorSettingsEntity
        {
            Id = "default",
            HomeAssistantEnabled = true,
            MqttHost = "192.168.178.50",
            MqttPort = 1883,
            MqttUsername = "user1",
            MqttPassword = "super-secret-password",
            MqttUseTls = true
        });
        await db.SaveChangesAsync();

        var mockVolume = new Mock<VolumeOrchestratorService>(db, Mock.Of<ISnapcastService>(), Mock.Of<ILibrespotApiClient>(), Mock.Of<IHubContext<AudioHub, IAudioClient>>(), Mock.Of<ILogger<VolumeOrchestratorService>>());
        var mockStateBuilder = new Mock<FullStateBuilder>(db, Mock.Of<ISnapcastService>(), null, null, null, null);
        var mockHub = new Mock<IHubContext<AudioHub, IAudioClient>>();
        var mockMqtt = new Mock<IHomeAssistantMqttService>();

        TestMqttConnectionRequest? capturedRequest = null;
        mockMqtt.Setup(m => m.TestConnectionAsync(It.IsAny<TestMqttConnectionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<TestMqttConnectionRequest, CancellationToken>((req, ct) => capturedRequest = req)
            .ReturnsAsync(new TestMqttConnectionResponse(true, "OK"));

        var controller = new SettingsController(db, mockVolume.Object, mockStateBuilder.Object, mockHub.Object, mockMqtt.Object, Mock.Of<ILogger<SettingsController>>());

        // Test with different port (e.g. 1884 instead of 1883) and null password
        var testReq = new TestMqttConnectionRequest("192.168.178.50", 1884, "user1", null, true);
        await controller.TestMqttConnection(testReq);

        Assert.NotNull(capturedRequest);
        // Password should NOT be populated because Port differed!
        Assert.Null(capturedRequest.Password);
    }

    [Fact]
    public async Task UpdateSettings_ShouldRejectEmptyBrokerHost_WhenMqttEnabled()
    {
        await using var db = new OrchestratorDbContext(_dbOptions);
        var mockVolume = new Mock<VolumeOrchestratorService>(db, Mock.Of<ISnapcastService>(), Mock.Of<ILibrespotApiClient>(), Mock.Of<IHubContext<AudioHub, IAudioClient>>(), Mock.Of<ILogger<VolumeOrchestratorService>>());
        var mockStateBuilder = new Mock<FullStateBuilder>(db, Mock.Of<ISnapcastService>(), null, null, null, null);
        var mockHub = new Mock<IHubContext<AudioHub, IAudioClient>>();
        var mockMqtt = new Mock<IHomeAssistantMqttService>();

        var controller = new SettingsController(db, mockVolume.Object, mockStateBuilder.Object, mockHub.Object, mockMqtt.Object, Mock.Of<ILogger<SettingsController>>());

        var req = new UpdateSettingsRequest(
            VolumeProfile: VolumeProfile.Linear,
            HomeAssistant: new HomeAssistantSettingsDto(
                Enabled: true,
                Host: "   ", // Blank host
                Port: 1883,
                Username: null,
                Password: null,
                HasPassword: false,
                UseTls: false,
                DiscoveryPrefix: "homeassistant",
                BaseTopic: "audio_orchestrator",
                IsConnected: false
            )
        );

        var result = await controller.UpdateSettings(req);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("Broker Host / IP darf nicht leer sein", badRequest.Value?.ToString());
    }

    [Fact]
    public async Task GetSettings_ShouldReturnDefaultPort1883_WhenStoredPortIsZero()
    {
        await using var db = new OrchestratorDbContext(_dbOptions);
        db.Settings.Add(new OrchestratorSettingsEntity
        {
            Id = "default",
            HomeAssistantEnabled = false,
            MqttHost = null,
            MqttPort = 0 // Migrated row with default 0
        });
        await db.SaveChangesAsync();

        var mockVolume = new Mock<VolumeOrchestratorService>(db, Mock.Of<ISnapcastService>(), Mock.Of<ILibrespotApiClient>(), Mock.Of<IHubContext<AudioHub, IAudioClient>>(), Mock.Of<ILogger<VolumeOrchestratorService>>());
        var mockStateBuilder = new Mock<FullStateBuilder>(db, Mock.Of<ISnapcastService>(), null, null, null, null);
        var mockHub = new Mock<IHubContext<AudioHub, IAudioClient>>();
        var mockMqtt = new Mock<IHomeAssistantMqttService>();

        var controller = new SettingsController(db, mockVolume.Object, mockStateBuilder.Object, mockHub.Object, mockMqtt.Object, Mock.Of<ILogger<SettingsController>>());

        var result = await controller.GetSettings();
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var settingsDto = Assert.IsType<SettingsDto>(okResult.Value);

        Assert.Equal(1883, settingsDto.HomeAssistant.Port);
    }

    [Fact]
    public void BuildSourceMapping_ShouldHandleLiteralNameCollisionsDeterministically_IndependentOfInputOrder()
    {
        var radiosOrder1 = new List<(string Id, string Name)>
        {
            ("id1", "Office"),
            ("id2", "Office"),
            ("id3", "Office (id1)")
        };

        var radiosOrder2 = new List<(string Id, string Name)>
        {
            ("id3", "Office (id1)"),
            ("id2", "Office"),
            ("id1", "Office")
        };

        var result1 = HomeAssistantMqttService.BuildSourceMapping(radiosOrder1, []);
        var result2 = HomeAssistantMqttService.BuildSourceMapping(radiosOrder2, []);

        // Assert all labels are unique
        Assert.Equal(result1.SourceList.Count, result1.SourceList.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(result2.SourceList.Count, result2.SourceList.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // Assert deterministic output independent of input order
        Assert.Equal(result1.SourceList, result2.SourceList);

        // Assert no label is overwritten in LabelToStreamId
        Assert.Equal("radio_id1", result1.LabelToStreamId["Radio: Office (id1)"]);
        Assert.Equal("radio_id2", result1.LabelToStreamId["Radio: Office (id2)"]);
        Assert.Equal("radio_id3", result1.LabelToStreamId["Radio: Office (id1) (id3)"]);

        Assert.Equal("radio_id1", result2.LabelToStreamId["Radio: Office (id1)"]);
        Assert.Equal("radio_id2", result2.LabelToStreamId["Radio: Office (id2)"]);
        Assert.Equal("radio_id3", result2.LabelToStreamId["Radio: Office (id1) (id3)"]);
    }

    private static (ServiceProvider ServiceProvider, OrchestratorDbContext Db, Mock<IMqttClient> MockClient, HomeAssistantMqttService Service, Mock<VolumeOrchestratorService> MockVolume) CreateTestContext(
        string discoveryPrefix = "homeassistant",
        string baseTopic = "audio_orchestrator")
    {
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString();
        var dbOptions = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        services.AddDbContext<OrchestratorDbContext>(opt => opt.UseInMemoryDatabase(dbName));

        var mockSnapcast = new Mock<ISnapcastService>();
        mockSnapcast.Setup(s => s.GetStatusAsync()).ReturnsAsync((System.Text.Json.Nodes.JsonNode?)null);
        var mockLibrespot = new Mock<ILibrespotApiClient>();
        var mockHub = new Mock<IHubContext<AudioHub, IAudioClient>>();
        mockHub.Setup(h => h.Clients.All).Returns(Mock.Of<IAudioClient>());

        var mockBroadcast = new Mock<IBroadcastService>();
        services.AddSingleton(mockBroadcast.Object);
        services.AddSingleton(mockSnapcast.Object);
        services.AddSingleton(mockLibrespot.Object);
        services.AddSingleton(mockHub.Object);
        services.AddLogging();

        var dummyDb = new OrchestratorDbContext(dbOptions);
        var mockVolume = new Mock<VolumeOrchestratorService>(
            dummyDb,
            mockSnapcast.Object,
            mockLibrespot.Object,
            mockHub.Object,
            Mock.Of<ILogger<VolumeOrchestratorService>>());
        services.AddScoped(_ => mockVolume.Object);

        // FullStateBuilder
        services.AddScoped(sp =>
        {
            var db = sp.GetRequiredService<OrchestratorDbContext>();
            return new FullStateBuilder(db, mockSnapcast.Object);
        });

        var sp = services.BuildServiceProvider();
        var db = sp.GetRequiredService<OrchestratorDbContext>();
        db.Settings.Add(new OrchestratorSettingsEntity
        {
            Id = "default",
            HomeAssistantEnabled = true,
            MqttHost = "127.0.0.1",
            MqttPort = 1883,
            MqttDiscoveryPrefix = discoveryPrefix,
            MqttBaseTopic = baseTopic
        });
        db.SaveChanges();

        var mockClient = new Mock<IMqttClient>();
        mockClient.Setup(c => c.PublishAsync(It.IsAny<MqttApplicationMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientPublishResult(1, MqttClientPublishReasonCode.Success, string.Empty, Array.Empty<MqttUserProperty>()));
        var mockFactory = new Mock<IMqttClientFactorySeam>();
        mockFactory.Setup(f => f.CreateClient()).Returns(mockClient.Object);

        var serviceLogger = sp.GetRequiredService<ILogger<HomeAssistantMqttService>>();
        var service = new HomeAssistantMqttService(serviceLogger, sp, mockFactory.Object);

        return (sp, db, mockClient, service, mockVolume);
    }

    [Fact]
    public async Task EnsureConnectionAsync_ShouldRetrySetup_WhenSubscriptionFails()
    {
        var (sp, db, mockClient, service, _) = CreateTestContext();

        mockClient.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientConnectResult());

        // First pass: Subscription fails with Error
        var failSubResult = new MqttClientSubscribeResult(
            1,
            new List<MqttClientSubscribeResultItem>
            {
                new(new MqttTopicFilter { Topic = "audio_orchestrator/groups/+/set/#" }, MqttClientSubscribeResultCode.UnspecifiedError)
            },
            string.Empty,
            new List<MqttUserProperty>()
        );

        mockClient.Setup(c => c.SubscribeAsync(It.IsAny<MqttClientSubscribeOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(failSubResult);

        await service.EnsureConnectionAsync(CancellationToken.None);

        Assert.False(service.IsConnected);
        mockClient.Verify(c => c.DisconnectAsync(It.IsAny<MqttClientDisconnectOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        // Birth message should NOT have been published
        mockClient.Verify(c => c.PublishAsync(It.Is<MqttApplicationMessage>(m => m.Topic == "audio_orchestrator/bridge/status"), It.IsAny<CancellationToken>()), Times.Never);

        // Second pass: Subscription succeeds
        var successSubResult = new MqttClientSubscribeResult(
            2,
            new List<MqttClientSubscribeResultItem>
            {
                new(new MqttTopicFilter { Topic = "audio_orchestrator/groups/+/set/#" }, MqttClientSubscribeResultCode.GrantedQoS0),
                new(new MqttTopicFilter { Topic = "homeassistant/status" }, MqttClientSubscribeResultCode.GrantedQoS0)
            },
            string.Empty,
            new List<MqttUserProperty>()
        );
        mockClient.Setup(c => c.SubscribeAsync(It.IsAny<MqttClientSubscribeOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(successSubResult);
        mockClient.SetupGet(c => c.IsConnected).Returns(true);

        await service.EnsureConnectionAsync(CancellationToken.None);

        // Birth message SHOULD now be published
        mockClient.Verify(c => c.PublishAsync(It.Is<MqttApplicationMessage>(m => m.Topic == "audio_orchestrator/bridge/status"), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task EnsureConnectionAsync_ShouldResetClient_WhenBirthMessagePublishThrows()
    {
        var (sp, db, mockClient, service, _) = CreateTestContext();

        mockClient.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientConnectResult());

        var successSubResult = new MqttClientSubscribeResult(
            1,
            new List<MqttClientSubscribeResultItem>
            {
                new(new MqttTopicFilter { Topic = "audio_orchestrator/groups/+/set/#" }, MqttClientSubscribeResultCode.GrantedQoS0)
            },
            string.Empty,
            new List<MqttUserProperty>()
        );
        mockClient.Setup(c => c.SubscribeAsync(It.IsAny<MqttClientSubscribeOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(successSubResult);
        mockClient.SetupGet(c => c.IsConnected).Returns(true);

        // Publishing birth message throws
        mockClient.Setup(c => c.PublishAsync(It.Is<MqttApplicationMessage>(m => m.Topic == "audio_orchestrator/bridge/status"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Broker connection dropped during birth publish"));

        await service.EnsureConnectionAsync(CancellationToken.None);

        // Client should have been reset and disconnected to avoid half-initialized state
        Assert.False(service.IsConnected);
        mockClient.Verify(c => c.Dispose(), Times.Once);
    }

    [Fact]
    public async Task EnsureConnectionAsync_ShouldUseCustomDiscoveryAndBaseTopics()
    {
        var (sp, db, mockClient, service, _) = CreateTestContext(discoveryPrefix: "custom_ha_disc", baseTopic: "custom_orch_base");

        MqttClientOptions? capturedOptions = null;
        mockClient.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .Callback<MqttClientOptions, CancellationToken>((opts, _) => capturedOptions = opts)
            .ReturnsAsync(new MqttClientConnectResult());

        MqttClientSubscribeOptions? capturedSubOptions = null;
        mockClient.Setup(c => c.SubscribeAsync(It.IsAny<MqttClientSubscribeOptions>(), It.IsAny<CancellationToken>()))
            .Callback<MqttClientSubscribeOptions, CancellationToken>((subOpts, _) => capturedSubOptions = subOpts)
            .ReturnsAsync(new MqttClientSubscribeResult(
                1,
                new List<MqttClientSubscribeResultItem>
                {
                    new(new MqttTopicFilter { Topic = "custom_orch_base/groups/+/set/#" }, MqttClientSubscribeResultCode.GrantedQoS0),
                    new(new MqttTopicFilter { Topic = "custom_ha_disc/status" }, MqttClientSubscribeResultCode.GrantedQoS0)
                },
                string.Empty,
                new List<MqttUserProperty>()
            ));
        mockClient.SetupGet(c => c.IsConnected).Returns(true);

        await service.EnsureConnectionAsync(CancellationToken.None);

        Assert.NotNull(capturedOptions);
        Assert.Equal("custom_orch_base/bridge/status", capturedOptions.WillTopic);

        Assert.NotNull(capturedSubOptions);
        var topicFilters = capturedSubOptions.TopicFilters.Select(tf => tf.Topic).ToList();
        Assert.Contains("custom_orch_base/groups/+/set/#", topicFilters);
        Assert.Contains("custom_ha_disc/status", topicFilters);

        // Birth message topic
        mockClient.Verify(c => c.PublishAsync(It.Is<MqttApplicationMessage>(m => m.Topic == "custom_orch_base/bridge/status"), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task HandleIncomingMessageAsync_ShouldRouteGroupCommandsCorrectly()
    {
        var (sp, db, mockClient, service, mockVolume) = CreateTestContext();

        mockClient.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientConnectResult());
        mockClient.Setup(c => c.SubscribeAsync(It.IsAny<MqttClientSubscribeOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientSubscribeResult(
                1,
                new List<MqttClientSubscribeResultItem>
                {
                    new(new MqttTopicFilter { Topic = "audio_orchestrator/groups/+/set/#" }, MqttClientSubscribeResultCode.GrantedQoS0),
                    new(new MqttTopicFilter { Topic = "homeassistant/status" }, MqttClientSubscribeResultCode.GrantedQoS0)
                },
                string.Empty,
                new List<MqttUserProperty>()
            ));
        mockClient.SetupGet(c => c.IsConnected).Returns(true);

        await service.EnsureConnectionAsync(CancellationToken.None);

        db.Groups.Add(new GroupEntity
        {
            Id = "g1",
            Name = "Wohnzimmer",
            SnapcastGroupId = "snap_g1",
            CurrentStreamId = "default",
            MasterVolume = 50,
            IsMuted = false
        });
        db.RadioStreams.Add(new RadioStreamEntity
        {
            Id = "r1",
            Name = "Rock",
            StreamUrl = "http://example.com/rock"
        });
        await db.SaveChangesAsync();

        // 1. Volume command: audio_orchestrator/groups/g1/set/volume => "65"
        var volMsg = new MqttApplicationMessageBuilder()
            .WithTopic("audio_orchestrator/groups/g1/set/volume")
            .WithPayload("65")
            .Build();
        var volArgs = new MqttApplicationMessageReceivedEventArgs("c1", volMsg, new MqttPublishPacket(), (_, _) => Task.CompletedTask);

        await service.HandleIncomingMessageAsync(volArgs);
        mockVolume.Verify(v => v.SetGroupVolumeAsync("g1", 65, false), Times.Once);

        // 2. Mute command: audio_orchestrator/groups/g1/set/mute => "true"
        var muteMsg = new MqttApplicationMessageBuilder()
            .WithTopic("audio_orchestrator/groups/g1/set/mute")
            .WithPayload("true")
            .Build();
        var muteArgs = new MqttApplicationMessageReceivedEventArgs("c1", muteMsg, new MqttPublishPacket(), (_, _) => Task.CompletedTask);

        await service.HandleIncomingMessageAsync(muteArgs);
        mockVolume.Verify(v => v.SetGroupVolumeAsync("g1", 50, true), Times.Once);

        // 3. Source command: audio_orchestrator/groups/g1/set/source => "Radio: Rock"
        var sourceMsg = new MqttApplicationMessageBuilder()
            .WithTopic("audio_orchestrator/groups/g1/set/source")
            .WithPayload("Radio: Rock")
            .Build();
        var sourceArgs = new MqttApplicationMessageReceivedEventArgs("c1", sourceMsg, new MqttPublishPacket(), (_, _) => Task.CompletedTask);

        await service.HandleIncomingMessageAsync(sourceArgs);
        var updatedGroup = await db.Groups.AsNoTracking().FirstAsync(g => g.Id == "g1");
        Assert.Equal("radio_r1", updatedGroup.CurrentStreamId);
    }

    [Fact]
    public async Task HandleIncomingMessageAsync_ShouldForceDiscovery_WhenHomeAssistantReportsOnline()
    {
        var (sp, db, mockClient, service, _) = CreateTestContext(discoveryPrefix: "custom_disc", baseTopic: "orch");

        mockClient.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientConnectResult());
        mockClient.Setup(c => c.SubscribeAsync(It.IsAny<MqttClientSubscribeOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientSubscribeResult(
                1,
                new List<MqttClientSubscribeResultItem>
                {
                    new(new MqttTopicFilter { Topic = "orch/groups/+/set/#" }, MqttClientSubscribeResultCode.GrantedQoS0),
                    new(new MqttTopicFilter { Topic = "custom_disc/status" }, MqttClientSubscribeResultCode.GrantedQoS0)
                },
                string.Empty,
                new List<MqttUserProperty>()
            ));
        mockClient.SetupGet(c => c.IsConnected).Returns(true);

        await service.EnsureConnectionAsync(CancellationToken.None);
        mockClient.Invocations.Clear();

        // Add a group to DB so discovery publishes something
        db.Groups.Add(new GroupEntity { Id = "test_g1", Name = "Wohnzimmer", CurrentStreamId = "default" });
        await db.SaveChangesAsync();

        var haOnlineMsg = new MqttApplicationMessageBuilder()
            .WithTopic("custom_disc/status")
            .WithPayload("online")
            .Build();
        var args = new MqttApplicationMessageReceivedEventArgs("c1", haOnlineMsg, new MqttPublishPacket(), (_, _) => Task.CompletedTask);

        await service.HandleIncomingMessageAsync(args);

        // Verify discovery config published for group
        mockClient.Verify(c => c.PublishAsync(
            It.Is<MqttApplicationMessage>(m => m.Topic.StartsWith("custom_disc/select/audio_orchestrator_test_g1/source/config")),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ReloadConfigurationAsync_ShouldCoordinateWithPublishLock_AndPerformInitialSync()
    {
        var (sp, db, mockClient, service, _) = CreateTestContext();

        mockClient.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientConnectResult());
        mockClient.Setup(c => c.SubscribeAsync(It.IsAny<MqttClientSubscribeOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientSubscribeResult(
                1,
                new List<MqttClientSubscribeResultItem>
                {
                    new(new MqttTopicFilter { Topic = "audio_orchestrator/groups/+/set/#" }, MqttClientSubscribeResultCode.GrantedQoS0),
                    new(new MqttTopicFilter { Topic = "homeassistant/status" }, MqttClientSubscribeResultCode.GrantedQoS0)
                },
                string.Empty,
                new List<MqttUserProperty>()
            ));
        mockClient.SetupGet(c => c.IsConnected).Returns(true);

        await service.ReloadConfigurationAsync(CancellationToken.None);

        Assert.True(service.IsConnected);
        mockClient.Verify(c => c.PublishAsync(It.Is<MqttApplicationMessage>(m => m.Topic == "audio_orchestrator/bridge/status"), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task DisconnectAsync_ShouldAvoidDeadlock_WhenIncomingCallbackIsBlockedOnPublishLock()
    {
        var (sp, db, mockClient, service, _) = CreateTestContext();

        mockClient.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientConnectResult());
        mockClient.Setup(c => c.SubscribeAsync(It.IsAny<MqttClientSubscribeOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientSubscribeResult(
                1,
                new List<MqttClientSubscribeResultItem>
                {
                    new(new MqttTopicFilter { Topic = "audio_orchestrator/groups/+/set/#" }, MqttClientSubscribeResultCode.GrantedQoS0),
                    new(new MqttTopicFilter { Topic = "homeassistant/status" }, MqttClientSubscribeResultCode.GrantedQoS0)
                },
                string.Empty,
                new List<MqttUserProperty>()
            ));
        mockClient.SetupGet(c => c.IsConnected).Returns(true);

        await service.EnsureConnectionAsync(CancellationToken.None);

        db.Groups.Add(new GroupEntity { Id = "g1", Name = "Wohnzimmer", CurrentStreamId = "default" });
        await db.SaveChangesAsync();

        var publishBlockedTcs = new TaskCompletionSource();
        var allowPublishFinishTcs = new TaskCompletionSource();

        // When NotifyFullStateAsync publishes state, hold the publish lock
        mockClient.Setup(c => c.PublishAsync(
                It.Is<MqttApplicationMessage>(m => m.Topic == "audio_orchestrator/groups/g1/state"),
                It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                publishBlockedTcs.TrySetResult();
                await allowPublishFinishTcs.Task;
                return new MqttClientPublishResult(1, MqttClientPublishReasonCode.Success, string.Empty, Array.Empty<MqttUserProperty>());
            });

        // 1. Start background publication that acquires and holds _publishLock
        var dummyState = new FullStateDto(
            new List<GroupDto> { new("g1", "Wohnzimmer", "default", new List<RoomDto>(), 50, false) },
            new List<RoomDto>(),
            new List<StreamDto>(),
            new List<SpotifySourceDto>(),
            new List<StreamDto>(),
            null,
            VolumeProfile.Linear
        );

        var publishTask = Task.Run(() => service.NotifyFullStateAsync(dummyState, CancellationToken.None));
        await publishBlockedTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));

        // 2. Trigger an incoming callback that tries to acquire _publishLock (e.g. HA status online)
        var haOnlineMsg = new MqttApplicationMessageBuilder()
            .WithTopic("homeassistant/status")
            .WithPayload("online")
            .Build();
        var args = new MqttApplicationMessageReceivedEventArgs("c1", haOnlineMsg, new MqttPublishPacket(), (_, _) => Task.CompletedTask);

        var callbackTask = Task.Run(() => service.HandleIncomingMessageAsync(args));

        // Give the callback task a moment to enter and block on _publishLock
        await Task.Delay(50);

        // 3. Initiate DisconnectAsync while the callback is blocked on _publishLock.
        // In vulnerable code, Disconnect held _publishLock while MQTTnet waited on the callback -> deadlock.
        // In fixed code, StopAndDrainCallbacksAsync cancels the session token, unblocking the callback immediately.
        var disconnectTask = Task.Run(() => service.DisconnectAsync());

        // Callback must be unblocked and complete promptly even before allowPublishFinishTcs is set!
        await callbackTask.WaitAsync(TimeSpan.FromSeconds(3));

        // Now allow original publisher to complete
        allowPublishFinishTcs.TrySetResult();
        await publishTask.WaitAsync(TimeSpan.FromSeconds(3));

        // Disconnect must finish cleanly without deadlock
        await disconnectTask.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(service.IsConnected);
        mockClient.Verify(c => c.DisconnectAsync(It.IsAny<MqttClientDisconnectOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void BuildSourceMapping_ShouldBoundLabelsTo255Characters_AndResolveCollisionsDeterministically()
    {
        var longNameA = new string('A', 300);
        var longNameB = new string('A', 248) + "XYZ";
        var longNameC = new string('A', 248) + "123";
        var longSpotify = new string('S', 300);

        var radios = new List<(string Id, string Name)>
        {
            ("r1", longNameA),
            ("r2", longNameA), // duplicate name
            ("r3", longNameB), // collides with longNameC when shortened to 248
            ("r4", longNameC)
        };

        var spotifies = new List<(string Id, string Name)>
        {
            ("s1", longSpotify),
            ("s2", longSpotify)
        };

        var result = HomeAssistantMqttService.BuildSourceMapping(radios, spotifies);

        // 1. Every label must strictly be <= 255 characters (Home Assistant maximum limit)
        foreach (var label in result.SourceList)
        {
            Assert.True(label.Length <= 255, $"Label '{label}' exceeds 255 chars (Length: {label.Length})");
        }

        // 2. Every label in SourceList must be unique
        Assert.Equal(result.SourceList.Count, result.SourceList.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // 3. Round-trip resolution: each advertised label must resolve back to its corresponding stream ID
        foreach (var (label, expectedStreamId) in result.LabelToStreamId)
        {
            Assert.True(label.Length <= 255);
            var resolvedId = HomeAssistantMqttService.ResolveSourceToStreamId(label, radios, spotifies);
            Assert.Equal(expectedStreamId, resolvedId);
        }

        // 4. Raw IDs and stream IDs must also resolve
        Assert.Equal("radio_r1", HomeAssistantMqttService.ResolveSourceToStreamId("r1", radios, spotifies));
        Assert.Equal("radio_r1", HomeAssistantMqttService.ResolveSourceToStreamId("radio_r1", radios, spotifies));
        Assert.Equal("spotify_s1", HomeAssistantMqttService.ResolveSourceToStreamId("s1", radios, spotifies));
        Assert.Equal("spotify_s1", HomeAssistantMqttService.ResolveSourceToStreamId("spotify_s1", radios, spotifies));
    }

    [Fact]
    public async Task PublishDiscovery_ShouldNotCacheDiscovery_AndPreserveRetry_WhenPublishIsNegativelyAcknowledged()
    {
        var (sp, db, mockClient, service, _) = CreateTestContext();

        mockClient.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientConnectResult());
        mockClient.Setup(c => c.SubscribeAsync(It.IsAny<MqttClientSubscribeOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientSubscribeResult(
                1,
                new List<MqttClientSubscribeResultItem>
                {
                    new(new MqttTopicFilter { Topic = "audio_orchestrator/groups/+/set/#" }, MqttClientSubscribeResultCode.GrantedQoS0),
                    new(new MqttTopicFilter { Topic = "homeassistant/status" }, MqttClientSubscribeResultCode.GrantedQoS0)
                },
                string.Empty,
                new List<MqttUserProperty>()
            ));
        mockClient.SetupGet(c => c.IsConnected).Returns(true);

        await service.EnsureConnectionAsync(CancellationToken.None);

        db.Groups.Add(new GroupEntity { Id = "g1", Name = "Wohnzimmer", CurrentStreamId = "default" });
        await db.SaveChangesAsync();

        // 1. Simulate negative acknowledgement from MQTT broker (e.g. NotAuthorized)
        mockClient.Setup(c => c.PublishAsync(
                It.Is<MqttApplicationMessage>(m => m.Topic.Contains("audio_orchestrator_g1")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientPublishResult(1, MqttClientPublishReasonCode.NotAuthorized, "ACL rejected", Array.Empty<MqttUserProperty>()));

        // Trigger discovery via HA status online (which sets forceDiscovery = true)
        var haOnlineMsg = new MqttApplicationMessageBuilder()
            .WithTopic("homeassistant/status")
            .WithPayload("online")
            .Build();
        var args = new MqttApplicationMessageReceivedEventArgs("c1", haOnlineMsg, new MqttPublishPacket(), (_, _) => Task.CompletedTask);

        await service.HandleIncomingMessageAsync(args);

        // Discovery failed due to negative acknowledgement, so it must not be cached as successful
        mockClient.Invocations.Clear();

        // 2. Broker recovers: publish calls now succeed
        mockClient.Setup(c => c.PublishAsync(It.IsAny<MqttApplicationMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientPublishResult(1, MqttClientPublishReasonCode.Success, string.Empty, Array.Empty<MqttUserProperty>()));

        // Next heartbeat/sync: Even though discoveryHash has not changed, the retry was preserved!
        await service.EnsureConnectionAsync(CancellationToken.None);

        // Verify discovery was re-attempted and published
        mockClient.Verify(c => c.PublishAsync(
            It.Is<MqttApplicationMessage>(m => m.Topic.StartsWith("homeassistant/select/audio_orchestrator_g1/source/config")),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task EnsureConnectionAsync_ShouldSucceed_WhenBrokerAcknowledgesWithNoMatchingSubscribers()
    {
        var (sp, db, mockClient, service, _) = CreateTestContext();

        mockClient.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientConnectResult());
        mockClient.Setup(c => c.SubscribeAsync(It.IsAny<MqttClientSubscribeOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientSubscribeResult(
                1,
                new List<MqttClientSubscribeResultItem>
                {
                    new(new MqttTopicFilter { Topic = "audio_orchestrator/groups/+/set/#" }, MqttClientSubscribeResultCode.GrantedQoS0),
                    new(new MqttTopicFilter { Topic = "homeassistant/status" }, MqttClientSubscribeResultCode.GrantedQoS0)
                },
                string.Empty,
                new List<MqttUserProperty>()
            ));
        mockClient.SetupGet(c => c.IsConnected).Returns(true);

        // Broker returns NoMatchingSubscribers (0x10), which is a successful MQTT 5 acknowledgement
        mockClient.Setup(c => c.PublishAsync(It.IsAny<MqttApplicationMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientPublishResult(1, MqttClientPublishReasonCode.NoMatchingSubscribers, string.Empty, Array.Empty<MqttUserProperty>()));

        await service.EnsureConnectionAsync(CancellationToken.None);

        Assert.True(service.IsConnected);
        mockClient.Verify(c => c.PublishAsync(
            It.Is<MqttApplicationMessage>(m => m.Topic == "audio_orchestrator/bridge/status" && m.ConvertPayloadToString() == "online"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublishDiscovery_ShouldPreservePendingGroupCleanup_WhenUnpublishFails()
    {
        var (sp, db, mockClient, service, _) = CreateTestContext();

        mockClient.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientConnectResult());
        mockClient.Setup(c => c.SubscribeAsync(It.IsAny<MqttClientSubscribeOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientSubscribeResult(
                1,
                new List<MqttClientSubscribeResultItem>
                {
                    new(new MqttTopicFilter { Topic = "audio_orchestrator/groups/+/set/#" }, MqttClientSubscribeResultCode.GrantedQoS0),
                    new(new MqttTopicFilter { Topic = "homeassistant/status" }, MqttClientSubscribeResultCode.GrantedQoS0)
                },
                string.Empty,
                new List<MqttUserProperty>()
            ));
        mockClient.SetupGet(c => c.IsConnected).Returns(true);

        // 1. Initial setup with group g1
        var group = new GroupEntity { Id = "g1", Name = "Wohnzimmer", CurrentStreamId = "default" };
        db.Groups.Add(group);
        await db.SaveChangesAsync();

        await service.EnsureConnectionAsync(CancellationToken.None);
        mockClient.Invocations.Clear();

        // 2. Delete group g1 from DB
        db.Groups.Remove(group);
        await db.SaveChangesAsync();

        // Make unpublishing g1 fail (e.g. broker rejects empty retained message)
        mockClient.Setup(c => c.PublishAsync(
                It.Is<MqttApplicationMessage>(m => m.Topic.Contains("audio_orchestrator_g1")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientPublishResult(1, MqttClientPublishReasonCode.NotAuthorized, "denied", Array.Empty<MqttUserProperty>()));

        // Sync fails
        await service.EnsureConnectionAsync(CancellationToken.None);
        mockClient.Invocations.Clear();

        // 3. Broker recovers
        mockClient.Setup(c => c.PublishAsync(It.IsAny<MqttApplicationMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientPublishResult(1, MqttClientPublishReasonCode.Success, string.Empty, Array.Empty<MqttUserProperty>()));

        // Next sync must still remember that g1 needs cleanup!
        await service.EnsureConnectionAsync(CancellationToken.None);

        // Verify unpublish was retried for g1 (empty payload removing discovery config)
        mockClient.Verify(c => c.PublishAsync(
            It.Is<MqttApplicationMessage>(m => m.Topic.StartsWith("homeassistant/select/audio_orchestrator_g1/source/config") && string.IsNullOrEmpty(m.ConvertPayloadToString())),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ReloadConfigurationAsync_ShouldUnpublishOldDiscoveryTopics_WhenDiscoveryPrefixOrBaseTopicChanges()
    {
        var (sp, db, mockClient, service, _) = CreateTestContext(discoveryPrefix: "old_ha", baseTopic: "old_orch");

        mockClient.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientConnectResult());
        mockClient.Setup(c => c.SubscribeAsync(It.IsAny<MqttClientSubscribeOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientSubscribeResult(
                1,
                new List<MqttClientSubscribeResultItem>
                {
                    new(new MqttTopicFilter { Topic = "old_orch/groups/+/set/#" }, MqttClientSubscribeResultCode.GrantedQoS0),
                    new(new MqttTopicFilter { Topic = "old_ha/status" }, MqttClientSubscribeResultCode.GrantedQoS0)
                },
                string.Empty,
                new List<MqttUserProperty>()
            ));
        mockClient.SetupGet(c => c.IsConnected).Returns(true);

        // 1. Initial setup with group g1
        var group = new GroupEntity { Id = "g1", Name = "Wohnzimmer", CurrentStreamId = "default" };
        db.Groups.Add(group);
        await db.SaveChangesAsync();

        await service.EnsureConnectionAsync(CancellationToken.None);

        // Verify group g1 was discovered under old_ha
        mockClient.Verify(c => c.PublishAsync(
            It.Is<MqttApplicationMessage>(m => m.Topic.StartsWith("old_ha/select/audio_orchestrator_g1/source/config") && !string.IsNullOrEmpty(m.ConvertPayloadToString())),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);

        mockClient.Invocations.Clear();

        // 2. Change discovery prefix and base topic in settings
        var settings = await db.Settings.FirstAsync();
        settings.MqttDiscoveryPrefix = "new_ha";
        settings.MqttBaseTopic = "new_orch";
        await db.SaveChangesAsync();

        // Subscription for new topic
        mockClient.Setup(c => c.SubscribeAsync(It.IsAny<MqttClientSubscribeOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientSubscribeResult(
                2,
                new List<MqttClientSubscribeResultItem>
                {
                    new(new MqttTopicFilter { Topic = "new_orch/groups/+/set/#" }, MqttClientSubscribeResultCode.GrantedQoS0),
                    new(new MqttTopicFilter { Topic = "new_ha/status" }, MqttClientSubscribeResultCode.GrantedQoS0)
                },
                string.Empty,
                new List<MqttUserProperty>()
            ));

        // Reload configuration
        await service.ReloadConfigurationAsync(CancellationToken.None);

        // Verify old discovery topics were cleaned up (empty payload retained publish)
        mockClient.Verify(c => c.PublishAsync(
            It.Is<MqttApplicationMessage>(m => m.Topic.StartsWith("old_ha/select/audio_orchestrator_g1/source/config") && string.IsNullOrEmpty(m.ConvertPayloadToString())),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        mockClient.Verify(c => c.PublishAsync(
            It.Is<MqttApplicationMessage>(m => m.Topic == "old_orch/groups/g1/state" && string.IsNullOrEmpty(m.ConvertPayloadToString())),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        mockClient.Verify(c => c.PublishAsync(
            It.Is<MqttApplicationMessage>(m => m.Topic == "old_orch/bridge/status" && string.IsNullOrEmpty(m.ConvertPayloadToString())),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);

        // Verify new discovery topics were published under new_ha and new_orch
        mockClient.Verify(c => c.PublishAsync(
            It.Is<MqttApplicationMessage>(m => m.Topic.StartsWith("new_ha/select/audio_orchestrator_g1/source/config") && !string.IsNullOrEmpty(m.ConvertPayloadToString())),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        mockClient.Verify(c => c.PublishAsync(
            It.Is<MqttApplicationMessage>(m => m.Topic == "new_orch/bridge/status" && m.ConvertPayloadToString() == "online"),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task NotifyFullStateAsync_ShouldDetectDiscoveryChange_WhenSourceNameDelimitersDiffer()
    {
        var (sp, db, mockClient, service, _) = CreateTestContext();

        mockClient.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientConnectResult());
        mockClient.Setup(c => c.SubscribeAsync(It.IsAny<MqttClientSubscribeOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientSubscribeResult(
                1,
                new List<MqttClientSubscribeResultItem>
                {
                    new(new MqttTopicFilter { Topic = "audio_orchestrator/groups/+/set/#" }, MqttClientSubscribeResultCode.GrantedQoS0),
                    new(new MqttTopicFilter { Topic = "homeassistant/status" }, MqttClientSubscribeResultCode.GrantedQoS0)
                },
                string.Empty,
                new List<MqttUserProperty>()
            ));
        mockClient.SetupGet(c => c.IsConnected).Returns(true);

        await service.EnsureConnectionAsync(CancellationToken.None);

        var groupDto = new GroupDto("g1", "Wohnzimmer", "default", [], 50, false);

        // Pass initial state with one radio whose name has a comma and "Radio: " prefix matching what would collide in naive join
        var state1 = new FullStateDto(
            [groupDto],
            [],
            [new StreamDto("radio_1", "Rock,Radio: Jazz", StreamStatus.Idle, "http://example.com/stream")],
            [],
            []
        );

        await service.NotifyFullStateAsync(state1);

        mockClient.Invocations.Clear();

        // Pass second state with two separate radios "Rock" and "Jazz"
        var state2 = new FullStateDto(
            [groupDto],
            [],
            [
                new StreamDto("radio_1", "Rock", StreamStatus.Idle, "http://example.com/rock"),
                new StreamDto("radio_2", "Jazz", StreamStatus.Idle, "http://example.com/jazz")
            ],
            [],
            []
        );

        await service.NotifyFullStateAsync(state2);

        // Discovery select entity MUST be republished because source list structured JSON changed
        mockClient.Verify(c => c.PublishAsync(
            It.Is<MqttApplicationMessage>(m => m.Topic.StartsWith("homeassistant/select/audio_orchestrator_g1/source/config")),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ReloadConfigurationAsync_ShouldRetryCleanupOfOldPrefix_WhenInitialUnpublishFails()
    {
        var (sp, db, mockClient, service, _) = CreateTestContext(discoveryPrefix: "old_ha", baseTopic: "old_orch");

        mockClient.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientConnectResult());
        mockClient.Setup(c => c.SubscribeAsync(It.IsAny<MqttClientSubscribeOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientSubscribeResult(
                1,
                new List<MqttClientSubscribeResultItem>
                {
                    new(new MqttTopicFilter { Topic = "old_orch/groups/+/set/#" }, MqttClientSubscribeResultCode.GrantedQoS0),
                    new(new MqttTopicFilter { Topic = "old_ha/status" }, MqttClientSubscribeResultCode.GrantedQoS0)
                },
                string.Empty,
                new List<MqttUserProperty>()
            ));
        mockClient.SetupGet(c => c.IsConnected).Returns(true);

        // 1. Initial setup with group g1
        var group = new GroupEntity { Id = "g1", Name = "Wohnzimmer", CurrentStreamId = "default" };
        db.Groups.Add(group);
        await db.SaveChangesAsync();

        await service.EnsureConnectionAsync(CancellationToken.None);

        // Verify g1 was discovered under old_ha
        mockClient.Verify(c => c.PublishAsync(
            It.Is<MqttApplicationMessage>(m => m.Topic.StartsWith("old_ha/select/audio_orchestrator_g1/source/config") && !string.IsNullOrEmpty(m.ConvertPayloadToString())),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);

        // 2. Change discovery prefix to new_ha
        var settings = await db.Settings.FirstAsync();
        settings.MqttDiscoveryPrefix = "new_ha";
        await db.SaveChangesAsync();

        // Simulate publish failure on unpublishing old_ha config
        mockClient.Setup(c => c.PublishAsync(
                It.Is<MqttApplicationMessage>(m => m.Topic.StartsWith("old_ha/select/audio_orchestrator_g1")),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Broker disconnected during unpublish"));

        // Subscription setup for new_ha
        mockClient.Setup(c => c.SubscribeAsync(It.IsAny<MqttClientSubscribeOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientSubscribeResult(
                2,
                new List<MqttClientSubscribeResultItem>
                {
                    new(new MqttTopicFilter { Topic = "old_orch/groups/+/set/#" }, MqttClientSubscribeResultCode.GrantedQoS0),
                    new(new MqttTopicFilter { Topic = "new_ha/status" }, MqttClientSubscribeResultCode.GrantedQoS0)
                },
                string.Empty,
                new List<MqttUserProperty>()
            ));

        // Reload configuration while unpublish on old_ha fails
        await service.ReloadConfigurationAsync(CancellationToken.None);

        // Verify new_ha was published during reload before resetting invocations
        mockClient.Verify(c => c.PublishAsync(
            It.Is<MqttApplicationMessage>(m => m.Topic.StartsWith("new_ha/select/audio_orchestrator_g1/source/config") && !string.IsNullOrEmpty(m.ConvertPayloadToString())),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);

        // 3. Broker recovers and reset invocation history so failed attempts from reload aren't counted
        mockClient.Invocations.Clear();
        mockClient.Setup(c => c.PublishAsync(It.IsAny<MqttApplicationMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientPublishResult(1, MqttClientPublishReasonCode.Success, string.Empty, Array.Empty<MqttUserProperty>()));

        // Trigger next sync / heartbeat
        await service.EnsureConnectionAsync(CancellationToken.None);

        // The pending cleanup MUST have remembered old_ha and retried unpublishing it with empty payload!
        mockClient.Verify(c => c.PublishAsync(
            It.Is<MqttApplicationMessage>(m => m.Topic.StartsWith("old_ha/select/audio_orchestrator_g1/source/config") && string.IsNullOrEmpty(m.ConvertPayloadToString())),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task EnsureConnectionAsync_ShouldEnqueueCleanup_WhenDisabledDuringBrokerOutage()
    {
        var (sp, db, mockClient, service, _) = CreateTestContext();

        mockClient.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientConnectResult());
        mockClient.Setup(c => c.SubscribeAsync(It.IsAny<MqttClientSubscribeOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientSubscribeResult(
                1,
                new List<MqttClientSubscribeResultItem>
                {
                    new(new MqttTopicFilter { Topic = "audio_orchestrator/groups/+/set/#" }, MqttClientSubscribeResultCode.GrantedQoS0),
                    new(new MqttTopicFilter { Topic = "homeassistant/status" }, MqttClientSubscribeResultCode.GrantedQoS0)
                },
                string.Empty,
                new List<MqttUserProperty>()
            ));
        mockClient.SetupGet(c => c.IsConnected).Returns(true);

        // 1. Initial connection with group g1
        var group = new GroupEntity { Id = "g1", Name = "Wohnzimmer", CurrentStreamId = "default" };
        db.Groups.Add(group);
        await db.SaveChangesAsync();

        await service.EnsureConnectionAsync(CancellationToken.None);

        // 2. Broker outage occurs while connected
        mockClient.SetupGet(c => c.IsConnected).Returns(false);

        // 3. User disables Home Assistant in settings during outage
        var settings = await db.Settings.FirstAsync();
        settings.HomeAssistantEnabled = false;
        await db.SaveChangesAsync();

        // 4. EnsureConnectionAsync runs during outage (disconnected)
        await service.EnsureConnectionAsync(CancellationToken.None);

        mockClient.Invocations.Clear();

        // 5. Broker recovers
        mockClient.SetupGet(c => c.IsConnected).Returns(true);

        // 6. Next EnsureConnectionAsync run should connect for cleanup and remove g1
        await service.EnsureConnectionAsync(CancellationToken.None);

        mockClient.Verify(c => c.PublishAsync(
            It.Is<MqttApplicationMessage>(m => m.Topic.StartsWith("homeassistant/select/audio_orchestrator_g1/source/config") && string.IsNullOrEmpty(m.ConvertPayloadToString())),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task PublishAsync_ShouldThrow_WhenDisconnectedDuringUnpublish()
    {
        var (sp, db, mockClient, service, _) = CreateTestContext();

        mockClient.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientConnectResult());
        mockClient.Setup(c => c.SubscribeAsync(It.IsAny<MqttClientSubscribeOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientSubscribeResult(
                1,
                new List<MqttClientSubscribeResultItem>
                {
                    new(new MqttTopicFilter { Topic = "audio_orchestrator/groups/+/set/#" }, MqttClientSubscribeResultCode.GrantedQoS0),
                    new(new MqttTopicFilter { Topic = "homeassistant/status" }, MqttClientSubscribeResultCode.GrantedQoS0)
                },
                string.Empty,
                new List<MqttUserProperty>()
            ));
        mockClient.SetupGet(c => c.IsConnected).Returns(true);

        var group = new GroupEntity { Id = "g1", Name = "Wohnzimmer", CurrentStreamId = "default" };
        db.Groups.Add(group);
        await db.SaveChangesAsync();

        await service.EnsureConnectionAsync(CancellationToken.None);

        // Delete group, but disconnect client before/during unpublish
        db.Groups.Remove(group);
        await db.SaveChangesAsync();

        mockClient.SetupGet(c => c.IsConnected).Returns(false);

        // EnsureConnectionAsync runs while disconnected
        await service.EnsureConnectionAsync(CancellationToken.None);

        // Reconnect broker
        mockClient.SetupGet(c => c.IsConnected).Returns(true);

        // Next pass: unpublish should be retried because previous attempt threw on disconnect
        await service.EnsureConnectionAsync(CancellationToken.None);

        mockClient.Verify(c => c.PublishAsync(
            It.Is<MqttApplicationMessage>(m => m.Topic.StartsWith("homeassistant/select/audio_orchestrator_g1/source/config") && string.IsNullOrEmpty(m.ConvertPayloadToString())),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task NotifyFullStateAsync_ShouldNotPublish_WhenIntegrationDisabledEvenIfClientConnected()
    {
        var (sp, db, mockClient, service, _) = CreateTestContext();

        mockClient.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientConnectResult());
        mockClient.Setup(c => c.SubscribeAsync(It.IsAny<MqttClientSubscribeOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientSubscribeResult(
                1,
                new List<MqttClientSubscribeResultItem>
                {
                    new(new MqttTopicFilter { Topic = "audio_orchestrator/groups/+/set/#" }, MqttClientSubscribeResultCode.GrantedQoS0),
                    new(new MqttTopicFilter { Topic = "homeassistant/status" }, MqttClientSubscribeResultCode.GrantedQoS0)
                },
                string.Empty,
                new List<MqttUserProperty>()
            ));
        mockClient.SetupGet(c => c.IsConnected).Returns(true);

        var group = new GroupEntity { Id = "g1", Name = "Wohnzimmer", CurrentStreamId = "default" };
        db.Groups.Add(group);
        await db.SaveChangesAsync();

        await service.EnsureConnectionAsync(CancellationToken.None);

        // Disable Home Assistant in settings, but simulate cleanup publish failure so client remains connected to retry
        var settings = await db.Settings.FirstAsync();
        settings.HomeAssistantEnabled = false;
        await db.SaveChangesAsync();

        mockClient.Setup(c => c.PublishAsync(
                It.Is<MqttApplicationMessage>(m => m.Topic.StartsWith("homeassistant/select/audio_orchestrator_g1")),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Broker refused unpublish"));

        await service.EnsureConnectionAsync(CancellationToken.None);
        mockClient.Invocations.Clear();

        // While client is connected and waiting to retry cleanup, attempt to publish full state
        var dummyState = new FullStateDto([new GroupDto("g1", "Wohnzimmer", "default", [], 50, false)], [], [], [], []);
        await service.NotifyFullStateAsync(dummyState);

        // Should NOT publish discovery or state for group g1
        mockClient.Verify(c => c.PublishAsync(
            It.Is<MqttApplicationMessage>(m => m.Topic.Contains("audio_orchestrator_g1") && !string.IsNullOrEmpty(m.ConvertPayloadToString())),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessPendingCleanupsAsync_ShouldNotClearBridgeStatus_WhenBaseTopicBelongsToActiveSession()
    {
        var (sp, db, mockClient, service, _) = CreateTestContext();

        mockClient.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientConnectResult());
        mockClient.Setup(c => c.SubscribeAsync(It.IsAny<MqttClientSubscribeOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientSubscribeResult(
                1,
                new List<MqttClientSubscribeResultItem>
                {
                    new(new MqttTopicFilter { Topic = "audio_orchestrator/groups/+/set/#" }, MqttClientSubscribeResultCode.GrantedQoS0),
                    new(new MqttTopicFilter { Topic = "homeassistant/status" }, MqttClientSubscribeResultCode.GrantedQoS0)
                },
                string.Empty,
                new List<MqttUserProperty>()
            ));
        mockClient.SetupGet(c => c.IsConnected).Returns(true);

        var group = new GroupEntity { Id = "g1", Name = "Wohnzimmer", CurrentStreamId = "default" };
        db.Groups.Add(group);
        await db.SaveChangesAsync();

        await service.EnsureConnectionAsync(CancellationToken.None);

        // 1. Simulate broker outage
        mockClient.SetupGet(c => c.IsConnected).Returns(false);

        // 2. Disable HA during outage (enqueues cleanup with ClearBridgeStatus = true for audio_orchestrator)
        var settings = await db.Settings.FirstAsync();
        settings.HomeAssistantEnabled = false;
        await db.SaveChangesAsync();
        await service.EnsureConnectionAsync(CancellationToken.None);

        // 3. Re-enable HA with the same base topic before broker recovers
        settings.HomeAssistantEnabled = true;
        await db.SaveChangesAsync();

        // 4. Broker recovers and active session connects
        mockClient.SetupGet(c => c.IsConnected).Returns(true);
        mockClient.Invocations.Clear();

        await service.EnsureConnectionAsync(CancellationToken.None);

        // Verify bridge status was NOT deleted with empty string because it belongs to the active session!
        mockClient.Verify(c => c.PublishAsync(
            It.Is<MqttApplicationMessage>(m => m.Topic == "audio_orchestrator/bridge/status" && string.IsNullOrEmpty(m.ConvertPayloadToString())),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PublishDiscovery_ShouldTrackGroup_WhenPublicationPartiallyFails()
    {
        var (sp, db, mockClient, service, _) = CreateTestContext();

        mockClient.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientConnectResult());
        mockClient.Setup(c => c.SubscribeAsync(It.IsAny<MqttClientSubscribeOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientSubscribeResult(
                1,
                new List<MqttClientSubscribeResultItem>
                {
                    new(new MqttTopicFilter { Topic = "audio_orchestrator/groups/+/set/#" }, MqttClientSubscribeResultCode.GrantedQoS0),
                    new(new MqttTopicFilter { Topic = "homeassistant/status" }, MqttClientSubscribeResultCode.GrantedQoS0)
                },
                string.Empty,
                new List<MqttUserProperty>()
            ));
        mockClient.SetupGet(c => c.IsConnected).Returns(true);

        var group = new GroupEntity { Id = "g1", Name = "Wohnzimmer", CurrentStreamId = "default" };
        db.Groups.Add(group);
        await db.SaveChangesAsync();

        // Make volume entity publish throw, so discovery for g1 partially fails after select entity
        mockClient.Setup(c => c.PublishAsync(
                It.Is<MqttApplicationMessage>(m => m.Topic.Contains("audio_orchestrator_g1/volume/config")),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Broker timeout during volume config publish"));

        // First pass partially fails
        await service.EnsureConnectionAsync(CancellationToken.None);

        // Now remove group g1 from DB before full discovery completes
        db.Groups.Remove(group);
        await db.SaveChangesAsync();

        // Broker recovers
        mockClient.Setup(c => c.PublishAsync(It.IsAny<MqttApplicationMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientPublishResult(1, MqttClientPublishReasonCode.Success, string.Empty, Array.Empty<MqttUserProperty>()));

        // Next pass: group g1 was tracked, so it MUST be unpublished!
        await service.EnsureConnectionAsync(CancellationToken.None);

        mockClient.Verify(c => c.PublishAsync(
            It.Is<MqttApplicationMessage>(m => m.Topic.StartsWith("homeassistant/select/audio_orchestrator_g1/source/config") && string.IsNullOrEmpty(m.ConvertPayloadToString())),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ProcessPendingCleanupsAsync_ShouldRepublishDiscovery_WhenCleanupFailsPartiallyOnStateTopic()
    {
        var (sp, db, mockClient, service, _) = CreateTestContext(discoveryPrefix: "homeassistant", baseTopic: "old_base");

        mockClient.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientConnectResult());
        mockClient.Setup(c => c.SubscribeAsync(It.IsAny<MqttClientSubscribeOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientSubscribeResult(
                1,
                new List<MqttClientSubscribeResultItem>
                {
                    new(new MqttTopicFilter { Topic = "old_base/groups/+/set/#" }, MqttClientSubscribeResultCode.GrantedQoS0),
                    new(new MqttTopicFilter { Topic = "new_base/groups/+/set/#" }, MqttClientSubscribeResultCode.GrantedQoS0),
                    new(new MqttTopicFilter { Topic = "homeassistant/status" }, MqttClientSubscribeResultCode.GrantedQoS0)
                },
                string.Empty,
                new List<MqttUserProperty>()
            ));
        mockClient.SetupGet(c => c.IsConnected).Returns(true);

        var group = new GroupEntity { Id = "g1", Name = "Wohnzimmer", CurrentStreamId = "default" };
        db.Groups.Add(group);
        await db.SaveChangesAsync();

        // 1. Initial connection
        await service.EnsureConnectionAsync(CancellationToken.None);

        // 2. Change base topic (discovery prefix stays identical: homeassistant)
        var settings = await db.Settings.FirstAsync();
        settings.MqttBaseTopic = "new_base";
        await db.SaveChangesAsync();

        // Setup: unpublishing configs succeeds, but clearing state on old_base throws!
        mockClient.Setup(c => c.PublishAsync(
                It.Is<MqttApplicationMessage>(m => m.Topic == "old_base/groups/g1/state"),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Broker disconnected when clearing old state topic"));

        await service.ReloadConfigurationAsync(CancellationToken.None);

        // Clear invocations after reload
        mockClient.Invocations.Clear();

        // 3. Heartbeat runs: cleanup will delete configs again and fail on old state topic,
        // but because _discoveredGroupIds was invalidated before cleanup, the active session MUST restore configs!
        await service.EnsureConnectionAsync(CancellationToken.None);

        // Verify discovery configs for new_base were restored with non-empty payload
        mockClient.Verify(c => c.PublishAsync(
            It.Is<MqttApplicationMessage>(m => m.Topic.StartsWith("homeassistant/select/audio_orchestrator_g1/source/config") && !string.IsNullOrEmpty(m.ConvertPayloadToString())),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public void BuildSourceMapping_ShouldPreserveSurrogatePairs_WhenTruncatingRadioLabels()
    {
        var emoji = "😀";
        var asciiPart = new string('a', 247);
        var longName = asciiPart + emoji;

        var radios = new List<(string Id, string Name)> { ("radio1", longName) };
        var spotifies = new List<(string Id, string Name)>();

        var mapping = HomeAssistantMqttService.BuildSourceMapping(radios, spotifies);
        var label = mapping.StreamIdToLabel["radio_radio1"];

        Assert.False(char.IsHighSurrogate(label[^1]));
        Assert.True(label.Length <= 255);

        var json = JsonSerializer.Serialize(label);
        Assert.DoesNotContain("\uFFFD", json);
        var deserialized = JsonSerializer.Deserialize<string>(json);
        Assert.Equal(label, deserialized);

        Assert.True(mapping.LabelToStreamId.TryGetValue(deserialized!, out var resolvedStreamId));
        Assert.Equal("radio_radio1", resolvedStreamId);
    }

    [Fact]
    public async Task EnsureConnectionAsync_ShouldPublishOffline_BeforeDiscoveryCleanup_WhenDisabled()
    {
        var (sp, db, mockClient, service, _) = CreateTestContext();

        mockClient.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientConnectResult());
        mockClient.Setup(c => c.SubscribeAsync(It.IsAny<MqttClientSubscribeOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientSubscribeResult(
                1,
                new List<MqttClientSubscribeResultItem>
                {
                    new(new MqttTopicFilter { Topic = "audio_orchestrator/groups/+/set/#" }, MqttClientSubscribeResultCode.GrantedQoS0),
                    new(new MqttTopicFilter { Topic = "homeassistant/status" }, MqttClientSubscribeResultCode.GrantedQoS0)
                },
                string.Empty,
                new List<MqttUserProperty>()
            ));
        mockClient.SetupGet(c => c.IsConnected).Returns(true);

        var group = new GroupEntity { Id = "g1", Name = "Wohnzimmer", CurrentStreamId = "default" };
        db.Groups.Add(group);
        await db.SaveChangesAsync();

        await service.EnsureConnectionAsync(CancellationToken.None);
        mockClient.Invocations.Clear();

        // Make unpublishing discovery fail
        mockClient.Setup(c => c.PublishAsync(
                It.Is<MqttApplicationMessage>(m => m.Topic.Contains("audio_orchestrator_g1")),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Broker disconnected during unpublish"));

        // Disable HA
        var settings = await db.Settings.FirstAsync();
        settings.HomeAssistantEnabled = false;
        await db.SaveChangesAsync();

        await service.EnsureConnectionAsync(CancellationToken.None);

        // Verify offline status was published before unpublish threw
        mockClient.Verify(c => c.PublishAsync(
            It.Is<MqttApplicationMessage>(m => m.Topic == "audio_orchestrator/bridge/status" && m.ConvertPayloadToString() == "offline"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReloadConfigurationAsync_ShouldCleanUpDiscovery_WhenBrokerIdentityChanges()
    {
        var (sp, db, mockClient, service, _) = CreateTestContext();

        mockClient.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientConnectResult());
        mockClient.Setup(c => c.SubscribeAsync(It.IsAny<MqttClientSubscribeOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientSubscribeResult(
                1,
                new List<MqttClientSubscribeResultItem>
                {
                    new(new MqttTopicFilter { Topic = "audio_orchestrator/groups/+/set/#" }, MqttClientSubscribeResultCode.GrantedQoS0),
                    new(new MqttTopicFilter { Topic = "homeassistant/status" }, MqttClientSubscribeResultCode.GrantedQoS0)
                },
                string.Empty,
                new List<MqttUserProperty>()
            ));
        mockClient.SetupGet(c => c.IsConnected).Returns(true);

        var group = new GroupEntity { Id = "g1", Name = "Wohnzimmer", CurrentStreamId = "default" };
        db.Groups.Add(group);
        await db.SaveChangesAsync();

        // 1. Initial connection on 127.0.0.1
        await service.EnsureConnectionAsync(CancellationToken.None);
        mockClient.Invocations.Clear();

        // 2. Change broker host
        var settings = await db.Settings.FirstAsync();
        settings.MqttHost = "192.168.1.100";
        await db.SaveChangesAsync();

        await service.ReloadConfigurationAsync(CancellationToken.None);

        // Verify old broker was cleaned up: unpublish g1 discovery config with empty payload
        mockClient.Verify(c => c.PublishAsync(
            It.Is<MqttApplicationMessage>(m => m.Topic.StartsWith("homeassistant/select/audio_orchestrator_g1/source/config") && string.IsNullOrEmpty(m.ConvertPayloadToString())),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);

        // Verify bridge status on old broker was cleared
        mockClient.Verify(c => c.PublishAsync(
            It.Is<MqttApplicationMessage>(m => m.Topic == "audio_orchestrator/bridge/status" && string.IsNullOrEmpty(m.ConvertPayloadToString())),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task EnsureConnectionAsync_ShouldUnpublishActivePrefix_WhenGroupDeletedDuringOutageAndOldPrefixCleanupPending()
    {
        var (sp, db, mockClient, service, _) = CreateTestContext(discoveryPrefix: "old_ha", baseTopic: "audio_orchestrator");

        mockClient.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientConnectResult());
        mockClient.Setup(c => c.SubscribeAsync(It.IsAny<MqttClientSubscribeOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientSubscribeResult(
                1,
                new List<MqttClientSubscribeResultItem>
                {
                    new(new MqttTopicFilter { Topic = "audio_orchestrator/groups/+/set/#" }, MqttClientSubscribeResultCode.GrantedQoS0),
                    new(new MqttTopicFilter { Topic = "new_ha/status" }, MqttClientSubscribeResultCode.GrantedQoS0)
                },
                string.Empty,
                new List<MqttUserProperty>()
            ));
        mockClient.SetupGet(c => c.IsConnected).Returns(true);

        var group = new GroupEntity { Id = "g1", Name = "Wohnzimmer", CurrentStreamId = "default" };
        db.Groups.Add(group);
        await db.SaveChangesAsync();

        // 1. Initial connection with old_ha
        await service.EnsureConnectionAsync(CancellationToken.None);

        // 2. Switch prefix to new_ha while unpublish on old_ha fails
        var settings = await db.Settings.FirstAsync();
        settings.MqttDiscoveryPrefix = "new_ha";
        await db.SaveChangesAsync();

        mockClient.Setup(c => c.PublishAsync(
                It.Is<MqttApplicationMessage>(m => m.Topic.StartsWith("old_ha/")),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Broker timeout during old_ha unpublish"));

        await service.ReloadConfigurationAsync(CancellationToken.None);

        // Verify g1 was discovered under new_ha
        mockClient.Verify(c => c.PublishAsync(
            It.Is<MqttApplicationMessage>(m => m.Topic.StartsWith("new_ha/select/audio_orchestrator_g1/source/config") && !string.IsNullOrEmpty(m.ConvertPayloadToString())),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);

        // 3. Broker outage happens, group g1 is deleted from DB
        mockClient.SetupGet(c => c.IsConnected).Returns(false);
        db.Groups.Remove(group);
        await db.SaveChangesAsync();

        // 4. Broker recovers
        mockClient.SetupGet(c => c.IsConnected).Returns(true);
        mockClient.Invocations.Clear();
        mockClient.Setup(c => c.PublishAsync(It.IsAny<MqttApplicationMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientPublishResult(1, MqttClientPublishReasonCode.Success, string.Empty, Array.Empty<MqttUserProperty>()));

        // Trigger heartbeat
        await service.EnsureConnectionAsync(CancellationToken.None);

        // MUST unpublish g1 under active new_ha prefix!
        mockClient.Verify(c => c.PublishAsync(
            It.Is<MqttApplicationMessage>(m => m.Topic.StartsWith("new_ha/select/audio_orchestrator_g1/source/config") && string.IsNullOrEmpty(m.ConvertPayloadToString())),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ProcessPendingCleanupsAsync_ShouldClearOldBridgeStatus_WhenBaseTopicChangesCaseOnly()
    {
        var (sp, db, mockClient, service, _) = CreateTestContext(baseTopic: "Audio_Orchestrator");

        mockClient.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientConnectResult());
        mockClient.Setup(c => c.SubscribeAsync(It.IsAny<MqttClientSubscribeOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientSubscribeResult(
                1,
                new List<MqttClientSubscribeResultItem>
                {
                    new(new MqttTopicFilter { Topic = "audio_orchestrator/groups/+/set/#" }, MqttClientSubscribeResultCode.GrantedQoS0),
                    new(new MqttTopicFilter { Topic = "homeassistant/status" }, MqttClientSubscribeResultCode.GrantedQoS0)
                },
                string.Empty,
                new List<MqttUserProperty>()
            ));
        mockClient.SetupGet(c => c.IsConnected).Returns(true);

        var group = new GroupEntity { Id = "g1", Name = "Wohnzimmer", CurrentStreamId = "default" };
        db.Groups.Add(group);
        await db.SaveChangesAsync();

        // 1. Initial connection with "Audio_Orchestrator"
        await service.EnsureConnectionAsync(CancellationToken.None);
        mockClient.Invocations.Clear();

        // 2. Change base topic with case-only difference: "audio_orchestrator"
        var settings = await db.Settings.FirstAsync();
        settings.MqttBaseTopic = "audio_orchestrator";
        await db.SaveChangesAsync();

        await service.ReloadConfigurationAsync(CancellationToken.None);

        // Verify the old case-variant bridge status was cleared with empty string
        mockClient.Verify(c => c.PublishAsync(
            It.Is<MqttApplicationMessage>(m => m.Topic == "Audio_Orchestrator/bridge/status" && string.IsNullOrEmpty(m.ConvertPayloadToString())),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);

        // And new active bridge status was published with "online"
        mockClient.Verify(c => c.PublishAsync(
            It.Is<MqttApplicationMessage>(m => m.Topic == "audio_orchestrator/bridge/status" && m.ConvertPayloadToString() == "online"),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ReloadConfigurationAsync_ShouldPublishOfflineOnOldBaseTopic_WhenBaseTopicChangesAndCleanupFails()
    {
        var (sp, db, mockClient, service, _) = CreateTestContext(baseTopic: "old_base");

        mockClient.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientConnectResult());
        mockClient.Setup(c => c.SubscribeAsync(It.IsAny<MqttClientSubscribeOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientSubscribeResult(
                1,
                new List<MqttClientSubscribeResultItem>
                {
                    new(new MqttTopicFilter { Topic = "old_base/groups/+/set/#" }, MqttClientSubscribeResultCode.GrantedQoS0),
                    new(new MqttTopicFilter { Topic = "new_base/groups/+/set/#" }, MqttClientSubscribeResultCode.GrantedQoS0),
                    new(new MqttTopicFilter { Topic = "homeassistant/status" }, MqttClientSubscribeResultCode.GrantedQoS0)
                },
                string.Empty,
                new List<MqttUserProperty>()
            ));
        mockClient.SetupGet(c => c.IsConnected).Returns(true);

        var group = new GroupEntity { Id = "g1", Name = "Wohnzimmer", CurrentStreamId = "default" };
        db.Groups.Add(group);
        await db.SaveChangesAsync();

        // 1. Initial connection with "old_base"
        await service.EnsureConnectionAsync(CancellationToken.None);
        mockClient.Invocations.Clear();

        // 2. Change base topic to "new_base"
        var settings = await db.Settings.FirstAsync();
        settings.MqttBaseTopic = "new_base";
        await db.SaveChangesAsync();

        // Simulate failure on unpublishing discovery config
        mockClient.Setup(c => c.PublishAsync(
                It.Is<MqttApplicationMessage>(m => m.Topic.Contains("audio_orchestrator_g1")),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Broker disconnected during unpublish"));

        await service.ReloadConfigurationAsync(CancellationToken.None);

        // Verify offline status was published to old_base/bridge/status BEFORE cleanup failed
        mockClient.Verify(c => c.PublishAsync(
            It.Is<MqttApplicationMessage>(m => m.Topic == "old_base/bridge/status" && m.ConvertPayloadToString() == "offline"),
            It.IsAny<CancellationToken>()), Times.Once);

        // And verify bridge status topic was NOT cleared with empty string because cleanup for g1 failed!
        mockClient.Verify(c => c.PublishAsync(
            It.Is<MqttApplicationMessage>(m => m.Topic == "old_base/bridge/status" && string.IsNullOrEmpty(m.ConvertPayloadToString())),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PublishDiscoveryAndStates_ShouldContinuePublishingLiveGroups_WhenDeletedGroupCleanupFails()
    {
        var (sp, db, mockClient, service, _) = CreateTestContext();

        mockClient.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientConnectResult());
        mockClient.Setup(c => c.SubscribeAsync(It.IsAny<MqttClientSubscribeOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientSubscribeResult(
                1,
                new List<MqttClientSubscribeResultItem>
                {
                    new(new MqttTopicFilter { Topic = "audio_orchestrator/groups/+/set/#" }, MqttClientSubscribeResultCode.GrantedQoS0),
                    new(new MqttTopicFilter { Topic = "homeassistant/status" }, MqttClientSubscribeResultCode.GrantedQoS0)
                },
                string.Empty,
                new List<MqttUserProperty>()
            ));
        mockClient.SetupGet(c => c.IsConnected).Returns(true);

        var group1 = new GroupEntity { Id = "g1", Name = "Wohnzimmer", CurrentStreamId = "default" };
        var group2 = new GroupEntity { Id = "g2", Name = "Küche", CurrentStreamId = "default" };
        db.Groups.AddRange(group1, group2);
        await db.SaveChangesAsync();

        // 1. Initial connection: both groups are discovered
        await service.EnsureConnectionAsync(CancellationToken.None);

        // 2. Remove group1 from DB
        db.Groups.Remove(group1);
        await db.SaveChangesAsync();

        // Simulate publish failure on unpublishing group1 (e.g. timeout on select entity)
        mockClient.Setup(c => c.PublishAsync(
                It.Is<MqttApplicationMessage>(m => m.Topic.Contains("audio_orchestrator_g1")),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Broker timeout during g1 unpublish"));

        mockClient.Invocations.Clear();

        // 3. Heartbeat / refresh: cleanup of g1 will throw, but live group g2 MUST still be published!
        await service.EnsureConnectionAsync(CancellationToken.None);

        // Verify g2 state was published successfully despite g1 cleanup failure
        mockClient.Verify(c => c.PublishAsync(
            It.Is<MqttApplicationMessage>(m => m.Topic == "audio_orchestrator/groups/g2/state" && !string.IsNullOrEmpty(m.ConvertPayloadToString())),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task HandleIncomingMessage_ShouldRejectUnknownSource_AndPreserveCurrentPlayback()
    {
        var (sp, db, mockClient, service, _) = CreateTestContext();

        mockClient.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientConnectResult());
        mockClient.Setup(c => c.SubscribeAsync(It.IsAny<MqttClientSubscribeOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientSubscribeResult(
                1,
                new List<MqttClientSubscribeResultItem>
                {
                    new(new MqttTopicFilter { Topic = "audio_orchestrator/groups/+/set/#" }, MqttClientSubscribeResultCode.GrantedQoS0),
                    new(new MqttTopicFilter { Topic = "homeassistant/status" }, MqttClientSubscribeResultCode.GrantedQoS0)
                },
                string.Empty,
                new List<MqttUserProperty>()
            ));
        mockClient.SetupGet(c => c.IsConnected).Returns(true);

        var group = new GroupEntity { Id = "g1", Name = "Wohnzimmer", CurrentStreamId = "spotify_s1" };
        var radio = new RadioStreamEntity { Id = "r1", Name = "Antenne Bayern" };
        db.Groups.Add(group);
        db.RadioStreams.Add(radio);
        await db.SaveChangesAsync();

        await service.EnsureConnectionAsync(CancellationToken.None);

        // 1. Send invalid / unknown source label
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic("audio_orchestrator/groups/g1/set/source")
            .WithPayload("Radio: Old Station Name")
            .Build();

        await service.HandleIncomingMessageAsync(new MqttApplicationMessageReceivedEventArgs("c1", msg, new MqttPublishPacket(), null));

        // Group stream MUST remain spotify_s1 and NOT switch to default / silence
        var updatedGroup = await db.Groups.FindAsync("g1");
        Assert.Equal("spotify_s1", updatedGroup!.CurrentStreamId);

        // 2. Sending supported off input "Aus (Stille)" switches to default
        var offMsg = new MqttApplicationMessageBuilder()
            .WithTopic("audio_orchestrator/groups/g1/set/source")
            .WithPayload("Aus (Stille)")
            .Build();

        await service.HandleIncomingMessageAsync(new MqttApplicationMessageReceivedEventArgs("c1", offMsg, new MqttPublishPacket(), null));

        db.ChangeTracker.Clear();
        updatedGroup = await db.Groups.FindAsync("g1");
        Assert.Equal("default", updatedGroup!.CurrentStreamId);
    }

    [Fact]
    public async Task EnsureConnectionAsync_ShouldNotDeadlock_WhenSubscriptionFailsAndCallbackIsBlocked()
    {
        var (sp, db, mockClient, service, _) = CreateTestContext();

        mockClient.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientConnectResult());

        // When subscribing, fire a retained homeassistant/status => online message which blocks on _publishLock held by EnsureConnectionAsync
        mockClient.Setup(c => c.SubscribeAsync(It.IsAny<MqttClientSubscribeOptions>(), It.IsAny<CancellationToken>()))
            .Callback<MqttClientSubscribeOptions, CancellationToken>((_, _) =>
            {
                var msg = new MqttApplicationMessageBuilder()
                    .WithTopic("homeassistant/status")
                    .WithPayload("online")
                    .Build();
                _ = service.HandleIncomingMessageAsync(new MqttApplicationMessageReceivedEventArgs("c1", msg, new MqttPublishPacket(), null));
            })
            .ReturnsAsync(new MqttClientSubscribeResult(
                1,
                new List<MqttClientSubscribeResultItem>
                {
                    new(new MqttTopicFilter { Topic = "audio_orchestrator/groups/+/set/#" }, MqttClientSubscribeResultCode.UnspecifiedError),
                    new(new MqttTopicFilter { Topic = "homeassistant/status" }, MqttClientSubscribeResultCode.GrantedQoS0)
                },
                string.Empty,
                new List<MqttUserProperty>()
            ));
        mockClient.SetupGet(c => c.IsConnected).Returns(true);

        // Disconnect should cancel the session and complete within timeout without deadlocking
        var ensureTask = service.EnsureConnectionAsync(CancellationToken.None);
        var completedTask = await Task.WhenAny(ensureTask, Task.Delay(5000));

        Assert.Same(ensureTask, completedTask);
        await ensureTask;
    }

    [Fact]
    public async Task HandleIncomingMessage_ShouldProcessBroadcastPlayCommand()
    {
        var (sp, db, mockClient, service, _) = CreateTestContext();
        var mockBroadcast = Mock.Get(sp.GetRequiredService<IBroadcastService>());

        mockClient.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientConnectResult());
        mockClient.Setup(c => c.SubscribeAsync(It.IsAny<MqttClientSubscribeOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientSubscribeResult(
                1,
                new List<MqttClientSubscribeResultItem>
                {
                    new(new MqttTopicFilter { Topic = "audio_orchestrator/groups/+/set/#" }, MqttClientSubscribeResultCode.GrantedQoS0),
                    new(new MqttTopicFilter { Topic = "audio_orchestrator/broadcast/+" }, MqttClientSubscribeResultCode.GrantedQoS0),
                    new(new MqttTopicFilter { Topic = "homeassistant/status" }, MqttClientSubscribeResultCode.GrantedQoS0)
                },
                string.Empty,
                new List<MqttUserProperty>()
            ));
        mockClient.SetupGet(c => c.IsConnected).Returns(true);

        await service.EnsureConnectionAsync(CancellationToken.None);

        BroadcastPlayRequest? capturedRequest = null;
        mockBroadcast.Setup(b => b.EnqueuePlayUrlAsync(It.IsAny<BroadcastPlayRequest>(), It.IsAny<CancellationToken>()))
            .Callback<BroadcastPlayRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new BroadcastJobDto(
                Id: "job-1",
                CreatedAt: DateTime.UtcNow,
                StartedAt: null,
                CompletedAt: null,
                AudioUrl: "url",
                SourceType: BroadcastSourceType.Url,
                TargetGroupIds: null,
                TargetRoomIds: null,
                Volume: null,
                FadeDurationMs: null,
                PausePlayback: false,
                Status: BroadcastJobStatus.Queued,
                DurationMs: 0,
                ErrorMessage: null
            ));

        // 1. Send plain URL payload
        var plainMsg = new MqttApplicationMessageBuilder()
            .WithTopic("audio_orchestrator/broadcast/play")
            .WithPayload("http://example.com/chime.mp3")
            .Build();

        await service.HandleIncomingMessageAsync(new MqttApplicationMessageReceivedEventArgs("c1", plainMsg, new MqttPublishPacket(), null));

        Assert.NotNull(capturedRequest);
        Assert.Equal("http://example.com/chime.mp3", capturedRequest!.AudioUrl);

        // 2. Send JSON payload
        var jsonMsg = new MqttApplicationMessageBuilder()
            .WithTopic("audio_orchestrator/broadcast/play")
            .WithPayload("{\"audioUrl\":\"http://example.com/alarm.wav\",\"volume\":90,\"fadeDurationMs\":500,\"pausePlayback\":true}")
            .Build();

        await service.HandleIncomingMessageAsync(new MqttApplicationMessageReceivedEventArgs("c1", jsonMsg, new MqttPublishPacket(), null));

        Assert.Equal("http://example.com/alarm.wav", capturedRequest.AudioUrl);
        Assert.Equal(90, capturedRequest.Volume);
        Assert.Equal(500, capturedRequest.FadeDurationMs);
        Assert.True(capturedRequest.PausePlayback);
    }

    [Fact]
    public async Task HandleIncomingMessage_ShouldProcessBroadcastStopCommand()
    {
        var (sp, db, mockClient, service, _) = CreateTestContext();
        var mockBroadcast = Mock.Get(sp.GetRequiredService<IBroadcastService>());

        mockClient.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientConnectResult());
        mockClient.Setup(c => c.SubscribeAsync(It.IsAny<MqttClientSubscribeOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MqttClientSubscribeResult(
                1,
                new List<MqttClientSubscribeResultItem>
                {
                    new(new MqttTopicFilter { Topic = "audio_orchestrator/groups/+/set/#" }, MqttClientSubscribeResultCode.GrantedQoS0),
                    new(new MqttTopicFilter { Topic = "audio_orchestrator/broadcast/+" }, MqttClientSubscribeResultCode.GrantedQoS0),
                    new(new MqttTopicFilter { Topic = "homeassistant/status" }, MqttClientSubscribeResultCode.GrantedQoS0)
                },
                string.Empty,
                new List<MqttUserProperty>()
            ));
        mockClient.SetupGet(c => c.IsConnected).Returns(true);

        await service.EnsureConnectionAsync(CancellationToken.None);

        var stopped = false;
        mockBroadcast.Setup(b => b.StopAsync(It.IsAny<CancellationToken>()))
            .Callback(() => stopped = true)
            .Returns(Task.CompletedTask);

        var stopMsg = new MqttApplicationMessageBuilder()
            .WithTopic("audio_orchestrator/broadcast/stop")
            .WithPayload("")
            .Build();

        await service.HandleIncomingMessageAsync(new MqttApplicationMessageReceivedEventArgs("c1", stopMsg, new MqttPublishPacket(), null));

        Assert.True(stopped);
    }
}
