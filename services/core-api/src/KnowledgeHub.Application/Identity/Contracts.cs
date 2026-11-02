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
