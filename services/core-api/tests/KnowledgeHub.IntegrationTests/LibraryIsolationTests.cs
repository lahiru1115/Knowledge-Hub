using System.Net;
using Xunit;

namespace KnowledgeHub.IntegrationTests;

/// <summary>
/// T-IT-SEC-01 for the library (NFR-SEC-05, DR-02): a second user calls every endpoint with the first user's
/// IDs and must always get 404, and must never see the first user's data in a list.
/// </summary>
[Collection(ApiTestGroup.Name)]
public class LibraryIsolationTests(ApiFactory factory)
{
    private sealed record World(TestClient Owner, TestClient Intruder, string CollectionId, string ResourceId, string TagId, string OwnTagId, string OwnCollectionId);

    private async Task<World> ArrangeAsync()
    {
        var owner = await factory.UserAsync();
        var intruder = await factory.UserAsync();
        var collection = await owner.CreateCollectionAsync("Private");
        var resource = await owner.CreateResourceAsync("https://example.com/secret", new { title = "Secret plan", notes = "classified", collection_id = collection.Id(), tags = new[] { "private-tag" } });
        var tagId = (await owner.GetAsync("/api/v1/tags").ExpectAsync(HttpStatusCode.OK)).GetProperty("items").EnumerateArray().Single().Id();
        var ownResource = await intruder.CreateResourceAsync(extra: new { tags = new[] { "mine" } });
        var ownTag = (await intruder.GetAsync("/api/v1/tags").ExpectAsync(HttpStatusCode.OK)).GetProperty("items").EnumerateArray().Single().Id();
        var ownCollection = await intruder.CreateCollectionAsync("Mine");
        _ = ownResource;
        return new World(owner, intruder, collection.Id(), resource.Id(), tagId, ownTag, ownCollection.Id());
    }

