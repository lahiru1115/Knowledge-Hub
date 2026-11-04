namespace KnowledgeHub.Infrastructure.Outbox;

/// <summary>Worker settings. Defaults are from Doc 02 §5.3 and SRS Appendix C; tests shorten the times.</summary>
public sealed class OutboxOptions
{
    public const string Section = "Outbox";

    /// <summary>Turn the background worker off (for example in a migration-only run).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>How often the worker looks for due messages when nothing wakes it earlier.</summary>
    public double PollSeconds { get; set; } = 2;
    /// <summary>How often the kh.outbox.pending and kh.outbox.dead gauges are re-read from the table.</summary>
    public double BacklogMetricSeconds { get; set; } = 15;
    /// <summary>Messages claimed and run at once.</summary>
    public int BatchSize { get; set; } = 10;
    /// <summary>After this many failed attempts a message is dead (NFR-REL-02).</summary>
    public int MaxAttempts { get; set; } = 8;
    /// <summary>First retry delay; each further failure doubles it, with jitter.</summary>
    public double BaseBackoffSeconds { get; set; } = 10;
    /// <summary>A claim older than this means the worker died; the message goes back to pending.</summary>
    public double StuckMinutes { get; set; } = 5;
}
