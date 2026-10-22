using AudioOrchestrator.Contracts;
using AudioOrchestrator.Controllers;
using AudioOrchestrator.Data;
using AudioOrchestrator.Data.Entities;
using AudioOrchestrator.Hubs;
using AudioOrchestrator.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
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
}
