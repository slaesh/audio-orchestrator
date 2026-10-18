using AudioOrchestrator.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using AudioOrchestrator.Hubs;
using AudioOrchestrator.Contracts;

namespace AudioOrchestrator.Services;

public class StartupReconciliationService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<StartupReconciliationService> _logger;
    private readonly IHostApplicationLifetime _appLifetime;

    public StartupReconciliationService(
        IServiceProvider serviceProvider, 
        ILogger<StartupReconciliationService> logger,
        IHostApplicationLifetime appLifetime)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _appLifetime = appLifetime;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Wait for the app to be fully started before reconciling once.
        _appLifetime.ApplicationStarted.Register(() => 
        {
            Task.Run(async () => 
            {
                try 
                {
                    using var scope = _serviceProvider.CreateScope();
                    var reconciliationService = scope.ServiceProvider.GetRequiredService<IReconciliationService>();
                    await reconciliationService.ReconcileAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Startup reconciliation failed.");
                }
            });
        });

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
