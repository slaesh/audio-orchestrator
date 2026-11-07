using Xunit;
using Moq;
using Microsoft.Extensions.Logging;
using AudioOrchestrator.Services;
using AudioOrchestrator.Controllers;
using AudioOrchestrator.Data;
using AudioOrchestrator.Data.Entities;
using AudioOrchestrator.Contracts;
using AudioOrchestrator.Hubs;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Mvc;

namespace AudioOrchestrator.OrchestratorTests;

public class SpotifyVolumeSyncTests
{
    private readonly Mock<ILibrespotApiClient> _librespotMock;
    private readonly Mock<ILogger<SpotifyVolumeSyncService>> _loggerMock;

    public SpotifyVolumeSyncTests()
    {
        _librespotMock = new Mock<ILibrespotApiClient>();
        _loggerMock = new Mock<ILogger<SpotifyVolumeSyncService>>();
    }

    [Fact]
    public async Task SchedulePushVolume_CoalescesRapidCalls_AndPushesOnlyFinalValue()
    {
        using var syncService = new SpotifyVolumeSyncService(
            null!,
            _loggerMock.Object,
            _librespotMock.Object)
        {
            DebounceDelay = TimeSpan.FromMilliseconds(200)
        };

        // Simulate rapid slider dragging: 82 -> 81 -> 79 -> 26 in immediate succession
        syncService.SchedulePushVolume("spotify-1", 82);
        syncService.SchedulePushVolume("spotify-1", 81);
        syncService.SchedulePushVolume("spotify-1", 79);
        syncService.SchedulePushVolume("spotify-1", 26);

        // Wait for debounce delay to elapse
        await Task.Delay(400);

        // Verify only the final value (26) was dispatched to librespot
        _librespotMock.Verify(l => l.SetVolumeAsync("spotify-1", 26), Times.Once);
        _librespotMock.Verify(l => l.SetVolumeAsync("spotify-1", 82), Times.Never);
        _librespotMock.Verify(l => l.SetVolumeAsync("spotify-1", 81), Times.Never);
        _librespotMock.Verify(l => l.SetVolumeAsync("spotify-1", 79), Times.Never);
    }

    [Fact]
    public async Task ShouldAcceptIncomingVolume_RejectsEchoOfPushedVolume()
    {
        using var syncService = new SpotifyVolumeSyncService(
            null!,
            _loggerMock.Object,
            _librespotMock.Object);

        await syncService.PushVolumeImmediateAsync("spotify-1", 70);

        // Spotify sends back 70 as echo
        bool accepted = syncService.ShouldAcceptIncomingVolume("spotify-1", 70, new[] { 50 });

        Assert.False(accepted);
    }

    [Fact]
    public void ShouldAcceptIncomingVolume_RejectsStaleVolume_DuringCooldownWindow()
    {
        using var syncService = new SpotifyVolumeSyncService(
            null!,
            _loggerMock.Object,
            _librespotMock.Object)
        {
            CooldownWindow = TimeSpan.FromSeconds(2.0)
        };

        // Local change just occurred (e.g. user moved slider down to 26)
        syncService.SchedulePushVolume("spotify-1", 26);

        // Stale in-flight event arrives with 82 while group is at 26
        bool accepted = syncService.ShouldAcceptIncomingVolume("spotify-1", 82, new[] { 26 });

        Assert.False(accepted);
    }

    [Fact]
    public async Task ShouldAcceptIncomingVolume_RejectsStaleVolume_MatchingRecentPushes()
    {
        using var syncService = new SpotifyVolumeSyncService(
            null!,
            _loggerMock.Object,
            _librespotMock.Object)
        {
            CooldownWindow = TimeSpan.Zero // zero cooldown to specifically test RecentPushes history
        };

        // Service pushed 82, then pushed 26
        await syncService.PushVolumeImmediateAsync("spotify-1", 82);
        await syncService.PushVolumeImmediateAsync("spotify-1", 26);

        // A delayed echo for 82 arrives from Spotify
        bool accepted = syncService.ShouldAcceptIncomingVolume("spotify-1", 82, new[] { 26 });

        Assert.False(accepted);
    }

    [Fact]
    public void ShouldAcceptIncomingVolume_AcceptsExternalVolume_WhenIdle()
    {
        using var syncService = new SpotifyVolumeSyncService(
            null!,
            _loggerMock.Object,
            _librespotMock.Object)
        {
            CooldownWindow = TimeSpan.FromMilliseconds(50)
        };

        // System has had no local changes, external Spotify app changes volume to 65
        bool accepted = syncService.ShouldAcceptIncomingVolume("spotify-1", 65, new[] { 50 });

        Assert.True(accepted);
    }

    [Fact]
    public void ShouldAcceptIncomingVolume_RejectsRedundantVolume_WhenAllGroupsMatch()
    {
        using var syncService = new SpotifyVolumeSyncService(
            null!,
            _loggerMock.Object,
            _librespotMock.Object);

        // Groups already at 50, incoming is 50
        bool accepted = syncService.ShouldAcceptIncomingVolume("spotify-1", 50, new[] { 50, 50 });

        Assert.False(accepted);
    }

