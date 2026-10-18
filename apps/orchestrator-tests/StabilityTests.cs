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

