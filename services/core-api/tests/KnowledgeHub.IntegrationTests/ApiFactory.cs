using KnowledgeHub.Application.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace KnowledgeHub.IntegrationTests;

public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string SigningKey = "integration-test-signing-key-0123456789abcdef";
    public const string AdminEmail = "admin@example.com";

    public Database Db { get; } = new();
    public CapturedEmails Emails { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureServices(services =>
        {
            // Capture emails instead of sending them over SMTP.
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Emails);
        });

    public async ValueTask InitializeAsync()
    {
        await Db.InitializeAsync();
        // Program reads configuration while building, so use environment variables rather than late overrides.
        Environment.SetEnvironmentVariable("ConnectionStrings__Core", Db.AppConnection);
        Environment.SetEnvironmentVariable("Auth__SigningKey", SigningKey);
        Environment.SetEnvironmentVariable("Auth__AdminEmail", AdminEmail);
        // Generous limits so unrelated tests never trip them; the rate-limit tests lower them.
        Environment.SetEnvironmentVariable("Auth__RateLimits__LoginPerIpAndEmailPerMinute", "1000");
        Environment.SetEnvironmentVariable("Auth__RateLimits__LoginPerIpPerMinute", "1000");
        Environment.SetEnvironmentVariable("Auth__RateLimits__RegisterPerIpPerHour", "1000");
        Environment.SetEnvironmentVariable("Auth__RateLimits__ResetPerIpPerHour", "1000");
        Environment.SetEnvironmentVariable("Auth__RateLimits__GeneralPerUserPerMinute", "100000");
    }

    public TestClient NewClient() => new(this);

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await Db.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class ApiTestGroup : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}
