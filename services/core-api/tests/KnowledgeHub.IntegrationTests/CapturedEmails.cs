using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using KnowledgeHub.Application.Common;
using Xunit;

namespace KnowledgeHub.IntegrationTests;

/// <summary>Stands in for the email sender so tests can read what would have been sent.</summary>
public sealed partial class CapturedEmails : IEmailSender
{
    private readonly ConcurrentQueue<EmailMessage> _sent = new();

    public void Send(EmailMessage message) => _sent.Enqueue(message);

    public IReadOnlyList<EmailMessage> To(string address) =>
        [.. _sent.Where(m => string.Equals(m.To, address, StringComparison.OrdinalIgnoreCase))];

    /// <summary>The reset token from the most recent email to <paramref name="address"/>.</summary>
    public string ResetToken(string address)
    {
        var message = To(address)[^1];
        var match = TokenPattern().Match(message.TextBody);
        Assert.True(match.Success, "no reset link in the email");
        return Uri.UnescapeDataString(match.Groups[1].Value);
    }

    [GeneratedRegex(@"/reset-password\?token=(\S+)")]
    private static partial Regex TokenPattern();
}
