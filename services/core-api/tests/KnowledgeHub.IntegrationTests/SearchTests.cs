using System.Net;
using Xunit;

namespace KnowledgeHub.IntegrationTests;

/// <summary>Listing, filtering, sorting, paging and keyword search (FR-SRCH-01 to 04).</summary>
[Collection(ApiTestGroup.Name)]
public class SearchTests(ApiFactory factory)
{
    private static string Q(string text) => LibraryApi.Q(text);

    // ---------- keyword search (FR-SRCH-01) ----------

    [Fact]
    public async Task Search_matches_title_description_notes_tags_and_the_words_in_the_url()
    {
        var c = await factory.UserAsync();
        var byTitle = await c.CreateResourceAsync(extra: new { title = "Zebra handbook" });
        var byDescription = await c.CreateResourceAsync(extra: new { title = "x1", description = "All about zebra herds" });
        var byNotes = await c.CreateResourceAsync(extra: new { title = "x2", notes = "# my zebra notes" });
        var byTag = await c.CreateResourceAsync(extra: new { title = "x3", tags = new[] { "zebra" } });
        var byUrl = await c.CreateResourceAsync("https://zebra.example.org/docs", new { title = "x4" });
        await c.CreateResourceAsync(extra: new { title = "Giraffe handbook" });

        var found = (await c.ListAsync("q=zebra")).Ids();

        Assert.Equivalent(new[] { byTitle.Id(), byDescription.Id(), byNotes.Id(), byTag.Id(), byUrl.Id() }, found);
    }

    [Fact]
    public async Task Search_uses_english_stemming()
    {
        var c = await factory.UserAsync();
        var r = await c.CreateResourceAsync(extra: new { title = "Indexing databases" });

        Assert.Contains(r.Id(), (await c.ListAsync("q=database")).Ids());
        Assert.Contains(r.Id(), (await c.ListAsync("q=index")).Ids());
        Assert.Contains(r.Id(), (await c.ListAsync("q=DATABASES")).Ids());
    }

    [Fact]
    public async Task The_last_word_matches_as_a_prefix_so_results_appear_while_typing()
    {
        var c = await factory.UserAsync();
        var r = await c.CreateResourceAsync(extra: new { title = "PostgreSQL connection pooling" });

        Assert.Contains(r.Id(), (await c.ListAsync("q=postg")).Ids());
        Assert.Contains(r.Id(), (await c.ListAsync("q=connection+poo")).Ids());
        // Only the last word is a prefix: "conn" alone as a non-final word is not a stem of "connection"... but is a word.
        Assert.Empty((await c.ListAsync("q=zzz")).Ids());
    }

    [Fact]
    public async Task Every_word_must_match()
    {
        var c = await factory.UserAsync();
        var both = await c.CreateResourceAsync(extra: new { title = "Redis cluster guide" });
        await c.CreateResourceAsync(extra: new { title = "Redis basics" });
        await c.CreateResourceAsync(extra: new { title = "Kubernetes cluster guide" });

        Assert.Equal([both.Id()], (await c.ListAsync("q=redis+cluster")).Ids());
    }

    [Fact]
    public async Task A_title_match_ranks_above_a_tag_match_which_ranks_above_a_notes_match()
    {
        var c = await factory.UserAsync();
        // Created oldest first, so recency alone would give the reverse order.
        var notes = await c.CreateResourceAsync(extra: new { title = "n", notes = "talks about quokka" });
        var tag = await c.CreateResourceAsync(extra: new { title = "t", tags = new[] { "quokka" } });
        var title = await c.CreateResourceAsync(extra: new { title = "Quokka facts" });

        var page = await c.ListAsync("q=quokka");

        Assert.Equal([title.Id(), tag.Id(), notes.Id()], page.Ids());
    }

    [Fact]
    public async Task With_a_query_relevance_is_the_default_order_and_it_can_be_overridden()
    {
        var c = await factory.UserAsync();
        var weak = await c.CreateResourceAsync(extra: new { title = "w", notes = "mentions wombat once" });
        var strong = await c.CreateResourceAsync(extra: new { title = "Wombat" });
        await Task.Delay(5, TestContext.Current.CancellationToken);
        var newest = await c.CreateResourceAsync(extra: new { title = "n", notes = "another wombat note" });

        Assert.Equal(strong.Id(), (await c.ListAsync("q=wombat")).Ids()[0]);
        Assert.Equal([newest.Id(), strong.Id(), weak.Id()], (await c.ListAsync("q=wombat&sort=newest")).Ids());
        Assert.Equal([weak.Id(), strong.Id(), newest.Id()], (await c.ListAsync("q=wombat&sort=oldest")).Ids());
    }

