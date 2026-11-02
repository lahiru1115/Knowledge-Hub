using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KnowledgeHub.Infrastructure.Outbox;

/// <summary>Runs the outbox processor in the Core API process, so no third container is needed (ADR-004).</summary>
public sealed partial class OutboxWorker(
    OutboxProcessor processor,
    OutboxSignal signal,
    IOptions<OutboxOptions> options,
    ILogger<OutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // A full batch means there is probably more waiting, so go again without sleeping.
                if (await processor.RunOnceAsync(stoppingToken) >= o.BatchSize) continue;
                await signal.WaitAsync(TimeSpan.FromSeconds(o.PollSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogLoopFailed(ex);
                try { await Task.Delay(TimeSpan.FromSeconds(o.PollSeconds), stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The outbox worker loop failed; retrying")]
    private partial void LogLoopFailed(Exception exception);
}
