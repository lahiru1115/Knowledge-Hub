using System.Net;
using Xunit;

namespace KnowledgeHub.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public class TagTests(ApiFactory factory)
{
    private static async Task<JsonElementTag[]> TagsAsync(TestClient c) =>
        [.. (await c.GetAsync("/api/v1/tags").ExpectAsync(HttpStatusCode.OK)).GetProperty("items").EnumerateArray()
            .Select(t => new JsonElementTag(t.Id(), t.GetProperty("name").GetString()!, t.GetProperty("resource_count").GetInt32()))];

    private sealed record JsonElementTag(string Id, string Name, int Count);

    [Fact]
    public async Task The_list_has_usage_counts_sorted_by_name_and_unused_tags_show_zero()
    {
        var c = await factory.UserAsync();
        var a = await c.CreateResourceAsync(extra: new { tags = new[] { "beta", "alpha" } });
        await c.CreateResourceAsync(extra: new { tags = new[] { "beta" } });
        await c.PatchAsync($"/api/v1/resources/{a.Id()}", new { tags = new[] { "beta" } }).ExpectAsync(HttpStatusCode.OK); // alpha is now unused

        var tags = await TagsAsync(c);

        Assert.Equal(["alpha", "beta"], tags.Select(t => t.Name).ToArray());
        Assert.Equal(0, tags[0].Count);
        Assert.Equal(2, tags[1].Count);
    }

    [Fact]
    public async Task Renaming_keeps_every_resource_tagged_under_the_new_name()
    {
        var c = await factory.UserAsync();
        var r1 = await c.CreateResourceAsync(extra: new { tags = new[] { "old name" } });
        var r2 = await c.CreateResourceAsync(extra: new { tags = new[] { "old name", "other" } });
        var tag = (await TagsAsync(c)).Single(t => t.Name == "old name");

        var renamed = await c.PatchAsync($"/api/v1/tags/{tag.Id}", new { name = "  New   NAME " }).ExpectAsync(HttpStatusCode.OK);

        Assert.Equal("new name", renamed.GetProperty("name").GetString());
        Assert.Equal(2, renamed.GetProperty("resource_count").GetInt32());
        Assert.Equal(["new name"], (await c.GetAsync($"/api/v1/resources/{r1.Id()}").ExpectAsync(HttpStatusCode.OK)).GetProperty("tags").Strings());
        Assert.Equal(["new name", "other"], (await c.GetAsync($"/api/v1/resources/{r2.Id()}").ExpectAsync(HttpStatusCode.OK)).GetProperty("tags").Strings());
    }

    [Fact]
    public async Task Renaming_to_an_existing_tag_returns_409_and_points_at_the_tag_to_merge_into()
    {
        var c = await factory.UserAsync();
        await c.CreateResourceAsync(extra: new { tags = new[] { "one", "two" } });
        var tags = await TagsAsync(c);
        var one = tags.Single(t => t.Name == "one");
        var two = tags.Single(t => t.Name == "two");

        var response = await c.PatchAsync($"/api/v1/tags/{one.Id}", new { name = "TWO" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Json();
        Assert.Equal("tag_name_taken", problem.GetProperty("code").GetString());
        Assert.Equal(two.Id, problem.GetProperty("suggest_merge_tag_id").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad/name")]
    [InlineData("has;semicolon")]
    public async Task Renaming_to_an_invalid_name_is_a_400(string name)
    {
        var c = await factory.UserAsync();
        await c.CreateResourceAsync(extra: new { tags = new[] { "x" } });
        var tag = (await TagsAsync(c)).Single();

        Assert.Equal(HttpStatusCode.BadRequest, (await c.PatchAsync($"/api/v1/tags/{tag.Id}", new { name })).StatusCode);
    }

    [Fact]
    public async Task Merging_gives_resources_the_target_tag_without_duplicates_and_deletes_the_source()
    {
        var c = await factory.UserAsync();
        var onlyA = await c.CreateResourceAsync(extra: new { tags = new[] { "a" } });
        var both = await c.CreateResourceAsync(extra: new { tags = new[] { "a", "b" } });
        var onlyB = await c.CreateResourceAsync(extra: new { tags = new[] { "b" } });
        var tags = await TagsAsync(c);
        var a = tags.Single(t => t.Name == "a");
        var b = tags.Single(t => t.Name == "b");

        var merged = await c.PostAsync($"/api/v1/tags/{a.Id}/merge", new { target_tag_id = b.Id }).ExpectAsync(HttpStatusCode.OK);

        Assert.Equal("b", merged.GetProperty("name").GetString());
        Assert.Equal(3, merged.GetProperty("resource_count").GetInt32());
        foreach (var r in new[] { onlyA, both, onlyB })
            Assert.Equal(["b"], (await c.GetAsync($"/api/v1/resources/{r.Id()}").ExpectAsync(HttpStatusCode.OK)).GetProperty("tags").Strings());
        Assert.Equal(["b"], (await TagsAsync(c)).Select(t => t.Name).ToArray());
    }

    [Fact]
    public async Task Merging_a_tag_into_itself_or_into_nothing_is_refused()
    {
        var c = await factory.UserAsync();
        await c.CreateResourceAsync(extra: new { tags = new[] { "solo" } });
        var tag = (await TagsAsync(c)).Single();

        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"/api/v1/tags/{tag.Id}/merge", new { target_tag_id = tag.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"/api/v1/tags/{tag.Id}/merge", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync($"/api/v1/tags/{tag.Id}/merge", new { target_tag_id = Guid.NewGuid() })).StatusCode);
        Assert.Single(await TagsAsync(c)); // nothing was lost
    }

    [Fact]
    public async Task Deleting_a_tag_removes_it_from_resources_but_keeps_the_resources()
    {
        var c = await factory.UserAsync();
        var r = await c.CreateResourceAsync(extra: new { title = "Stays", tags = new[] { "doomed", "survivor" } });
        var doomed = (await TagsAsync(c)).Single(t => t.Name == "doomed");

        await c.DeleteAsync($"/api/v1/tags/{doomed.Id}").ExpectAsync(HttpStatusCode.NoContent);

        var after = await c.GetAsync($"/api/v1/resources/{r.Id()}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["survivor"], after.GetProperty("tags").Strings());
        Assert.Equal("Stays", after.GetProperty("title").GetString());
        Assert.Equal(["survivor"], (await TagsAsync(c)).Select(t => t.Name).ToArray());
    }

    [Fact]
    public async Task Merging_keeps_a_deleted_resources_tags_for_when_it_is_restored()
    {
        var c = await factory.UserAsync();
        var r = await c.CreateResourceAsync(extra: new { tags = new[] { "from" } });
        await c.CreateResourceAsync(extra: new { tags = new[] { "into" } });
        await c.DeleteAsync($"/api/v1/resources/{r.Id()}").ExpectAsync(HttpStatusCode.NoContent);
        var tags = await TagsAsync(c);

        await c.PostAsync($"/api/v1/tags/{tags.Single(t => t.Name == "from").Id}/merge", new { target_tag_id = tags.Single(t => t.Name == "into").Id }).ExpectAsync(HttpStatusCode.OK);

        var back = await c.PostAsync($"/api/v1/resources/{r.Id()}/restore").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["into"], back.GetProperty("tags").Strings());
    }
}
