using AudioOrchestrator.Services;
using Xunit;

namespace AudioOrchestrator.Tests;

public class VolumeProfileTests
{
    [Theory]
    [InlineData(0, "cubic", 0.0)]
    [InlineData(0, "linear", 0.0)]
    [InlineData(1, "cubic", 1.0)]
    [InlineData(1, "linear", 1.0)]
    [InlineData(0.5, "linear", 0.5)]
    [InlineData(0.5, "cubic", 0.125)]
    [InlineData(0.4, "cubic", 0.064)]
    public void CalculateAudioGain_CalculatesCorrectGain(double input, string profile, double expected)
    {
        var gain = VolumeOrchestratorService.CalculateAudioGain(input, profile);
        Assert.Equal(expected, gain, precision: 6);
    }

    [Fact]
    public void CalculateSnapcastVolume_BoundaryTests()
    {
        // 0% slider -> Snapcast 0%
        Assert.Equal(0, VolumeOrchestratorService.CalculateSnapcastVolume(0, 100, "cubic"));
        Assert.Equal(0, VolumeOrchestratorService.CalculateSnapcastVolume(100, 0, "cubic"));
        Assert.Equal(0, VolumeOrchestratorService.CalculateSnapcastVolume(0, 100, "linear"));
        Assert.Equal(0, VolumeOrchestratorService.CalculateSnapcastVolume(100, 0, "linear"));

        // 100% slider -> Snapcast 100%
        Assert.Equal(100, VolumeOrchestratorService.CalculateSnapcastVolume(100, 100, "cubic"));
        Assert.Equal(100, VolumeOrchestratorService.CalculateSnapcastVolume(100, 100, "linear"));
    }

    [Fact]
    public void CalculateSnapcastVolume_FiftyPercentBehavior()
    {
        // Linear mode at 50%
        Assert.Equal(50, VolumeOrchestratorService.CalculateSnapcastVolume(100, 50, "linear"));
        Assert.Equal(50, VolumeOrchestratorService.CalculateSnapcastVolume(50, 100, "linear"));

        // Cubic mode at 50%: 0.5^3 = 0.125 -> 12.5% -> rounds away from zero to 13%
        Assert.Equal(13, VolumeOrchestratorService.CalculateSnapcastVolume(100, 50, "cubic"));
        Assert.Equal(13, VolumeOrchestratorService.CalculateSnapcastVolume(50, 100, "cubic"));
    }

    [Fact]
    public void CalculateSnapcastVolume_MultiRoomMixingTests()
    {
        // Spec test: Group 50%, Room 80% (cubic):
        // effective = 0.5 * 0.8 = 0.40 -> 0.4^3 = 0.064 -> 6.4% -> rounds to 6%
        Assert.Equal(6, VolumeOrchestratorService.CalculateSnapcastVolume(50, 80, "cubic"));

        // Spec test: Group 50%, Room 80% (linear):
        // effective = 0.5 * 0.8 = 0.40 -> 40%
        Assert.Equal(40, VolumeOrchestratorService.CalculateSnapcastVolume(50, 80, "linear"));

        // Group 50%, Room 50% (linear): 25%
        Assert.Equal(25, VolumeOrchestratorService.CalculateSnapcastVolume(50, 50, "linear"));

        // Group 50%, Room 50% (cubic): (0.5 * 0.5)^3 = 0.25^3 = 0.015625 -> 1.56% -> rounds to 2%
        Assert.Equal(2, VolumeOrchestratorService.CalculateSnapcastVolume(50, 50, "cubic"));
    }

    [Fact]
    public void CalculateSnapcastVolume_Clamping()
    {
        // Values outside [0, 100] are clamped
        Assert.Equal(0, VolumeOrchestratorService.CalculateSnapcastVolume(-10, 50, "cubic"));
        Assert.Equal(100, VolumeOrchestratorService.CalculateSnapcastVolume(150, 100, "cubic"));
    }
}
