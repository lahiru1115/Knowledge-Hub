using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace KnowledgeHub.Application.Common;

/// <summary>
/// The application's own measurements (NFR-OBS-03, Doc 02 §11). Request rate, errors and duration per endpoint come
/// from the web server's built-in <c>http.server.request.duration</c>; these are the ones it cannot know about.
/// They cost nothing until an exporter or listener subscribes to <see cref="MeterName"/>.
/// </summary>
public static class KhMetrics
{
    public const string MeterName = "KnowledgeHub";
    private static readonly Meter s_meter = new(MeterName);

    private static int s_outboxPending;
    private static int s_outboxDead;

    /// <summary>Jobs waiting to run or retry (<c>kh.outbox.pending</c>).</summary>
    public static readonly ObservableGauge<int> OutboxPending =
        s_meter.CreateObservableGauge("kh.outbox.pending", () => Volatile.Read(ref s_outboxPending), description: "Outbox messages waiting");

    /// <summary>Jobs that ran out of attempts and need a person (<c>kh.outbox.dead</c>).</summary>
    public static readonly ObservableGauge<int> OutboxDead =
        s_meter.CreateObservableGauge("kh.outbox.dead", () => Volatile.Read(ref s_outboxDead), description: "Outbox messages that failed for good");

    public static readonly Histogram<double> OutboxJobDuration =
        s_meter.CreateHistogram<double>("kh.outbox.job.duration", "s", "How long an outbox job took, by type and outcome");

    public static readonly Counter<long> MetadataFetches =
        s_meter.CreateCounter<long>("kh.metadata.fetch", description: "Page metadata fetches, by outcome");

    public static readonly Counter<long> AiCalls =
        s_meter.CreateCounter<long>("kh.ai.calls", description: "Calls from the Core API to the AI service, by operation and outcome");

    public static readonly Histogram<double> AiCallDuration =
        s_meter.CreateHistogram<double>("kh.ai.call.duration", "s", "How long a call to the AI service took");

    public static void SetOutboxBacklog(int pending, int dead)
    {
        Volatile.Write(ref s_outboxPending, pending);
        Volatile.Write(ref s_outboxDead, dead);
    }

    /// <summary>Starts a stopwatch for the duration histograms.</summary>
    public static long Start() => Stopwatch.GetTimestamp();

    public static double Seconds(long start) => Stopwatch.GetElapsedTime(start).TotalSeconds;
}
