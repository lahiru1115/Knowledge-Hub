using FluentValidation;
using KnowledgeHub.Application.Common;
using KnowledgeHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KnowledgeHub.Application.Identity;

/// <summary>Password reset and change, profile and preferences, account deletion (FR-AUTH-06 to 09).</summary>
public sealed class AccountService(
    IAppDbContext db,
    IPasswordHasher hasher,
    ITokenService tokens,
    IClock clock,
    SessionIssuer sessions,
    AuditLog audit,
    IEmailSender email,
    IOutbox outbox,
    ISessionCache sessionCache,
    IOptions<AuthOptions> options,
    IValidator<PasswordResetRequest> resetRequestValidator,
    IValidator<PasswordResetConfirmRequest> resetConfirmValidator,
    IValidator<ChangePasswordRequest> changePasswordValidator,
    IValidator<UpdateMeRequest> updateMeValidator)
{
    private readonly AuthOptions _options = options.Value;

    /// <summary>
    /// Always succeeds from the caller's point of view. If the account exists and can sign in, a single-use
    /// link is emailed; nothing in the response or its timing reveals whether it exists (FR-AUTH-06).
    /// </summary>
    public async Task RequestPasswordResetAsync(PasswordResetRequest input, CancellationToken ct)
    {
        await resetRequestValidator.EnsureValidAsync(input, ct);
        var address = input.Email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == address, ct);
        if (user is null || user.IsDisabled) return;

        var now = clock.UtcNow;
        var raw = tokens.NewRefreshToken();
        db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = tokens.HashToken(raw),
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(_options.PasswordResetMinutes),
        });
        audit.Write("auth.password_reset_requested", user.Id);
        await db.SaveChangesAsync(ct);

        var link = $"{_options.AppBaseUrl.TrimEnd('/')}/reset-password?token={Uri.EscapeDataString(raw)}";
        email.Send(new EmailMessage(user.Email, "Reset your Knowledge Hub password",
            $"""
            Hi {user.DisplayName},

            Someone asked to reset the password for your Knowledge Hub account. To choose a new password, open this link within {_options.PasswordResetMinutes} minutes:

            {link}

            If this wasn't you, ignore this email. Your password stays the same.
            """));
    }

    public async Task ConfirmPasswordResetAsync(PasswordResetConfirmRequest input, CancellationToken ct)
    {
        await resetConfirmValidator.EnsureValidAsync(input, ct);
        var now = clock.UtcNow;
        var hash = tokens.HashToken(input.Token);
        var token = await db.PasswordResetTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null || !token.IsUsable(now)) throw new AppException(ProblemCode.InvalidToken);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == token.UserId, ct)
            ?? throw new AppException(ProblemCode.InvalidToken);
        if (user.IsDisabled) throw new AppException(ProblemCode.InvalidToken);

        user.SetPassword(hasher.Hash(input.NewPassword), now);
        // Every outstanding link for this user is spent, and every session ends.
        var outstanding = await db.PasswordResetTokens.Where(t => t.UserId == user.Id && t.UsedAt == null).ToListAsync(ct);
        foreach (var t in outstanding) t.UsedAt = now;
        await sessions.RevokeAllAsync(user.Id, now, ct);
        audit.Write("auth.password_reset", user.Id);
        await db.SaveChangesAsync(ct);
        sessionCache.Forget(user.Id);
    }

    /// <summary>
    /// Changes the password. Every other session ends; the current one continues on freshly issued tokens
    /// (its old access token carries the old security stamp), which the caller sets as cookies (FR-AUTH-07).
    /// </summary>
    public async Task<IssuedTokens> ChangePasswordAsync(Guid userId, ChangePasswordRequest input, CancellationToken ct)
    {
        await changePasswordValidator.EnsureValidAsync(input, ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new AppException(ProblemCode.Unauthenticated);
        var now = clock.UtcNow;

        if (user.PasswordHash is null || !hasher.Verify(input.CurrentPassword, user.PasswordHash))
        {
            audit.Write("auth.password_change_failed", user.Id);
            await db.SaveChangesAsync(ct);
            throw new AppException(ProblemCode.InvalidCredentials);
        }

        user.SetPassword(hasher.Hash(input.NewPassword), now);
        await sessions.RevokeAllAsync(user.Id, now, ct);
        var issued = sessions.Issue(user, Guid.CreateVersion7(), now);
        audit.Write("auth.password_changed", user.Id);
        await db.SaveChangesAsync(ct);
        sessionCache.Forget(user.Id);
        return issued;
    }

    public async Task<UserDto> UpdateMeAsync(Guid userId, UpdateMeRequest input, CancellationToken ct)
    {
        await updateMeValidator.EnsureValidAsync(input, ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new AppException(ProblemCode.Unauthenticated);

        if (input.DisplayName is not null) user.DisplayName = input.DisplayName.Trim();
        if (input.Preferences is { } p)
        {
            // Preferences is stored as one jsonb value, so build a new object for change tracking.
            var current = user.Preferences;
            user.Preferences = new UserPreferences
            {
                Theme = p.Theme ?? current.Theme,
                AiAutoSummary = p.AiAutoSummary ?? current.AiAutoSummary,
                AiTagSuggestions = p.AiTagSuggestions ?? current.AiTagSuggestions,
                OnboardingDismissed = p.OnboardingDismissed ?? current.OnboardingDismissed,
            };
        }

        user.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return UserDto.From(user);
    }

    /// <summary>
    /// Deletes the user and all of their core data in one transaction (FR-AUTH-09, DR-03). Only an
    /// <c>account.deleted</c> audit event survives, holding the user ID and no email.
    /// A <c>user.deleted</c> outbox event, written in the same transaction, makes the AI service purge its data.
    /// </summary>
    public async Task DeleteAccountAsync(Guid userId, string? password, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new AppException(ProblemCode.Unauthenticated);
        var now = clock.UtcNow;

        var confirmed = user.PasswordHash is not null
            ? password is not null && hasher.Verify(password, user.PasswordHash)
            : user.LastLoginAt is { } last && now - last <= TimeSpan.FromMinutes(_options.RecentSignInMinutes);
        if (!confirmed)
        {
            audit.Write("account.delete_failed", user.Id);
            await db.SaveChangesAsync(ct);
            throw new AppException(ProblemCode.InvalidCredentials);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.AuditEvents.Where(e => e.UserId == userId).ExecuteDeleteAsync(ct);
        await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(ct); // refresh and reset tokens cascade
        db.ChangeTracker.Clear();
        audit.Write("account.deleted", userId);
        // The AI service removes everything it holds for this user (FR-AUTH-09); written in the same transaction.
        outbox.Add(OutboxEventTypes.UserDeleted, new { user_id = userId });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        sessionCache.Forget(userId);
    }
}
