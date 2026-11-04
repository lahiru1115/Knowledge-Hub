using KnowledgeHub.Application.Common;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace KnowledgeHub.Api.Observability;

public static class Telemetry
{
    /// <summary>
    /// Traces and metrics through OpenTelemetry (Doc 02 §11). Instrumentation always runs; data leaves the process
    /// only when an OTLP endpoint is configured with the standard <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> setting, so a
    /// deployment without a backend keeps just its logs. <c>Telemetry:SampleRatio</c> (default 1) keeps that share of traces.
    /// </summary>
    public static void AddKnowledgeHubTelemetry(this WebApplicationBuilder builder)
    {
        var ratio = Math.Clamp(builder.Configuration.GetValue("Telemetry:SampleRatio", 1.0), 0.0, 1.0);
        var export = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("core-api"))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
                .AddHttpClientInstrumentation()     // adds traceparent to calls to the AI service
                .AddSource("KnowledgeHub.Outbox")
                .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(ratio)))
                .When(export, x => x.AddOtlpExporter()))
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()     // http.server.request.duration: rate, errors and duration per endpoint
                .AddHttpClientInstrumentation()
                .AddMeter(KhMetrics.MeterName)
                .When(export, x => x.AddOtlpExporter()));
        // Logs are deliberately not exported through OpenTelemetry: they go to stdout as redacted JSON only, so
        // nothing reaches a backend without passing LogRedactor (NFR-PRIV-04).
    }

    private static T When<T>(this T builder, bool condition, Func<T, T> apply) => condition ? apply(builder) : builder;
}
