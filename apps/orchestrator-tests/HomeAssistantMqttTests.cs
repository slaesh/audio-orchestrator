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
}
