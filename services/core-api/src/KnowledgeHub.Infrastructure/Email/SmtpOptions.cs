namespace KnowledgeHub.Infrastructure.Email;

public sealed class SmtpOptions
{
    public const string Section = "Smtp";

    /// <summary>Mailpit in development (localhost:1025); a transactional email provider in the demo.</summary>
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1025;
    public string From { get; set; } = "Knowledge Hub <no-reply@knowledge-hub.local>";
    /// <summary>Use STARTTLS when the server offers it. Off for Mailpit, on for real providers.</summary>
    public bool StartTls { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
}
