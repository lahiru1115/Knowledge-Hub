using System.Net;
using KnowledgeHub.Application.Library;
using KnowledgeHub.Domain;
using KnowledgeHub.Infrastructure.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace KnowledgeHub.UnitTests;

/// <summary>Lets the tests reach their own loopback server (any port) while every other rule stays the real one.</summary>
internal sealed class LoopbackAllowedPolicy : ISsrfPolicy
{
    private readonly SsrfPolicy _real = new();
    public MetadataError? CheckUri(Uri uri) =>
        uri.IdnHost is "127.0.0.1" or "site.test" && uri.Scheme == "http" ? null : _real.CheckUri(uri);
    public bool IsAddressAllowed(IPAddress a) => IPAddress.IsLoopback(a) || _real.IsAddressAllowed(a);
}

/// <summary>The fetcher against a real local web server: what it reads, and what it refuses.</summary>
public sealed class MetadataFetcherTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private string _base = "";

    private sealed class MapResolver : IHostResolver
    {
        public int Calls;
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(host is "site.test" or "127.0.0.1"
                ? new[] { IPAddress.Loopback } : new[] { IPAddress.Parse("10.1.1.1") });   // evil.test → internal
        }
    }

    private const string Page = """
        <html lang="en"><head>
        <title>  Plain   title </title>
        <meta property="og:title" content="Open Graph Title">
        <meta property="og:description" content="A short description.">
        <meta property="og:image" content="/img/cover.png">
        <meta property="og:site_name" content="Example Site">
        <link rel="icon" href="/static/icon.png">
        </head><body><article><h1>Heading</h1>
        <p>Lorem ipsum dolor sit amet, consectetur adipiscing elit. Sed do eiusmod tempor incididunt ut labore et dolore magna aliqua.
        Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat.</p>
        <p>Duis aute irure dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat nulla pariatur.
        Excepteur sint occaecat cupidatat non proident, sunt in culpa qui officia deserunt mollit anim id est laborum.</p>
        </article></body></html>
        """;

    public async ValueTask InitializeAsync()
    {
        var b = WebApplication.CreateBuilder();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Logging.ClearProviders();
        _app = b.Build();
        _app.MapGet("/page", () => Results.Content(Page, "text/html; charset=utf-8"));
        _app.MapGet("/bare", () => Results.Content("<html><head><title>Only a title</title></head><body>hi</body></html>", "text/html"));
        _app.MapGet("/redirect", () => Results.Redirect("/page"));
        _app.MapGet("/loop", () => Results.Redirect("/loop"));
        _app.MapGet("/chain/{n:int}", (int n) => Results.Redirect(n == 0 ? "/page" : $"/chain/{n - 1}"));
        _app.MapGet("/to-metadata", () => Results.Redirect("http://169.254.169.254/latest/meta-data/"));
        _app.MapGet("/to-localhost", () => Results.Redirect("http://localhost/admin"));
        _app.MapGet("/to-file", () => Results.Redirect("file:///etc/passwd"));
        _app.MapGet("/to-private-name", () => Results.Redirect("http://evil.test/page"));
        _app.MapGet("/to-site", () => Results.Redirect($"http://site.test:{new Uri(_base).Port}/page"));
        _app.MapGet("/json", () => Results.Json(new { a = 1 }));
        _app.MapGet("/pdf", () => Results.File([37, 80, 68, 70], "application/pdf"));
        _app.MapGet("/files/My_Great-Paper.pdf", () => Results.File([37, 80, 68, 70], "application/pdf"));
        _app.MapGet("/png", () => Results.File([1, 2, 3], "image/png"));
        _app.MapGet("/missing", () => Results.StatusCode(404));
        _app.MapGet("/boom", () => Results.StatusCode(503));
        _app.MapGet("/slow", async (HttpContext c) => { await Task.Delay(4000, c.RequestAborted); return Results.Content("<title>late</title>", "text/html"); });
        _app.MapGet("/big", async (HttpContext c) =>
        {
            c.Response.ContentType = "text/html";
            var chunk = new string('a', 1024);
            for (var i = 0; i < 64; i++) await c.Response.WriteAsync(chunk);   // no Content-Length: chunked
        });
        _app.MapGet("/declared-big", async (HttpContext c) =>
        {
            c.Response.ContentType = "text/html";
            c.Response.ContentLength = 50_000_000;
            await c.Response.StartAsync();   // the headers promise 50 MB; the fetcher must not wait for them
            await Task.Delay(500, c.RequestAborted);
        });
        await _app.StartAsync();
        _base = _app.Urls.First();
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private static MetadataFetcher Fetcher(Action<MetadataOptions>? configure = null, MapResolver? resolver = null, ISsrfPolicy? policy = null)
    {
        var o = new MetadataOptions();
        configure?.Invoke(o);
        var opts = Options.Create(o);
        return new MetadataFetcher(policy ?? new LoopbackAllowedPolicy(), resolver ?? new MapResolver(), opts,
            new HtmlMetadataParser(new TypeGuesser(opts)));
    }

    private string U(string path) => _base + path;

    [Fact]
    public async Task Reads_the_details_of_a_page()
    {
        using var f = Fetcher();
        var r = await f.FetchAsync(U("/page"), CancellationToken.None);
        Assert.Null(r.Error);
        var m = r.Metadata!;
        Assert.Equal("Open Graph Title", m.Title);
        Assert.Equal("A short description.", m.Description);
        Assert.Equal(U("/img/cover.png"), m.ImageUrl);
        Assert.Equal(U("/static/icon.png"), m.FaviconUrl);
        Assert.Equal("Example Site", m.SiteName);
        Assert.Contains("Lorem ipsum", m.Text);
    }

    [Fact]
    public async Task Falls_back_to_the_title_tag_and_the_default_favicon()
    {
        using var f = Fetcher();
        var m = (await f.FetchAsync(U("/bare"), CancellationToken.None)).Metadata!;
        Assert.Equal("Only a title", m.Title);
        Assert.Null(m.Description);
        Assert.Null(m.ImageUrl);
        Assert.Equal(U("/favicon.ico"), m.FaviconUrl);
        Assert.Equal(ResourceType.Website, m.Type);
    }

    [Fact]
    public async Task Follows_redirects_up_to_the_limit()
    {
        using var f = Fetcher();
        Assert.Null((await f.FetchAsync(U("/redirect"), CancellationToken.None)).Error);
        Assert.Null((await f.FetchAsync(U("/chain/4"), CancellationToken.None)).Error);      // 5 hops
        Assert.Equal(MetadataError.TooManyRedirects, (await f.FetchAsync(U("/chain/5"), CancellationToken.None)).Error);  // 6 hops
        Assert.Equal(MetadataError.TooManyRedirects, (await f.FetchAsync(U("/loop"), CancellationToken.None)).Error);
    }

    [Theory]
    [InlineData("/to-metadata")]
    [InlineData("/to-localhost")]
    [InlineData("/to-file")]
    [InlineData("/to-private-name")]
    public async Task Refuses_a_redirect_to_a_blocked_place(string path)
    {
        // /to-private-name: a public-looking name that resolves to 10.1.1.1.
        using var f = Fetcher(policy: new RealRulesExceptOurServer());
        var r = await f.FetchAsync(U(path), CancellationToken.None);
        Assert.Equal(MetadataError.BlockedUrl, r.Error);
        Assert.False(r.Transient);
    }

    /// <summary>Like production, but our own server's exact address may be used.</summary>
    private sealed class RealRulesExceptOurServer : ISsrfPolicy
    {
        private readonly SsrfPolicy _real = new();
        public MetadataError? CheckUri(Uri uri) => uri.IdnHost == "127.0.0.1" ? null : _real.CheckUri(uri);
        public bool IsAddressAllowed(IPAddress a) => a.Equals(IPAddress.Loopback) || _real.IsAddressAllowed(a);
    }

    [Fact]
    public async Task A_name_that_resolves_to_a_private_address_is_blocked_at_connection_time()
    {
        // The real policy: the name looks fine, but its address is internal.
        var resolver = new MapResolver();
        using var f = Fetcher(resolver: resolver, policy: new SsrfPolicy());
        var r = await f.FetchAsync("http://evil.test/page", CancellationToken.None);
        Assert.Equal(MetadataError.BlockedUrl, r.Error);
        Assert.Equal(1, resolver.Calls);   // resolved once, judged once; nothing was connected
    }

    [Fact]
    public async Task Connects_to_the_address_it_checked_and_resolves_once_per_connection()
    {
        var resolver = new MapResolver();
        using var f = Fetcher(resolver: resolver);
        var r = await f.FetchAsync($"http://site.test:{new Uri(_base).Port}/page", CancellationToken.None);
        Assert.Null(r.Error);
        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public async Task The_real_policy_refuses_the_local_server_outright()
    {
        using var f = Fetcher(policy: new SsrfPolicy());
        Assert.Equal(MetadataError.BlockedUrl, (await f.FetchAsync(U("/page"), CancellationToken.None)).Error);
    }

    [Theory]
    [InlineData("/json")]
    [InlineData("/png")]
    public async Task Refuses_content_that_is_not_a_web_page(string path)
    {
        using var f = Fetcher();
        Assert.Equal(MetadataError.NotHtml, (await f.FetchAsync(U(path), CancellationToken.None)).Error);
    }

    [Fact]
    public async Task A_pdf_is_recognised_without_being_downloaded()
    {
        using var f = Fetcher();
        var m = (await f.FetchAsync(U("/files/My_Great-Paper.pdf"), CancellationToken.None)).Metadata!;
        Assert.Equal(ResourceType.Pdf, m.Type);
        Assert.Equal("My Great Paper", m.Title);
        Assert.Null(m.Text);
    }

    [Fact]
    public async Task Http_errors_are_reported_and_only_server_errors_are_worth_retrying()
    {
        using var f = Fetcher();
        var missing = await f.FetchAsync(U("/missing"), CancellationToken.None);
        Assert.Equal(MetadataError.HttpError, missing.Error);
        Assert.False(missing.Transient);
        var boom = await f.FetchAsync(U("/boom"), CancellationToken.None);
        Assert.Equal(MetadataError.HttpError, boom.Error);
        Assert.True(boom.Transient);
    }

    [Fact]
    public async Task Gives_up_after_the_timeout()
    {
        using var f = Fetcher(o => o.TimeoutSeconds = 1);
        var started = DateTime.UtcNow;
        var r = await f.FetchAsync(U("/slow"), CancellationToken.None);
        Assert.Equal(MetadataError.Timeout, r.Error);
        Assert.True(r.Transient);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Stops_reading_a_body_that_is_too_large()
    {
        using var f = Fetcher(o => o.MaxBytes = 10_000);
        Assert.Equal(MetadataError.TooLarge, (await f.FetchAsync(U("/big"), CancellationToken.None)).Error);
        Assert.Equal(MetadataError.TooLarge, (await f.FetchAsync(U("/declared-big"), CancellationToken.None)).Error);
    }

    [Fact]
    public async Task A_closed_port_is_unreachable_and_worth_retrying()
    {
        using var f = Fetcher();
        var r = await f.FetchAsync("http://127.0.0.1:1/", CancellationToken.None);
        Assert.Equal(MetadataError.Unreachable, r.Error);
        Assert.True(r.Transient);
    }

    [Fact]
    public async Task Refuses_text_that_is_not_a_url()
    {
        using var f = Fetcher();
        Assert.Equal(MetadataError.BlockedUrl, (await f.FetchAsync("not a url", CancellationToken.None)).Error);
    }
}

public class TypeGuesserTests
{
    private static ResourceType Guess(string url, string? contentType = null, string? og = null, MetadataOptions? o = null) =>
        new TypeGuesser(Options.Create(o ?? new MetadataOptions())).Guess(new Uri(url), contentType, og);

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=1", ResourceType.Video)]
    [InlineData("https://youtu.be/abc", ResourceType.Video)]
    [InlineData("https://vimeo.com/123", ResourceType.Video)]
    [InlineData("https://github.com/dotnet/runtime", ResourceType.Repo)]
    [InlineData("https://github.com/dotnet/runtime/", ResourceType.Repo)]
    [InlineData("https://github.com/dotnet/runtime/issues/1", ResourceType.Website)]
    [InlineData("https://github.com/", ResourceType.Website)]
    [InlineData("https://www.coursera.org/learn/x", ResourceType.Course)]
    [InlineData("https://udemy.com/course/x", ResourceType.Course)]
    [InlineData("https://dev.to/x/y", ResourceType.Article)]
    [InlineData("https://example.com/paper.pdf", ResourceType.Pdf)]
    [InlineData("https://example.com/", ResourceType.Website)]
    [InlineData("https://notyoutube.com/", ResourceType.Website)]
    public void Guesses_from_the_address(string url, ResourceType expected) => Assert.Equal(expected, Guess(url));

    [Fact]
    public void Uses_the_content_type_and_open_graph_type()
    {
        Assert.Equal(ResourceType.Pdf, Guess("https://example.com/download?id=1", "application/pdf"));
        Assert.Equal(ResourceType.Article, Guess("https://example.com/post", "text/html", "article"));
        Assert.Equal(ResourceType.Video, Guess("https://example.com/clip", "text/html", "video.other"));
    }

    [Fact]
    public void The_rules_come_from_configuration()
    {
        var o = new MetadataOptions { TypeRules = [new() { Type = ResourceType.Course, Hosts = ["learn.example.org"] }] };
        Assert.Equal(ResourceType.Course, Guess("https://learn.example.org/x", o: o));
        Assert.Equal(ResourceType.Website, Guess("https://www.youtube.com/watch?v=1", o: o));   // defaults are replaced, not merged
    }
}