    [Fact]
    public async Task EventsController_SuppressesEchoEvent_AndDoesNotUpdateGroup()
    {
        var dbOptions = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        using var db = new OrchestratorDbContext(dbOptions);
        var source = new SpotifySourceEntity
        {
            Id = "spotify-1",
            Name = "Living Room Spotify",
            IsActive = true,
            MasterVolume = 26
        };
        var group = new GroupEntity
        {
            Id = "grp-1",
            Name = "Living Room",
            CurrentStreamId = $"{OrchestratorConstants.SpotifyPrefix}spotify-1",
            MasterVolume = 26
        };
        db.SpotifySources.Add(source);
        db.Groups.Add(group);
        await db.SaveChangesAsync();

        using var syncService = new SpotifyVolumeSyncService(
            null!,
            _loggerMock.Object,
            _librespotMock.Object);

        // Simulate local push was 26
        await syncService.PushVolumeImmediateAsync("spotify-1", 26);

        var volumeOrchestratorMock = new Mock<VolumeOrchestratorService>(
            db, Mock.Of<ISnapcastService>(), _librespotMock.Object, Mock.Of<IHubContext<AudioHub, IAudioClient>>(), null!, Mock.Of<ILogger<VolumeOrchestratorService>>());

        var hubMock = new Mock<IHubContext<AudioHub, IAudioClient>>();
        var clientsMock = new Mock<IHubClients<IAudioClient>>();
        clientsMock.Setup(c => c.All).Returns(Mock.Of<IAudioClient>());
        hubMock.Setup(h => h.Clients).Returns(clientsMock.Object);

        var controller = new EventsController(
            Mock.Of<ISnapcastService>(),
            db,
            Mock.Of<ILogger<EventsController>>(),
            hubMock.Object,
            new Mock<FullStateBuilder>(null!, null!).Object,
            volumeOrchestratorMock.Object,
            _librespotMock.Object,
            Mock.Of<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            Mock.Of<IHomeAssistantMqttService>(),
            broadcastService: null,
            spotifyVolumeSync: syncService
        );

        // Echo arrives with 82 while local cooldown is active
        syncService.SchedulePushVolume("spotify-1", 26);

        var payload = new WebhookEventPayload
        {
            Event = "volume_changed",
            GroupId = "spotify-1",
            Volume = (int)Math.Round((82 / 100.0) * 65535)
        };

        var result = await controller.HandleEvent(payload);

        Assert.IsType<OkResult>(result);

        // Group master volume remains 26, NOT overwritten by 82
        var freshGroup = await db.Groups.FindAsync("grp-1");
        Assert.Equal(26, freshGroup!.MasterVolume);

        // Source master volume remains 26, but pre-emptive connection flag is persisted
        var freshSource = await db.SpotifySources.FindAsync("spotify-1");
        Assert.Equal(26, freshSource!.MasterVolume);
        Assert.True(freshSource.IsConnected);

        // Volume orchestrator was NOT called with 82
        volumeOrchestratorMock.Verify(
            v => v.SetGroupVolumeAsync("grp-1", 82, It.IsAny<bool?>(), true, false, false),
            Times.Never);
    }

    [Fact]
    public async Task ShouldAcceptIncomingVolume_AcceptsExternalVolume_AfterHistoryWindowExpires()
    {
        using var syncService = new SpotifyVolumeSyncService(
            null!,
            _loggerMock.Object,
            _librespotMock.Object)
        {
            HistoryWindow = TimeSpan.FromMilliseconds(50),
            CooldownWindow = TimeSpan.FromMilliseconds(50)
        };

        // Service pushed 70
        await syncService.PushVolumeImmediateAsync("spotify-1", 70);

        // Immediately after, 70 is rejected as echo
        Assert.False(syncService.ShouldAcceptIncomingVolume("spotify-1", 70, new[] { 50 }));

        // Wait for HistoryWindow and CooldownWindow to elapse
        await Task.Delay(100);

        // Later, external Spotify app changes volume back to 70 when groups are at 50
        bool accepted = syncService.ShouldAcceptIncomingVolume("spotify-1", 70, new[] { 50 });

        Assert.True(accepted);
    }

    [Fact]
    public async Task SchedulePushVolume_SerializesConcurrentWrites_AndPreservesLatestVolume()
    {
        var firstCallGate = new TaskCompletionSource<bool>();
        var secondCallStarted = new TaskCompletionSource<bool>();
        var calls = new List<int>();

        _librespotMock
            .Setup(l => l.SetVolumeAsync("spotify-1", It.IsAny<int>()))
            .Returns<string, int>(async (id, vol) =>
            {
                lock (calls) calls.Add(vol);
                if (vol == 30)
                {
                    // Block the first call in flight
                    await firstCallGate.Task;
                }
            });

        using var syncService = new SpotifyVolumeSyncService(
            null!,
            _loggerMock.Object,
            _librespotMock.Object)
        {
            DebounceDelay = TimeSpan.FromMilliseconds(20)
        };

        // Push 30
        syncService.SchedulePushVolume("spotify-1", 30);
        await Task.Delay(50); // let debounce fire and enter SetVolumeAsync(30)

        // While SetVolumeAsync(30) is in flight, push 80 immediately
        var pushTask = Task.Run(async () =>
        {
            secondCallStarted.SetResult(true);
            await syncService.PushVolumeImmediateAsync("spotify-1", 80);
        });

        await secondCallStarted.Task;
        // Release the first call now
        firstCallGate.SetResult(true);

        await pushTask;

        // Verify SetVolumeAsync was called with 30, then with 80 (serialized, never concurrent or reversed)
        lock (calls)
        {
            Assert.Equal(new[] { 30, 80 }, calls);
        }
    }
}
