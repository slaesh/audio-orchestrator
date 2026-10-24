using Xunit;
using Moq;
using AudioOrchestrator.Controllers;
using AudioOrchestrator.Services;
using AudioOrchestrator.Data;
using AudioOrchestrator.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using AudioOrchestrator.Hubs;
using AudioOrchestrator.Contracts;
using Microsoft.Extensions.Configuration;

namespace AudioOrchestrator.OrchestratorTests;

public class StabilityTests
{
    private readonly DbContextOptions<OrchestratorDbContext> _dbOptions;
    private readonly Mock<ISnapcastService> _snapMock;
    private readonly Mock<IReconciliationService> _reconciliationMock;
    private readonly Mock<ILogger<GroupsController>> _loggerMock;
    private readonly Mock<IHubContext<AudioHub, IAudioClient>> _hubMock;
    private readonly Mock<FullStateBuilder> _builderMock;
    private readonly Mock<VolumeOrchestratorService> _volumeOrchestratorMock;

    public StabilityTests()
    {
        _dbOptions = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _snapMock = new Mock<ISnapcastService>();
        
        _reconciliationMock = new Mock<IReconciliationService>();
        _loggerMock = new Mock<ILogger<GroupsController>>();
        _hubMock = new Mock<IHubContext<AudioHub, IAudioClient>>();
        
        // Mocking FullStateBuilder
        _builderMock = new Mock<FullStateBuilder>(null!, null!);
        _builderMock.Setup(b => b.BuildAsync()).ReturnsAsync(new FullStateDto(new(), new(), new(), new(), new()));
        
        // Mocking VolumeOrchestratorService
        _volumeOrchestratorMock = new Mock<VolumeOrchestratorService>(null!, null!, null!, null!, null!);

        // Setup Hub Context mock
        var clientsMock = new Mock<IHubClients<IAudioClient>>();
        var allClientsMock = new Mock<IAudioClient>();
        clientsMock.Setup(c => c.All).Returns(allClientsMock.Object);
        _hubMock.Setup(h => h.Clients).Returns(clientsMock.Object);
    }

    private OrchestratorDbContext GetDbContext() => new OrchestratorDbContext(_dbOptions);

