using Xunit;
using Moq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using Docker.DotNet;
using Docker.DotNet.Models;
using AudioOrchestrator.Services;
using AudioOrchestrator.Data;
using AudioOrchestrator.Hubs;
using AudioOrchestrator.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AudioOrchestrator.OrchestratorTests;

public class DockerLifecycleServiceTests
{
    [Theory]
    [InlineData("localhost:5000/librespot-server", "0.3.5", "localhost:5000/librespot-server:0.3.5")]
    [InlineData("localhost:5000/librespot-server:0.1.0", "0.3.5", "localhost:5000/librespot-server:0.3.5")]
    [InlineData("ghcr.io/slaesh/librespot-server", "0.3.5", "ghcr.io/slaesh/librespot-server:0.3.5")]
    [InlineData("ghcr.io/slaesh/librespot-server:latest", "0.3.5", "ghcr.io/slaesh/librespot-server:0.3.5")]
    [InlineData("localhost:5000/librespot-server", null, "localhost:5000/librespot-server")]
    [InlineData("ghcr.io/slaesh/librespot-server:v0.5.2", null, "ghcr.io/slaesh/librespot-server:v0.5.2")]
    public async Task SpawnLibreSpotAsync_ResolvesImageReferenceAccurately(string baseConfiguredImage, string? overrideVersion, string expectedSpawnedImage)
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            { "SPOTIFY_NODE_IMAGE", baseConfiguredImage }
        };
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();

        var dockerClientMock = new Mock<IDockerClient>();
        var containerOperationsMock = new Mock<IContainerOperations>();
        dockerClientMock.Setup(d => d.Containers).Returns(containerOperationsMock.Object);

        containerOperationsMock.Setup(c => c.ListContainersAsync(It.IsAny<ContainersListParameters>(), default))
            .ReturnsAsync(new List<ContainerListResponse>());

        string? capturedImage = null;
        containerOperationsMock.Setup(c => c.CreateContainerAsync(It.IsAny<CreateContainerParameters>(), default))
            .Callback<CreateContainerParameters, System.Threading.CancellationToken>((p, _) => capturedImage = p.Image)
            .ReturnsAsync(new CreateContainerResponse { ID = "test-container-id" });

        containerOperationsMock.Setup(c => c.StartContainerAsync(It.IsAny<string>(), It.IsAny<ContainerStartParameters>(), default))
            .ReturnsAsync(true);

        var imagesOperationsMock = new Mock<IImageOperations>();
        dockerClientMock.Setup(d => d.Images).Returns(imagesOperationsMock.Object);
        imagesOperationsMock.Setup(i => i.ListImagesAsync(It.IsAny<ImagesListParameters>(), default))
            .ReturnsAsync(new List<ImagesListResponse> { new ImagesListResponse { RepoTags = new List<string> { expectedSpawnedImage } } });

        var fifoServiceMock = new Mock<IFifoService>();
        var loggerMock = new Mock<ILogger<DockerLifecycleService>>();

        var dbOptions = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var db = new OrchestratorDbContext(dbOptions);

        var serviceProviderMock = new Mock<IServiceProvider>();
        var scopeMock = new Mock<IServiceScope>();
        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        serviceProviderMock.Setup(s => s.GetService(typeof(IServiceScopeFactory))).Returns(scopeFactoryMock.Object);
        scopeFactoryMock.Setup(s => s.CreateScope()).Returns(scopeMock.Object);
        scopeMock.Setup(s => s.ServiceProvider).Returns(serviceProviderMock.Object);
        serviceProviderMock.Setup(s => s.GetService(typeof(OrchestratorDbContext))).Returns(db);

        var hubMock = new Mock<IHubContext<AudioHub, IAudioClient>>();
        var clientsMock = new Mock<IHubClients<IAudioClient>>();
        clientsMock.Setup(c => c.All).Returns(new Mock<IAudioClient>().Object);
        hubMock.Setup(h => h.Clients).Returns(clientsMock.Object);
        serviceProviderMock.Setup(s => s.GetService(typeof(IHubContext<AudioHub, IAudioClient>))).Returns(hubMock.Object);

        var stateBuilderMock = new Mock<FullStateBuilder>(null!, null!);
        stateBuilderMock.Setup(b => b.BuildAsync()).ReturnsAsync(new FullStateDto(new(), new(), new(), new(), new()));
        serviceProviderMock.Setup(s => s.GetService(typeof(FullStateBuilder))).Returns(stateBuilderMock.Object);

        var service = new DockerLifecycleService(
            dockerClientMock.Object,
            loggerMock.Object,
            fifoServiceMock.Object,
            null!,
            serviceProviderMock.Object,
            config
        );

        await service.SpawnLibreSpotAsync("src-1", "Source 1", "/tmp/audio_fifos/test", 50, version: overrideVersion);

        Assert.Equal(expectedSpawnedImage, capturedImage);
    }
}
