using AudioOrchestrator.Contracts;
using AudioOrchestrator.Services;
using Xunit;

namespace AudioOrchestrator.Tests;

public class VolumeProfileTests
{
    [Theory]
    [InlineData(0, VolumeProfile.Cubic, 0.0)]
    [InlineData(0, VolumeProfile.Linear, 0.0)]
    [InlineData(1, VolumeProfile.Cubic, 1.0)]
    [InlineData(1, VolumeProfile.Linear, 1.0)]
    [InlineData(0.5, VolumeProfile.Linear, 0.5)]
    [InlineData(0.5, VolumeProfile.Cubic, 0.25)]
    [InlineData(0.4, VolumeProfile.Cubic, 0.16)]
    public void CalculateAudioGain_CalculatesCorrectGain(double input, VolumeProfile profile, double expected)
    {
        var gain = VolumeOrchestratorService.CalculateAudioGain(input, profile);
        Assert.Equal(expected, gain, precision: 6);
    }

    [Fact]
    public void CalculateSnapcastVolume_BoundaryTests()
    {
        // 0% slider -> Snapcast 0%
        Assert.Equal(0, VolumeOrchestratorService.CalculateSnapcastVolume(0, 100, VolumeProfile.Cubic));
        Assert.Equal(0, VolumeOrchestratorService.CalculateSnapcastVolume(100, 0, VolumeProfile.Cubic));
        Assert.Equal(0, VolumeOrchestratorService.CalculateSnapcastVolume(0, 100, VolumeProfile.Linear));
        Assert.Equal(0, VolumeOrchestratorService.CalculateSnapcastVolume(100, 0, VolumeProfile.Linear));

        // 100% slider -> Snapcast 100%
        Assert.Equal(100, VolumeOrchestratorService.CalculateSnapcastVolume(100, 100, VolumeProfile.Cubic));
        Assert.Equal(100, VolumeOrchestratorService.CalculateSnapcastVolume(100, 100, VolumeProfile.Linear));
    }

    [Fact]
    public void CalculateSnapcastVolume_FiftyPercentBehavior()
    {
        // Linear mode at 50%
        Assert.Equal(50, VolumeOrchestratorService.CalculateSnapcastVolume(100, 50, VolumeProfile.Linear));
        Assert.Equal(50, VolumeOrchestratorService.CalculateSnapcastVolume(50, 100, VolumeProfile.Linear));

        // Quadratic natural mode at 50%: 0.5^2 = 0.25 -> 25%
        Assert.Equal(25, VolumeOrchestratorService.CalculateSnapcastVolume(50, 100, VolumeProfile.Cubic));
    }

    [Fact]
    public void CalculateSnapcastVolume_MultiRoomMixingTests()
    {
        // Group 40%, Room 40% (quadratic):
        // groupGain = 0.4^2 = 0.16 -> room mix 40% = 0.16 * 0.4 = 0.064 -> 6% (not muted!)
        Assert.Equal(6, VolumeOrchestratorService.CalculateSnapcastVolume(40, 40, VolumeProfile.Cubic));

        // Group 50%, Room 80% (quadratic):
        // groupGain = 0.5^2 = 0.25 -> room mix 80% = 0.25 * 0.8 = 0.20 -> 20%
        Assert.Equal(20, VolumeOrchestratorService.CalculateSnapcastVolume(50, 80, VolumeProfile.Cubic));

        // Group 50%, Room 50% (quadratic):
        // groupGain = 0.5^2 = 0.25 -> room mix 50% = 0.25 * 0.5 = 0.125 -> 13%
        Assert.Equal(13, VolumeOrchestratorService.CalculateSnapcastVolume(50, 50, VolumeProfile.Cubic));

        // Group 50%, Room 80% (linear):
        // groupGain = 0.50 -> room mix 80% = 0.50 * 0.8 = 40%
        Assert.Equal(40, VolumeOrchestratorService.CalculateSnapcastVolume(50, 80, VolumeProfile.Linear));

        // Group 50%, Room 50% (linear): 25%
        Assert.Equal(25, VolumeOrchestratorService.CalculateSnapcastVolume(50, 50, VolumeProfile.Linear));

        // Never-0 protection: Group 5%, Room 5%:
        // Mathematically 0.000125 (0.0125%), but protected to 1% because neither is 0
        Assert.Equal(1, VolumeOrchestratorService.CalculateSnapcastVolume(5, 5, VolumeProfile.Cubic));
    }

    [Fact]
    public void CalculateSnapcastVolume_Clamping()
    {
        // Values outside [0, 100] are clamped
        Assert.Equal(0, VolumeOrchestratorService.CalculateSnapcastVolume(-10, 50, VolumeProfile.Cubic));
        Assert.Equal(100, VolumeOrchestratorService.CalculateSnapcastVolume(150, 100, VolumeProfile.Cubic));
    }
}
