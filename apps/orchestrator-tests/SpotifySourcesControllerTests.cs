using Xunit;
using Moq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AudioOrchestrator.Controllers;
using AudioOrchestrator.Services;
using AudioOrchestrator.Data;
using AudioOrchestrator.Data.Entities;
using AudioOrchestrator.Contracts;
using AudioOrchestrator.Hubs;
using Microsoft.Extensions.Configuration;

namespace AudioOrchestrator.OrchestratorTests;

public class SpotifySourcesControllerTests
{
    private readonly DbContextOptions<OrchestratorDbContext> _dbOptions;
    private readonly Mock<IContainerLifecycleService> _containerLifecycleMock;
    private readonly Mock<ILibrespotApiClient> _librespotApiMock;
    private readonly Mock<ISnapcastService> _snapcastMock;
    private readonly Mock<ILogger<SpotifySourcesController>> _loggerMock;
    private readonly Mock<IHubContext<AudioHub, IAudioClient>> _hubMock;
    private readonly Mock<FullStateBuilder> _builderMock;
    private readonly Mock<ISpotifyNodeReleaseService> _releaseServiceMock;

    public SpotifySourcesControllerTests()
    {
        _dbOptions = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _snapcastMock = new Mock<ISnapcastService>();
        _containerLifecycleMock = new Mock<IContainerLifecycleService>();
        _loggerMock = new Mock<ILogger<SpotifySourcesController>>();
        _librespotApiMock = new Mock<ILibrespotApiClient>();
        _releaseServiceMock = new Mock<ISpotifyNodeReleaseService>();

        _hubMock = new Mock<IHubContext<AudioHub, IAudioClient>>();
        var clientsMock = new Mock<IHubClients<IAudioClient>>();
        var allClientsMock = new Mock<IAudioClient>();
        clientsMock.Setup(c => c.All).Returns(allClientsMock.Object);
        _hubMock.Setup(h => h.Clients).Returns(clientsMock.Object);

        _builderMock = new Mock<FullStateBuilder>(null!, null!);
        _builderMock.Setup(b => b.BuildAsync()).ReturnsAsync(new FullStateDto(new(), new(), new(), new(), new()));
    }

    private OrchestratorDbContext GetDbContext() => new OrchestratorDbContext(_dbOptions);

    private SpotifySourcesController CreateController(OrchestratorDbContext db)
    {
        return new SpotifySourcesController(
            _containerLifecycleMock.Object,
            _librespotApiMock.Object,
            _snapcastMock.Object,
            db,
            _loggerMock.Object,
            _hubMock.Object,
            _builderMock.Object,
            _releaseServiceMock.Object
        );
    }

    [Fact]
    public async Task CreateSource_SavesResolvedVersionAndSpawnsAppropriateTag()
    {
        using var db = GetDbContext();
        _releaseServiceMock
            .Setup(r => r.ResolveInitialSpotifyVersionAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new InitialSpotifyVersionResult("0.3.2", "0.3.2"));

        var controller = CreateController(db);

        var req = new CreateSourceRequest { Name = "Living Room Spotify", MaxVolume = 80 };
        var actionResult = await controller.CreateSource(req);
        var okResult = Assert.IsType<OkObjectResult>(actionResult);
        var entity = Assert.IsType<SpotifySourceEntity>(okResult.Value);

        Assert.Equal("0.3.2", entity.CurrentVersion);

        // Verify container was spawned with version "0.3.2"
        _containerLifecycleMock.Verify(c => c.SpawnLibreSpotAsync(
            entity.Id,
            entity.Name,
            It.IsAny<string>(),
            80,
            null,
            "0.3.2"
        ), Times.Once);
    }

