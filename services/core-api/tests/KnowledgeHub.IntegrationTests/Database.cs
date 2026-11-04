using KnowledgeHub.Infrastructure.Persistence;
using Testcontainers.PostgreSql;
using Xunit;

namespace KnowledgeHub.IntegrationTests;

/// <summary>
/// A PostgreSQL prepared like the real one: <c>infra/db/init.sql</c> (schemas, four roles), then the EF Core
/// migrations applied as <c>core_migrator</c>. The API runs as <c>core_app</c>, so missing grants fail tests.
///
/// By default a Testcontainers pgvector image is started (needs Docker). To use an existing server instead, set
/// <c>KH_TEST_MIGRATOR_CONNECTION</c> and <c>KH_TEST_APP_CONNECTION</c> to a database already initialised with init.sql.
/// </summary>
public sealed class Database : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public string MigratorConnection { get; private set; } = "";
    public string AppConnection { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        var migrator = Environment.GetEnvironmentVariable("KH_TEST_MIGRATOR_CONNECTION");
        var app = Environment.GetEnvironmentVariable("KH_TEST_APP_CONNECTION");
        if (migrator is not null && app is not null)
        {
            MigratorConnection = migrator;
            AppConnection = app;
        }
        else
        {
            _container = new PostgreSqlBuilder("pgvector/pgvector:pg17")
                .WithDatabase("knowledge_hub")
                .WithResourceMapping(Path.Combine(AppContext.BaseDirectory, "init.sql"), "/docker-entrypoint-initdb.d/")
                .Build();
            await _container.StartAsync();
            var host = _container.Hostname;
            var port = _container.GetMappedPublicPort(5432);
            MigratorConnection = $"Host={host};Port={port};Database=knowledge_hub;Username=core_migrator;Password=core_migrator_dev";
            AppConnection = $"Host={host};Port={port};Database=knowledge_hub;Username=core_app;Password=core_app_dev";
        }

        await using var db = new AppDbContext(DesignTimeFactory.Options(MigratorConnection));
        await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.MigrateAsync(db.Database);
    }

    /// <summary>A context running as the migrator, for arranging and inspecting data in tests.</summary>
    public AppDbContext Admin() => new(DesignTimeFactory.Options(MigratorConnection));

    public async ValueTask DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }
}
