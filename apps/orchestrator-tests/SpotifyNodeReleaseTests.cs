using Xunit;
using AudioOrchestrator.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace AudioOrchestrator.OrchestratorTests;

public class SpotifyNodeReleaseTests
{
    [Theory]
    [InlineData("0.3.0", "0.3.1", true)]
    [InlineData("0.3.0", "0.3.9", true)]
    [InlineData("v0.3.0", "v0.3.1", true)]
    [InlineData("0.3.0", "0.4.0", false)] // Minor bump in 0.x is breaking
    [InlineData("0.3.0", "1.0.0", false)] // Major bump is breaking
    [InlineData("0.3.1", "0.3.0", false)] // Downgrade
    [InlineData("0.3.0", "0.3.0", false)] // Same version
    [InlineData("1.0.0", "1.1.0", true)]  // Minor bump in 1.x is non-breaking
    [InlineData("1.0.0", "1.0.1", true)]  // Patch bump in 1.x is non-breaking
    [InlineData("1.0.0", "2.0.0", false)] // Major bump in 1.x is breaking
    [InlineData("0.3.1", "0.3.3-rc.1", false)] // Prerelease target is excluded when current is stable
    [InlineData("1.0.0", "1.1.0-alpha.1", false)] // Prerelease target is excluded when current is stable
    public void IsNonBreakingUpgrade_EvaluatesCorrectly(string current, string target, bool expected)
    {
        var result = NodeReleaseService.IsNonBreakingUpgrade(current, target);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ParseSemVer_ValidStrings_ParsedAccurately()
    {
        var v1 = NodeReleaseService.ParseSemVer("v0.3.1");
        Assert.NotNull(v1);
        Assert.Equal(0, v1.Major);
        Assert.Equal(3, v1.Minor);
        Assert.Equal(1, v1.Patch);
        Assert.Null(v1.Prerelease);

        var v2 = NodeReleaseService.ParseSemVer("1.2.3-beta.1");
        Assert.NotNull(v2);
        Assert.Equal(1, v2.Major);
        Assert.Equal(2, v2.Minor);
        Assert.Equal(3, v2.Patch);
        Assert.Equal("beta.1", v2.Prerelease);
    }

    [Fact]
    public void ParseSemVer_Comparison_OrdersCorrectly()
    {
        var v030 = NodeReleaseService.ParseSemVer("0.3.0")!;
        var v031 = NodeReleaseService.ParseSemVer("0.3.1")!;
        var v040 = NodeReleaseService.ParseSemVer("0.4.0")!;
        var v100 = NodeReleaseService.ParseSemVer("1.0.0")!;

        Assert.True(v031.CompareTo(v030) > 0);
        Assert.True(v040.CompareTo(v031) > 0);
        Assert.True(v100.CompareTo(v040) > 0);
    }

    [Theory]
    [InlineData("ghcr.io/slaesh/radio-node-audalaxy:latest", "slaesh/radio-node-audalaxy")]
    [InlineData("ghcr.io/slaesh/radio-node-audalaxy:0.1.2", "slaesh/radio-node-audalaxy")]
    [InlineData("slaesh/radio-node-audalaxy:latest", "slaesh/radio-node-audalaxy")]
    [InlineData("docker.io/library/alpine:latest", "library/alpine")]
    [InlineData("alpine:latest", null)]
    public void RadioPluginEntity_GetGitHubRepository_ParsesCorrectly(string dockerImage, string? expectedRepo)
    {
        var entity = new AudioOrchestrator.Data.Entities.RadioPluginEntity { DockerImage = dockerImage };
        Assert.Equal(expectedRepo, entity.GetGitHubRepository());
    }

    [Theory]
    [InlineData("0.2.0", "ghcr.io/slaesh/radio:latest", "0.2.0")]
    [InlineData(null, "ghcr.io/slaesh/radio:0.3.5", "0.3.5")]
    [InlineData("", "ghcr.io/slaesh/radio:latest", "0.1.0")]
    [InlineData(null, "localhost:5000/radio", "0.1.0")]
    [InlineData("", "localhost:5000/radio", "0.1.0")]
    [InlineData(null, "localhost:5000/radio:0.3.5", "0.3.5")]
    [InlineData(null, "localhost:5000/radio:latest", "0.1.0")]
    public void RadioPluginEntity_GetEffectiveVersion_ResolvesCorrectly(string? currentVersion, string dockerImage, string expected)
    {
        var entity = new AudioOrchestrator.Data.Entities.RadioPluginEntity
        {
            CurrentVersion = currentVersion!,
            DockerImage = dockerImage
        };
        Assert.Equal(expected, entity.GetEffectiveVersion());
    }

    [Fact]
    public async Task ResolveInitialSpotifyVersionAsync_PicksLatestCompatibleFromGitHub()
    {
        var json = @"[
            { ""tag_name"": ""v0.3.2"", ""name"": ""0.3.2"", ""draft"": false, ""prerelease"": false },
            { ""tag_name"": ""v0.3.1"", ""name"": ""0.3.1"", ""draft"": false, ""prerelease"": false }
        ]";
        var service = CreateReleaseService(json);

        var resolved = await service.ResolveInitialSpotifyVersionAsync(new[] { "0.3.0" });
        Assert.Equal("0.3.2", resolved.Version);
        Assert.Equal("0.3.2", resolved.ImageTagToSpawn);
    }