    [Fact]
    public async Task CreateSource_WhenFallingBackToConfiguredImage_SpawnsWithNullVersion()
    {
        using var db = GetDbContext();
        _releaseServiceMock
            .Setup(r => r.ResolveInitialSpotifyVersionAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new InitialSpotifyVersionResult("latest", null));

        var controller = CreateController(db);

        var req = new CreateSourceRequest { Name = "Kitchen Spotify", MaxVolume = 60 };
        var actionResult = await controller.CreateSource(req);
        var okResult = Assert.IsType<OkObjectResult>(actionResult);
        var entity = Assert.IsType<SpotifySourceEntity>(okResult.Value);

        Assert.Equal("", entity.CurrentVersion);

        // Verify container was spawned with version: null to preserve configured image verbatim
        _containerLifecycleMock.Verify(c => c.SpawnLibreSpotAsync(
            entity.Id,
            entity.Name,
            It.IsAny<string>(),
            60,
            null,
            null
        ), Times.Once);
    }

    [Fact]
    public async Task CreateSource_ThenUpdate_ThenRestart_PreservesNullVersionAcrossAllLifecycleOperations()
    {
        using var db = GetDbContext();
        _releaseServiceMock
            .Setup(r => r.ResolveInitialSpotifyVersionAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new InitialSpotifyVersionResult("0.5.2", null));

        var controller = CreateController(db);

        // 1. Create with configured image fallback
        var createReq = new CreateSourceRequest { Name = "Persistent Spotify", MaxVolume = 60 };
        var createResult = await controller.CreateSource(createReq);
        var createdEntity = Assert.IsType<SpotifySourceEntity>(Assert.IsType<OkObjectResult>(createResult).Value);

        Assert.Equal("", createdEntity.CurrentVersion);
        _containerLifecycleMock.Verify(c => c.SpawnLibreSpotAsync(
            createdEntity.Id,
            createdEntity.Name,
            It.IsAny<string>(),
            60,
            null,
            null
        ), Times.Once);

        // 2. Update settings (recreates container)
        var updateReq = new UpdateSourceRequest { Name = "Persistent Spotify Renamed", MaxVolume = 75 };
        var updateResult = await controller.UpdateSource(createdEntity.Id, updateReq);
        Assert.IsType<OkObjectResult>(updateResult);

        // Must still pass version: null so configured image reference (e.g. :v0.5.2) is not switched to :0.5.2
        _containerLifecycleMock.Verify(c => c.SpawnLibreSpotAsync(
            createdEntity.Id,
            "Persistent Spotify Renamed",
            It.IsAny<string>(),
            75,
            null,
            null
        ), Times.Once);

        // 3. Restart container
        var restartResult = await controller.RestartSource(createdEntity.Id);
        Assert.IsType<OkResult>(restartResult);

        _containerLifecycleMock.Verify(c => c.SpawnLibreSpotAsync(
            createdEntity.Id,
            "Persistent Spotify Renamed",
            It.IsAny<string>(),
            75,
            null,
            null
        ), Times.Exactly(2)); // Once in Update, once in Restart
    }

    [Fact]
    public async Task UpdateSource_WhenCurrentVersionEmpty_SpawnsWithNullVersion()
    {
        using var db = GetDbContext();
        var source = new SpotifySourceEntity
        {
            Id = "src-empty-ver",
            Name = "Empty Version Source",
            CurrentVersion = "",
            MaxVolume = 50
        };
        db.SpotifySources.Add(source);
        await db.SaveChangesAsync();

        var controller = CreateController(db);

        var req = new UpdateSourceRequest { Name = "Renamed Source", MaxVolume = 70 };
        var actionResult = await controller.UpdateSource("src-empty-ver", req);
        Assert.IsType<OkObjectResult>(actionResult);

        _containerLifecycleMock.Verify(c => c.SpawnLibreSpotAsync(
            "src-empty-ver",
            "Renamed Source",
            It.IsAny<string>(),
            70,
            null,
            null
        ), Times.Once);
    }

