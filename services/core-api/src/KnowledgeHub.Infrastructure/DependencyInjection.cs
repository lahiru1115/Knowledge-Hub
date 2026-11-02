using KnowledgeHub.Application.Common;
using KnowledgeHub.Application.Identity;
using KnowledgeHub.Application.Library;
using KnowledgeHub.Infrastructure.Ai;
using KnowledgeHub.Infrastructure.Library;
using KnowledgeHub.Infrastructure.Maintenance;
using KnowledgeHub.Infrastructure.Metadata;
using KnowledgeHub.Infrastructure.Outbox;
using KnowledgeHub.Infrastructure.Email;
using KnowledgeHub.Infrastructure.Persistence;
using KnowledgeHub.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KnowledgeHub.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Core")
            ?? throw new InvalidOperationException("ConnectionStrings:Core is not configured.");

        services.AddOptions<OutboxOptions>().Bind(configuration.GetSection(OutboxOptions.Section));
        services.AddSingleton<OutboxSignal>();
        services.AddSingleton<OutboxSignalInterceptor>();
        services.AddDbContext<AppDbContext>((sp, o) =>
        {
            ConfigureDb(o, connectionString);
            o.AddInterceptors(sp.GetRequiredService<OutboxSignalInterceptor>());
        });
        services.AddSingleton<OutboxProcessor>();
        services.AddHostedService<OutboxWorker>();
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        services.AddAiClient(configuration);

        services.AddOptions<MaintenanceOptions>().Bind(configuration.GetSection(MaintenanceOptions.Section));
        services.AddScoped<MaintenanceService>();
        services.AddHostedService<MaintenanceWorker>();

        services.AddScoped<IResourceSearch, ResourceSearch>();
        services.AddScoped<ITagsTextSync, TagsTextSync>();

        services.AddOptions<MetadataOptions>().Bind(configuration.GetSection(MetadataOptions.Section));
        services.TryAddSingleton<ISsrfPolicy, SsrfPolicy>();
        services.TryAddSingleton<IHostResolver, SystemHostResolver>();
        services.AddSingleton<TypeGuesser>();
        services.AddSingleton<HtmlMetadataParser>();
        services.TryAddSingleton<IMetadataFetcher, MetadataFetcher>();

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IPasswordHasher, Argon2idPasswordHasher>();
        services.AddSingleton<ICommonPasswords, CommonPasswords>();
        services.AddSingleton<ITokenService, TokenService>();

        services.AddOptions<SmtpOptions>().Bind(configuration.GetSection(SmtpOptions.Section));
        services.AddSingleton<SmtpEmailTransport>();
        services.AddSingleton<BackgroundEmailSender>();
        services.AddSingleton<IEmailSender>(sp => sp.GetRequiredService<BackgroundEmailSender>());
        services.AddHostedService(sp => sp.GetRequiredService<BackgroundEmailSender>());
        return services;
    }

    public static void ConfigureDb(DbContextOptionsBuilder builder, string connectionString) =>
        builder
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", AppDbContext.Schema))
            .UseSnakeCaseNamingConvention();
}

internal sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
