using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using KnowledgeHub.Application.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace KnowledgeHub.Api.Observability;

/// <summary>
/// One JSON object per line with timestamp, level, category, message, <c>trace_id</c>, <c>span_id</c> and
/// <c>user_id</c> where known (NFR-OBS-01). The message and the exception text pass through <see cref="LogRedactor"/>
/// before they are written (NFR-PRIV-04).
/// </summary>
public sealed class KhJsonConsoleFormatter() : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "kh-json";

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (message is null && logEntry.Exception is null) return;

        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("timestamp", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            w.WriteString("level", LevelName(logEntry.LogLevel));
            w.WriteString("category", logEntry.Category);
            w.WriteString("message", LogRedactor.Redact(message));
            if (Activity.Current is { } activity)
            {
                w.WriteString("trace_id", activity.TraceId.ToString());
                w.WriteString("span_id", activity.SpanId.ToString());
                if (UserId(activity) is { } user) w.WriteString("user_id", user);
            }

            if (logEntry.Exception is { } ex)
            {
                w.WriteString("exception_type", ex.GetType().FullName);
                w.WriteString("exception", LogRedactor.Redact(ex.ToString()));
            }

            w.WriteEndObject();
        }

        textWriter.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <summary>The user is tagged on the request's span; a log line may be written from a span below it.</summary>
    private static string? UserId(Activity? activity)
    {
        for (var a = activity; a is not null; a = a.Parent)
            if (a.GetTagItem(UserLogEnrichmentMiddleware.Tag) is string id) return id;
        return null;
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "trace",
        LogLevel.Debug => "debug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "error",
        LogLevel.Critical => "critical",
        _ => "none",
    };
}

/// <summary>Puts the signed-in user's ID on the request's span so log lines can carry it (it is an opaque ID, not an email).</summary>
public sealed class UserLogEnrichmentMiddleware(RequestDelegate next)
{
    public const string Tag = "user_id";

    public Task InvokeAsync(HttpContext http, ICurrentUser user)
    {
        if (user.UserId is { } id) Activity.Current?.SetTag(Tag, id.ToString());
        return next(http);
    }
}