    [Fact]
    public async Task GetGroups_ShouldSilenceOrphans()
    {
        // Arrange
        using var db = GetDbContext();
        // No groups in DB
        
        var snapStatus = JsonNode.Parse(@"
        {
            ""server"": {
                ""groups"": [
                    {
                        ""id"": ""orphan-id"",
                        ""name"": ""Old Zombi Group"",
                        ""stream_id"": ""Spotify"",
                        ""clients"": []
                    }
                ]
            }
        }");
        _snapMock.Setup(s => s.GetStatusAsync()).ReturnsAsync(snapStatus);

        var serviceProviderMock = new Mock<IServiceProvider>();
        var scopeMock = new Mock<Microsoft.Extensions.DependencyInjection.IServiceScope>();
        var scopeFactoryMock = new Mock<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>();
        serviceProviderMock.Setup(s => s.GetService(typeof(Microsoft.Extensions.DependencyInjection.IServiceScopeFactory))).Returns(scopeFactoryMock.Object);
        scopeFactoryMock.Setup(s => s.CreateScope()).Returns(scopeMock.Object);
        scopeMock.Setup(s => s.ServiceProvider).Returns(serviceProviderMock.Object);
        serviceProviderMock.Setup(s => s.GetService(typeof(OrchestratorDbContext))).Returns(db);
        serviceProviderMock.Setup(s => s.GetService(typeof(Microsoft.AspNetCore.SignalR.IHubContext<AudioHub, IAudioClient>))).Returns(_hubMock.Object);
        serviceProviderMock.Setup(s => s.GetService(typeof(FullStateBuilder))).Returns(_builderMock.Object);

        var service = new ReconciliationService(
            new Mock<Docker.DotNet.IDockerClient>().Object,
            new Mock<ILogger<ReconciliationService>>().Object,
            serviceProviderMock.Object,
            new Mock<IContainerLifecycleService>().Object,
            new Mock<IFifoService>().Object,
            _snapMock.Object);

        // Act
        await service.ReconcileSnapcastAsync(new List<GroupEntity>(), new List<RadioStreamEntity>(), new List<SpotifySourceEntity>(), snapStatus);

        // Assert
        // Pass 3 (Orphan Silence) should have triggered for 'orphan-id'
        _snapMock.Verify(s => s.SetGroupStreamAsync("orphan-id", "default"), Times.Once);
        _snapMock.Verify(s => s.SetGroupNameAsync("orphan-id", ""), Times.Once);
    }

    [Fact]
    public async Task GetGroups_ShouldHealByName()
    {
        // Arrange
        using var db = GetDbContext();
        var dbGroup = new GroupEntity { Name = "LivingRoom", SnapcastGroupId = null };
        db.Groups.Add(dbGroup);
        await db.SaveChangesAsync();

        var snapStatus = JsonNode.Parse(@"
        {
            ""server"": {
                ""groups"": [
                    {
                        ""id"": ""physical-lr-id"",
                        ""name"": ""LivingRoom"",
                        ""stream_id"": ""default"",
                        ""clients"": []
                    }
                ]
            }
        }");
        _snapMock.Setup(s => s.GetStatusAsync()).ReturnsAsync(snapStatus);

        var serviceProviderMock = new Mock<IServiceProvider>();
        var scopeMock = new Mock<Microsoft.Extensions.DependencyInjection.IServiceScope>();
        var scopeFactoryMock = new Mock<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>();
        serviceProviderMock.Setup(s => s.GetService(typeof(Microsoft.Extensions.DependencyInjection.IServiceScopeFactory))).Returns(scopeFactoryMock.Object);
        scopeFactoryMock.Setup(s => s.CreateScope()).Returns(scopeMock.Object);
        scopeMock.Setup(s => s.ServiceProvider).Returns(serviceProviderMock.Object);
        serviceProviderMock.Setup(s => s.GetService(typeof(OrchestratorDbContext))).Returns(db);
        serviceProviderMock.Setup(s => s.GetService(typeof(Microsoft.AspNetCore.SignalR.IHubContext<AudioHub, IAudioClient>))).Returns(_hubMock.Object);
        serviceProviderMock.Setup(s => s.GetService(typeof(FullStateBuilder))).Returns(_builderMock.Object);

        var service = new ReconciliationService(
            new Mock<Docker.DotNet.IDockerClient>().Object,
            new Mock<ILogger<ReconciliationService>>().Object,
            serviceProviderMock.Object,
            new Mock<IContainerLifecycleService>().Object,
            new Mock<IFifoService>().Object,
            _snapMock.Object);

        // Act
        await service.ReconcileSnapcastAsync(new List<GroupEntity> { dbGroup }, new List<RadioStreamEntity>(), new List<SpotifySourceEntity>(), snapStatus);

        // Assert
        // Pass 2 (Self-Healing) should have linked the group
        var updatedGroup = await db.Groups.FirstAsync();
        Assert.Equal("physical-lr-id", updatedGroup.SnapcastGroupId);
    }

    [Fact]
    public async Task AssignClients_ShouldPruneSiblings_WhenMovingOneOfMany()
    {
        // Arrange
        using var db = GetDbContext();
        var targetGroup = new GroupEntity { Name = "NewGroup", SnapcastGroupId = "target-id" };
        db.Groups.Add(targetGroup);
        await db.SaveChangesAsync();

        var snapStatus = JsonNode.Parse(@"
        {
            ""server"": {
                ""groups"": [
                    {
                        ""id"": ""source-group-id"",
                        ""clients"": [
                            { ""id"": ""client-to-move"" },
                            { ""id"": ""client-to-stay"" }
                        ]
                    },
                    {
                        ""id"": ""target-id"",
                        ""clients"": []
                    }
                ]
            }
        }");
        _snapMock.Setup(s => s.GetStatusAsync()).ReturnsAsync(snapStatus);

        var controller = new GroupsController(_reconciliationMock.Object, _snapMock.Object, db, _loggerMock.Object, _hubMock.Object, _builderMock.Object, _volumeOrchestratorMock.Object);

        // Act
        await controller.AssignClients(targetGroup.Id.ToString(), new[] { "client-to-move" });

        // Assert
        // Pruning pass should have called SetClients on 'source-group-id' with ONLY 'client-to-stay'
        _snapMock.Verify(s => s.SetGroupClientsAsync("source-group-id", It.Is<string[]>(c => c.Length == 1 && c[0] == "client-to-stay")), Times.Once);
    }
    [Fact]
    public async Task AssignClients_ShouldHijackHardware_WhenMovingLastRoomOfGroup()
    {
        // Arrange
        using var db = GetDbContext();
        
        // Group A owns a hardware ID but is about to lose its last room
        var sourceGroup = new GroupEntity { Name = "GroupA", SnapcastGroupId = "shared-hardware-id" };
        var targetGroup = new GroupEntity { Name = "GroupB", SnapcastGroupId = null };
        db.Groups.AddRange(sourceGroup, targetGroup);
        await db.SaveChangesAsync();

        var snapStatus = JsonNode.Parse(@"
        {
            ""server"": {
                ""groups"": [
                    {
                        ""id"": ""shared-hardware-id"",
                        ""clients"": [ { ""id"": ""the-room"" } ]
                    }
                ]
            }
        }");
        _snapMock.Setup(s => s.GetStatusAsync()).ReturnsAsync(snapStatus);

        var controller = new GroupsController(_reconciliationMock.Object, _snapMock.Object, db, _loggerMock.Object, _hubMock.Object, _builderMock.Object, _volumeOrchestratorMock.Object);

        // Act
        // Moving 'the-room' into GroupB.
        // GroupB should be allowed to hijack 'shared-hardware-id' from GroupA.
        await controller.AssignClients(targetGroup.Id.ToString(), new[] { "the-room" });

        // Assert
        // 1. Resolver should have picked the ID even though it was 'Occupied'
        var updatedTarget = await db.Groups.FindAsync(targetGroup.Id);
        Assert.Equal("shared-hardware-id", updatedTarget?.SnapcastGroupId);

        // 2. Exclusivity Enforcement should have cleaned up GroupA
        var updatedSource = await db.Groups.FindAsync(sourceGroup.Id);
        Assert.Null(updatedSource?.SnapcastGroupId);
    }

    [Fact]
    public async Task AssignClients_ShouldReturnConflict_WhenDestinationGroupIsBroadcasting()
    {
        // Arrange
        using var db = GetDbContext();
        var targetGroup = new GroupEntity { Name = "GroupB", SnapcastGroupId = "target-snap-id" };
        db.Groups.Add(targetGroup);
        await db.SaveChangesAsync();

        var snapStatus = JsonNode.Parse(@"
        {
            ""server"": {
                ""groups"": [
                    {
                        ""id"": ""source-snap-id"",
                        ""clients"": [ { ""id"": ""client1"" } ]
                    },
                    {
                        ""id"": ""target-snap-id"",
                        ""clients"": []
                    }
                ]
            }
        }");
        _snapMock.Setup(s => s.GetStatusAsync()).ReturnsAsync(snapStatus);

        var broadcastMock = new Mock<IBroadcastService>();
        broadcastMock.Setup(b => b.GetGroupGate(It.IsAny<string>())).Returns<string>(_ => new SemaphoreSlim(1, 1));
        broadcastMock.Setup(b => b.IsGroupActiveInBroadcast(targetGroup.Id, "target-snap-id")).Returns(true);

        var serviceProviderMock = new Mock<IServiceProvider>();
        serviceProviderMock.Setup(sp => sp.GetService(typeof(IBroadcastService))).Returns(broadcastMock.Object);

        var controller = new GroupsController(_reconciliationMock.Object, _snapMock.Object, db, _loggerMock.Object, _hubMock.Object, _builderMock.Object, _volumeOrchestratorMock.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                {
                    RequestServices = serviceProviderMock.Object
                }
            }
        };

        // Act
        var result = await controller.AssignClients(targetGroup.Id, new[] { "client1" });

        // Assert
        var conflictResult = Assert.IsType<ConflictObjectResult>(result);
        Assert.Contains("actively playing a broadcast", conflictResult.Value?.ToString());
    }

    [Fact]
    public async Task Unassign_ShouldReturnConflict_WhenGroupIsBroadcasting()
    {
        // Arrange
        using var db = GetDbContext();
        var group = new GroupEntity { Name = "GroupA", SnapcastGroupId = "snap-group-1" };
        db.Groups.Add(group);
        await db.SaveChangesAsync();

        var snapStatus = JsonNode.Parse(@"
        {
            ""server"": {
                ""groups"": [
                    {
                        ""id"": ""snap-group-1"",
                        ""clients"": [ { ""id"": ""room1"" }, { ""id"": ""room2"" } ]
                    }
                ]
            }
        }");
        _snapMock.Setup(s => s.GetStatusAsync()).ReturnsAsync(snapStatus);

        var broadcastMock = new Mock<IBroadcastService>();
        broadcastMock.Setup(b => b.GetGroupGate(It.IsAny<string>())).Returns<string>(_ => new SemaphoreSlim(1, 1));
        broadcastMock.Setup(b => b.IsGroupActiveInBroadcast(group.Id, "snap-group-1")).Returns(true);

        var serviceProviderMock = new Mock<IServiceProvider>();
        serviceProviderMock.Setup(sp => sp.GetService(typeof(IBroadcastService))).Returns(broadcastMock.Object);

        var controller = new RoomsController(_snapMock.Object, db, _hubMock.Object, _builderMock.Object, _volumeOrchestratorMock.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                {
                    RequestServices = serviceProviderMock.Object
                }
            }
        };

        // Act
        var result = await controller.Unassign("room1");

        // Assert
        var conflictResult = Assert.IsType<ConflictObjectResult>(result);
        Assert.Contains("actively playing a broadcast", conflictResult.Value?.ToString());
    }

    [Fact]
    public async Task LibrespotApiClient_ShouldUseVersionedV1Routes()
    {
        var requestedUrls = new List<(HttpMethod Method, string Uri)>();
        var handler = new FakeHttpMessageHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            requestedUrls.Add((req.Method, uri));
            if (req.Method == HttpMethod.Get && uri.EndsWith("/api/v1/health"))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        @"{""status"":""ok"",""version"":""0.3.0"",""node_id"":""test-src"",""device_name"":""Test Room"",""is_connected"":true}",
                        System.Text.Encoding.UTF8,
                        "application/json")
                };
            }
            if (req.Method == HttpMethod.Get && uri.EndsWith("/api/v1/status"))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        @"{""node_id"":""test-src"",""device_name"":""Test Room"",""is_connected"":true,""is_playing"":true,""is_active_device"":true,""volume"":75,""position_ms"":12000,""shuffle"":false,""repeat_context"":true,""repeat_track"":false,""autoplay"":true}",
                        System.Text.Encoding.UTF8,
                        "application/json")
                };
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("OK")
            };
        });

        var httpClient = new HttpClient(handler);
        var client = new LibrespotApiClient(httpClient, new Mock<ILogger<LibrespotApiClient>>().Object);
        var expectedPort = LibrespotApiClient.GetApiPortForSource("test-src");

        await client.SetVolumeAsync("test-src", 75);
        await client.SendCommandAsync("test-src", "pause");
        await client.SendCommandAsync("test-src", "resume");
        await client.SendCommandAsync("test-src", "next");
        await client.PlayContextAsync("test-src", "spotify:playlist:123");
        var status = await client.GetStatusAsync("test-src");
        var health = await client.GetHealthAsync("test-src");

        Assert.NotNull(status);
        Assert.Equal("test-src", status!.Node_id);
        Assert.Equal(75, status.Volume);
        Assert.True(status.Is_playing);

        Assert.NotNull(health);
        Assert.Equal("ok", health!.Status);
        Assert.Equal("0.3.0", health.Version);

        Assert.Contains((HttpMethod.Post, $"http://localhost:{expectedPort}/api/v1/volume"), requestedUrls);
        Assert.Contains((HttpMethod.Post, $"http://localhost:{expectedPort}/api/v1/pause"), requestedUrls);
        Assert.Contains((HttpMethod.Post, $"http://localhost:{expectedPort}/api/v1/resume"), requestedUrls);
        Assert.Contains((HttpMethod.Post, $"http://localhost:{expectedPort}/api/v1/next"), requestedUrls);
        Assert.Contains((HttpMethod.Post, $"http://localhost:{expectedPort}/api/v1/play-context"), requestedUrls);
        Assert.Contains((HttpMethod.Get, $"http://localhost:{expectedPort}/api/v1/status"), requestedUrls);
        Assert.Contains((HttpMethod.Get, $"http://localhost:{expectedPort}/api/v1/health"), requestedUrls);
    }

    [Fact]
    public async Task ReconciliationService_UntaggedImage_SpawnsWithNullVersion()
    {
        using var db = GetDbContext();
        var source = new SpotifySourceEntity
        {
            Id = "src-untagged",
            Name = "Untagged Spotify",
            CurrentVersion = "",
            MaxVolume = 50
        };
        db.SpotifySources.Add(source);
        await db.SaveChangesAsync();

        var dockerClientMock = new Mock<Docker.DotNet.IDockerClient>();
        dockerClientMock.Setup(d => d.Containers.ListContainersAsync(It.IsAny<Docker.DotNet.Models.ContainersListParameters>(), default))
            .ReturnsAsync(new List<Docker.DotNet.Models.ContainerListResponse>());

        var containerLifecycleMock = new Mock<IContainerLifecycleService>();
        var fifoServiceMock = new Mock<IFifoService>();
        var loggerMock = new Mock<ILogger<ReconciliationService>>();

        var inMemorySettings = new Dictionary<string, string?>
        {
            { "SPOTIFY_NODE_IMAGE", "ghcr.io/slaesh/librespot-server" }
        };
        Microsoft.Extensions.Configuration.IConfiguration config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var serviceProviderMock = new Mock<IServiceProvider>();
        var scopeMock = new Mock<Microsoft.Extensions.DependencyInjection.IServiceScope>();
        var scopeFactoryMock = new Mock<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>();
        serviceProviderMock.Setup(s => s.GetService(typeof(Microsoft.Extensions.DependencyInjection.IServiceScopeFactory))).Returns(scopeFactoryMock.Object);
        scopeFactoryMock.Setup(s => s.CreateScope()).Returns(scopeMock.Object);
        scopeMock.Setup(s => s.ServiceProvider).Returns(serviceProviderMock.Object);
        serviceProviderMock.Setup(s => s.GetService(typeof(OrchestratorDbContext))).Returns(db);
        serviceProviderMock.Setup(s => s.GetService(typeof(Microsoft.AspNetCore.SignalR.IHubContext<AudioHub, IAudioClient>))).Returns(_hubMock.Object);
        serviceProviderMock.Setup(s => s.GetService(typeof(FullStateBuilder))).Returns(_builderMock.Object);

        var reconciliation = new ReconciliationService(
            dockerClientMock.Object,
            loggerMock.Object,
            serviceProviderMock.Object,
            containerLifecycleMock.Object,
            fifoServiceMock.Object,
            _snapMock.Object,
            config
        );

        await reconciliation.ReconcileDockerOnStartupAsync();

        // Must spawn with version: null so DockerLifecycleService preserves the untagged image reference verbatim
        containerLifecycleMock.Verify(c => c.SpawnLibreSpotAsync(
            "src-untagged",
            "Untagged Spotify",
            It.IsAny<string>(),
            50,
            It.IsAny<IList<Docker.DotNet.Models.ContainerListResponse>>(),
            null
        ), Times.Once);
    }

    [Fact]
    public async Task SetGroupVolumeAsync_FromDocker_IgnoredWhenBroadcasting()
    {
        using var db = GetDbContext();
        var grp = new GroupEntity
        {
            Id = "grp-broadcast-active",
            Name = "Active Broadcast Group",
            SnapcastGroupId = "snap-active",
            MasterVolume = 50,
            IsMuted = false
        };
        db.Groups.Add(grp);
        await db.SaveChangesAsync();

        var broadcastMock = new Mock<IBroadcastService>();
        broadcastMock.Setup(b => b.GetGroupGate("grp-broadcast-active")).Returns(new SemaphoreSlim(1, 1));
        broadcastMock.Setup(b => b.IsGroupActiveInBroadcast("grp-broadcast-active", "snap-active")).Returns(true);

        var serviceProviderMock = new Mock<IServiceProvider>();
        serviceProviderMock.Setup(s => s.GetService(typeof(IBroadcastService))).Returns(broadcastMock.Object);

        var librespotMock = new Mock<ILibrespotApiClient>();
        var volumeOrchestrator = new VolumeOrchestratorService(
            db,
            _snapMock.Object,
            librespotMock.Object,
            _hubMock.Object,
            serviceProviderMock.Object,
            new Mock<ILogger<VolumeOrchestratorService>>().Object
        );

        // Webhook arrives from Docker while broadcasting
        await volumeOrchestrator.SetGroupVolumeAsync("grp-broadcast-active", 85, isMuted: true, syncStreamSource: true, fromDocker: true);

        // Group volume in DB must not be overwritten by Docker webhook during broadcast
        var freshGroup = await db.Groups.FindAsync("grp-broadcast-active");
        Assert.NotNull(freshGroup);
        Assert.Equal(50, freshGroup!.MasterVolume);
        Assert.False(freshGroup.IsMuted);
    }

    [Fact]
    public async Task VolumeOrchestrator_SyncSpotifySourceAndSiblingsAsync_UpdatesSourceAndSiblings()
    {
        using var db = GetDbContext();
        var source = new SpotifySourceEntity
        {
            Id = "spotify-1",
            Name = "Spotify Test",
            MasterVolume = 30
        };
        var grp1 = new GroupEntity
        {
            Id = "grp-1",
            Name = "Group 1",
            SnapcastGroupId = "snap-1",
            CurrentStreamId = $"{OrchestratorConstants.SpotifyPrefix}spotify-1",
            MasterVolume = 70
        };
        var grp2 = new GroupEntity
        {
            Id = "grp-2",
            Name = "Group 2",
            SnapcastGroupId = "snap-2",
            CurrentStreamId = $"{OrchestratorConstants.SpotifyPrefix}spotify-1",
            MasterVolume = 30
        };
        db.SpotifySources.Add(source);
        db.Groups.AddRange(grp1, grp2);
        await db.SaveChangesAsync();

        var librespotMock = new Mock<ILibrespotApiClient>();
        var serviceProviderMock = new Mock<IServiceProvider>();
        var broadcastMock = new Mock<IBroadcastService>();
        broadcastMock.Setup(b => b.GetGroupGate(It.IsAny<string>())).Returns<string>(_ => new SemaphoreSlim(1, 1));
        broadcastMock.Setup(b => b.IsGroupActiveInBroadcast(It.IsAny<string>(), It.IsAny<string>())).Returns(false);
        serviceProviderMock.Setup(s => s.GetService(typeof(IBroadcastService))).Returns(broadcastMock.Object);

        _snapMock.Setup(s => s.GetStatusAsync()).ReturnsAsync(System.Text.Json.Nodes.JsonNode.Parse(
            """{"server":{"groups":[{"id":"snap-1","clients":[]},{"id":"snap-2","clients":[]}]}}"""));

        var volumeOrchestrator = new VolumeOrchestratorService(
            db,
            _snapMock.Object,
            librespotMock.Object,
            _hubMock.Object,
            serviceProviderMock.Object,
            new Mock<ILogger<VolumeOrchestratorService>>().Object
        );

        await volumeOrchestrator.SyncSpotifySourceAndSiblingsAsync("grp-1", $"{OrchestratorConstants.SpotifyPrefix}spotify-1", 70);

        var freshSource = await db.SpotifySources.FindAsync("spotify-1");
        var freshGrp1 = await db.Groups.FindAsync("grp-1");
        var freshGrp2 = await db.Groups.FindAsync("grp-2");

        Assert.Equal(70, freshSource!.MasterVolume);
        Assert.Equal(70, freshGrp1!.MasterVolume);
        Assert.Equal(70, freshGrp2!.MasterVolume);
        librespotMock.Verify(l => l.SetVolumeAsync("spotify-1", 70), Times.Once);
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request));
        }
    }
}

