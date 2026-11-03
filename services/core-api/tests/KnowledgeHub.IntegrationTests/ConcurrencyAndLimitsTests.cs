using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KnowledgeHub.IntegrationTests;
// Races and limits from the code review: timing of unknown sign-ins, concurrent tag creation, bulk tag limit.

[Collection(ApiTestGroup.Name)]
public class LoginTimingTests(ApiFactory factory)
{
    private sealed class CountingHasher(KnowledgeHub.Application.Common.IPasswordHasher inner) : KnowledgeHub.Application.Common.IPasswordHasher
    {
        public int Hashes;
        public int Verifies;
        public string Hash(string password) { Interlocked.Increment(ref Hashes); return inner.Hash(password); }
        public bool Verify(string password, string hash) { Interlocked.Increment(ref Verifies); return inner.Verify(password, hash); }
        public bool NeedsRehash(string hash) => inner.NeedsRehash(hash);
    }

    [Fact]
    public async Task An_unknown_email_costs_one_verify_like_a_known_one_and_no_extra_hash()
    {
        CountingHasher? counting = null;
        using var web = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            var real = s.Single(d => d.ServiceType == typeof(KnowledgeHub.Application.Common.IPasswordHasher));
            s.Remove(real);
            s.AddSingleton<KnowledgeHub.Application.Common.IPasswordHasher>(sp =>
                counting = new CountingHasher((KnowledgeHub.Application.Common.IPasswordHasher)ActivatorUtilities.CreateInstance(sp, real.ImplementationType!)));
        }));
        var c = new TestClient(web);
        await c.LoginAsync(TestClient.UniqueEmail(), "the wrong password entirely");   // may make the decoy, once per process
        var (hashes, verifies) = (counting!.Hashes, counting.Verifies);

        for (var i = 0; i < 3; i++) await new TestClient(web).LoginAsync(TestClient.UniqueEmail(), "the wrong password entirely");

        Assert.Equal(0, counting.Hashes - hashes);       // the decoy is not made again for each request
        Assert.Equal(3, counting.Verifies - verifies);   // one verify each, as for a real account
    }
}

[Collection(ApiTestGroup.Name)]
public class TagRaceTests(ApiFactory factory)
{
    [Fact]
    public async Task Saves_that_create_the_same_new_tag_at_the_same_moment_all_succeed_and_share_it()
    {
        var c = await factory.UserAsync();
        var tag = "race-" + Guid.NewGuid().ToString("N")[..8];
        var saves = Enumerable.Range(0, 12).Select(i =>
            c.PostAsync("/api/v1/resources", new { url = $"https://example.com/race/{Guid.NewGuid():N}", tags = new[] { tag, "shared-" + tag } }));

        var results = await Task.WhenAll(saves);

        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var tags = await c.GetAsync("/api/v1/tags").ExpectAsync(HttpStatusCode.OK);
        var mine = tags.GetProperty("items").EnumerateArray().Where(t => t.GetProperty("name").GetString()!.EndsWith(tag, StringComparison.Ordinal)).ToList();
        Assert.Equal(2, mine.Count);
        Assert.All(mine, t => Assert.Equal(12, t.GetProperty("resource_count").GetInt32()));
    }
}

[Collection(ApiTestGroup.Name)]
public class BulkTagLimitTests(ApiFactory factory)
{
    [Fact]
    public async Task Bulk_tagging_that_would_take_a_resource_past_twenty_tags_is_refused_and_changes_nothing()
    {
        var c = await factory.UserAsync();
        var full = await c.CreateResourceAsync("https://example.com/full", new { tags = Enumerable.Range(1, 18).Select(i => $"t{i}").ToArray() });
        var empty = await c.CreateResourceAsync("https://example.com/empty");
        var extra = new[] { "n1", "n2", "n3", "t1" };   // t1 is already there, so 18 + 3 = 21

        var refused = await c.PostAsync("/api/v1/resources/bulk", new { action = "add_tags", ids = new[] { full.Id(), empty.Id() }, tags = extra });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.True((await refused.Json()).GetProperty("errors").TryGetProperty("tags", out _));
        var after = await c.GetAsync("/api/v1/resources/" + empty.Id()).ExpectAsync(HttpStatusCode.OK);
        Assert.Empty(after.GetProperty("tags").EnumerateArray());   // nothing applied to the others either
        var tags = await c.GetAsync("/api/v1/tags").ExpectAsync(HttpStatusCode.OK);
        Assert.DoesNotContain(tags.GetProperty("items").EnumerateArray(), t => t.GetProperty("name").GetString() == "n1");

        // Exactly 20 is fine.
        await c.PostAsync("/api/v1/resources/bulk", new { action = "add_tags", ids = new[] { full.Id() }, tags = new[] { "n1", "n2" } }).ExpectAsync(HttpStatusCode.OK);
    }
}
