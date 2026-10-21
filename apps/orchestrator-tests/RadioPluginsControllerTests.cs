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

namespace AudioOrchestrator.OrchestratorTests;

public class RadioPluginsControllerTests
{
    private readonly DbContextOptions<OrchestratorDbContext> _dbOptions;
    private readonly Mock<IHubContext<AudioHub, IAudioClient>> _hubMock;
    private readonly Mock<FullStateBuilder> _builderMock;
    private readonly Mock<INodeReleaseService> _releaseServiceMock;
    private readonly Mock<IContainerLifecycleService> _containerLifecycleMock;
    private readonly Mock<ILogger<RadioPluginsController>> _loggerMock;

    public RadioPluginsControllerTests()
    {
        _dbOptions = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _hubMock = new Mock<IHubContext<AudioHub, IAudioClient>>();
        var clientsMock = new Mock<IHubClients<IAudioClient>>();
        var allClientsMock = new Mock<IAudioClient>();
        clientsMock.Setup(c => c.All).Returns(allClientsMock.Object);
        _hubMock.Setup(h => h.Clients).Returns(clientsMock.Object);

        _builderMock = new Mock<FullStateBuilder>(null!, null!);
        _builderMock.Setup(b => b.BuildAsync()).ReturnsAsync(new FullStateDto(new(), new(), new(), new(), new()));

        _releaseServiceMock = new Mock<INodeReleaseService>();
        _containerLifecycleMock = new Mock<IContainerLifecycleService>();
        _loggerMock = new Mock<ILogger<RadioPluginsController>>();
    }

    private OrchestratorDbContext GetDbContext() => new OrchestratorDbContext(_dbOptions);

    [Fact]
    public async Task CreatePlugin_ReturnsEffectiveVersionInDto_WhenImageHasTag()
    {
        using var db = GetDbContext();
        var controller = new RadioPluginsController(
            db,
            _hubMock.Object,
            _builderMock.Object,
            _releaseServiceMock.Object,
            _containerLifecycleMock.Object,
            _loggerMock.Object
        );

        var request = new RadioPluginCreateRequest(
            Id: "plugin-tagged",
            Name: "Tagged Plugin",
            DockerImage: "ghcr.io/slaesh/radio:0.3.5",
            UrlPatterns: new List<string> { "^https://example\\.com" }
        );

        var result = await controller.CreatePlugin(request);
        var createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        var dto = Assert.IsType<RadioPluginDto>(createdResult.Value);

        Assert.Equal("0.3.5", dto.CurrentVersion);
        Assert.Equal("plugin-tagged", dto.Id);
    }

    [Fact]
    public async Task CreatePlugin_ReturnsDefaultEffectiveVersionInDto_WhenImageIsLatest()
    {
        using var db = GetDbContext();
        var controller = new RadioPluginsController(
            db,
            _hubMock.Object,
            _builderMock.Object,
            _releaseServiceMock.Object,
            _containerLifecycleMock.Object,
            _loggerMock.Object
        );

        var request = new RadioPluginCreateRequest(
            Id: "plugin-latest",
            Name: "Latest Plugin",
            DockerImage: "ghcr.io/slaesh/radio:latest",
            UrlPatterns: new List<string>()
        );

        var result = await controller.CreatePlugin(request);
        var createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        var dto = Assert.IsType<RadioPluginDto>(createdResult.Value);

        Assert.Equal("0.1.0", dto.CurrentVersion);
    }

    [Fact]
    public async Task UpdatePlugin_ReturnsEffectiveVersionInDto()
    {
        using var db = GetDbContext();
        var existing = new RadioPluginEntity
        {
            Id = "updatable",
            Name = "Initial",
            DockerImage = "ghcr.io/slaesh/radio:0.1.0",
            CurrentVersion = "0.1.0"
        };
        db.RadioPlugins.Add(existing);
        await db.SaveChangesAsync();

        var controller = new RadioPluginsController(
            db,
            _hubMock.Object,
            _builderMock.Object,
            _releaseServiceMock.Object,
            _containerLifecycleMock.Object,
            _loggerMock.Object
        );

        var updateReq = new RadioPluginUpdateRequest(
            Name: "Updated Name",
            DockerImage: "ghcr.io/slaesh/radio:0.4.0",
            UrlPatterns: new List<string>(),
            Enabled: true
        );

        var result = await controller.UpdatePlugin("updatable", updateReq);
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<RadioPluginDto>(okResult.Value);

        Assert.Equal("0.4.0", dto.CurrentVersion);
    }
}
