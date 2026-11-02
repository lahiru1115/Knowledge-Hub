namespace KnowledgeHub.Infrastructure.Ai;

public sealed class AiOptions
{
    public const string Section = "AiService";

    /// <summary>Off by default so the app runs without the AI service; indexing events then complete without calling it.</summary>
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "http://localhost:8000";
    /// <summary>Shared with the AI service (SERVICE_JWT_SECRET), at least 32 bytes.</summary>
    public string JwtSecret { get; set; } = "";
    public string Issuer { get; set; } = "core-api";
    public string Audience { get; set; } = "ai-service";
    public int TokenLifetimeSeconds { get; set; } = 60;
    /// <summary>Indexing embeds every chunk, so it gets longer than the interactive calls of Doc 02 §5.2.</summary>
    public int IndexTimeoutSeconds { get; set; } = 60;
    public int OtherTimeoutSeconds { get; set; } = 10;
    public int Retries { get; set; } = 2;
    public double RetryBaseDelaySeconds { get; set; } = 0.5;
    public int BreakAfterFailures { get; set; } = 5;
    public int BreakSeconds { get; set; } = 30;
}