    [Fact]
    public async Task An_empty_or_symbol_only_query_lists_everything()
    {
        var c = await factory.UserAsync();
        await c.CreateResourceAsync();
        await c.CreateResourceAsync();

        Assert.Equal(2, (await c.ListAsync()).GetProperty("total").GetInt32());
        Assert.Equal(2, (await c.ListAsync("q=")).GetProperty("total").GetInt32());
        Assert.Equal(2, (await c.ListAsync("q=" + Q("!!! ??? ..."))).GetProperty("total").GetInt32());
    }

    [Theory]
    [InlineData("'; DROP TABLE core.resources; --")]
    [InlineData("a & b | !c")]
    [InlineData("foo:* bar:*")]
    [InlineData("((")]
    [InlineData("\\")]
    [InlineData("the")]
    [InlineData("naïve café 日本語")]
    public async Task Awkward_queries_never_break_the_search_or_inject_query_syntax(string query)
    {
        var c = await factory.UserAsync();
        await c.CreateResourceAsync(extra: new { title = "Normal" });

        var response = await c.GetAsync("/api/v1/resources?q=" + Q(query));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await c.GetAsync("/api/v1/resources").ExpectAsync(HttpStatusCode.OK); // the table is still there
    }

    [Fact]
    public async Task Unicode_words_are_searchable()
    {
        var c = await factory.UserAsync();
        var r = await c.CreateResourceAsync(extra: new { title = "Café culture in Zürich" });

        Assert.Contains(r.Id(), (await c.ListAsync("q=" + Q("zürich"))).Ids());
    }

    // ---------- filters (FR-SRCH-02) ----------

    [Fact]
    public async Task Archived_resources_are_hidden_unless_the_archived_filter_is_on()
    {
        var c = await factory.UserAsync();
        var live = await c.CreateResourceAsync(extra: new { title = "Gnu live" });
        var old = await c.CreateResourceAsync(extra: new { title = "Gnu old" });
        await c.PatchAsync($"/api/v1/resources/{old.Id()}", new { is_archived = true }).ExpectAsync(HttpStatusCode.OK);

        Assert.Equal([live.Id()], (await c.ListAsync()).Ids());
        Assert.Equal([live.Id()], (await c.ListAsync("q=gnu")).Ids());
        Assert.Equal([old.Id()], (await c.ListAsync("archived=true")).Ids());
        Assert.Equal([old.Id()], (await c.ListAsync("archived=true&q=gnu")).Ids());
    }

    [Fact]
    public async Task Type_is_repeatable_and_combines_with_or()
    {
        var c = await factory.UserAsync();
        var video = await c.CreateResourceAsync(extra: new { type = "video" });
        var pdf = await c.CreateResourceAsync(extra: new { type = "pdf" });
        await c.CreateResourceAsync(extra: new { type = "repo" });

        Assert.Equal([video.Id()], (await c.ListAsync("type=video")).Ids());
        Assert.Equivalent(new[] { video.Id(), pdf.Id() }, (await c.ListAsync("type=video&type=pdf")).Ids());
        Assert.Equal(1, (await c.ListAsync("type=VIDEO")).GetProperty("total").GetInt32()); // case does not matter
    }

    [Fact]
    public async Task Tag_is_repeatable_and_every_tag_must_match()
    {
        var c = await factory.UserAsync();
        var both = await c.CreateResourceAsync(extra: new { tags = new[] { "red", "round" } });
        await c.CreateResourceAsync(extra: new { tags = new[] { "red" } });
        await c.CreateResourceAsync(extra: new { tags = new[] { "round" } });

        Assert.Equal([both.Id()], (await c.ListAsync("tag=red&tag=round")).Ids());
        Assert.Equal(2, (await c.ListAsync("tag=red")).GetProperty("total").GetInt32());
        Assert.Empty((await c.ListAsync("tag=red&tag=blue")).Ids());
    }

    [Fact]
    public async Task Collection_and_inbox_filters()
    {
        var c = await factory.UserAsync();
        var col = await c.CreateCollectionAsync("Box");
        var inBox = await c.CreateResourceAsync(extra: new { collection_id = col.Id() });
        var inInbox = await c.CreateResourceAsync();

        Assert.Equal([inBox.Id()], (await c.ListAsync("collection_id=" + col.Id())).Ids());
        Assert.Equal([inInbox.Id()], (await c.ListAsync("collection_id=inbox")).Ids());
        Assert.Empty((await c.ListAsync("collection_id=" + Guid.NewGuid())).Ids()); // unknown: empty, not an error
    }

