using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using KnowledgeHub.Application.Common;

namespace KnowledgeHub.Infrastructure.Email;

/// <summary>Sends one message over SMTP (IR-40).</summary>
public sealed class SmtpEmailTransport(IOptions<SmtpOptions> options)
{
    private readonly SmtpOptions _o = options.Value;

    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse(_o.From));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new TextPart("plain") { Text = message.TextBody };

        using var client = new SmtpClient();
        await client.ConnectAsync(_o.Host, _o.Port, _o.StartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None, ct);
        if (!string.IsNullOrEmpty(_o.Username)) await client.AuthenticateAsync(_o.Username, _o.Password ?? "", ct);
        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(true, ct);
    }
}
