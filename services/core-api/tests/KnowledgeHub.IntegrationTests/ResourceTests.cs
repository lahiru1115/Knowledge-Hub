using System.Net;
using System.Text.Json;
using KnowledgeHub.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace KnowledgeHub.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public class ResourceTests(ApiFactory factory)
{
    // ---------- create (FR-RES-01, 05, 06, 07) ----------

    [Fact]
    public async Task Saving_only_a_url_creates_a_pending_resource_in_the_inbox()
    {
        var c = await factory.UserAsync();

        var response = await c.PostAsync("/api/v1/resources", new { url = "https://example.com/article" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.StartsWith("/api/v1/resources/", response.Headers.Location?.ToString(), StringComparison.Ordinal);
        var r = await response.Json();
        Assert.Equal("https://example.com/article", r.GetProperty("url").GetString());
        Assert.Equal("pending", r.GetProperty("metadata_status").GetString());
        Assert.Equal("website", r.GetProperty("type").GetString());
        Assert.Equal("none", r.GetProperty("summary_source").GetString());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("collection_id").ValueKind);
        Assert.Empty(r.GetProperty("tags").EnumerateArray());
        Assert.False(r.GetProperty("is_favourite").GetBoolean());
        Assert.False(r.GetProperty("is_archived").GetBoolean());
        Assert.Equal(7, Guid.Parse(r.Id()).Version);
    }

    [Fact]
    public async Task Saving_with_every_field_keeps_them_and_normalises_tags()
    {
        var c = await factory.UserAsync();
        var col = await c.CreateCollectionAsync("Learning");

        var r = await c.CreateResourceAsync("https://example.com/a", new
        {
            title = "  A title ", description = "About it", notes = "# Notes\n\n- one", type = "video",
            collection_id = col.Id(), tags = new[] { " Machine  Learning ", "machine learning", "C#", "node.js" },
        });

        Assert.Equal("A title", r.GetProperty("title").GetString());
        Assert.Equal("# Notes\n\n- one", r.GetProperty("notes").GetString()); // notes are stored as entered
        Assert.Equal("video", r.GetProperty("type").GetString());
        Assert.Equal(col.Id(), r.GetProperty("collection_id").GetString());
        Assert.Equal(["c#", "machine learning", "node.js"], r.GetProperty("tags").Strings()); // trimmed, lowercased, de-duplicated
        var tags = await c.GetAsync("/api/v1/tags").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(3, tags.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Tags_are_created_once_and_reused()
    {
        var c = await factory.UserAsync();
        await c.CreateResourceAsync(extra: new { tags = new[] { "shared" } });
        await c.CreateResourceAsync(extra: new { tags = new[] { "shared", "only-here" } });

        var tags = await c.GetAsync("/api/v1/tags").ExpectAsync(HttpStatusCode.OK);

        var shared = tags.GetProperty("items").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "shared");
        Assert.Equal(2, shared.GetProperty("resource_count").GetInt32());
        Assert.Equal(2, tags.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Accepted_ai_suggestions_are_recorded_with_source_ai()
    {
        var c = await factory.UserAsync();
        var r = await c.CreateResourceAsync(extra: new { tags = new[] { "mine", "suggested" }, ai_tags = new[] { "suggested" } });
        var id = Guid.Parse(r.Id());

        await using var db = factory.Db.Admin();
        var sources = await db.ResourceTags.Where(rt => rt.ResourceId == id).Select(rt => new { rt.Tag.Name, rt.Source }).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TagSource.Ai, sources.Single(s => s.Name == "suggested").Source);
        Assert.Equal(TagSource.User, sources.Single(s => s.Name == "mine").Source);
    }

    public static TheoryData<string, object> InvalidCreates => new()
    {
        { "url", new { } },
        { "url", new { url = "" } },
        { "url", new { url = "ftp://example.com/file" } },
        { "url", new { url = "javascript:alert(1)" } },
        { "url", new { url = "not a url" } },
        { "url", new { url = "https://example.com/" + new string('a', 2050) } },
        { "title", new { url = "https://e.com", title = new string('t', 301) } },
        { "description", new { url = "https://e.com", description = new string('d', 2001) } },
        { "notes", new { url = "https://e.com", notes = new string('n', 100_001) } },
        { "tags", new { url = "https://e.com", tags = new[] { "bad/tag" } } },
        { "tags", new { url = "https://e.com", tags = new[] { new string('t', 51) } } },
        { "tags", new { url = "https://e.com", tags = Enumerable.Range(0, 21).Select(i => $"tag{i}").ToArray() } },
        { "ai_tags", new { url = "https://e.com", tags = new[] { "a" }, ai_tags = new[] { "b" } } },
    };

    [Theory]
    [MemberData(nameof(InvalidCreates))]
    public async Task Invalid_input_is_rejected_with_an_error_on_the_right_field(string field, object body)
    {
        var c = await factory.UserAsync();

        var response = await c.PostAsync("/api/v1/resources", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Json();
        Assert.Equal("validation_failed", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), problem.GetRawText());
    }

    [Fact]
    public async Task An_unknown_type_returns_400_and_so_does_a_type_given_as_a_number()
    {
        var c = await factory.UserAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/v1/resources", new { url = "https://e.com", type = "podcast" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/v1/resources", new { url = "https://e.com", type = 2 })).StatusCode);
    }

    [Fact]
    public async Task Exactly_at_the_limits_is_accepted()
    {
        var c = await factory.UserAsync();
        await c.CreateResourceAsync("https://example.com/" + new string('a', 2048 - 20), new
        {
            title = new string('t', 300), description = new string('d', 2000), notes = new string('n', 100_000),
            tags = Enumerable.Range(0, 20).Select(i => $"tag{i}").ToArray(),
        });
    }

    [Fact]
    public async Task Saving_the_same_url_twice_is_allowed()
    {
        var c = await factory.UserAsync();
        var a = await c.CreateResourceAsync("https://example.com/same");
        var b = await c.CreateResourceAsync("https://example.com/same");
        Assert.NotEqual(a.Id(), b.Id());
    }

    // ---------- update (FR-RES-03, 06, 07, 09, 10) ----------

    [Fact]
    public async Task Patch_changes_only_the_fields_sent()
    {
        var c = await factory.UserAsync();
        var r = await c.CreateResourceAsync(extra: new { title = "Keep", description = "Keep too", notes = "Keep notes", tags = new[] { "a" } });

        var patched = await c.PatchAsync($"/api/v1/resources/{r.Id()}", new { is_favourite = true }).ExpectAsync(HttpStatusCode.OK);

        Assert.True(patched.GetProperty("is_favourite").GetBoolean());
        Assert.Equal("Keep", patched.GetProperty("title").GetString());
        Assert.Equal("Keep too", patched.GetProperty("description").GetString());
        Assert.Equal("Keep notes", patched.GetProperty("notes").GetString());
        Assert.Equal(["a"], patched.GetProperty("tags").Strings());
    }

    [Fact]
    public async Task Null_clears_a_field_and_an_absent_field_leaves_it()
    {
        var c = await factory.UserAsync();
        var col = await c.CreateCollectionAsync("Somewhere");
        var r = await c.CreateResourceAsync(extra: new { title = "T", description = "D", notes = "N", collection_id = col.Id() });

        var cleared = await c.SendAsync(HttpMethod.Patch, $"/api/v1/resources/{r.Id()}",
            new Dictionary<string, object?> { ["title"] = null, ["collection_id"] = null }).ExpectAsync(HttpStatusCode.OK);

        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("title").ValueKind);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("collection_id").ValueKind); // back to the Inbox
        Assert.Equal("D", cleared.GetProperty("description").GetString());
        Assert.Equal("N", cleared.GetProperty("notes").GetString());
    }

    [Fact]
    public async Task Moving_changes_only_the_collection()
    {
        var c = await factory.UserAsync();
        var col = await c.CreateCollectionAsync("Target");
        var r = await c.CreateResourceAsync(extra: new { notes = "N", tags = new[] { "t" } });

        var moved = await c.PatchAsync($"/api/v1/resources/{r.Id()}", new { collection_id = col.Id() }).ExpectAsync(HttpStatusCode.OK);

        Assert.Equal(col.Id(), moved.GetProperty("collection_id").GetString());
        Assert.Equal("N", moved.GetProperty("notes").GetString());
        Assert.Equal(["t"], moved.GetProperty("tags").Strings());
    }

    [Fact]
    public async Task Sending_tags_replaces_the_set_and_an_empty_list_removes_them_all()
    {
        var c = await factory.UserAsync();
        var r = await c.CreateResourceAsync(extra: new { tags = new[] { "a", "b" } });

        var replaced = await c.PatchAsync($"/api/v1/resources/{r.Id()}", new { tags = new[] { "b", "c" } }).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["b", "c"], replaced.GetProperty("tags").Strings());

        var none = await c.PatchAsync($"/api/v1/resources/{r.Id()}", new { tags = Array.Empty<string>() }).ExpectAsync(HttpStatusCode.OK);
        Assert.Empty(none.GetProperty("tags").EnumerateArray());
    }

    [Fact]
    public async Task Writing_a_summary_marks_it_as_the_users_and_clearing_it_resets_the_source()
    {
        var c = await factory.UserAsync();
        var r = await c.CreateResourceAsync();

        var written = await c.PatchAsync($"/api/v1/resources/{r.Id()}", new { summary = "My own summary." }).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("My own summary.", written.GetProperty("summary").GetString());
        Assert.Equal("user", written.GetProperty("summary_source").GetString());

        var cleared = await c.SendAsync(HttpMethod.Patch, $"/api/v1/resources/{r.Id()}", new Dictionary<string, object?> { ["summary"] = null }).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("summary").ValueKind);
        Assert.Equal("none", cleared.GetProperty("summary_source").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await c.PatchAsync($"/api/v1/resources/{r.Id()}", new { summary = new string('s', 2001) })).StatusCode);
    }

    [Fact]
    public async Task Changing_the_url_updates_the_normal_form_and_asks_for_metadata_again()
    {
        var c = await factory.UserAsync();
        var r = await c.CreateResourceAsync("https://example.com/old");
        var id = Guid.Parse(r.Id());
        await using (var db = factory.Db.Admin())
            await db.Resources.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.MetadataStatus, MetadataStatus.Ready), TestContext.Current.CancellationToken);

        var changed = await c.PatchAsync($"/api/v1/resources/{r.Id()}", new { url = "https://Example.com/new/?utm_source=x" }).ExpectAsync(HttpStatusCode.OK);

        Assert.Equal("https://Example.com/new/?utm_source=x", changed.GetProperty("url").GetString()); // as entered
        Assert.Equal("pending", changed.GetProperty("metadata_status").GetString());
        var dup = await c.GetAsync("/api/v1/resources/check-url?url=" + LibraryApi.Q("https://example.com/new")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(r.Id(), dup.GetProperty("existing").Id()); // found by the new normal form
    }

    [Fact]
    public async Task Favourite_and_archive_toggle()
    {
        var c = await factory.UserAsync();
        var r = await c.CreateResourceAsync();
        var on = await c.PatchAsync($"/api/v1/resources/{r.Id()}", new { is_favourite = true, is_archived = true }).ExpectAsync(HttpStatusCode.OK);
        Assert.True(on.GetProperty("is_favourite").GetBoolean());
        Assert.True(on.GetProperty("is_archived").GetBoolean());
        var off = await c.PatchAsync($"/api/v1/resources/{r.Id()}", new { is_favourite = false, is_archived = false }).ExpectAsync(HttpStatusCode.OK);
        Assert.False(off.GetProperty("is_favourite").GetBoolean());
        Assert.False(off.GetProperty("is_archived").GetBoolean());
    }

    [Fact]
    public async Task A_stale_expected_updated_at_is_a_409_and_the_current_one_is_accepted()
    {
        var c = await factory.UserAsync();
        var r = await c.CreateResourceAsync(extra: new { notes = "v1" });
        var first = r.GetProperty("updated_at").GetString();
        var second = (await c.PatchAsync($"/api/v1/resources/{r.Id()}", new { notes = "v2" }).ExpectAsync(HttpStatusCode.OK)).GetProperty("updated_at").GetString();
        Assert.NotEqual(first, second);

        var stale = await c.PatchAsync($"/api/v1/resources/{r.Id()}", new { notes = "v3", expected_updated_at = first });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("edit_conflict", await stale.CodeAsync());
        Assert.Equal("v2", (await c.GetAsync($"/api/v1/resources/{r.Id()}").ExpectAsync(HttpStatusCode.OK)).GetProperty("notes").GetString());

        await c.PatchAsync($"/api/v1/resources/{r.Id()}", new { notes = "v3", expected_updated_at = second }).ExpectAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Patch_validates_what_it_is_given()
    {
        var c = await factory.UserAsync();
        var r = await c.CreateResourceAsync();
        foreach (var body in new object[] { new { url = "ftp://x.com" }, new { type = "nope" }, new { title = new string('t', 301) }, new { tags = new[] { "bad/tag" } } })
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PatchAsync($"/api/v1/resources/{r.Id()}", body)).StatusCode);
    }

    // ---------- delete and restore (FR-RES-04) ----------

    [Fact]
    public async Task Deleting_hides_the_resource_everywhere_and_restore_brings_back_tags_and_notes()
    {
        var c = await factory.UserAsync();
        var r = await c.CreateResourceAsync(extra: new { title = "Soon gone", notes = "my notes", tags = new[] { "keepme" } });

        await c.DeleteAsync($"/api/v1/resources/{r.Id()}").ExpectAsync(HttpStatusCode.NoContent);

        await c.GetAsync($"/api/v1/resources/{r.Id()}").ExpectAsync(HttpStatusCode.NotFound);
        Assert.DoesNotContain(r.Id(), (await c.ListAsync()).Ids());
        Assert.Empty((await c.ListAsync("q=gone")).GetProperty("items").EnumerateArray());
        var tag = (await c.GetAsync("/api/v1/tags").ExpectAsync(HttpStatusCode.OK)).GetProperty("items").EnumerateArray().Single();
        Assert.Equal(0, tag.GetProperty("resource_count").GetInt32()); // not counted while in the trash
        await c.PatchAsync($"/api/v1/resources/{r.Id()}", new { notes = "x" }).ExpectAsync(HttpStatusCode.NotFound);
        await c.DeleteAsync($"/api/v1/resources/{r.Id()}").ExpectAsync(HttpStatusCode.NotFound); // already deleted

        var back = await c.PostAsync($"/api/v1/resources/{r.Id()}/restore").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("my notes", back.GetProperty("notes").GetString());
        Assert.Equal(["keepme"], back.GetProperty("tags").Strings());
        Assert.Contains(r.Id(), (await c.ListAsync()).Ids());
        Assert.Contains((await c.ListAsync("q=gone")).Ids(), x => x == r.Id());
    }

    [Fact]
    public async Task Restoring_something_that_is_not_deleted_or_is_older_than_7_days_is_a_404()
    {
        var c = await factory.UserAsync();
        var live = await c.CreateResourceAsync();
        await c.PostAsync($"/api/v1/resources/{live.Id()}/restore").ExpectAsync(HttpStatusCode.NotFound);

        var old = await c.CreateResourceAsync();
        await c.DeleteAsync($"/api/v1/resources/{old.Id()}").ExpectAsync(HttpStatusCode.NoContent);
        var oldId = Guid.Parse(old.Id());
        await using (var db = factory.Db.Admin())
            await db.Resources.IgnoreQueryFilters().Where(x => x.Id == oldId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.DeletedAt, DateTimeOffset.UtcNow.AddDays(-7).AddMinutes(-1)), TestContext.Current.CancellationToken);
        await c.PostAsync($"/api/v1/resources/{old.Id()}/restore").ExpectAsync(HttpStatusCode.NotFound);

        var recent = await c.CreateResourceAsync();
        await c.DeleteAsync($"/api/v1/resources/{recent.Id()}").ExpectAsync(HttpStatusCode.NoContent);
        var recentId = Guid.Parse(recent.Id());
        await using (var db = factory.Db.Admin())
            await db.Resources.IgnoreQueryFilters().Where(x => x.Id == recentId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.DeletedAt, DateTimeOffset.UtcNow.AddDays(-6)), TestContext.Current.CancellationToken);
        await c.PostAsync($"/api/v1/resources/{recent.Id()}/restore").ExpectAsync(HttpStatusCode.OK);
    }

    // ---------- duplicate check (FR-RES-11) ----------

    [Theory]
    [InlineData("https://example.com/a/b", "HTTPS://EXAMPLE.COM/a/b/")]
    [InlineData("https://example.com/a/b", "https://example.com:443/a/b")]
    [InlineData("http://example.com/a", "http://example.com:80/a")]
    [InlineData("https://example.com/a/b", "https://example.com/a/b#section")]
    [InlineData("https://example.com/a?x=1", "https://example.com/a?x=1&utm_source=newsletter&utm_medium=email")]
    [InlineData("https://example.com/a", "https://example.com/a?UTM_CAMPAIGN=x")]
    public async Task The_same_page_written_differently_is_recognised_as_a_duplicate(string saved, string typed)
    {
        var c = await factory.UserAsync();
        var r = await c.CreateResourceAsync(saved);

        var check = await c.GetAsync("/api/v1/resources/check-url?url=" + LibraryApi.Q(typed)).ExpectAsync(HttpStatusCode.OK);

        Assert.Equal(r.Id(), check.GetProperty("existing").Id());
    }

    [Theory]
    [InlineData("https://example.com/a/b", "https://example.com/a/c")]
    [InlineData("https://example.com/a?x=1", "https://example.com/a?x=2")]
    [InlineData("https://example.com/a", "http://example.com/a")]
    [InlineData("https://example.com/a", "https://www.example.com/a")]
    public async Task Different_pages_are_not_duplicates(string saved, string typed)
    {
        var c = await factory.UserAsync();
        await c.CreateResourceAsync(saved);

        var check = await c.GetAsync("/api/v1/resources/check-url?url=" + LibraryApi.Q(typed)).ExpectAsync(HttpStatusCode.OK);

        Assert.Equal(JsonValueKind.Null, check.GetProperty("existing").ValueKind);
    }

    [Fact]
    public async Task The_duplicate_check_ignores_the_trash_and_other_users_and_needs_a_valid_url()
    {
        var c = await factory.UserAsync();
        var other = await factory.UserAsync();
        await other.CreateResourceAsync("https://example.com/theirs");
        var mine = await c.CreateResourceAsync("https://example.com/mine");
        await c.DeleteAsync($"/api/v1/resources/{mine.Id()}").ExpectAsync(HttpStatusCode.NoContent);

        var theirs = await c.GetAsync("/api/v1/resources/check-url?url=" + LibraryApi.Q("https://example.com/theirs")).ExpectAsync(HttpStatusCode.OK);
        var trashed = await c.GetAsync("/api/v1/resources/check-url?url=" + LibraryApi.Q("https://example.com/mine")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, theirs.GetProperty("existing").ValueKind);
        Assert.Equal(JsonValueKind.Null, trashed.GetProperty("existing").ValueKind);

        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/v1/resources/check-url?url=ftp://x")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/v1/resources/check-url")).StatusCode);
    }

    // ---------- bulk (FR-RES-12) ----------

    [Fact]
    public async Task Bulk_move_moves_them_all_and_reports_how_many_changed()
    {
        var c = await factory.UserAsync();
        var col = await c.CreateCollectionAsync("Dest");
        var a = await c.CreateResourceAsync();
        var b = await c.CreateResourceAsync(extra: new { collection_id = col.Id() }); // already there

        var result = await c.PostAsync("/api/v1/resources/bulk", new { action = "move", ids = new[] { a.Id(), b.Id() }, collection_id = col.Id() }).ExpectAsync(HttpStatusCode.OK);

        Assert.Equal(1, result.GetProperty("changed").GetInt32());
        Assert.Equal(col.Id(), (await c.GetAsync($"/api/v1/resources/{a.Id()}").ExpectAsync(HttpStatusCode.OK)).GetProperty("collection_id").GetString());

        // null means the Inbox.
        var home = await c.SendAsync(HttpMethod.Post, "/api/v1/resources/bulk", new Dictionary<string, object?> { ["action"] = "move", ["ids"] = new[] { a.Id() }, ["collection_id"] = null }).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(1, home.GetProperty("changed").GetInt32());
    }

    [Fact]
    public async Task Bulk_add_and_remove_tags()
    {
        var c = await factory.UserAsync();
        var a = await c.CreateResourceAsync(extra: new { tags = new[] { "x" } });
        var b = await c.CreateResourceAsync();

        var added = await c.PostAsync("/api/v1/resources/bulk", new { action = "add_tags", ids = new[] { a.Id(), b.Id() }, tags = new[] { "X", "y" } }).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(2, added.GetProperty("changed").GetInt32());
        Assert.Equal(["x", "y"], (await c.GetAsync($"/api/v1/resources/{a.Id()}").ExpectAsync(HttpStatusCode.OK)).GetProperty("tags").Strings());
        Assert.Equal(["x", "y"], (await c.GetAsync($"/api/v1/resources/{b.Id()}").ExpectAsync(HttpStatusCode.OK)).GetProperty("tags").Strings());
        var again = await c.PostAsync("/api/v1/resources/bulk", new { action = "add_tags", ids = new[] { a.Id() }, tags = new[] { "x" } }).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(0, again.GetProperty("changed").GetInt32()); // nothing new to add

        var removed = await c.PostAsync("/api/v1/resources/bulk", new { action = "remove_tags", ids = new[] { a.Id(), b.Id() }, tags = new[] { "x" } }).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(2, removed.GetProperty("changed").GetInt32());
        Assert.Equal(["y"], (await c.GetAsync($"/api/v1/resources/{a.Id()}").ExpectAsync(HttpStatusCode.OK)).GetProperty("tags").Strings());
    }

    [Fact]
    public async Task Bulk_delete_can_be_undone_with_bulk_restore()
    {
        var c = await factory.UserAsync();
        var a = await c.CreateResourceAsync(extra: new { notes = "n1" });
        var b = await c.CreateResourceAsync();

        var deleted = await c.PostAsync("/api/v1/resources/bulk", new { action = "delete", ids = new[] { a.Id(), b.Id() } }).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(2, deleted.GetProperty("changed").GetInt32());
        Assert.Empty((await c.ListAsync()).GetProperty("items").EnumerateArray());

        var restored = await c.PostAsync("/api/v1/resources/bulk", new { action = "restore", ids = new[] { a.Id(), b.Id() } }).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(2, restored.GetProperty("changed").GetInt32());
        Assert.Equal(2, (await c.ListAsync()).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Bulk_applies_to_all_or_none()
    {
        var c = await factory.UserAsync();
        var other = await factory.UserAsync();
        var mine = await c.CreateResourceAsync(extra: new { tags = new[] { "t" } });
        var theirs = await other.CreateResourceAsync();

        var response = await c.PostAsync("/api/v1/resources/bulk", new { action = "delete", ids = new[] { mine.Id(), theirs.Id() } });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await c.GetAsync($"/api/v1/resources/{mine.Id()}").ExpectAsync(HttpStatusCode.OK); // untouched
        await other.GetAsync($"/api/v1/resources/{theirs.Id()}").ExpectAsync(HttpStatusCode.OK);
        var missing = await c.PostAsync("/api/v1/resources/bulk", new { action = "add_tags", ids = new[] { mine.Id(), Guid.NewGuid().ToString() }, tags = new[] { "new" } });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(["t"], (await c.GetAsync($"/api/v1/resources/{mine.Id()}").ExpectAsync(HttpStatusCode.OK)).GetProperty("tags").Strings()); // not even partly applied
    }

    [Fact]
    public async Task Bulk_accepts_100_ids_and_refuses_101_or_none()
    {
        var c = await factory.UserAsync();
        var ids = new List<string>();
        for (var i = 0; i < 100; i++) ids.Add((await c.CreateResourceAsync()).Id());
        var extra = await c.CreateResourceAsync();

        var ok = await c.PostAsync("/api/v1/resources/bulk", new { action = "add_tags", ids, tags = new[] { "all" } }).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(100, ok.GetProperty("changed").GetInt32());

        ids.Add(extra.Id());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/v1/resources/bulk", new { action = "add_tags", ids, tags = new[] { "all" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/v1/resources/bulk", new { action = "delete", ids = Array.Empty<string>() })).StatusCode);
    }

    [Fact]
    public async Task Bulk_validates_the_request()
    {
        var c = await factory.UserAsync();
        var r = await c.CreateResourceAsync();
        var ids = new[] { r.Id() };
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/v1/resources/bulk", new { ids })).StatusCode); // no action
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/v1/resources/bulk", new { action = "explode", ids })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/v1/resources/bulk", new { action = "move", ids })).StatusCode); // no collection_id
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/v1/resources/bulk", new { action = "add_tags", ids })).StatusCode); // no tags
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/v1/resources/bulk", new { action = "add_tags", ids, tags = new[] { "bad/tag" } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync("/api/v1/resources/bulk", new { action = "move", ids, collection_id = Guid.NewGuid() })).StatusCode);
    }
}
