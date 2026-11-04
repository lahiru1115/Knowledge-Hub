using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace KnowledgeHub.Infrastructure.Persistence;

/// <summary>Used by `dotnet ef` and the migration bundle. Runs as core_migrator (Doc 02 §7.4).</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("CORE_MIGRATOR_CONNECTION")
            ?? "Host=localhost;Database=knowledge_hub;Username=core_migrator;Password=core_migrator_dev";
        return new AppDbContext(Options(cs));
    }

    public static DbContextOptions<AppDbContext> Options(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        DependencyInjection.ConfigureDb(builder, connectionString);
        return builder.Options;
    }
}
