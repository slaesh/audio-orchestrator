using Xunit;
using AudioOrchestrator.Services;
using Microsoft.Extensions.Caching.Memory;
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
    public void RadioPluginEntity_GetEffectiveVersion_ResolvesCorrectly(string? currentVersion, string dockerImage, string expected)
    {
        var entity = new AudioOrchestrator.Data.Entities.RadioPluginEntity
        {
            CurrentVersion = currentVersion!,
            DockerImage = dockerImage
        };
        Assert.Equal(expected, entity.GetEffectiveVersion());
    }
}
