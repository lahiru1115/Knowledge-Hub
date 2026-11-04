using KnowledgeHub.Application.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KnowledgeHub.Infrastructure.Maintenance;

/// <summary>Runs the housekeeping on a timer (hourly by default), starting a minute after the app does.</summary>
public sealed partial class MaintenanceWorker(
    IServiceScopeFactory scopes, IClock clock, IOptions<MaintenanceOptions> options, ILogger<MaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.Enabled) return;
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(o.IntervalMinutes));
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<MaintenanceService>().RunOnceAsync(clock.UtcNow, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogFailed(ex);   // try again next hour
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Maintenance run failed")]
    private partial void LogFailed(Exception exception);
}
