using System.Threading.Channels;
using KnowledgeHub.Application.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KnowledgeHub.Infrastructure.Email;

/// <summary>
/// <see cref="IEmailSender"/> that returns immediately and delivers on a background worker. Failures are logged
/// (never with the message body, which holds a secret link). Messages are held in memory, so one is lost if the
/// process stops before sending; the user can simply ask again. Moving this onto the outbox is possible later.
/// </summary>
public sealed partial class BackgroundEmailSender(IServiceScopeFactory scopes, ILogger<BackgroundEmailSender> logger)
    : BackgroundService, IEmailSender
{
    private readonly Channel<EmailMessage> _queue = Channel.CreateBounded<EmailMessage>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    public void Send(EmailMessage message)
    {
        if (!_queue.Writer.TryWrite(message)) LogDropped();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var message in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<SmtpEmailTransport>().SendAsync(message, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogFailed(ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Sending an email failed")]
    private partial void LogFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The email queue is full; a message was dropped")]
    private partial void LogDropped();
}