    [Fact]
    public async Task Favourite_and_date_range_filters()
    {
        var c = await factory.UserAsync();
        var fav = await c.CreateResourceAsync();
        await c.CreateResourceAsync();
        await c.PatchAsync($"/api/v1/resources/{fav.Id()}", new { is_favourite = true }).ExpectAsync(HttpStatusCode.OK);

        Assert.Equal([fav.Id()], (await c.ListAsync("favourite=true")).Ids());
        Assert.Equal(1, (await c.ListAsync("favourite=false")).GetProperty("total").GetInt32());

        var past = Q(DateTimeOffset.UtcNow.AddHours(-1).ToString("o"));
        var future = Q(DateTimeOffset.UtcNow.AddHours(1).ToString("o"));
        Assert.Equal(2, (await c.ListAsync($"created_from={past}&created_to={future}")).GetProperty("total").GetInt32());
        Assert.Empty((await c.ListAsync($"created_from={future}")).Ids());
        Assert.Empty((await c.ListAsync($"created_to={past}")).Ids());
    }

    [Fact]
    public async Task Filters_combine_with_and_and_with_the_keyword_query()
    {
        var c = await factory.UserAsync();
        var hit = await c.CreateResourceAsync(extra: new { title = "Lemur facts", type = "video", tags = new[] { "zoo" } });
        await c.CreateResourceAsync(extra: new { title = "Lemur facts", type = "article", tags = new[] { "zoo" } });
        await c.CreateResourceAsync(extra: new { title = "Lemur facts", type = "video" });
        await c.CreateResourceAsync(extra: new { title = "Otter facts", type = "video", tags = new[] { "zoo" } });

        Assert.Equal([hit.Id()], (await c.ListAsync("q=lemur&type=video&tag=zoo")).Ids());
    }

    // ---------- sorting and paging (FR-SRCH-03, 04) ----------

    [Fact]
    public async Task Sorting_by_newest_oldest_title_and_updated()
    {
        var c = await factory.UserAsync();
        var banana = await c.CreateResourceAsync(extra: new { title = "banana" });
        var apple = await c.CreateResourceAsync(extra: new { title = "Apple" });
        var cherry = await c.CreateResourceAsync(extra: new { title = "cherry" });

        Assert.Equal([cherry.Id(), apple.Id(), banana.Id()], (await c.ListAsync()).Ids()); // newest is the default
        Assert.Equal([banana.Id(), apple.Id(), cherry.Id()], (await c.ListAsync("sort=oldest")).Ids());
        Assert.Equal([apple.Id(), banana.Id(), cherry.Id()], (await c.ListAsync("sort=title")).Ids()); // A to Z, ignoring case

        await c.PatchAsync($"/api/v1/resources/{banana.Id()}", new { notes = "touched" }).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(banana.Id(), (await c.ListAsync("sort=updated")).Ids()[0]);
    }

    [Fact]
    public async Task Sorting_by_title_uses_the_url_when_there_is_no_title()
    {
        var c = await factory.UserAsync();
        // The sort key is the title, or the whole URL (which starts "https://") when there is none.
        var zulu = await c.CreateResourceAsync("https://aaa.example.com/a", new { title = "Zulu" });
        var hello = await c.CreateResourceAsync("https://mmm.example.com/a", new { title = "Hello" });
        var untitledA = await c.CreateResourceAsync("https://aaa.example.com/b");
        var untitledZ = await c.CreateResourceAsync("https://zzz.example.com/z");

        Assert.Equal([hello.Id(), untitledA.Id(), untitledZ.Id(), zulu.Id()], (await c.ListAsync("sort=title")).Ids());
    }

    [Fact]
    public async Task Pages_have_the_documented_shape_and_a_page_past_the_end_is_empty()
    {
        var c = await factory.UserAsync();
        for (var i = 0; i < 25; i++) await c.CreateResourceAsync();

        var p1 = await c.ListAsync("page_size=10");
        var p2 = await c.ListAsync("page=2&page_size=10");
        var p3 = await c.ListAsync("page=3&page_size=10");
        var p4 = await c.ListAsync("page=4&page_size=10");

        Assert.Equal(10, p1.GetProperty("items").GetArrayLength());
        Assert.Equal(1, p1.GetProperty("page").GetInt32());
        Assert.Equal(10, p1.GetProperty("page_size").GetInt32());
        Assert.Equal(25, p1.GetProperty("total").GetInt32());
        Assert.Equal(10, p2.GetProperty("items").GetArrayLength());
        Assert.Equal(5, p3.GetProperty("items").GetArrayLength());
        Assert.Empty(p4.GetProperty("items").EnumerateArray());
        Assert.Equal(25, p4.GetProperty("total").GetInt32());
        var all = p1.Ids().Concat(p2.Ids()).Concat(p3.Ids()).ToList();
        Assert.Equal(25, all.Distinct().Count()); // no resource appears twice
        Assert.Equal(20, (await c.ListAsync()).GetProperty("items").GetArrayLength()); // default page size
    }

