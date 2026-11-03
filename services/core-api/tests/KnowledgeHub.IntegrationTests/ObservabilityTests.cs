using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using KnowledgeHub.Api.Observability;
using KnowledgeHub.Application.Common;
using KnowledgeHub.Domain;
using KnowledgeHub.Infrastructure.Outbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnowledgeHub.IntegrationTests;

/// <summary>Collects what the application's meter reports.</summary>
public sealed class MeterCapture : IDisposable
{
    private readonly MeterListener _listener = new();
    public List<(string Name, double Value, Dictionary<string, object?> Tags)> Measurements { get; } = [];

    public MeterCapture()
    {
        _listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == KhMetrics.MeterName) l.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<double>(Record);
        _listener.SetMeasurementEventCallback<long>((i, v, t, s) => Record(i, v, t, s));
        _listener.SetMeasurementEventCallback<int>((i, v, t, s) => Record(i, v, t, s));
        _listener.Start();
    }

    private void Record(Instrument i, double v, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? _)
    {
        var d = tags.ToArray().ToDictionary(t => t.Key, t => t.Value);
        lock (Measurements) Measurements.Add((i.Name, v, d));
    }

    /// <summary>Reads the gauges now.</summary>
    public double Gauge(string name)
    {
        lock (Measurements) Measurements.Clear();
        _listener.RecordObservableInstruments();
        lock (Measurements) return Measurements.Where(m => m.Name == name).Select(m => m.Value).DefaultIfEmpty(double.NaN).Last();
    }

    public List<(string Name, double Value, Dictionary<string, object?> Tags)> Of(string name)
    {
        lock (Measurements) return [.. Measurements.Where(m => m.Name == name)];
    }

    public void Dispose() => _listener.Dispose();
}

public class LogFormatterTests
{
    private static JsonElement Write(Exception? ex, string message)
    {
        using var source = new ActivitySource("test.logs");
        using var listener = new ActivityListener { ShouldListenTo = _ => true, Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData };
        ActivitySource.AddActivityListener(listener);
        using var parent = source.StartActivity("request");
        parent!.SetTag(UserLogEnrichmentMiddleware.Tag, "0193a000-0000-7000-8000-000000000001");
        using var child = source.StartActivity("job");   // a line written from a span below the request still names the user
        var writer = new StringWriter();
        new KhJsonConsoleFormatter().Write(new LogEntry<string>(LogLevel.Warning, "KnowledgeHub.Test", new EventId(1), message, ex, (s, _) => s), null, writer);
        var line = writer.ToString().TrimEnd();
        Assert.DoesNotContain('\n', line);   // one line per entry
        return JsonDocument.Parse(line).RootElement.Clone();
    }

