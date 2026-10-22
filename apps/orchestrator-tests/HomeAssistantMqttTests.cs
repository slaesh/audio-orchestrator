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
}