    [Fact]
    public async Task Another_users_resource_is_a_404_for_every_operation()
    {
        var w = await ArrangeAsync();
        var i = w.Intruder;
        var id = w.ResourceId;

        Assert.Equal(HttpStatusCode.NotFound, (await i.GetAsync($"/api/v1/resources/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await i.PatchAsync($"/api/v1/resources/{id}", new { title = "pwned" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await i.DeleteAsync($"/api/v1/resources/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await i.PostAsync($"/api/v1/resources/{id}/restore")).StatusCode);

        // It was not touched, and a 404 for a foreign ID looks exactly like a 404 for an ID that never existed.
        var untouched = await w.Owner.GetAsync($"/api/v1/resources/{id}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Secret plan", untouched.GetProperty("title").GetString());
        var foreign = await (await i.GetAsync($"/api/v1/resources/{id}")).Json();
        var missing = await (await i.GetAsync($"/api/v1/resources/{Guid.NewGuid()}")).Json();
        Assert.Equal("not_found", foreign.GetProperty("code").GetString());
        Assert.Equal(missing.GetProperty("title").GetString(), foreign.GetProperty("title").GetString());
        Assert.Equal(missing.GetProperty("detail").GetString(), foreign.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Another_users_collection_is_a_404_for_every_operation()
    {
        var w = await ArrangeAsync();
        var i = w.Intruder;
        var id = w.CollectionId;

        Assert.Equal(HttpStatusCode.NotFound, (await i.GetAsync($"/api/v1/collections/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await i.PatchAsync($"/api/v1/collections/{id}", new { name = "pwned" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await i.DeleteAsync($"/api/v1/collections/{id}?resources=delete")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await i.PutAsync("/api/v1/collections/order", new { ids = new[] { id, w.OwnCollectionId } })).StatusCode);

        var still = await w.Owner.GetAsync($"/api/v1/collections/{id}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Private", still.GetProperty("name").GetString());
        await w.Owner.GetAsync($"/api/v1/resources/{w.ResourceId}").ExpectAsync(HttpStatusCode.OK); // its resources were not deleted
    }

    [Fact]
    public async Task Another_users_tag_is_a_404_for_every_operation()
    {
        var w = await ArrangeAsync();
        var i = w.Intruder;

        Assert.Equal(HttpStatusCode.NotFound, (await i.PatchAsync($"/api/v1/tags/{w.TagId}", new { name = "pwned" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await i.DeleteAsync($"/api/v1/tags/{w.TagId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await i.PostAsync($"/api/v1/tags/{w.TagId}/merge", new { target_tag_id = w.OwnTagId })).StatusCode); // foreign source
        Assert.Equal(HttpStatusCode.NotFound, (await i.PostAsync($"/api/v1/tags/{w.OwnTagId}/merge", new { target_tag_id = w.TagId })).StatusCode); // foreign target

        var tags = await w.Owner.GetAsync("/api/v1/tags").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("private-tag", tags.GetProperty("items").EnumerateArray().Single().GetProperty("name").GetString());
        Assert.Equal(["mine"], (await i.GetAsync("/api/v1/tags").ExpectAsync(HttpStatusCode.OK)).GetProperty("items").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToArray());
    }

    [Fact]
    public async Task References_to_another_users_collection_are_refused_wherever_they_appear()
    {
        var w = await ArrangeAsync();
        var i = w.Intruder;
        var mine = await i.CreateResourceAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await i.PostAsync("/api/v1/resources", new { url = "https://e.com", collection_id = w.CollectionId })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await i.PatchAsync($"/api/v1/resources/{mine.Id()}", new { collection_id = w.CollectionId })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await i.PostAsync("/api/v1/resources/bulk", new { action = "move", ids = new[] { mine.Id() }, collection_id = w.CollectionId })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await i.DeleteAsync($"/api/v1/collections/{w.OwnCollectionId}?resources=move&target_collection_id={w.CollectionId}")).StatusCode);

        Assert.Equal(0, (await w.Owner.GetAsync($"/api/v1/collections/{w.CollectionId}").ExpectAsync(HttpStatusCode.OK)).GetProperty("resource_count").GetInt32() - 1);
        await i.GetAsync($"/api/v1/collections/{w.OwnCollectionId}").ExpectAsync(HttpStatusCode.OK); // not deleted by the refused call
    }

    [Fact]
    public async Task Bulk_with_another_users_ids_changes_nothing()
    {
        var w = await ArrangeAsync();
        var i = w.Intruder;
        var mine = await i.CreateResourceAsync();

        foreach (var body in new object[]
        {
            new { action = "delete", ids = new[] { mine.Id(), w.ResourceId } },
            new { action = "add_tags", ids = new[] { w.ResourceId }, tags = new[] { "pwned" } },
            new { action = "restore", ids = new[] { w.ResourceId } },
        })
            Assert.Equal(HttpStatusCode.NotFound, (await i.PostAsync("/api/v1/resources/bulk", body)).StatusCode);

        var intact = await w.Owner.GetAsync($"/api/v1/resources/{w.ResourceId}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["private-tag"], intact.GetProperty("tags").Strings());
        await i.GetAsync($"/api/v1/resources/{mine.Id()}").ExpectAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Lists_search_and_the_duplicate_check_never_show_another_users_data()
    {
        var w = await ArrangeAsync();
        var i = w.Intruder;

        Assert.DoesNotContain(w.ResourceId, (await i.ListAsync()).Ids());
        Assert.Empty((await i.ListAsync("q=secret")).Ids());
        Assert.Empty((await i.ListAsync("q=classified")).Ids()); // notes
        Assert.Empty((await i.ListAsync("q=" + LibraryApi.Q("private-tag"))).Ids());
        Assert.Empty((await i.ListAsync("tag=private-tag")).Ids());
        Assert.Empty((await i.ListAsync("collection_id=" + w.CollectionId)).Ids());
        var dup = await i.GetAsync("/api/v1/resources/check-url?url=" + LibraryApi.Q("https://example.com/secret")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, dup.GetProperty("existing").ValueKind);
        var collections = await i.GetAsync("/api/v1/collections").ExpectAsync(HttpStatusCode.OK);
        Assert.DoesNotContain(w.CollectionId, collections.GetProperty("items").EnumerateArray().Select(x => x.Id()));
        Assert.Equal(1, collections.GetProperty("inbox_count").GetInt32()); // only their own
    }

    [Fact]
    public async Task Every_library_endpoint_needs_a_session_and_a_csrf_token()
    {
        var anonymous = factory.NewClient();
        await anonymous.FetchCsrfAsync();
        var id = Guid.NewGuid();
        var reads = new[]
        {
            "/api/v1/collections", $"/api/v1/collections/{id}", "/api/v1/resources", $"/api/v1/resources/{id}",
            "/api/v1/resources/check-url?url=https://e.com", "/api/v1/tags",
        };
        foreach (var path in reads)
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);

        var writes = new (HttpMethod, string)[]
        {
            (HttpMethod.Post, "/api/v1/collections"), (HttpMethod.Put, "/api/v1/collections/order"),
            (HttpMethod.Patch, $"/api/v1/collections/{id}"), (HttpMethod.Delete, $"/api/v1/collections/{id}?resources=delete"),
            (HttpMethod.Post, "/api/v1/resources"), (HttpMethod.Post, "/api/v1/resources/bulk"),
            (HttpMethod.Patch, $"/api/v1/resources/{id}"), (HttpMethod.Delete, $"/api/v1/resources/{id}"),
            (HttpMethod.Post, $"/api/v1/resources/{id}/restore"), (HttpMethod.Patch, $"/api/v1/tags/{id}"),
            (HttpMethod.Post, $"/api/v1/tags/{id}/merge"), (HttpMethod.Delete, $"/api/v1/tags/{id}"),
        };
        var signedIn = await factory.UserAsync();
        foreach (var (method, path) in writes)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(method, path, new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await signedIn.SendAsync(method, path, new { }, csrf: false)).StatusCode);
        }
    }
}
