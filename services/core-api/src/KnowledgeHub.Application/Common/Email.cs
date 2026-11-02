namespace KnowledgeHub.Application.Common;

public sealed record EmailMessage(string To, string Subject, string TextBody);

/// <summary>
/// Hands an email to a background worker and returns at once, so the time a request takes does not reveal
/// whether an address has an account (FR-AUTH-06). Delivery failures are logged, not shown to the caller.
/// </summary>
public interface IEmailSender
{
    void Send(EmailMessage message);
}

/// <summary>Drops the cached security-stamp snapshot so a stamp change takes effect immediately in this process.</summary>
public interface ISessionCache
{
    void Forget(Guid userId);
}
