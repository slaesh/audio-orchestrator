using System.Text.Json.Nodes;
using AudioOrchestrator.Controllers;
using AudioOrchestrator.Contracts;
using AudioOrchestrator.Data;
using AudioOrchestrator.Data.Entities;
using AudioOrchestrator.Hubs;
using AudioOrchestrator.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace AudioOrchestrator.Tests;

public class BroadcastServiceTests
{
    private readonly DbContextOptions<OrchestratorDbContext> _dbOptions;

    public BroadcastServiceTests()
    {
        _dbOptions = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(databaseName: $"Broadcast_Test_{Guid.NewGuid()}")
            .Options;
    }

    private (IServiceProvider Sp, OrchestratorDbContext Db, Mock<ISnapcastService> MockSnapcast, Mock<ILibrespotApiClient> MockLibrespot, Mock<IAudioPlayerProcessRunner> MockRunner, BroadcastService Service) CreateTestContext()
    {
        var db = new OrchestratorDbContext(_dbOptions);
        var mockSnapcast = new Mock<ISnapcastService>();
        var mockFifo = new Mock<IFifoService>();
        var mockLibrespot = new Mock<ILibrespotApiClient>();
        var mockRunner = new Mock<IAudioPlayerProcessRunner>();
        mockRunner.Setup(r => r.StreamToFifoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessRunResult(0, ""));

        var mockLogger = new Mock<ILogger<BroadcastService>>();
        var mockHub = new Mock<IHubContext<AudioHub, IAudioClient>>();
        var mockClients = new Mock<IHubClients<IAudioClient>>();
        var mockClientProxy = new Mock<IAudioClient>();
        mockClients.Setup(c => c.All).Returns(mockClientProxy.Object);
        mockHub.Setup(h => h.Clients).Returns(mockClients.Object);

        var mockMqtt = new Mock<IHomeAssistantMqttService>();

        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(mockSnapcast.Object);
        services.AddSingleton(mockFifo.Object);
        services.AddSingleton(mockLibrespot.Object);
        services.AddSingleton(mockRunner.Object);
        services.AddSingleton(mockHub.Object);
        services.AddSingleton(mockMqtt.Object);
        services.AddSingleton(new VolumeOrchestratorService(db, mockSnapcast.Object, mockLibrespot.Object, mockHub.Object, Mock.Of<ILogger<VolumeOrchestratorService>>()));
        services.AddSingleton(new FullStateBuilder(db, mockSnapcast.Object));
        var sp = services.BuildServiceProvider();

        var service = new BroadcastService(
            mockLogger.Object,
            sp,
            mockSnapcast.Object,
            mockFifo.Object,
            mockLibrespot.Object,
            mockRunner.Object
        );

        return (sp, db, mockSnapcast, mockLibrespot, mockRunner, service);
    }

    private static JsonNode CreateSnapcastStatus(params (string SnapGroupId, string StreamId, string[] ClientIds)[] groups)
    {
        var snapGroups = new JsonArray();
        foreach (var (sgId, streamId, clientIds) in groups)
        {
            var clients = new JsonArray();
            foreach (var cid in clientIds)
            {
                clients.Add(new JsonObject
                {
                    ["id"] = cid,
                    ["config"] = new JsonObject
                    {
                        ["volume"] = new JsonObject
                        {
                            ["percent"] = 100,
                            ["muted"] = false
                        }
                    }
                });
            }

            snapGroups.Add(new JsonObject
            {
                ["id"] = sgId,
                ["name"] = sgId,
                ["stream_id"] = streamId,
                ["clients"] = clients
            });
        }

        return new JsonObject
        {
            ["server"] = new JsonObject
            {
                ["groups"] = snapGroups
            }
        };
    }

    [Fact]
    public async Task PlayUrlAsync_ShouldSwitchGroupStreamToAnnouncement_AndRestoreOriginalStreamAndVolume()
    {
        var (sp, db, mockSnapcast, mockLibrespot, mockRunner, service) = CreateTestContext();

        var group = new GroupEntity
        {
            Id = "g1",
            Name = "Wohnzimmer",
            SnapcastGroupId = "sg1",
            CurrentStreamId = "radio_1",
            MasterVolume = 60,
            IsMuted = false
        };
        db.Groups.Add(group);
        await db.SaveChangesAsync();

        mockSnapcast.Setup(s => s.GetStatusAsync())
            .ReturnsAsync(CreateSnapcastStatus(("sg1", "radio_1", new[] { "client_1" })));

        var switchedStreams = new List<string>();
        mockSnapcast.Setup(s => s.SetGroupStreamAsync("sg1", It.IsAny<string>()))
            .Callback<string, string>((_, stream) => switchedStreams.Add(stream))
            .Returns(Task.CompletedTask);

        var request = new BroadcastPlayRequest(
            AudioUrl: "http://example.com/chime.mp3",
            GroupIds: new List<string> { "g1" },
            Volume: 85,
            FadeDurationMs: 0
        );

        var result = await service.PlayUrlAsync(request, CancellationToken.None);

        Assert.True(result.Success);
        // Verify switched to announcement, then back to radio_1
        Assert.Contains("announcement", switchedStreams);
        Assert.Equal("radio_1", switchedStreams.Last());

        // Group volume must be restored to original 60
        db.ChangeTracker.Clear();
        var reloadedGroup = await db.Groups.FindAsync("g1");
        Assert.Equal(60, reloadedGroup!.MasterVolume);
    }

