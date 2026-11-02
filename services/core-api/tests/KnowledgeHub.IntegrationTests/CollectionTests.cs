using System.Net;
using KnowledgeHub.Domain;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Xunit;

namespace KnowledgeHub.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public class CollectionTests(ApiFactory factory)
{
    [Fact]
    public async Task Create_returns_the_collection_and_the_list_shows_counts_and_the_inbox_count()
    {
        var c = await factory.UserAsync();
        var books = await c.CreateCollectionAsync("Books", new { description = " Things to read ", color = "teal", icon = "📚" });
        Assert.Equal("Books", books.GetProperty("name").GetString());
        Assert.Equal("Things to read", books.GetProperty("description").GetString());
        Assert.Equal("teal", books.GetProperty("color").GetString());
        Assert.Equal(0, books.GetProperty("resource_count").GetInt32());

        await c.CreateResourceAsync(extra: new { collection_id = books.Id() });
        await c.CreateResourceAsync(extra: new { collection_id = books.Id() });
        await c.CreateResourceAsync(); // Inbox
        var trashed = await c.CreateResourceAsync(extra: new { collection_id = books.Id() });
        await c.DeleteAsync($"/api/v1/resources/{trashed.Id()}").ExpectAsync(HttpStatusCode.NoContent);

        var list = await c.GetAsync("/api/v1/collections").ExpectAsync(HttpStatusCode.OK);

        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(2, item.GetProperty("resource_count").GetInt32()); // the trashed one is not counted (FR-COL-04)
        Assert.Equal(1, list.GetProperty("inbox_count").GetInt32());
        var one = await c.GetAsync($"/api/v1/collections/{books.Id()}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(2, one.GetProperty("resource_count").GetInt32());
    }

    [Fact]
    public async Task Names_are_unique_per_user_ignoring_case_and_other_users_can_reuse_them()
    {
        var c = await factory.UserAsync();
        var first = await c.CreateCollectionAsync("Reading list");

        var dup = await c.PostAsync("/api/v1/collections", new { name = "  READING LIST " });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Equal("collection_name_taken", await dup.CodeAsync());

        var other = await c.CreateCollectionAsync("Other");
        var rename = await c.PatchAsync($"/api/v1/collections/{other.Id()}", new { name = "reading list" });
        Assert.Equal(HttpStatusCode.Conflict, rename.StatusCode);
        // Renaming to its own name, with different case, is fine.
        await c.PatchAsync($"/api/v1/collections/{first.Id()}", new { name = "READING LIST" }).ExpectAsync(HttpStatusCode.OK);

        var someoneElse = await factory.UserAsync();
        await someoneElse.CreateCollectionAsync("Reading list");
    }

    [Theory]
    [InlineData("name", "")]
    [InlineData("name", "   ")]
    [InlineData("color", "pink")]
    [InlineData("icon", "ab")]
    [InlineData("icon", "a")]
    [InlineData("icon", "📚📚")]
    [InlineData("icon", ".")]
    public async Task Invalid_fields_are_rejected_with_a_field_error(string field, string value)
    {
        var c = await factory.UserAsync();
        var body = new Dictionary<string, object?> { ["name"] = "Valid" };
        body[field] = value;

        var response = await c.PostAsync("/api/v1/collections", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.Json()).GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task Length_limits_are_enforced()
    {
        var c = await factory.UserAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/v1/collections", new { name = new string('x', 101) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/v1/collections", new { name = "ok", description = new string('x', 501) })).StatusCode);
        await c.CreateCollectionAsync(new string('x', 100), new { description = new string('y', 500) });
    }

    [Fact]
    public async Task Patch_changes_only_what_is_sent_and_null_clears()
    {
        var c = await factory.UserAsync();
        var col = await c.CreateCollectionAsync("Work", new { description = "Desk stuff", color = "blue", icon = "💼" });

        var renamed = await c.PatchAsync($"/api/v1/collections/{col.Id()}", new { name = "Work stuff" }).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Desk stuff", renamed.GetProperty("description").GetString()); // absent: unchanged
        Assert.Equal("blue", renamed.GetProperty("color").GetString());

        var cleared = await c.SendAsync(HttpMethod.Patch, $"/api/v1/collections/{col.Id()}", new Dictionary<string, object?> { ["description"] = null, ["is_pinned"] = true }).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("description").ValueKind);
        Assert.True(cleared.GetProperty("is_pinned").GetBoolean());
        Assert.Equal("blue", cleared.GetProperty("color").GetString());
    }

    [Fact]
    public async Task A_stale_expected_updated_at_returns_409_edit_conflict()
    {
        var c = await factory.UserAsync();
        var col = await c.CreateCollectionAsync("Notes");
        var stamp = col.GetProperty("updated_at").GetString();
        await c.PatchAsync($"/api/v1/collections/{col.Id()}", new { name = "Notes 2" }).ExpectAsync(HttpStatusCode.OK);

        var stale = await c.PatchAsync($"/api/v1/collections/{col.Id()}", new { name = "Notes 3", expected_updated_at = stamp });

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("edit_conflict", await stale.CodeAsync());
    }

    [Fact]
    public async Task Pinned_collections_come_first_and_the_saved_order_survives_a_reload()
    {
        var c = await factory.UserAsync();
        var a = await c.CreateCollectionAsync("A");
        var b = await c.CreateCollectionAsync("B");
        var d = await c.CreateCollectionAsync("D");
        string[] Names(JsonElement list) => [.. list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!)];

        Assert.Equal(["A", "B", "D"], Names(await c.GetAsync("/api/v1/collections").ExpectAsync(HttpStatusCode.OK)));
        await c.PutAsync("/api/v1/collections/order", new { ids = new[] { d.Id(), a.Id(), b.Id() } }).ExpectAsync(HttpStatusCode.NoContent);
        Assert.Equal(["D", "A", "B"], Names(await c.GetAsync("/api/v1/collections").ExpectAsync(HttpStatusCode.OK)));

        await c.PatchAsync($"/api/v1/collections/{b.Id()}", new { is_pinned = true }).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["B", "D", "A"], Names(await c.GetAsync("/api/v1/collections").ExpectAsync(HttpStatusCode.OK)));
    }

    [Fact]
    public async Task New_collections_are_added_at_the_end()
    {
        var c = await factory.UserAsync();
        var a = await c.CreateCollectionAsync("A");
        var b = await c.CreateCollectionAsync("B");
        Assert.True(b.GetProperty("position").GetInt32() > a.GetProperty("position").GetInt32());
    }

    [Fact]
    public async Task Deleting_needs_a_choice_for_the_resources()
    {
        var c = await factory.UserAsync();
        var col = await c.CreateCollectionAsync("Temp");

        var none = await c.DeleteAsync($"/api/v1/collections/{col.Id()}");
        var bogus = await c.DeleteAsync($"/api/v1/collections/{col.Id()}?resources=maybe");

        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bogus.StatusCode);
        await c.GetAsync($"/api/v1/collections/{col.Id()}").ExpectAsync(HttpStatusCode.OK); // still there
    }

    [Fact]
    public async Task Delete_with_move_sends_resources_to_the_target_or_the_inbox_and_keeps_their_tags_and_notes()
    {
        var c = await factory.UserAsync();
        var source = await c.CreateCollectionAsync("Source");
        var target = await c.CreateCollectionAsync("Target");
        var r1 = await c.CreateResourceAsync(extra: new { collection_id = source.Id(), notes = "keep me", tags = new[] { "keep" } });

        await c.DeleteAsync($"/api/v1/collections/{source.Id()}?resources=move&target_collection_id={target.Id()}").ExpectAsync(HttpStatusCode.NoContent);

        var moved = await c.GetAsync($"/api/v1/resources/{r1.Id()}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(target.Id(), moved.GetProperty("collection_id").GetString());
        Assert.Equal("keep me", moved.GetProperty("notes").GetString());
        Assert.Equal(["keep"], moved.GetProperty("tags").Strings());
        await c.GetAsync($"/api/v1/collections/{source.Id()}").ExpectAsync(HttpStatusCode.NotFound);

        // No target means the Inbox.
        await c.DeleteAsync($"/api/v1/collections/{target.Id()}?resources=move").ExpectAsync(HttpStatusCode.NoContent);
        var inbox = await c.GetAsync($"/api/v1/resources/{r1.Id()}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, inbox.GetProperty("collection_id").ValueKind);
    }

    [Fact]
    public async Task Delete_with_delete_trashes_the_resources_so_they_can_still_be_restored()
    {
        var c = await factory.UserAsync();
        var col = await c.CreateCollectionAsync("Doomed");
        var r = await c.CreateResourceAsync(extra: new { collection_id = col.Id(), notes = "important" });

        await c.DeleteAsync($"/api/v1/collections/{col.Id()}?resources=delete").ExpectAsync(HttpStatusCode.NoContent);

        await c.GetAsync($"/api/v1/resources/{r.Id()}").ExpectAsync(HttpStatusCode.NotFound);
        var restored = await c.PostAsync($"/api/v1/resources/{r.Id()}/restore").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("important", restored.GetProperty("notes").GetString());
        Assert.Equal(JsonValueKind.Null, restored.GetProperty("collection_id").ValueKind); // back in the Inbox
    }

    [Fact]
    public async Task A_resource_already_in_the_trash_does_not_block_deleting_its_collection()
    {
        var c = await factory.UserAsync();
        var col = await c.CreateCollectionAsync("Has trash");
        var r = await c.CreateResourceAsync(extra: new { collection_id = col.Id() });
        await c.DeleteAsync($"/api/v1/resources/{r.Id()}").ExpectAsync(HttpStatusCode.NoContent);

        await c.DeleteAsync($"/api/v1/collections/{col.Id()}?resources=move").ExpectAsync(HttpStatusCode.NoContent);

        await c.PostAsync($"/api/v1/resources/{r.Id()}/restore").ExpectAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Moving_to_a_collection_that_does_not_exist_changes_nothing()
    {
        var c = await factory.UserAsync();
        var col = await c.CreateCollectionAsync("Keep");
        var r = await c.CreateResourceAsync(extra: new { collection_id = col.Id() });

        var response = await c.DeleteAsync($"/api/v1/collections/{col.Id()}?resources=move&target_collection_id={Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var still = await c.GetAsync($"/api/v1/resources/{r.Id()}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(col.Id(), still.GetProperty("collection_id").GetString());
    }

    [Fact]
    public async Task Collections_are_removed_with_the_account()
    {
        var c = await factory.UserAsync();
        await c.CreateCollectionAsync("Gone soon");
        await c.CreateResourceAsync();
        var me = await c.GetAsync("/api/v1/me").ExpectAsync(HttpStatusCode.OK);
        var userId = Guid.Parse(me.Id());
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(HttpMethod.Delete, "/api/v1/me", new { password = "a long unusual passphrase" })).StatusCode);

        await using var db = factory.Db.Admin();
        Assert.False(await db.Collections.AnyAsync(x => x.UserId == userId, TestContext.Current.CancellationToken));
        Assert.False(await db.Resources.IgnoreQueryFilters().AnyAsync(x => x.UserId == userId, TestContext.Current.CancellationToken));
    }
}
