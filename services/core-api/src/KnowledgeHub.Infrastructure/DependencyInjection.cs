using KnowledgeHub.Application.Common;
using KnowledgeHub.Application.Identity;
using KnowledgeHub.Application.Library;
using KnowledgeHub.Infrastructure.Library;
using KnowledgeHub.Infrastructure.Email;
using KnowledgeHub.Infrastructure.Persistence;
using KnowledgeHub.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeHub.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Core")
            ?? throw new InvalidOperationException("ConnectionStrings:Core is not configured.");

        services.AddDbContext<AppDbContext>(o => ConfigureDb(o, connectionString));
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        services.AddScoped<IResourceSearch, ResourceSearch>();
        services.AddScoped<ITagsTextSync, TagsTextSync>();

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
