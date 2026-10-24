using AudioOrchestrator.Contracts;
using AudioOrchestrator.Controllers;
using AudioOrchestrator.Data;
using AudioOrchestrator.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace AudioOrchestrator.Tests;

public class SystemControllerTests
{
    private readonly DbContextOptions<OrchestratorDbContext> _dbOptions;

    public SystemControllerTests()
    {
        _dbOptions = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    [Fact]
    public async Task GetSystemInfo_ShouldReturnOk_WithHealthyStatus_WhenDependenciesConnected()
    {
        // Arrange
        await using var db = new OrchestratorDbContext(_dbOptions);
        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.Setup(e => e.EnvironmentName).Returns("Production");

        var mockSnapcast = new Mock<ISnapcastService>();
        mockSnapcast.Setup(s => s.IsConnected).Returns(true);

        var mockMqtt = new Mock<IHomeAssistantMqttService>();
        mockMqtt.Setup(m => m.IsConnected).Returns(true);

        var mockLogger = new Mock<ILogger<SystemController>>();

        var controller = new SystemController(mockEnv.Object, mockSnapcast.Object, db, mockMqtt.Object, mockLogger.Object);

        // Act
        var result = await controller.GetSystemInfo();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var info = Assert.IsType<SystemInfoDto>(okResult.Value);

        Assert.Equal(SystemStatus.Healthy, info.Status);
        Assert.Equal("Production", info.Environment);
        Assert.Equal("0.4.1", info.Version);
        Assert.True(info.SnapserverConnected);
        Assert.True(info.DatabaseConnected);
        Assert.True(info.MqttConnected);
        Assert.True(info.UptimeSeconds >= 0);
        Assert.False(string.IsNullOrEmpty(info.UptimeFormatted));
    }

    [Fact]
    public async Task GetHealth_ShouldReturn503_WhenSnapcastDisconnected()
    {
        // Arrange
        await using var db = new OrchestratorDbContext(_dbOptions);
        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.Setup(e => e.EnvironmentName).Returns("Production");

        var mockSnapcast = new Mock<ISnapcastService>();
        mockSnapcast.Setup(s => s.IsConnected).Returns(false); // Disconnected

        var mockMqtt = new Mock<IHomeAssistantMqttService>();
        mockMqtt.Setup(m => m.IsConnected).Returns(true);

        var mockLogger = new Mock<ILogger<SystemController>>();

        var controller = new SystemController(mockEnv.Object, mockSnapcast.Object, db, mockMqtt.Object, mockLogger.Object);

        // Act
        var result = await controller.GetHealth();

        // Assert
        var statusResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, statusResult.StatusCode);

        var info = Assert.IsType<SystemInfoDto>(statusResult.Value);
        Assert.Equal(SystemStatus.Unhealthy, info.Status);
        Assert.False(info.SnapserverConnected);
    }
}
