namespace KnowledgeHub.Application.Identity;

public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>HS256 signing key (at least 32 bytes). From the environment, never from the repository.</summary>
    public string SigningKey { get; set; } = "";
    /// <summary>Previous key, accepted for validation only during key rotation (Doc 02 §9.1).</summary>
    public string? PreviousSigningKey { get; set; }
    public string Issuer { get; set; } = "knowledge-hub";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 7;
    public int LockoutThreshold { get; set; } = 10;
    public int LockoutWindowMinutes { get; set; } = 15;
    public int LockoutMinutes { get; set; } = 15;
    /// <summary>The account registered with this email becomes the first admin (FR-ADM-04).</summary>
    public string? AdminEmail { get; set; }
    public bool SecureCookies { get; set; } = true;
    /// <summary>Public address of the web app; used in links in emails.</summary>
    public string AppBaseUrl { get; set; } = "http://localhost:5173";
    public int PasswordResetMinutes { get; set; } = 30;
    /// <summary>Deleting an OAuth-only account needs a sign-in this recent (FR-AUTH-09).</summary>
    public int RecentSignInMinutes { get; set; } = 5;

    public RateLimitOptions RateLimits { get; set; } = new();
}

/// <summary>Defaults from NFR-SEC-06.</summary>
public sealed class RateLimitOptions
{
    public int LoginPerIpAndEmailPerMinute { get; set; } = 5;
    public int LoginPerIpPerMinute { get; set; } = 20;
    public int RegisterPerIpPerHour { get; set; } = 5;
    public int ResetPerIpPerHour { get; set; } = 5;
    public int GeneralPerUserPerMinute { get; set; } = 300;
}
