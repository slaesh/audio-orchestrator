using System.Text.Json.Nodes;
using AudioOrchestrator.Controllers;
using AudioOrchestrator.Contracts;
using AudioOrchestrator.Data;
using AudioOrchestrator.Data.Entities;
using AudioOrchestrator.Hubs;
using AudioOrchestrator.Services;
using AudioOrchestrator.Services.Generated;using Microsoft.AspNetCore.Http;
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
        services.AddScoped(_ => new OrchestratorDbContext(_dbOptions));
        services.AddSingleton(mockSnapcast.Object);
        services.AddSingleton(mockFifo.Object);
        services.AddSingleton(mockLibrespot.Object);
        services.AddSingleton(mockRunner.Object);
        services.AddSingleton(mockHub.Object);
        services.AddSingleton(mockMqtt.Object);
        BroadcastService? service = null;
        services.AddSingleton<IBroadcastService>(_ => service!);
        services.AddScoped(p => new VolumeOrchestratorService(
            p.GetRequiredService<OrchestratorDbContext>(),
            mockSnapcast.Object,
            mockLibrespot.Object,
            mockHub.Object,
            p,
            Mock.Of<ILogger<VolumeOrchestratorService>>()));
        services.AddScoped(p => new FullStateBuilder(p.GetRequiredService<OrchestratorDbContext>(), mockSnapcast.Object));
        var sp = services.BuildServiceProvider();

        service = new BroadcastService(
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

    private static JsonNode CreateSnapcastStatusWithClients(params (string SnapGroupId, string StreamId, (string ClientId, int Volume, bool Muted)[] Clients)[] groups)
    {
        var snapGroups = new JsonArray();
        foreach (var (sgId, streamId, clientsList) in groups)
        {
            var clients = new JsonArray();
            foreach (var (cid, vol, muted) in clientsList)
            {
                clients.Add(new JsonObject
                {
                    ["id"] = cid,
                    ["config"] = new JsonObject
                    {
                        ["volume"] = new JsonObject
                        {
                            ["percent"] = vol,
                            ["muted"] = muted
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
        mockLibrespot.Setup(l => l.GetStatusAsync("source1"))
            .ReturnsAsync(new NodeStatusResponse { Is_playing = true });

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
    public async Task PlayUrlAsync_ShouldNotPauseOrResumeSpotify_WhenAlreadyPaused()
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
        mockLibrespot.Setup(l => l.GetStatusAsync("source1"))
            .ReturnsAsync(new NodeStatusResponse { Is_playing = false });

        var request = new BroadcastPlayRequest(
            AudioUrl: "http://example.com/doorbell.mp3",
            GroupIds: new List<string> { "g1" },
            PausePlayback: true
        );

        var result = await service.PlayUrlAsync(request, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(commands);
    }

    [Fact]
    public async Task PlayUrlAsync_ShouldFailAndRestore_WhenSwitchingToAnnouncementStreamFails()
    {
        var (sp, db, mockSnapcast, mockLibrespot, mockRunner, service) = CreateTestContext();

        db.Groups.Add(new GroupEntity { Id = "g1", Name = "Wohnzimmer", SnapcastGroupId = "sg1", CurrentStreamId = "radio_1", MasterVolume = 40 });
        await db.SaveChangesAsync();

        mockSnapcast.Setup(s => s.GetStatusAsync())
            .ReturnsAsync(CreateSnapcastStatus(("sg1", "radio_1", new[] { "c1" })));

        var switched = new List<string>();
        mockSnapcast.Setup(s => s.SetGroupStreamAsync("sg1", "announcement"))
            .ThrowsAsync(new InvalidOperationException("snapcast offline"));
        mockSnapcast.Setup(s => s.SetGroupStreamAsync("sg1", "radio_1"))
            .Callback<string, string>((_, stream) => switched.Add(stream))
            .Returns(Task.CompletedTask);

        var result = await service.PlayUrlAsync(new BroadcastPlayRequest(
            AudioUrl: "http://example.com/chime.mp3",
            GroupIds: new List<string> { "g1" },
            Volume: 90), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("snapcast offline", result.Message);
        Assert.Contains("radio_1", switched);

        db.ChangeTracker.Clear();
        var job = await db.BroadcastJobs.FindAsync(result.JobId);
        Assert.Equal(BroadcastJobStatus.Failed, job!.Status);
        Assert.Equal(40, (await db.Groups.FindAsync("g1"))!.MasterVolume);
    }

    [Fact]
    public async Task PlayUrlAsync_ShouldTargetLiveClientWithoutRoomRow()
    {
        var (sp, db, mockSnapcast, mockLibrespot, mockRunner, service) = CreateTestContext();

        db.Groups.Add(new GroupEntity { Id = "g1", Name = "Wohnzimmer", SnapcastGroupId = "sg1", CurrentStreamId = "radio_1", MasterVolume = 40 });
        await db.SaveChangesAsync();

        mockSnapcast.Setup(s => s.GetStatusAsync())
            .ReturnsAsync(CreateSnapcastStatus(("sg1", "radio_1", new[] { "aa:bb:cc", "dd:ee:ff" })));

        var muted = new List<string>();
        mockSnapcast.Setup(s => s.SetClientVolumeAsync(It.IsAny<string>(), It.IsAny<int>(), true))
            .Callback<string, int, bool>((id, _, _) => muted.Add(id))
            .Returns(Task.CompletedTask);

        var result = await service.PlayUrlAsync(new BroadcastPlayRequest(
            AudioUrl: "http://example.com/chime.mp3",
            RoomIds: new List<string> { "aa:bb:cc" }), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(new[] { "dd:ee:ff" }, muted.Distinct());
    }

    [Fact]
    public async Task PlayUrlAsync_ShouldTargetUnassignedRoomInNativeSnapcastGroup()
    {
        var (sp, db, mockSnapcast, mockLibrespot, mockRunner, service) = CreateTestContext();

        mockSnapcast.Setup(s => s.GetStatusAsync())
            .ReturnsAsync(CreateSnapcastStatus(("sg9", "default", new[] { "c9" })));

        var switched = new List<(string Group, string Stream)>();
        mockSnapcast.Setup(s => s.SetGroupStreamAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((g, st) => switched.Add((g, st)))
            .Returns(Task.CompletedTask);

        var result = await service.PlayUrlAsync(new BroadcastPlayRequest(
            AudioUrl: "http://example.com/chime.mp3",
            RoomIds: new List<string> { "c9" }), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(("sg9", "announcement"), switched.First());
        Assert.Equal(("sg9", "default"), switched.Last());
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

    [Fact]
    public async Task CancelJobAsync_ShouldCompleteWaitersImmediately_WhenJobIsQueued()
    {
        var (sp, db, mockSnapcast, _, mockRunner, service) = CreateTestContext();

        db.Groups.Add(new GroupEntity { Id = "g1", Name = "Wohnzimmer", SnapcastGroupId = "sg1", CurrentStreamId = "radio_1", MasterVolume = 50 });
        await db.SaveChangesAsync();

        mockSnapcast.Setup(s => s.GetStatusAsync())
            .ReturnsAsync(CreateSnapcastStatus(("sg1", "radio_1", new[] { "c1" })));

        var firstJobStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unblockFirstJob = new TaskCompletionSource<ProcessRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        int callCount = 0;
        mockRunner.Setup(r => r.StreamToFifoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream?>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, Stream?, CancellationToken>(async (_, _, _, ct) =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                {
                    firstJobStarted.TrySetResult();
                    using var reg = ct.Register(() => unblockFirstJob.TrySetCanceled(ct));
                    return await unblockFirstJob.Task;
                }
                return new ProcessRunResult(0, string.Empty);
            });

        // Job 1 occupies the worker
        var job1 = await service.EnqueuePlayUrlAsync(new BroadcastPlayRequest(AudioUrl: "http://example.com/1.wav", GroupIds: new List<string> { "g1" }));
        await firstJobStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Job 2 is queued behind Job 1 (Interrupt = false to avoid cancelling active Job 1)
        var job2 = await service.EnqueuePlayUrlAsync(new BroadcastPlayRequest(
            AudioUrl: "http://example.com/2.wav",
            GroupIds: new List<string> { "g1" },
            Interrupt: false
        ));
        var waiterTask = service.WaitForJobAsync(job2.Id);

        // Cancel Job 2 while Job 1 is still blocking the worker
        var cancelled = await service.CancelJobAsync(job2.Id);
        Assert.True(cancelled);

        // Waiter for Job 2 must complete immediately without waiting for Job 1 to finish
        var waiterResult = await waiterTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(waiterResult.Success);

        // Let Job 1 finish
        unblockFirstJob.TrySetResult(new ProcessRunResult(0, string.Empty));
        await service.WaitForJobAsync(job1.Id);
    }

    [Fact]
    public async Task PlayUrlAsync_WithPausePlaybackAndFade_ShouldFadeDownBeforePause_AndResumeBeforeFadeUp()
    {
        var (sp, db, mockSnapcast, mockLibrespot, _, service) = CreateTestContext();

        db.Groups.Add(new GroupEntity { Id = "g1", Name = "Wohnzimmer", SnapcastGroupId = "sg1", CurrentStreamId = "spotify_1", MasterVolume = 60 });
        await db.SaveChangesAsync();

        mockSnapcast.Setup(s => s.GetStatusAsync())
            .ReturnsAsync(CreateSnapcastStatusWithClients(("sg1", "spotify_1", new[] { ("c1", 36, false) })));

        mockLibrespot.Setup(l => l.GetStatusAsync("1"))
            .ReturnsAsync(new NodeStatusResponse { Is_playing = true });

        var events = new List<string>();
        mockSnapcast.Setup(s => s.SetClientVolumeAsync("c1", It.IsAny<int>(), It.IsAny<bool>()))
            .Callback<string, int, bool>((_, vol, _) => events.Add($"vol:{vol}"))
            .Returns(Task.CompletedTask);
        mockLibrespot.Setup(l => l.SendCommandAsync("1", It.IsAny<string>()))
            .Callback<string, string>((_, cmd) => events.Add($"spotify:{cmd}"))
            .Returns(Task.CompletedTask);
        mockSnapcast.Setup(s => s.SetGroupStreamAsync("sg1", It.IsAny<string>()))
            .Callback<string, string>((_, stream) => events.Add($"stream:{stream}"))
            .Returns(Task.CompletedTask);

        var req = new BroadcastPlayRequest(
            AudioUrl: "http://example.com/test.mp3",
            GroupIds: new List<string> { "g1" },
            Volume: 90,
            FadeDurationMs: 200,
            PausePlayback: true
        );

        var result = await service.PlayUrlAsync(req);
        Assert.True(result.Success);

        var pauseIdx = events.IndexOf("spotify:pause");
        var firstZeroVolIdx = events.IndexOf("vol:0");
        var switchBackIdx = events.IndexOf("stream:spotify_1");
        var resumeIdx = events.IndexOf("spotify:resume");
        var lastVolIdx = events.LastIndexOf("vol:36"); // 60% quadratic = 36

        Assert.True(firstZeroVolIdx >= 0 && firstZeroVolIdx < pauseIdx, $"Expected fade to 0 before spotify:pause, got: {string.Join(", ", events)}");
        Assert.True(switchBackIdx < resumeIdx, $"Expected stream switch back before spotify:resume, got: {string.Join(", ", events)}");
        Assert.True(resumeIdx < lastVolIdx, $"Expected spotify:resume before fade-up finishes, got: {string.Join(", ", events)}");
    }

    [Fact]
    public async Task StartAsync_ShouldNotCancelJobEnqueuedBeforeStartAsync()
    {
        var (sp, db, mockSnapcast, _, _, service) = CreateTestContext();

        db.Groups.Add(new GroupEntity { Id = "g1", Name = "Wohnzimmer", SnapcastGroupId = "sg1", CurrentStreamId = "radio_1", MasterVolume = 50 });
        await db.SaveChangesAsync();

        mockSnapcast.Setup(s => s.GetStatusAsync())
            .ReturnsAsync(CreateSnapcastStatus(("sg1", "radio_1", new[] { "c1" })));

        // Enqueue before StartAsync is called (e.g. early MQTT command during startup)
        var job = await service.EnqueuePlayUrlAsync(new BroadcastPlayRequest(AudioUrl: "http://example.com/early.wav", GroupIds: new List<string> { "g1" }));
        await service.StartAsync(CancellationToken.None);

        var result = await service.WaitForJobAsync(job.Id);
        Assert.True(result.Success);

        var finalJob = await service.GetJobAsync(job.Id);
        Assert.NotNull(finalJob);
        Assert.Equal(BroadcastJobStatus.Completed, finalJob!.Status);
    }

    [Fact]
    public async Task PlayUrlAsync_ShouldFailAndReportError_WhenStreamRestorationFails()
    {
        var (sp, db, mockSnapcast, _, _, service) = CreateTestContext();

        db.Groups.Add(new GroupEntity { Id = "g1", Name = "Wohnzimmer", SnapcastGroupId = "sg1", CurrentStreamId = "radio_1", MasterVolume = 50 });
        await db.SaveChangesAsync();

        mockSnapcast.Setup(s => s.GetStatusAsync())
            .ReturnsAsync(CreateSnapcastStatus(("sg1", "radio_1", new[] { "c1" })));

        // Restoration switch back to radio_1 fails
        mockSnapcast.Setup(s => s.SetGroupStreamAsync("sg1", "radio_1"))
            .ThrowsAsync(new InvalidOperationException("Snapcast socket closed during restoration"));

        var result = await service.PlayUrlAsync(new BroadcastPlayRequest(
            AudioUrl: "http://example.com/sound.wav",
            GroupIds: new List<string> { "g1" }
        ));

        Assert.False(result.Success);
        Assert.Contains("Wiederherstellung", result.Message);
        Assert.Contains("Snapcast socket closed", result.Message);

        var job = await service.GetJobAsync(result.JobId!);
        Assert.NotNull(job);
        Assert.Equal(BroadcastJobStatus.Failed, job!.Status);
        Assert.Contains("Snapcast socket closed", job.ErrorMessage);
    }

    [Fact]
    public async Task PlayUrlAsync_ShouldUnmuteTargetedMutedRoom_AndRestoreMutedStateAfterward()
    {
        var (sp, db, mockSnapcast, _, _, service) = CreateTestContext();

        db.Groups.Add(new GroupEntity { Id = "g1", Name = "Wohnzimmer", SnapcastGroupId = "sg1", CurrentStreamId = "radio_1", MasterVolume = 50, IsMuted = false });
        await db.SaveChangesAsync();

        // c1 is initially muted in Snapcast
        mockSnapcast.Setup(s => s.GetStatusAsync())
            .ReturnsAsync(CreateSnapcastStatusWithClients(("sg1", "radio_1", new[] { ("c1", 70, true) })));

        var clientMuteEvents = new List<bool>();
        mockSnapcast.Setup(s => s.SetClientVolumeAsync("c1", It.IsAny<int>(), It.IsAny<bool>()))
            .Callback<string, int, bool>((_, _, muted) => clientMuteEvents.Add(muted))
            .Returns(Task.CompletedTask);

        var result = await service.PlayUrlAsync(new BroadcastPlayRequest(
            AudioUrl: "http://example.com/sound.wav",
            GroupIds: new List<string> { "g1" }
        ));

        Assert.True(result.Success);
        // c1 was temporarily unmuted (false) during broadcast, and restored to muted (true) at the end
        Assert.Contains(false, clientMuteEvents);
        Assert.True(clientMuteEvents.Last());
    }

    [Fact]
    public async Task Reconciliation_ShouldPreserveBroadcastOwnedTargetsAndNativeGroups()
    {
        var (sp, db, mockSnapcast, _, _, _) = CreateTestContext();

        var mockBroadcast = new Mock<IBroadcastService>();
        mockBroadcast.Setup(b => b.IsGroupActiveInBroadcast("g1", "sg1")).Returns(true);
        mockBroadcast.Setup(b => b.IsGroupActiveInBroadcast(null, "sg_native")).Returns(true);

        var mockSp = new Mock<IServiceProvider>();
        mockSp.Setup(s => s.GetService(typeof(IBroadcastService))).Returns(mockBroadcast.Object);

        var recon = new ReconciliationService(
            Mock.Of<Docker.DotNet.IDockerClient>(),
            Mock.Of<ILogger<ReconciliationService>>(),
            mockSp.Object,
            Mock.Of<IContainerLifecycleService>(),
            Mock.Of<IFifoService>(),
            mockSnapcast.Object
        );

        var groups = new List<GroupEntity>
        {
            new() { Id = "g1", Name = "Wohnzimmer", SnapcastGroupId = "sg1", CurrentStreamId = "spotify_1" }
        };

        var snapcastStatus = new JsonObject
        {
            ["server"] = new JsonObject
            {
                ["streams"] = new JsonArray(),
                ["groups"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "sg1",
                        ["name"] = "Wohnzimmer",
                        ["stream_id"] = "announcement",
                        ["clients"] = new JsonArray()
                    },
                    new JsonObject
                    {
                        ["id"] = "sg_native",
                        ["name"] = "",
                        ["stream_id"] = "announcement",
                        ["clients"] = new JsonArray()
                    }
                }
            }
        };

        await recon.ReconcileSnapcastAsync(groups, new List<RadioStreamEntity>(), new List<SpotifySourceEntity>(), snapcastStatus);

        // Neither sg1 nor sg_native should have been switched back to their persisted stream or default
        mockSnapcast.Verify(s => s.SetGroupStreamAsync("sg1", It.IsAny<string>()), Times.Never);
        mockSnapcast.Verify(s => s.SetGroupStreamAsync("sg_native", "default"), Times.Never);
    }

    [Fact]
    public async Task SiblingVolumePropagation_ShouldNotAffectBroadcastOwnedGroups()
    {
        var (sp, db, mockSnapcast, mockLibrespot, _, _) = CreateTestContext();

        var g1 = new GroupEntity { Id = "g1", Name = "Group 1", SnapcastGroupId = "sg1", CurrentStreamId = "spotify_s1", MasterVolume = 40 };
        var g2 = new GroupEntity { Id = "g2", Name = "Group 2", SnapcastGroupId = "sg2", CurrentStreamId = "spotify_s1", MasterVolume = 40 };
        db.Groups.AddRange(g1, g2);
        await db.SaveChangesAsync();

        var mockBroadcast = new Mock<IBroadcastService>();
        mockBroadcast.Setup(b => b.IsGroupActiveInBroadcast("g1", "sg1")).Returns(true);
        mockBroadcast.Setup(b => b.IsGroupActiveInBroadcast("g2", "sg2")).Returns(false);

        var mockSp = new Mock<IServiceProvider>();
        mockSp.Setup(s => s.GetService(typeof(IBroadcastService))).Returns(mockBroadcast.Object);

        var mockClients = new Mock<IHubClients<IAudioClient>>();
        mockClients.Setup(c => c.All).Returns(Mock.Of<IAudioClient>());
        var mockHub = new Mock<IHubContext<AudioHub, IAudioClient>>();
        mockHub.Setup(h => h.Clients).Returns(mockClients.Object);

        var volOrch = new VolumeOrchestratorService(
            db,
            mockSnapcast.Object,
            mockLibrespot.Object,
            mockHub.Object,
            mockSp.Object,
            Mock.Of<ILogger<VolumeOrchestratorService>>()
        );

        // Changing volume on sibling group g2
        await volOrch.SetGroupVolumeAsync("g2", 80, isMuted: false, syncStreamSource: true);

        // Group 1 volume in DB should remain unchanged at 40 because it is in an active broadcast
        var reloadedG1 = await db.Groups.FindAsync("g1");
        Assert.Equal(40, reloadedG1!.MasterVolume);
    }

    [Fact]
    public async Task RecalculateAndPushGroupAsync_ShouldDefer_WhenGroupIsActiveInBroadcastAndNotForced()
    {
        var (sp, db, mockSnapcast, mockLibrespot, _, _) = CreateTestContext();

        var g1 = new GroupEntity { Id = "g1", Name = "Group 1", SnapcastGroupId = "sg1", MasterVolume = 50 };
        db.Groups.Add(g1);
        await db.SaveChangesAsync();

        var mockBroadcast = new Mock<IBroadcastService>();
        mockBroadcast.Setup(b => b.IsGroupActiveInBroadcast("g1", "sg1")).Returns(true);

        var mockSp = new Mock<IServiceProvider>();
        mockSp.Setup(s => s.GetService(typeof(IBroadcastService))).Returns(mockBroadcast.Object);

        var mockClients = new Mock<IHubClients<IAudioClient>>();
        mockClients.Setup(c => c.All).Returns(Mock.Of<IAudioClient>());
        var mockHub = new Mock<IHubContext<AudioHub, IAudioClient>>();
        mockHub.Setup(h => h.Clients).Returns(mockClients.Object);

        var volOrch = new VolumeOrchestratorService(
            db,
            mockSnapcast.Object,
            mockLibrespot.Object,
            mockHub.Object,
            mockSp.Object,
            Mock.Of<ILogger<VolumeOrchestratorService>>()
        );

        await volOrch.RecalculateAndPushGroupAsync("g1", force: false);

        mockSnapcast.Verify(s => s.GetStatusAsync(), Times.Never);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("/tmp/malicious.mp3")]
    [InlineData("ftp://example.com/audio.mp3")]
    [InlineData("relative/path/sound.wav")]
    public async Task EnqueuePlayUrlAsync_ShouldThrow_WhenUrlIsNotAbsoluteHttpOrHttps(string invalidUrl)
    {
        var (_, _, _, _, _, service) = CreateTestContext();
        var req = new BroadcastPlayRequest(AudioUrl: invalidUrl);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.EnqueuePlayUrlAsync(req));
        Assert.Contains("absolute HTTP- oder HTTPS-URL", ex.Message);
    }

    [Theory]
    [InlineData("file:///tmp/chime.wav")]
    [InlineData("/var/audio.mp3")]
    [InlineData("ftp://example.com/sound.wav")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BroadcastController_Play_ShouldReturnBadRequest_WhenUrlIsInvalid(string invalidUrl)
    {
        var mockService = new Mock<IBroadcastService>();
        var controller = new BroadcastController(mockService.Object, Mock.Of<ILogger<BroadcastController>>());
        var req = new BroadcastPlayRequest(AudioUrl: invalidUrl);

        var result = await controller.Play(req);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task BroadcastController_Play_ShouldReturnBadRequest_WhenServiceThrowsArgumentException()
    {
        var mockService = new Mock<IBroadcastService>();
        mockService.Setup(s => s.EnqueuePlayUrlAsync(It.IsAny<BroadcastPlayRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Ungültige URL"));
        var controller = new BroadcastController(mockService.Object, Mock.Of<ILogger<BroadcastController>>());
        var req = new BroadcastPlayRequest(AudioUrl: "https://example.com/chime.mp3");

        var result = await controller.Play(req);

        var badReq = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Ungültige URL", badReq.Value);
    }

    [Fact]
    public void GetGroupGate_ShouldReturnSameSemaphore_ForSameGroupId()
    {
        var (_, _, _, _, _, service) = CreateTestContext();
        var gate1 = service.GetGroupGate("group-1");
        var gate2 = service.GetGroupGate("group-1");

        Assert.Same(gate1, gate2);
    }

    [Fact]
    public async Task PlayUrlAsync_ShouldReplayVolumeAndMuteEdits_MadeDuringRestorationFadeUp()
    {
        var (sp, db, mockSnapcast, mockLibrespot, mockRunner, service) = CreateTestContext();

        var group = new GroupEntity { Id = "g1", Name = "Wohnzimmer", SnapcastGroupId = "sg1", CurrentStreamId = "radio_1", MasterVolume = 50, IsMuted = false };
        var room = new RoomEntity { MacAddress = "c1", FriendlyName = "Speaker 1", MixVolume = 100, IsMuted = false };
        db.Groups.Add(group);
        db.Rooms.Add(room);
        db.Settings.Add(new OrchestratorSettingsEntity { VolumeProfile = VolumeProfile.Linear });
        await db.SaveChangesAsync();

        mockSnapcast.Setup(s => s.GetStatusAsync())
            .ReturnsAsync(CreateSnapcastStatus(("sg1", "radio_1", new[] { "c1" })));

        var setVolumes = new List<(string ClientId, int Volume, bool Muted)>();
        mockSnapcast.Setup(s => s.SetClientVolumeAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Callback<string, int, bool>((cid, vol, muted) => setVolumes.Add((cid, vol, muted)))
            .Returns(Task.CompletedTask);

        // When runner streams audio, simulate user editing group volume to 85% and mute=true via VolumeOrchestratorService
        mockRunner.Setup(r => r.StreamToFifoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream?>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, Stream?, CancellationToken>(async (_, _, _, ct) =>
            {
                await Task.Delay(50, ct);
                using var scope = sp.CreateScope();
                var volumeService = scope.ServiceProvider.GetRequiredService<VolumeOrchestratorService>();
                await volumeService.SetGroupVolumeAsync("g1", 85, isMuted: true);
                return new ProcessRunResult(0, "");
            });

        var request = new BroadcastPlayRequest(
            AudioUrl: "http://example.com/chime.mp3",
            GroupIds: new List<string> { "g1" },
            FadeDurationMs: 100
        );

        var result = await service.PlayUrlAsync(request, CancellationToken.None);
        Assert.True(result.Success);

        // The final volume push for c1 must reflect the edited 85% volume and muted = true
        var lastCall = setVolumes.LastOrDefault(c => c.ClientId == "c1");
        Assert.Equal(85, lastCall.Volume);
        Assert.True(lastCall.Muted);
    }

    [Fact]
    public async Task PlayUrlAsync_ShouldReplayNativeRoomVolumeAndMuteEdits_MadeDuringRestoration()
    {
        var (sp, db, mockSnapcast, mockLibrespot, mockRunner, service) = CreateTestContext();

        var room = new RoomEntity { MacAddress = "c_unassigned", FriendlyName = "Kitchen Standalone", MixVolume = 40, IsMuted = false };
        db.Rooms.Add(room);
        db.Settings.Add(new OrchestratorSettingsEntity { VolumeProfile = VolumeProfile.Linear });
        await db.SaveChangesAsync();

        mockSnapcast.Setup(s => s.GetStatusAsync())
            .ReturnsAsync(CreateSnapcastStatus(("native_group_1", "default", new[] { "c_unassigned" })));

        var setVolumes = new List<(string ClientId, int Volume, bool Muted)>();
        mockSnapcast.Setup(s => s.SetClientVolumeAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Callback<string, int, bool>((cid, vol, muted) => setVolumes.Add((cid, vol, muted)))
            .Returns(Task.CompletedTask);

        mockRunner.Setup(r => r.StreamToFifoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream?>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, Stream?, CancellationToken>(async (_, _, _, ct) =>
            {
                await Task.Delay(50, ct);
                using var scope = sp.CreateScope();
                var volumeService = scope.ServiceProvider.GetRequiredService<VolumeOrchestratorService>();
                await volumeService.SetRoomMixVolumeAsync("c_unassigned", 75);
                var testDb = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                var r = await testDb.Rooms.FindAsync("c_unassigned");
                if (r != null) { r.IsMuted = true; await testDb.SaveChangesAsync(); }
                return new ProcessRunResult(0, "");
            });

        var request = new BroadcastPlayRequest(
            AudioUrl: "http://example.com/doorbell.wav",
            RoomIds: new List<string> { "c_unassigned" },
            FadeDurationMs: 50
        );

        var result = await service.PlayUrlAsync(request, CancellationToken.None);
        Assert.True(result.Success);

        var lastCall = setVolumes.LastOrDefault(c => c.ClientId == "c_unassigned");
        Assert.Equal(75, lastCall.Volume);
        Assert.True(lastCall.Muted);
    }
}
