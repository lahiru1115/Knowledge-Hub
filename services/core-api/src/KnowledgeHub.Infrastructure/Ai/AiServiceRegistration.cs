using KnowledgeHub.Application.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;

namespace KnowledgeHub.Infrastructure.Ai;

public static class AiServiceRegistration
{
    /// <summary>
    /// Registers the AI client. With <c>AiService:Enabled</c> off it is a no-op client. Otherwise calls retry twice
    /// with backoff on 5xx, timeouts and connection errors (all calls are idempotent), and a circuit breaker opens
    /// after 5 failures for 30 s so a down service costs nothing (Doc 02 §5.5).
    /// </summary>
    public static IServiceCollection AddAiClient(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AiOptions>().Bind(configuration.GetSection(AiOptions.Section));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ServiceTokenFactory>();

        var options = configuration.GetSection(AiOptions.Section).Get<AiOptions>() ?? new AiOptions();
        if (!options.Enabled)
        {
            services.AddSingleton<IAiClient, DisabledAiClient>();
            return services;
        }
        if (System.Text.Encoding.UTF8.GetByteCount(options.JwtSecret) < 32)
            throw new InvalidOperationException("AiService:JwtSecret must be at least 32 bytes when AiService:Enabled is true.");

        services.AddHttpClient<IAiClient, AiClient>(c => c.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"))
            .AddResilienceHandler("ai-service", (builder, context) =>
            {
                var o = context.ServiceProvider.GetRequiredService<IOptions<AiOptions>>().Value;
                if (o.Retries > 0)   // the strategy refuses zero attempts
                    builder.AddRetry(new HttpRetryStrategyOptions
                    {
                        MaxRetryAttempts = o.Retries,
                        BackoffType = DelayBackoffType.Exponential,
                        UseJitter = true,
                        Delay = TimeSpan.FromSeconds(o.RetryBaseDelaySeconds),
                    });
                builder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                {
                    FailureRatio = 1.0,
                    MinimumThroughput = o.BreakAfterFailures,
                    SamplingDuration = TimeSpan.FromSeconds(Math.Max(o.BreakSeconds, 1)),
                    BreakDuration = TimeSpan.FromSeconds(o.BreakSeconds),
                });
            });
        return services;
    }
}
