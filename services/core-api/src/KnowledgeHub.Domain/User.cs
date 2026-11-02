namespace KnowledgeHub.Domain;

public enum UserRole { Member, Admin }

public enum ThemePreference { System, Light, Dark }

/// <summary>Stored as jsonb on the user (Doc 01 data dictionary).</summary>
public sealed class UserPreferences
{
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public bool AiAutoSummary { get; set; } = true;
    public bool AiTagSuggestions { get; set; } = true;
    public bool OnboardingDismissed { get; set; }
}

public sealed class User
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public string Email { get; set; } = "";
    /// <summary>Null for OAuth-only accounts.</summary>
    public string? PasswordHash { get; set; }
    public string DisplayName { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.Member;
    public bool IsDisabled { get; set; }
    public UserPreferences Preferences { get; set; } = new();
    /// <summary>Replaced when the user is disabled or changes/resets their password (Doc 02 §9.1).</summary>
    public Guid SecurityStamp { get; set; } = Guid.NewGuid();
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? FailedLoginWindowStartedAt { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsLocked(DateTimeOffset now) => LockedUntil is { } until && until > now;

    /// <summary>Records a failed sign-in; locks the account when the threshold is reached (FR-AUTH-02).</summary>
    public void RegisterFailedLogin(DateTimeOffset now, int threshold, TimeSpan window, TimeSpan lockDuration)
    {
        if (FailedLoginWindowStartedAt is not { } start || now - start > window)
        {
            FailedLoginWindowStartedAt = now;
            FailedLoginCount = 0;
        }

        FailedLoginCount++;
        if (FailedLoginCount >= threshold)
        {
            LockedUntil = now + lockDuration;
            FailedLoginCount = 0;
            FailedLoginWindowStartedAt = null;
        }
    }

    public void RegisterSuccessfulLogin(DateTimeOffset now)
    {
        FailedLoginCount = 0;
        FailedLoginWindowStartedAt = null;
        LockedUntil = null;
        LastLoginAt = now;
    }
}
