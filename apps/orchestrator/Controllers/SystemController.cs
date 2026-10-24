using System.Diagnostics;
using System.Reflection;
using AudioOrchestrator.Contracts;
using AudioOrchestrator.Data;
using AudioOrchestrator.Services;
using Microsoft.AspNetCore.Mvc;

namespace AudioOrchestrator.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SystemController : ControllerBase
{
    private readonly IWebHostEnvironment _env;
    private readonly ISnapcastService _snapcast;
    private readonly OrchestratorDbContext _db;
    private readonly IHomeAssistantMqttService _mqttService;
    private readonly ILogger<SystemController> _logger;

    public SystemController(
        IWebHostEnvironment env,
        ISnapcastService snapcast,
        OrchestratorDbContext db,
        IHomeAssistantMqttService mqttService,
        ILogger<SystemController> logger)
    {
        _env = env;
        _snapcast = snapcast;
        _db = db;
        _mqttService = mqttService;
        _logger = logger;
    }

    [HttpGet("info")]
    [ProducesResponseType(typeof(SystemInfoDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<SystemInfoDto>> GetSystemInfo()
    {
        var info = await BuildSystemInfoDtoAsync();
        return Ok(info);
    }

    [HttpGet("/health")]
    [HttpGet("/api/health")]
    [ProducesResponseType(typeof(SystemInfoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(SystemInfoDto), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<SystemInfoDto>> GetHealth()
    {
        var info = await BuildSystemInfoDtoAsync();
        if (info.Status == SystemStatus.Unhealthy)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, info);
        }

        return Ok(info);
    }

    private async Task<SystemInfoDto> BuildSystemInfoDtoAsync()
    {
        var version = GetAssemblyVersion();
        var startTimeUtc = GetProcessStartTimeUtc();
        var uptime = DateTime.UtcNow - startTimeUtc;
        if (uptime < TimeSpan.Zero) uptime = TimeSpan.Zero;

        bool dbConnected = false;
        try
        {
            dbConnected = await _db.Database.CanConnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Database connectivity check failed.");
        }

        bool snapConnected = _snapcast.IsConnected;
        bool mqttConnected = _mqttService.IsConnected;

        var status = DetermineStatus(dbConnected, snapConnected, mqttConnected);

        return new SystemInfoDto(
            Status: status,
            Version: version,
            Environment: _env.EnvironmentName,
            StartTimeUtc: startTimeUtc,
            UptimeSeconds: (long)uptime.TotalSeconds,
            UptimeFormatted: FormatUptime(uptime),
            SnapserverConnected: snapConnected,
            DatabaseConnected: dbConnected,
            MqttConnected: mqttConnected
        );
    }

    private static SystemStatus DetermineStatus(bool dbConnected, bool snapConnected, bool mqttConnected)
    {
        if (!dbConnected || !snapConnected)
        {
            return SystemStatus.Unhealthy;
        }

        return SystemStatus.Healthy;
    }

    private static string GetAssemblyVersion()
    {
        var assembly = typeof(SystemController).Assembly;
        var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVersion))
        {
            var plusIdx = infoVersion.IndexOf('+');
            return plusIdx > 0 ? infoVersion[..plusIdx] : infoVersion;
        }

        var ver = assembly.GetName().Version;
        return ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "unknown";
    }

    private static DateTime GetProcessStartTimeUtc()
    {
        try
        {
            return Process.GetCurrentProcess().StartTime.ToUniversalTime();
        }
        catch
        {
            return DateTime.UtcNow;
        }
    }

    private static string FormatUptime(TimeSpan ts)
    {
        if (ts.TotalDays >= 1)
        {
            return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m {ts.Seconds}s";
        }
        if (ts.TotalHours >= 1)
        {
            return $"{ts.Hours}h {ts.Minutes}m {ts.Seconds}s";
        }
        if (ts.TotalMinutes >= 1)
        {
            return $"{ts.Minutes}m {ts.Seconds}s";
        }
        return $"{ts.Seconds}s";
    }
}