    [Fact]
    public async Task RestartSource_WhenCurrentVersionEmpty_SpawnsWithNullVersion()
    {
        using var db = GetDbContext();
        var source = new SpotifySourceEntity
        {
            Id = "src-restart",
            Name = "Restart Source",
            CurrentVersion = "",
            MaxVolume = 50
        };
        db.SpotifySources.Add(source);
        await db.SaveChangesAsync();

        var controller = CreateController(db);

        var actionResult = await controller.RestartSource("src-restart");
        Assert.IsType<OkResult>(actionResult);

        _containerLifecycleMock.Verify(c => c.SpawnLibreSpotAsync(
            "src-restart",
            "Restart Source",
            It.IsAny<string>(),
            50,
            null,
            null
        ), Times.Once);
    }

    [Fact]
    public async Task GetSourceReleases_WhenCurrentVersionEmpty_ResolvesFromConfiguredImage()
    {
        using var db = GetDbContext();
        var source = new SpotifySourceEntity
        {
            Id = "src-empty-rel",
            Name = "Empty Version Source",
            CurrentVersion = "",
            MaxVolume = 50
        };
        db.SpotifySources.Add(source);
        await db.SaveChangesAsync();

        var inMemorySettings = new Dictionary<string, string?>
        {
            { "SPOTIFY_NODE_IMAGE", "ghcr.io/slaesh/librespot-server:0.3.1" }
        };
        Microsoft.Extensions.Configuration.IConfiguration config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();

        _releaseServiceMock
            .Setup(r => r.GetReleaseHistoryAsync("0.3.1", null))
            .ReturnsAsync(new List<SpotifyReleaseDto>
            {
                new SpotifyReleaseDto("v0.3.2", "0.3.2", "0.3.2", "notes", DateTime.UtcNow, true)
            });

        var controller = new SpotifySourcesController(
            _containerLifecycleMock.Object,
            _librespotApiMock.Object,
            _snapcastMock.Object,
            db,
            _loggerMock.Object,
            _hubMock.Object,
            _builderMock.Object,
            _releaseServiceMock.Object,
            config
        );

        var actionResult = await controller.GetSourceReleases("src-empty-rel");
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var releases = Assert.IsAssignableFrom<IEnumerable<SpotifyReleaseDto>>(okResult.Value);

        Assert.Single(releases);
        _releaseServiceMock.Verify(r => r.GetReleaseHistoryAsync("0.3.1", null), Times.Once);
    }

    [Fact]
    public async Task UpgradeSource_WhenCurrentVersionEmpty_ResolvesFromConfiguredImage()
    {
        using var db = GetDbContext();
        var source = new SpotifySourceEntity
        {
            Id = "src-empty-upg",
            Name = "Empty Version Source",
            CurrentVersion = "",
            MaxVolume = 50
        };
        db.SpotifySources.Add(source);
        await db.SaveChangesAsync();

        var inMemorySettings = new Dictionary<string, string?>
        {
            { "SPOTIFY_NODE_IMAGE", "ghcr.io/slaesh/librespot-server:0.3.1" }
        };
        Microsoft.Extensions.Configuration.IConfiguration config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();

        _releaseServiceMock
            .Setup(r => r.GetLatestCompatibleVersionAsync("0.3.1"))
            .ReturnsAsync("0.3.2");

        var controller = new SpotifySourcesController(
            _containerLifecycleMock.Object,
            _librespotApiMock.Object,
            _snapcastMock.Object,
            db,
            _loggerMock.Object,
            _hubMock.Object,
            _builderMock.Object,
            _releaseServiceMock.Object,
            config
        );

        var actionResult = await controller.UpgradeSource("src-empty-upg");
        Assert.IsType<OkObjectResult>(actionResult);

        _releaseServiceMock.Verify(r => r.GetLatestCompatibleVersionAsync("0.3.1"), Times.Once);
        _containerLifecycleMock.Verify(c => c.UpgradeSpotifyNodeAsync("src-empty-upg", "0.3.2"), Times.Once);
    }
}