    [Fact]
    public void A_line_is_one_json_object_with_the_trace_and_the_user()
    {
        var json = Write(null, "Outbox message failed");
        Assert.Equal(("warn", "KnowledgeHub.Test", "Outbox message failed"), (json.GetProperty("level").GetString(), json.GetProperty("category").GetString(), json.GetProperty("message").GetString()));
        Assert.Equal(32, json.GetProperty("trace_id").GetString()!.Length);
        Assert.Equal(16, json.GetProperty("span_id").GetString()!.Length);
        Assert.Equal("0193a000-0000-7000-8000-000000000001", json.GetProperty("user_id").GetString());
        Assert.True(json.GetProperty("timestamp").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void The_message_and_the_exception_are_redacted_before_they_are_written()
    {
        var json = Write(new InvalidOperationException("bad token=abc123 for ada@example.com"), "Sign-in failed for ada@example.com with password=hunter2");
        var all = json.GetRawText();
        Assert.DoesNotContain("ada@example.com", all, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", all, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", all, StringComparison.Ordinal);
        Assert.Contains("a***@example.com", json.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal("System.InvalidOperationException", json.GetProperty("exception_type").GetString());
    }
}

[Collection(ApiTestGroup.Name)]
public class ObservabilityTests(ApiFactory factory)
{
    private sealed class CaptureProvider : ILoggerProvider, ILogger
    {
        public List<string> Lines { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add(formatter(state, exception) + " " + exception);
        }
    }

    [Fact]
    public async Task Logs_written_while_people_sign_in_fail_and_save_things_hold_no_credentials_or_content()
    {
        var logs = new CaptureProvider();
        using var web = factory.WithWebHostBuilder(b => b.ConfigureLogging(l =>
        {
            // Everything, framework and database included: the strictest version of the check.
            l.ClearProviders().AddProvider(logs).SetMinimumLevel(LogLevel.Trace);
            l.AddFilter("Microsoft", LogLevel.Trace);
        }));
        var c = new TestClient(web);
        var email = TestClient.UniqueEmail();
        const string pass = "a long unusual passphrase";
        await c.RegisterAsync(email, pass);
        await c.LoginAsync(email, "the wrong password entirely");
        await c.LoginAsync(email, pass);
        await c.CreateResourceAsync("https://example.com/private", new { title = "Plain title", notes = "my very private journal entry" });
        await c.PostAsync("/api/v1/auth/password-reset", new { email });
        await c.GetAsync("/api/v1/resources?q=journal");
        var session = c.LastRefreshCookie;

        string text;
        lock (logs.Lines)
        {
            Assert.True(logs.Lines.Count > 5, "The test captured too little to mean anything");
            text = string.Join('\n', logs.Lines);
        }

        foreach (var secret in new[] { pass, "the wrong password entirely", "my very private journal entry", email })
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        if (session is not null) Assert.DoesNotContain(session, text, StringComparison.Ordinal);
    }

    private sealed class Harness(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> web, ScriptedHandler handler) : IDisposable
    {
        public ScriptedHandler Handler { get; } = handler;
        public OutboxProcessor Processor => web.Services.GetRequiredService<OutboxProcessor>();
        public void Dispose() => web.Dispose();
    }

    private async Task<Harness> ArrangeAsync()
    {
        await using (var db = factory.Db.Admin())
            await db.Outbox.Where(m => m.Status == OutboxStatus.Pending || m.Status == OutboxStatus.Processing).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        var handler = new ScriptedHandler();
        var web = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Outbox:Enabled", "false");
            b.UseSetting("Outbox:BaseBackoffSeconds", "0.05");
            b.UseSetting("Outbox:MaxAttempts", "2");
            b.ConfigureServices(s => s.AddSingleton<IOutboxHandler>(handler));
        });
        _ = web.Server;   // build the host
        return new Harness(web, handler);
    }

    private async Task InsertAsync(OutboxStatus status = OutboxStatus.Pending, int attempts = 0, DateTimeOffset? due = null)
    {
        await using var db = factory.Db.Admin();
        var now = DateTimeOffset.UtcNow;
        db.Outbox.Add(new OutboxMessage { Type = ScriptedHandler.Type, Payload = "{}", NextAttemptAt = due ?? now.AddSeconds(-1), CreatedAt = now, Status = status, Attempts = attempts });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_job_that_succeeds_and_one_that_fails_are_both_timed_with_their_outcome()
    {
        using var h = await ArrangeAsync();
        using var meter = new MeterCapture();
        await InsertAsync();
        await h.Processor.RunOnceAsync(TestContext.Current.CancellationToken);
        h.Handler.Behaviour = _ => throw new InvalidOperationException("boom");
        await InsertAsync();
        await h.Processor.RunOnceAsync(TestContext.Current.CancellationToken);
        await InsertAsync(attempts: 1);   // its second and last attempt
        await h.Processor.RunOnceAsync(TestContext.Current.CancellationToken);

        var outcomes = meter.Of("kh.outbox.job.duration").Select(m => (string?)m.Tags["outcome"]).ToList();
        Assert.Equal(["ok", "retry", "dead"], outcomes);
        Assert.All(meter.Of("kh.outbox.job.duration"), m => { Assert.Equal(ScriptedHandler.Type, m.Tags["type"]); Assert.True(m.Value >= 0); });
    }

    [Fact]
    public async Task The_backlog_gauges_follow_the_pending_and_dead_messages()
    {
        using var h = await ArrangeAsync();
        using var meter = new MeterCapture();
        await h.Processor.RunOnceAsync(TestContext.Current.CancellationToken);
        var (pending0, dead0) = (meter.Gauge("kh.outbox.pending"), meter.Gauge("kh.outbox.dead"));

        await InsertAsync(due: DateTimeOffset.UtcNow.AddHours(1));   // waits, so it stays pending
        await InsertAsync(due: DateTimeOffset.UtcNow.AddHours(1));
        await InsertAsync(status: OutboxStatus.Dead);
        await h.Processor.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal((pending0 + 2, dead0 + 1), (meter.Gauge("kh.outbox.pending"), meter.Gauge("kh.outbox.dead")));
    }
}