    [Fact]
    public async Task Page_size_up_to_100_is_allowed()
    {
        var c = await factory.UserAsync();
        await c.CreateResourceAsync();
        Assert.Equal(100, (await c.ListAsync("page_size=100")).GetProperty("page_size").GetInt32());
    }

    [Theory]
    [InlineData("page_size=101", "page_size")]
    [InlineData("page_size=0", "page_size")]
    [InlineData("page=0", "page")]
    [InlineData("sort=sideways", "sort")]
    [InlineData("sort=1", "sort")]
    [InlineData("type=podcast", "type")]
    [InlineData("tag=bad%2Ftag", "tag")]
    [InlineData("collection_id=not-a-guid", "collection_id")]
    public async Task Invalid_parameters_return_400_with_the_field_named(string query, string field)
    {
        var c = await factory.UserAsync();

        var response = await c.GetAsync("/api/v1/resources?" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.Json()).GetProperty("errors").TryGetProperty(field, out _));
    }

    // ---------- the index follows tag changes (tags_text) ----------

    [Fact]
    public async Task Search_follows_tag_renames_merges_and_deletes()
    {
        var c = await factory.UserAsync();
        var r = await c.CreateResourceAsync(extra: new { title = "plain", tags = new[] { "oldtag" } });
        var tags = (await c.GetAsync("/api/v1/tags").ExpectAsync(HttpStatusCode.OK)).GetProperty("items");
        var oldTag = tags.EnumerateArray().Single().Id();
        Assert.Contains(r.Id(), (await c.ListAsync("q=oldtag")).Ids());

        await c.PatchAsync($"/api/v1/tags/{oldTag}", new { name = "newtag" }).ExpectAsync(HttpStatusCode.OK);
        Assert.Contains(r.Id(), (await c.ListAsync("q=newtag")).Ids());
        Assert.Empty((await c.ListAsync("q=oldtag")).Ids());

        var other = await c.CreateResourceAsync(extra: new { tags = new[] { "target" } });
        var target = (await c.GetAsync("/api/v1/tags").ExpectAsync(HttpStatusCode.OK)).GetProperty("items").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "target").Id();
        await c.PostAsync($"/api/v1/tags/{oldTag}/merge", new { target_tag_id = target }).ExpectAsync(HttpStatusCode.OK);
        Assert.Contains(r.Id(), (await c.ListAsync("q=target")).Ids());
        Assert.Empty((await c.ListAsync("q=newtag")).Ids());

        await c.DeleteAsync($"/api/v1/tags/{target}").ExpectAsync(HttpStatusCode.NoContent);
        Assert.Empty((await c.ListAsync("q=target")).Ids());
        Assert.Contains(other.Id(), (await c.ListAsync()).Ids());
    }

    [Fact]
    public async Task Search_follows_tags_added_by_edit_and_in_bulk()
    {
        var c = await factory.UserAsync();
        var a = await c.CreateResourceAsync(extra: new { title = "a" });
        var b = await c.CreateResourceAsync(extra: new { title = "b" });

        await c.PatchAsync($"/api/v1/resources/{a.Id()}", new { tags = new[] { "viaedit" } }).ExpectAsync(HttpStatusCode.OK);
        await c.PostAsync("/api/v1/resources/bulk", new { action = "add_tags", ids = new[] { b.Id() }, tags = new[] { "viabulk" } }).ExpectAsync(HttpStatusCode.OK);

        Assert.Equal([a.Id()], (await c.ListAsync("q=viaedit")).Ids());
        Assert.Equal([b.Id()], (await c.ListAsync("q=viabulk")).Ids());

        await c.PostAsync("/api/v1/resources/bulk", new { action = "remove_tags", ids = new[] { b.Id() }, tags = new[] { "viabulk" } }).ExpectAsync(HttpStatusCode.OK);
        Assert.Empty((await c.ListAsync("q=viabulk")).Ids());
    }
}