    [Fact]
    public async Task ResolveInitialSpotifyVersionAsync_HonorsFleetMinimumWhenOffline()
    {
        var service = CreateFailingReleaseService();

        var resolved = await service.ResolveInitialSpotifyVersionAsync(new[] { "0.3.5", "0.3.1" });
        Assert.Equal("0.3.5", resolved.Version);
        Assert.Equal("0.3.5", resolved.ImageTagToSpawn);
    }

    [Fact]
    public async Task ResolveInitialSpotifyVersionAsync_IgnoresBreakingVersionsOnGitHub()
    {
        var json = @"[
            { ""tag_name"": ""v0.4.0"", ""name"": ""0.4.0"", ""draft"": false, ""prerelease"": false },
            { ""tag_name"": ""v0.3.2"", ""name"": ""0.3.2"", ""draft"": false, ""prerelease"": false }
        ]";
        var service = CreateReleaseService(json);

        var resolved = await service.ResolveInitialSpotifyVersionAsync(new[] { "0.3.1" });
        Assert.Equal("0.3.2", resolved.Version);
        Assert.Equal("0.3.2", resolved.ImageTagToSpawn);
    }

    [Fact]
    public async Task ResolveInitialSpotifyVersionAsync_IgnoresPrereleasesOnGitHub_WhenBaselineIsStable()
    {
        var json = @"[
            { ""tag_name"": ""v0.3.3-rc.1"", ""name"": ""0.3.3-rc.1"", ""draft"": false, ""prerelease"": true },
            { ""tag_name"": ""v0.3.2"", ""name"": ""0.3.2"", ""draft"": false, ""prerelease"": false }
        ]";
        var service = CreateReleaseService(json);

        var resolved = await service.ResolveInitialSpotifyVersionAsync(new[] { "0.3.1" });
        Assert.Equal("0.3.2", resolved.Version);
        Assert.Equal("0.3.2", resolved.ImageTagToSpawn);
    }

    [Fact]
    public async Task ResolveInitialSpotifyVersionAsync_IgnoresPrereleaseFleetVersions_WhenConfiguredIsStable()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            { "SPOTIFY_NODE_IMAGE", "ghcr.io/slaesh/librespot-server:0.3.0" }
        };
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var service = CreateFailingReleaseService(config);
        var resolved = await service.ResolveInitialSpotifyVersionAsync(new[] { "0.3.5-rc.1" });
        Assert.Equal("0.3.0", resolved.Version);
        Assert.Null(resolved.ImageTagToSpawn);
    }

    [Fact]
    public async Task ResolveInitialSpotifyVersionAsync_FallsBackToConfiguredImageTagWhenNoFleetAndOffline()
    {
        var service = CreateFailingReleaseService();

        var resolved = await service.ResolveInitialSpotifyVersionAsync(null);
        var expected = AudioOrchestrator.Contracts.OrchestratorConstants.ExtractImageTag(AudioOrchestrator.Contracts.OrchestratorConstants.DefaultSpotifyNodeImage);
        Assert.Equal(expected, resolved.Version);
        Assert.Null(resolved.ImageTagToSpawn);
    }

    [Fact]
    public async Task ResolveInitialSpotifyVersionAsync_DerivesBaselineFromConfiguredSpotifyNodeImage()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            { "SPOTIFY_NODE_IMAGE", "ghcr.io/slaesh/librespot-server:0.5.2" }
        };
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var service = CreateFailingReleaseService(config);
        var resolved = await service.ResolveInitialSpotifyVersionAsync(null);
        Assert.Equal("0.5.2", resolved.Version);
        Assert.Null(resolved.ImageTagToSpawn);
    }

    [Theory]
    [InlineData("ghcr.io/slaesh/librespot-server:latest", "latest")]
    [InlineData("ghcr.io/slaesh/librespot-server:v0.5.2", "0.5.2")]
    [InlineData("ghcr.io/slaesh/librespot-server", "")]
    public async Task ResolveInitialSpotifyVersionAsync_PreservesConfiguredImageReferenceVerbatim(string configuredImage, string expectedVersion)
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            { "SPOTIFY_NODE_IMAGE", configuredImage }
        };
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var service = CreateFailingReleaseService(config);
        var resolved = await service.ResolveInitialSpotifyVersionAsync(null);
        Assert.Equal(expectedVersion, resolved.Version);
        Assert.Null(resolved.ImageTagToSpawn);
    }

    [Theory]
    [InlineData("ghcr.io/slaesh/librespot-server:latest", "latest")]
    [InlineData("ghcr.io/slaesh/librespot-server:v0.5.2", "0.5.2")]
    [InlineData("ghcr.io/slaesh/librespot-server:0.3.1", "0.3.1")]
    [InlineData("ghcr.io/slaesh/librespot-server", "")]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("localhost:5000/image:1.2.3", "1.2.3")]
    public void ExtractImageTag_HandlesAllEdgeCases(string? image, string expected)
    {
        var result = AudioOrchestrator.Contracts.OrchestratorConstants.ExtractImageTag(image);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ExtractImageTag_UsesCustomDefault_WhenSpecified()
    {
        Assert.Equal("fallback", AudioOrchestrator.Contracts.OrchestratorConstants.ExtractImageTag(null, "fallback"));
        Assert.Equal("fallback", AudioOrchestrator.Contracts.OrchestratorConstants.ExtractImageTag("ghcr.io/slaesh/librespot-server", "fallback"));
    }

    [Fact]
    public async Task GetReleasesAsync_PrereleaseMarkedIncompatible_WhenBaselineIsStable()
    {
        var json = @"[
            { ""tag_name"": ""v0.3.2-rc.1"", ""name"": ""0.3.2-rc.1"", ""draft"": false, ""prerelease"": false },
            { ""tag_name"": ""v0.3.3"", ""name"": ""0.3.3"", ""draft"": false, ""prerelease"": true },
            { ""tag_name"": ""v0.3.4"", ""name"": ""0.3.4"", ""draft"": false, ""prerelease"": false }
        ]";
        var service = CreateReleaseService(json);

        var releases = await service.GetReleasesAsync("slaesh/librespot-server", currentVersion: "0.3.1");

        var r1 = releases.First(r => r.Version == "0.3.2-rc.1");
        Assert.False(r1.IsCompatible);

        var r2 = releases.First(r => r.Version == "0.3.3");
        Assert.False(r2.IsCompatible);

        var r3 = releases.First(r => r.Version == "0.3.4");
        Assert.True(r3.IsCompatible);
    }

    private static NodeReleaseService CreateReleaseService(string githubResponseJson, IConfiguration? config = null)
    {
        var handler = new TestHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(githubResponseJson, System.Text.Encoding.UTF8, "application/json")
        });
        var httpClient = new HttpClient(handler);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var logger = new Mock<ILogger<NodeReleaseService>>().Object;
        return new NodeReleaseService(httpClient, cache, logger, config);
    }

    private static NodeReleaseService CreateFailingReleaseService(IConfiguration? config = null)
    {
        var handler = new TestHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        var httpClient = new HttpClient(handler);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var logger = new Mock<ILogger<NodeReleaseService>>().Object;
        return new NodeReleaseService(httpClient, cache, logger, config);
    }

    private sealed class TestHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public TestHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }
}