    [Fact]
    public async Task PlayUrlAsync_ShouldDefaultToAllActiveGroups_WhenNoTargetSpecified()
    {
        var (sp, db, mockSnapcast, mockLibrespot, mockRunner, service) = CreateTestContext();

        var g1 = new GroupEntity { Id = "g1", Name = "Wohnzimmer", SnapcastGroupId = "sg1", CurrentStreamId = "radio_1", MasterVolume = 50 };
        var g2 = new GroupEntity { Id = "g2", Name = "Küche", SnapcastGroupId = "sg2", CurrentStreamId = "default", MasterVolume = 70 };
        db.Groups.AddRange(g1, g2);
        await db.SaveChangesAsync();

        mockSnapcast.Setup(s => s.GetStatusAsync())
            .ReturnsAsync(CreateSnapcastStatus(
                ("sg1", "radio_1", new[] { "c1" }),
                ("sg2", "default", new[] { "c2" })
            ));

        var switchedGroups = new HashSet<string>();
        mockSnapcast.Setup(s => s.SetGroupStreamAsync(It.IsAny<string>(), "announcement"))
            .Callback<string, string>((sg, _) => switchedGroups.Add(sg))
            .Returns(Task.CompletedTask);

        var request = new BroadcastPlayRequest(
            AudioUrl: "http://example.com/alarm.mp3",
            GroupIds: null,
            RoomIds: null
        );

        var result = await service.PlayUrlAsync(request, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("sg1", switchedGroups);
        Assert.Contains("sg2", switchedGroups);
    }

    [Fact]
    public async Task PlayUrlAsync_ShouldPauseAndResumeSpotify_WhenPausePlaybackIsTrue()
    {
        var (sp, db, mockSnapcast, mockLibrespot, mockRunner, service) = CreateTestContext();

        var group = new GroupEntity
        {
            Id = "g1",
            Name = "Wohnzimmer",
            SnapcastGroupId = "sg1",
            CurrentStreamId = $"{OrchestratorConstants.SpotifyPrefix}source1",
            MasterVolume = 50
        };
        db.Groups.Add(group);
        await db.SaveChangesAsync();

        mockSnapcast.Setup(s => s.GetStatusAsync())
            .ReturnsAsync(CreateSnapcastStatus(("sg1", group.CurrentStreamId, new[] { "c1" })));

        var commands = new List<string>();
        mockLibrespot.Setup(l => l.SendCommandAsync("source1", It.IsAny<string>()))
            .Callback<string, string>((_, cmd) => commands.Add(cmd))
            .Returns(Task.CompletedTask);

        var request = new BroadcastPlayRequest(
            AudioUrl: "http://example.com/doorbell.mp3",
            GroupIds: new List<string> { "g1" },
            PausePlayback: true
        );

        var result = await service.PlayUrlAsync(request, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("pause", commands);
        Assert.Contains("resume", commands);
        Assert.Equal("resume", commands.Last());
    }

    [Fact]
    public async Task PlayUrlAsync_ShouldMuteUnselectedRoomsInTargetGroup_AndRestoreAfterward()
    {
        var (sp, db, mockSnapcast, mockLibrespot, mockRunner, service) = CreateTestContext();

        var group = new GroupEntity { Id = "g1", Name = "Etage", SnapcastGroupId = "sg1", CurrentStreamId = "radio_1", MasterVolume = 50 };
        var r1 = new RoomEntity { MacAddress = "mac_kitchen", FriendlyName = "Küche" };
        var r2 = new RoomEntity { MacAddress = "mac_hallway", FriendlyName = "Flur" };
        db.Groups.Add(group);
        db.Rooms.AddRange(r1, r2);
        await db.SaveChangesAsync();

        mockSnapcast.Setup(s => s.GetStatusAsync())
            .ReturnsAsync(CreateSnapcastStatus(("sg1", "radio_1", new[] { "mac_kitchen", "mac_hallway" })));

        var clientMuteStates = new List<(string ClientId, bool Muted)>();
        mockSnapcast.Setup(s => s.SetClientVolumeAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Callback<string, int, bool>((cid, vol, muted) => clientMuteStates.Add((cid, muted)))
            .Returns(Task.CompletedTask);

        // Target ONLY Kitchen (mac_kitchen), so Hallway (mac_hallway) must be muted during clip
        var request = new BroadcastPlayRequest(
            AudioUrl: "http://example.com/bell.mp3",
            RoomIds: new List<string> { "Küche" }
        );

        var result = await service.PlayUrlAsync(request, CancellationToken.None);

        Assert.True(result.Success);
        // mac_hallway was muted, then unmuted
        Assert.Contains(clientMuteStates, x => x.ClientId == "mac_hallway" && x.Muted);
        Assert.Contains(clientMuteStates, x => x.ClientId == "mac_hallway" && !x.Muted);
    }

    [Fact]
    public async Task PlayUrlAsync_ShouldRejectEmptyAudioUrl()
    {
        var (sp, db, mockSnapcast, mockLibrespot, mockRunner, service) = CreateTestContext();

        var request = new BroadcastPlayRequest(AudioUrl: "");
        var result = await service.PlayUrlAsync(request, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("leer", result.Message!);
    }

    [Fact]
    public async Task StopAsync_ShouldCancelActiveBroadcast_AndRestoreState()
    {
        var (sp, db, mockSnapcast, mockLibrespot, mockRunner, service) = CreateTestContext();

        var group = new GroupEntity { Id = "g1", Name = "Wohnzimmer", SnapcastGroupId = "sg1", CurrentStreamId = "radio_1", MasterVolume = 50 };
        db.Groups.Add(group);
        await db.SaveChangesAsync();

        mockSnapcast.Setup(s => s.GetStatusAsync())
            .ReturnsAsync(CreateSnapcastStatus(("sg1", "radio_1", new[] { "c1" })));

        var restoredStream = false;
        mockSnapcast.Setup(s => s.SetGroupStreamAsync("sg1", "radio_1"))
            .Callback(() => restoredStream = true)
            .Returns(Task.CompletedTask);

        // Process runner simulates long-running 10s audio
        mockRunner.Setup(r => r.StreamToFifoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream?>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, Stream?, CancellationToken>(async (_, _, _, ct) =>
            {
                await Task.Delay(10000, ct);
                return new ProcessRunResult(0, "");
            });

        var request = new BroadcastPlayRequest(
            AudioUrl: "http://example.com/siren.mp3",
            GroupIds: new List<string> { "g1" }
        );

        var playTask = service.PlayUrlAsync(request, CancellationToken.None);

        // Wait for broadcast to start
        await Task.Delay(100);
        Assert.True(service.GetStatus().IsPlaying);

        // Call StopAsync
        await service.StopAsync(CancellationToken.None);

        var result = await playTask;
        Assert.False(result.Success);
        Assert.False(service.GetStatus().IsPlaying);
        Assert.True(restoredStream);
    }

    [Fact]
    public async Task BroadcastController_PlayAndStop_EndpointsWork()
    {
        var (sp, db, mockSnapcast, mockLibrespot, mockRunner, service) = CreateTestContext();

        var group = new GroupEntity { Id = "g1", Name = "Wohnzimmer", SnapcastGroupId = "sg1", CurrentStreamId = "radio_1", MasterVolume = 50 };
        db.Groups.Add(group);
        await db.SaveChangesAsync();

        mockSnapcast.Setup(s => s.GetStatusAsync())
            .ReturnsAsync(CreateSnapcastStatus(("sg1", "radio_1", new[] { "c1" })));

        var controller = new BroadcastController(service, Mock.Of<ILogger<BroadcastController>>());

        // 1. Asynchronous play (wait = false)
        var req = new BroadcastPlayRequest(AudioUrl: "http://example.com/chime.wav", GroupIds: new List<string> { "g1" });
        var playResult = await controller.Play(req, wait: false);
        var acceptedResult = Assert.IsType<AcceptedAtActionResult>(playResult.Result);
        var job = Assert.IsType<BroadcastJobDto>(acceptedResult.Value);
        Assert.NotNull(job.Id);

        // 2. Query job status by ID
        var jobResult = await controller.GetJob(job.Id);
        var okJob = Assert.IsType<OkObjectResult>(jobResult.Result);
        var retrievedJob = Assert.IsType<BroadcastJobDto>(okJob.Value);
        Assert.Equal(job.Id, retrievedJob.Id);

        // 3. Query recent jobs list
        var jobsListResult = await controller.GetRecentJobs(10);
        var okJobsList = Assert.IsType<OkObjectResult>(jobsListResult.Result);
        var jobsList = Assert.IsType<List<BroadcastJobDto>>(okJobsList.Value);
        Assert.Contains(jobsList, j => j.Id == job.Id);

        // 4. Synchronous play (wait = true)
        var waitReq = new BroadcastPlayRequest(AudioUrl: "http://example.com/chime.wav", GroupIds: new List<string> { "g1" });
        var syncResult = await controller.Play(waitReq, wait: true);
        var okSync = Assert.IsType<OkObjectResult>(syncResult.Result);
        var finishedJob = Assert.IsType<BroadcastJobDto>(okSync.Value);
        Assert.Equal(BroadcastJobStatus.Completed, finishedJob.Status);

        // 5. Stop broadcast
        var stopResult = await controller.Stop();
        Assert.IsType<OkResult>(stopResult);

        // 6. Broadcast status
        var statusResult = controller.GetStatus();
        var okStatus = Assert.IsType<OkObjectResult>(statusResult.Result);
        var status = Assert.IsType<BroadcastStatusDto>(okStatus.Value);
        Assert.False(status.IsPlaying);
    }
}
