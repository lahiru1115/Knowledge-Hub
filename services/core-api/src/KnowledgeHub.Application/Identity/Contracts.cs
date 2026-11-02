using KnowledgeHub.Domain;

namespace KnowledgeHub.Application.Identity;

public sealed record RegisterRequest(string Email, string Password, string DisplayName);

public sealed record LoginRequest(string Email, string Password);

public sealed record PreferencesDto(ThemePreference Theme, bool AiAutoSummary, bool AiTagSuggestions, bool OnboardingDismissed);

public sealed record UserDto(
    Guid Id, string Email, string DisplayName, UserRole Role, PreferencesDto Preferences, DateTimeOffset CreatedAt)
{
    public static UserDto From(User u) => new(
        u.Id, u.Email, u.DisplayName, u.Role,
        new PreferencesDto(u.Preferences.Theme, u.Preferences.AiAutoSummary, u.Preferences.AiTagSuggestions, u.Preferences.OnboardingDismissed),
        u.CreatedAt);
}

public sealed record IssuedTokens(string AccessToken, DateTimeOffset AccessExpiresAt, string RefreshToken, DateTimeOffset RefreshExpiresAt);

public sealed record AuthResult(UserDto User, IssuedTokens Tokens);

public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt);

public sealed record PasswordResetRequest(string Email);

public sealed record PasswordResetConfirmRequest(string Token, string NewPassword);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record DeleteAccountRequest(string? Password);

/// <summary>Absent fields are left unchanged (merge semantics, Doc 02 §8.3).</summary>
public sealed record PreferencesUpdate(ThemePreference? Theme, bool? AiAutoSummary, bool? AiTagSuggestions, bool? OnboardingDismissed);

public sealed record UpdateMeRequest(string? DisplayName, PreferencesUpdate? Preferences);
