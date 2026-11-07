using System.Text.Json.Nodes;
using AudioOrchestrator.Contracts;
using AudioOrchestrator.Controllers;
using AudioOrchestrator.Data;
using AudioOrchestrator.Data.Entities;
using AudioOrchestrator.Hubs;
using AudioOrchestrator.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace AudioOrchestrator.OrchestratorTests;

public class FullStateBuilderTests
{
    private readonly DbContextOptions<OrchestratorDbContext> _dbOptions;

    public FullStateBuilderTests()
    {
        _dbOptions = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(databaseName: $"Orchestrator_FullState_Test_{Guid.NewGuid()}")
            .Options;
    }

    private class CustomTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private static async Task SeedGroupAsync(OrchestratorDbContext db, string snapcastGroupId = "test-group-id")
    {
        db.Groups.Add(new GroupEntity
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Test Group",
            SnapcastGroupId = snapcastGroupId,
            MasterVolume = 50,
            IsMuted = false
        });
        await db.SaveChangesAsync();
    }

    private static JsonNode CreateSnapcastStatus(JsonArray clients, string groupId = "test-group-id")
    {
        var group = new JsonObject
        {
            ["id"] = groupId,
            ["name"] = "Test Group",
            ["muted"] = false,
            ["stream_id"] = "default",
            ["clients"] = clients
        };

        var server = new JsonObject
        {
            ["groups"] = new JsonArray { group },
            ["streams"] = new JsonArray()
        };

        return new JsonObject
        {
            ["server"] = server
        };
    }

    [Fact]
    public async Task BuildAsync_RoomWithoutDbEntity_UsesHostNameAndNullFriendlyName()
    {
        await using var db = new OrchestratorDbContext(_dbOptions);
        await SeedGroupAsync(db);

        var mockSnapcast = new Mock<ISnapcastService>();

        var client = new JsonObject
        {
            ["id"] = "E8:F6:0A:E7:FB:6D",
            ["connected"] = true,
            ["host"] = new JsonObject { ["name"] = "ESP32-P4-Caster" },
            ["config"] = new JsonObject { ["volume"] = new JsonObject { ["percent"] = 50, ["muted"] = false } },
            ["lastSeen"] = new JsonObject { ["sec"] = 1700000000, ["usec"] = 0 }
        };

        mockSnapcast.Setup(s => s.GetStatusAsync())
            .ReturnsAsync(CreateSnapcastStatus(new JsonArray { client }));

        var timeProvider = new CustomTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1700000005));
        var builder = new FullStateBuilder(db, mockSnapcast.Object, null, null, null, null, timeProvider);

        var state = await builder.BuildAsync();

        var room = Assert.Single(state.Groups[0].Rooms);
        Assert.Equal("E8:F6:0A:E7:FB:6D", room.Id);
        Assert.Null(room.FriendlyName);
        Assert.Equal("ESP32-P4-Caster", room.HostName);
        Assert.Equal("ESP32-P4-Caster", room.Name);
        Assert.True(room.Connected);
    }

    [Fact]
    public async Task BuildAsync_RoomWithDefaultedFriendlyNameMatchingMac_FallsBackToHostName()
    {
        await using var db = new OrchestratorDbContext(_dbOptions);
        await SeedGroupAsync(db);

        db.Rooms.Add(new RoomEntity
        {
            MacAddress = "E8:F6:0A:E7:FB:6D",
            FriendlyName = "E8:F6:0A:E7:FB:6D",
            MixVolume = 80
        });
        await db.SaveChangesAsync();

        var mockSnapcast = new Mock<ISnapcastService>();

        var client = new JsonObject
        {
            ["id"] = "E8:F6:0A:E7:FB:6D",
            ["connected"] = true,
            ["host"] = new JsonObject { ["name"] = "ESP32-P4-Caster" },
            ["config"] = new JsonObject { ["volume"] = new JsonObject { ["percent"] = 50, ["muted"] = false } },
            ["lastSeen"] = new JsonObject { ["sec"] = 1700000000, ["usec"] = 0 }
        };

        mockSnapcast.Setup(s => s.GetStatusAsync())
            .ReturnsAsync(CreateSnapcastStatus(new JsonArray { client }));

        var timeProvider = new CustomTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1700000005));
        var builder = new FullStateBuilder(db, mockSnapcast.Object, null, null, null, null, timeProvider);

        var state = await builder.BuildAsync();

        var room = Assert.Single(state.Groups[0].Rooms);
        Assert.Null(room.FriendlyName);
        Assert.Equal("ESP32-P4-Caster", room.HostName);
        Assert.Equal("ESP32-P4-Caster", room.Name);
        Assert.Equal(80, room.MixVolume);
    }

    [Fact]
    public async Task BuildAsync_RoomWithEmptyFriendlyName_FallsBackToHostName()
    {
        await using var db = new OrchestratorDbContext(_dbOptions);
        await SeedGroupAsync(db);

        db.Rooms.Add(new RoomEntity
        {
            MacAddress = "E8:F6:0A:E7:FB:6D",
            FriendlyName = "",
            MixVolume = 65
        });
        await db.SaveChangesAsync();

        var mockSnapcast = new Mock<ISnapcastService>();

        var client = new JsonObject
        {
            ["id"] = "E8:F6:0A:E7:FB:6D",
            ["connected"] = true,
            ["host"] = new JsonObject { ["name"] = "ESP32-P4-Caster" },
            ["config"] = new JsonObject { ["volume"] = new JsonObject { ["percent"] = 50, ["muted"] = false } }
        };

        mockSnapcast.Setup(s => s.GetStatusAsync())
            .ReturnsAsync(CreateSnapcastStatus(new JsonArray { client }));

        var builder = new FullStateBuilder(db, mockSnapcast.Object);

        var state = await builder.BuildAsync();

        var room = Assert.Single(state.Groups[0].Rooms);
        Assert.Null(room.FriendlyName);
        Assert.Equal("ESP32-P4-Caster", room.HostName);
        Assert.Equal("ESP32-P4-Caster", room.Name);
    }

    [Fact]
    public async Task BuildAsync_RoomWithCustomFriendlyName_ReturnsCustomFriendlyName()
    {
        await using var db = new OrchestratorDbContext(_dbOptions);
        await SeedGroupAsync(db);

        db.Rooms.Add(new RoomEntity
        {
            MacAddress = "E8:F6:0A:E7:FB:6D",
            FriendlyName = "Wohnzimmer",
            MixVolume = 90
        });
        await db.SaveChangesAsync();

        var mockSnapcast = new Mock<ISnapcastService>();

        var client = new JsonObject
        {
            ["id"] = "E8:F6:0A:E7:FB:6D",
            ["connected"] = true,
            ["host"] = new JsonObject { ["name"] = "ESP32-P4-Caster" },
            ["config"] = new JsonObject { ["volume"] = new JsonObject { ["percent"] = 50, ["muted"] = false } }
        };

        mockSnapcast.Setup(s => s.GetStatusAsync())
            .ReturnsAsync(CreateSnapcastStatus(new JsonArray { client }));

        var builder = new FullStateBuilder(db, mockSnapcast.Object);

        var state = await builder.BuildAsync();

        var room = Assert.Single(state.Groups[0].Rooms);
        Assert.Equal("Wohnzimmer", room.FriendlyName);
        Assert.Equal("ESP32-P4-Caster", room.HostName);
        Assert.Equal("Wohnzimmer (ESP32-P4-Caster)", room.Name);
    }

    [Fact]
    public async Task BuildAsync_ClientConnectedWithFreshLastSeen_IsMarkedConnected()
    {
        await using var db = new OrchestratorDbContext(_dbOptions);
        await SeedGroupAsync(db);

        var mockSnapcast = new Mock<ISnapcastService>();

        var client = new JsonObject
        {
            ["id"] = "E8:F6:0A:E7:FB:6D",
            ["connected"] = true,
            ["host"] = new JsonObject { ["name"] = "ESP32-P4-Caster" },
            ["config"] = new JsonObject { ["volume"] = new JsonObject { ["percent"] = 50, ["muted"] = false } },
            ["lastSeen"] = new JsonObject { ["sec"] = 1700000000, ["usec"] = 0 }
        };

        mockSnapcast.Setup(s => s.GetStatusAsync())
            .ReturnsAsync(CreateSnapcastStatus(new JsonArray { client }));

        // 5 seconds later
        var timeProvider = new CustomTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1700000005));
        var builder = new FullStateBuilder(db, mockSnapcast.Object, null, null, null, null, timeProvider);

        var state = await builder.BuildAsync();

        var room = Assert.Single(state.Groups[0].Rooms);
        Assert.True(room.Connected);
    }

    [Fact]
    public async Task BuildAsync_ClientConnectedWithStaleLastSeen_IsMarkedDisconnected()
    {
        await using var db = new OrchestratorDbContext(_dbOptions);
        await SeedGroupAsync(db);

        var mockSnapcast = new Mock<ISnapcastService>();

        var client = new JsonObject
        {
            ["id"] = "68:FE:71:9C:D6:78",
            ["connected"] = true,
            ["host"] = new JsonObject { ["name"] = "GhostClient" },
            ["config"] = new JsonObject { ["volume"] = new JsonObject { ["percent"] = 50, ["muted"] = false } },
            ["lastSeen"] = new JsonObject { ["sec"] = 1700000000, ["usec"] = 0 }
        };

        mockSnapcast.Setup(s => s.GetStatusAsync())
            .ReturnsAsync(CreateSnapcastStatus(new JsonArray { client }));

        // 60 seconds later (> 30s threshold)
        var timeProvider = new CustomTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1700000060));
        var builder = new FullStateBuilder(db, mockSnapcast.Object, null, null, null, null, timeProvider);

        var state = await builder.BuildAsync();

        var room = Assert.Single(state.Groups[0].Rooms);
        Assert.False(room.Connected);
    }

    [Fact]
    public async Task BuildAsync_ClientConnectedWithoutLastSeen_MaintainsConnectedStatus()
    {
        await using var db = new OrchestratorDbContext(_dbOptions);
        await SeedGroupAsync(db);

        var mockSnapcast = new Mock<ISnapcastService>();

        var client = new JsonObject
        {
            ["id"] = "nanopi-2",
            ["connected"] = true,
            ["host"] = new JsonObject { ["name"] = "nanopi-2" },
            ["config"] = new JsonObject { ["volume"] = new JsonObject { ["percent"] = 50, ["muted"] = false } }
        };

        mockSnapcast.Setup(s => s.GetStatusAsync())
            .ReturnsAsync(CreateSnapcastStatus(new JsonArray { client }));

        var builder = new FullStateBuilder(db, mockSnapcast.Object);

        var state = await builder.BuildAsync();

        var room = Assert.Single(state.Groups[0].Rooms);
        Assert.True(room.Connected);
    }

    [Fact]
    public async Task VolumeOrchestratorService_SetRoomMixVolumeAsync_NewRoom_DoesNotSetFriendlyNameToMac()
    {
        await using var db = new OrchestratorDbContext(_dbOptions);
        var mockSnapcast = new Mock<ISnapcastService>();
        var mockSpotify = new Mock<ILibrespotApiClient>();
        var mockHub = new Mock<IHubContext<AudioHub, IAudioClient>>();
        var mockClients = new Mock<IHubClients<IAudioClient>>();
        mockClients.Setup(c => c.All).Returns(Mock.Of<IAudioClient>());
        mockHub.Setup(h => h.Clients).Returns(mockClients.Object);
        var mockLogger = new Mock<ILogger<VolumeOrchestratorService>>();

        var service = new VolumeOrchestratorService(db, mockSnapcast.Object, mockSpotify.Object, mockHub.Object, mockLogger.Object);

        await service.SetRoomMixVolumeAsync("E8:F6:0A:E7:FB:6D", 75);

        var entity = await db.Rooms.FindAsync("E8:F6:0A:E7:FB:6D");
        Assert.NotNull(entity);
        Assert.Equal(string.Empty, entity.FriendlyName);
        Assert.Equal(75, entity.MixVolume);
    }

    [Fact]
    public async Task RoomsController_SetVolume_NewRoom_DoesNotSetFriendlyNameToMac()
    {
        await using var db = new OrchestratorDbContext(_dbOptions);
        var mockSnapcast = new Mock<ISnapcastService>();
        var mockHub = new Mock<IHubContext<AudioHub, IAudioClient>>();
        var mockClients = new Mock<IHubClients<IAudioClient>>();
        mockClients.Setup(c => c.All).Returns(Mock.Of<IAudioClient>());
        mockHub.Setup(h => h.Clients).Returns(mockClients.Object);

        var mockBuilder = new Mock<FullStateBuilder>(db, mockSnapcast.Object);
        var mockVolume = new Mock<VolumeOrchestratorService>(db, mockSnapcast.Object, Mock.Of<ILibrespotApiClient>(), mockHub.Object, Mock.Of<ILogger<VolumeOrchestratorService>>());

        var controller = new RoomsController(mockSnapcast.Object, db, mockHub.Object, mockBuilder.Object, mockVolume.Object);

        await controller.SetVolume("E8:F6:0A:E7:FB:6D", new VolumeRequest { Percent = 70, Muted = false });

        var entity = await db.Rooms.FindAsync("E8:F6:0A:E7:FB:6D");
        Assert.NotNull(entity);
        Assert.Equal(string.Empty, entity.FriendlyName);
        Assert.Equal(70, entity.MixVolume);
    }

    [Fact]
    public async Task RoomsController_RenameRoom_EmptyOrMacName_ResetsFriendlyName()
    {
        await using var db = new OrchestratorDbContext(_dbOptions);
        db.Rooms.Add(new RoomEntity
        {
            MacAddress = "E8:F6:0A:E7:FB:6D",
            FriendlyName = "OldName",
            MixVolume = 50
        });
        await db.SaveChangesAsync();

        var mockSnapcast = new Mock<ISnapcastService>();
        var mockHub = new Mock<IHubContext<AudioHub, IAudioClient>>();
        var mockClients = new Mock<IHubClients<IAudioClient>>();
        mockClients.Setup(c => c.All).Returns(Mock.Of<IAudioClient>());
        mockHub.Setup(h => h.Clients).Returns(mockClients.Object);

        var mockBuilder = new Mock<FullStateBuilder>(db, mockSnapcast.Object);
        mockBuilder.Setup(b => b.BuildAsync()).ReturnsAsync(new FullStateDto([], [], [], [], [], [], VolumeProfile.Cubic));
        var mockVolume = new Mock<VolumeOrchestratorService>(db, mockSnapcast.Object, Mock.Of<ILibrespotApiClient>(), mockHub.Object, Mock.Of<ILogger<VolumeOrchestratorService>>());

        var controller = new RoomsController(mockSnapcast.Object, db, mockHub.Object, mockBuilder.Object, mockVolume.Object);

        // Renaming to MAC address should clear it to empty string
        await controller.RenameRoom("E8:F6:0A:E7:FB:6D", new RenameRoomRequest { Name = "E8:F6:0A:E7:FB:6D" });

        var entity = await db.Rooms.FindAsync("E8:F6:0A:E7:FB:6D");
        Assert.NotNull(entity);
        Assert.Equal(string.Empty, entity.FriendlyName);
    }

    [Fact]
    public async Task BuildAsync_RoomWithDefaultedFriendlyNameMatchingHostName_FallsBackToHostName()
    {
        await using var db = new OrchestratorDbContext(_dbOptions);
        await SeedGroupAsync(db);

        db.Rooms.Add(new RoomEntity
        {
            MacAddress = "E8:F6:0A:E7:FB:6D",
            FriendlyName = "ESP32-P4-Caster",
            MixVolume = 80
        });
        await db.SaveChangesAsync();

        var mockSnapcast = new Mock<ISnapcastService>();

        var client = new JsonObject
        {
            ["id"] = "E8:F6:0A:E7:FB:6D",
            ["connected"] = true,
            ["host"] = new JsonObject { ["name"] = "ESP32-P4-Caster" },
            ["config"] = new JsonObject { ["volume"] = new JsonObject { ["percent"] = 50, ["muted"] = false } },
            ["lastSeen"] = new JsonObject { ["sec"] = 1700000000, ["usec"] = 0 }
        };

        mockSnapcast.Setup(s => s.GetStatusAsync())
            .ReturnsAsync(CreateSnapcastStatus(new JsonArray { client }));

        var timeProvider = new CustomTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1700000005));
        var builder = new FullStateBuilder(db, mockSnapcast.Object, null, null, null, null, timeProvider);

        var state = await builder.BuildAsync();

        var room = Assert.Single(state.Groups[0].Rooms);
        Assert.Null(room.FriendlyName);
        Assert.Equal("ESP32-P4-Caster", room.HostName);
        Assert.Equal("ESP32-P4-Caster", room.Name);
    }

    [Fact]
    public async Task RoomsController_RenameRoom_HostName_ResetsFriendlyName()
    {
        await using var db = new OrchestratorDbContext(_dbOptions);
        db.Rooms.Add(new RoomEntity
        {
            MacAddress = "E8:F6:0A:E7:FB:6D",
            FriendlyName = "OldName",
            MixVolume = 50
        });
        await db.SaveChangesAsync();

        var client = new JsonObject
        {
            ["id"] = "E8:F6:0A:E7:FB:6D",
            ["host"] = new JsonObject { ["name"] = "ESP32-P4-Caster" }
        };

        var mockSnapcast = new Mock<ISnapcastService>();
        mockSnapcast.Setup(s => s.GetStatusAsync())
            .ReturnsAsync(CreateSnapcastStatus(new JsonArray { client }));

        var mockHub = new Mock<IHubContext<AudioHub, IAudioClient>>();
        var mockClients = new Mock<IHubClients<IAudioClient>>();
        mockClients.Setup(c => c.All).Returns(Mock.Of<IAudioClient>());
        mockHub.Setup(h => h.Clients).Returns(mockClients.Object);

        var mockBuilder = new Mock<FullStateBuilder>(db, mockSnapcast.Object);
        mockBuilder.Setup(b => b.BuildAsync()).ReturnsAsync(new FullStateDto([], [], [], [], [], [], VolumeProfile.Cubic));
        var mockVolume = new Mock<VolumeOrchestratorService>(db, mockSnapcast.Object, Mock.Of<ILibrespotApiClient>(), mockHub.Object, Mock.Of<ILogger<VolumeOrchestratorService>>());

        var controller = new RoomsController(mockSnapcast.Object, db, mockHub.Object, mockBuilder.Object, mockVolume.Object);

        // Renaming to the device hostname should clear it to empty string
        await controller.RenameRoom("E8:F6:0A:E7:FB:6D", new RenameRoomRequest { Name = "ESP32-P4-Caster" });

        var entity = await db.Rooms.FindAsync("E8:F6:0A:E7:FB:6D");
        Assert.NotNull(entity);
        Assert.Equal(string.Empty, entity.FriendlyName);
    }
}
