using System.Net;
using System.Text.Json;
using KnowledgeHub.Application.Common;
using KnowledgeHub.Application.Insights;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KnowledgeHub.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public class DashboardTests(ApiFactory factory)
{
    private static Task<JsonElement> Dash(TestClient c, string query = "") =>
        c.GetAsync("/api/v1/dashboard" + query).ExpectAsync(HttpStatusCode.OK);

    private static string Id(JsonElement e) => e.GetProperty("id").GetString()!;

    private static int Total(JsonElement list) => list.GetProperty("total").GetInt32();

    // ---------- the numbers ----------

    [Fact]
    public async Task A_new_user_gets_zeros_twelve_empty_weeks_and_an_unticked_checklist()
    {
        var c = await factory.UserAsync();
        var d = await Dash(c);

        Assert.Equal([0, 0, 0, 0], new[] { "resources", "collections", "tags", "favourites" }.Select(k => d.GetProperty("totals").GetProperty(k).GetInt32()));
        Assert.All(d.GetProperty("by_type").EnumerateObject(), p => Assert.Equal(0, p.Value.GetInt32()));
        Assert.Equal(["article", "video", "pdf", "course", "repo", "website", "other"], d.GetProperty("by_type").EnumerateObject().Select(p => p.Name));
        Assert.Empty(d.GetProperty("recent").EnumerateArray());
        var weeks = d.GetProperty("weekly_activity").EnumerateArray().ToList();
        Assert.Equal(12, weeks.Count);
        Assert.All(weeks, w => Assert.Equal(0, w.GetProperty("count").GetInt32()));
        var onboarding = d.GetProperty("onboarding");
        Assert.False(onboarding.GetProperty("dismissed").GetBoolean());
        Assert.All(onboarding.GetProperty("steps").EnumerateObject(), p => Assert.False(p.Value.GetBoolean()));
    }

    [Fact]
    public async Task The_totals_match_the_list_endpoints_and_the_types_add_up()
    {
        var c = await factory.UserAsync();
        await c.CreateCollectionAsync("Databases");
        await c.CreateCollectionAsync("Algorithms");
        await c.CreateResourceAsync(extra: new { type = "article", tags = new[] { "a", "b" }, is_favourite = true });
        await c.CreateResourceAsync(extra: new { type = "article", tags = new[] { "a" } });
        await c.CreateResourceAsync(extra: new { type = "video" });
        var archived = await c.CreateResourceAsync(extra: new { type = "pdf" });
        var deleted = await c.CreateResourceAsync(extra: new { type = "repo" });
        await c.PatchAsync($"/api/v1/resources/{Id(archived)}", new { is_archived = true }).ExpectAsync(HttpStatusCode.OK);
        await c.SendAsync(HttpMethod.Delete, $"/api/v1/resources/{Id(deleted)}").ExpectAsync(HttpStatusCode.NoContent);
        var fav = (await c.GetAsync("/api/v1/resources?favourite=true").ExpectAsync(HttpStatusCode.OK));

        var d = await Dash(c);
        var totals = d.GetProperty("totals");

        Assert.Equal(Total(await c.GetAsync("/api/v1/resources").ExpectAsync(HttpStatusCode.OK)), totals.GetProperty("resources").GetInt32());
        Assert.Equal(3, totals.GetProperty("resources").GetInt32());   // the archived and the deleted one are not counted
        Assert.Equal(Total(fav), totals.GetProperty("favourites").GetInt32());
        Assert.Equal(2, totals.GetProperty("collections").GetInt32());
        Assert.Equal((await c.GetAsync("/api/v1/tags").ExpectAsync(HttpStatusCode.OK)).GetProperty("items").GetArrayLength(), totals.GetProperty("tags").GetInt32());
        var byType = d.GetProperty("by_type");
        Assert.Equal((2, 1, 0, 0, 0), (byType.GetProperty("article").GetInt32(), byType.GetProperty("video").GetInt32(), byType.GetProperty("pdf").GetInt32(), byType.GetProperty("repo").GetInt32(), byType.GetProperty("other").GetInt32()));
        Assert.Equal(totals.GetProperty("resources").GetInt32(), byType.EnumerateObject().Sum(p => p.Value.GetInt32()));
    }

    [Fact]
    public async Task Recent_is_the_five_newest_first_with_their_tags_and_leaves_out_archived_ones()
    {
        var c = await factory.UserAsync();
        var made = new List<string>();
        for (var i = 0; i < 7; i++)
        {
            made.Add(Id(await c.CreateResourceAsync(extra: new { title = $"R{i}", tags = new[] { "t" } })));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        await c.PatchAsync($"/api/v1/resources/{made[6]}", new { is_archived = true }).ExpectAsync(HttpStatusCode.OK);

        var recent = (await Dash(c)).GetProperty("recent").EnumerateArray().ToList();

        Assert.Equal(made.Take(6).Reverse().Take(5), recent.Select(Id));
        Assert.Equal(["t"], recent[0].GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
    }

    [Fact]
    public async Task Nothing_of_another_users_is_counted_or_shown()
    {
        var mine = await factory.UserAsync();
        var theirs = await factory.UserAsync();
        await theirs.CreateResourceAsync(extra: new { title = "Secret", is_favourite = true, tags = new[] { "x" } });
        await theirs.CreateCollectionAsync("Theirs");

        var d = await Dash(mine);

        Assert.Equal(0, d.GetProperty("totals").GetProperty("resources").GetInt32());
        Assert.Equal(0, d.GetProperty("totals").GetProperty("collections").GetInt32());
        Assert.Empty(d.GetProperty("recent").EnumerateArray());
    }

    // ---------- weekly activity ----------

    private async Task SetCreatedAt(string resourceId, DateTimeOffset when)
    {
        await using var db = factory.Db.Admin();
        await db.Resources.IgnoreQueryFilters().Where(r => r.Id == Guid.Parse(resourceId))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.CreatedAt, when.ToUniversalTime()), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Weekly_activity_is_twelve_weeks_oldest_first_starting_on_mondays_ending_with_this_week()
    {
        var c = await factory.UserAsync();
        var thisWeek = await c.CreateResourceAsync();
        var lastWeek = await c.CreateResourceAsync();
        var twice = new[] { await c.CreateResourceAsync(), await c.CreateResourceAsync() };
        var tooOld = await c.CreateResourceAsync();
        var oldest = await c.CreateResourceAsync();
        var now = DateTimeOffset.UtcNow;
        var monday = DashboardService.StartOfWeek(DateOnly.FromDateTime(now.UtcDateTime));
        DateTimeOffset At(DateOnly d, int hour) => new(d.ToDateTime(new TimeOnly(hour, 0)), TimeSpan.Zero);
        await SetCreatedAt(Id(lastWeek), At(monday.AddDays(-3), 12));
        await SetCreatedAt(Id(twice[0]), At(monday.AddDays(-14), 9));
        await SetCreatedAt(Id(twice[1]), At(monday.AddDays(-9), 23));
        await SetCreatedAt(Id(oldest), At(monday.AddDays(-77), 10));    // the first of the twelve weeks
        await SetCreatedAt(Id(tooOld), At(monday.AddDays(-78), 10));    // one week before it
        _ = thisWeek;

        var weeks = (await Dash(c, "?time_zone=UTC")).GetProperty("weekly_activity").EnumerateArray()
            .Select(w => (DateOnly.Parse(w.GetProperty("week_start").GetString()!, System.Globalization.CultureInfo.InvariantCulture), w.GetProperty("count").GetInt32())).ToList();

        Assert.Equal(12, weeks.Count);
        Assert.All(weeks, w => Assert.Equal(DayOfWeek.Monday, w.Item1.DayOfWeek));
        Assert.Equal(weeks.Select(w => w.Item1).Order(), weeks.Select(w => w.Item1));
        Assert.Equal(monday, weeks[^1].Item1);
        Assert.Equal(monday.AddDays(-77), weeks[0].Item1);
        Assert.Equal(1, weeks[0].Item2);          // included
        Assert.Equal(1, weeks[^1].Item2);          // this week
        Assert.Equal(1, weeks[^2].Item2);          // last week
        Assert.Equal(2, weeks[^3].Item2 + weeks[^4].Item2);   // 14 days and 9 days back
        Assert.Equal(5, weeks.Sum(w => w.Item2));  // the one before the first week is not counted
    }

    [Fact]
    public async Task A_resource_added_just_after_midnight_belongs_to_the_week_in_the_users_own_time_zone()
    {
        var c = await factory.UserAsync();
        var r = await c.CreateResourceAsync();
        var nz = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");
        var nowNz = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, nz);
        var lastMondayNz = DashboardService.StartOfWeek(DateOnly.FromDateTime(nowNz.DateTime)).AddDays(-7);
        // 00:30 on a Monday in Auckland: still the Sunday before in UTC.
        var local = lastMondayNz.ToDateTime(new TimeOnly(0, 30));
        var instant = new DateTimeOffset(local, nz.GetUtcOffset(local));
        await SetCreatedAt(Id(r), instant);
        Assert.Equal(DayOfWeek.Sunday, instant.UtcDateTime.DayOfWeek);

        async Task<DateOnly?> WeekOf(string zone)
        {
            var weeks = (await Dash(c, "?time_zone=" + Uri.EscapeDataString(zone))).GetProperty("weekly_activity").EnumerateArray();
            var hit = weeks.SingleOrDefault(w => w.GetProperty("count").GetInt32() == 1);
            return hit.ValueKind == JsonValueKind.Undefined ? null : DateOnly.Parse(hit.GetProperty("week_start").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        }

        Assert.Equal(lastMondayNz, await WeekOf("Pacific/Auckland"));
        Assert.Equal(lastMondayNz.AddDays(-7), await WeekOf("UTC"));
        Assert.Equal(await WeekOf("UTC"), await WeekOf(""));   // UTC is the default
    }

    [Theory]
    [InlineData("Mars/Olympus")]
    [InlineData("not a zone")]
    [InlineData("../../etc/passwd")]
    public async Task An_unknown_time_zone_is_a_validation_error(string zone)
    {
        var c = await factory.UserAsync();
        var response = await Raw(c.GetAsync("/api/v1/dashboard?time_zone=" + Uri.EscapeDataString(zone)), HttpStatusCode.BadRequest);
        Assert.Equal("time_zone", (await response.Json()).GetProperty("errors").EnumerateObject().Single().Name);
    }

    private static async Task<HttpResponseMessage> Raw(Task<HttpResponseMessage> call, HttpStatusCode status)
    {
        var response = await call;
        Assert.True(response.StatusCode == status, $"expected {status}, got {(int)response.StatusCode}");
        return response;
    }

    // ---------- the checklist ----------

    private static bool Step(JsonElement d, string name) => d.GetProperty("onboarding").GetProperty("steps").GetProperty(name).GetBoolean();

    [Fact]
    public async Task Saving_and_creating_tick_themselves_and_saving_stays_ticked_after_deleting()
    {
        var c = await factory.UserAsync();
        var r = await c.CreateResourceAsync();
        var col = await c.CreateCollectionAsync("First");
        var d = await Dash(c);
        Assert.True(Step(d, "first_resource") && Step(d, "first_collection"));
        Assert.False(Step(d, "tried_search") || Step(d, "asked_question"));

        await c.SendAsync(HttpMethod.Delete, $"/api/v1/resources/{Id(r)}").ExpectAsync(HttpStatusCode.NoContent);
        await c.SendAsync(HttpMethod.Delete, $"/api/v1/collections/{Id(col)}?resources=delete").ExpectAsync(HttpStatusCode.NoContent);

        d = await Dash(c);
        Assert.True(Step(d, "first_resource"));      // they did save one
        Assert.False(Step(d, "first_collection"));   // there is no collection to show for it
    }

    [Fact]
    public async Task A_keyword_search_ticks_the_search_step_but_an_empty_one_does_not()
    {
        var c = await factory.UserAsync();
        await c.GetAsync("/api/v1/resources").ExpectAsync(HttpStatusCode.OK);
        await c.GetAsync("/api/v1/resources?q=").ExpectAsync(HttpStatusCode.OK);
        await c.GetAsync("/api/v1/resources?q=%20%20").ExpectAsync(HttpStatusCode.OK);
        Assert.False(Step(await Dash(c), "tried_search"));

        await c.GetAsync("/api/v1/resources?q=pooling").ExpectAsync(HttpStatusCode.OK);
        Assert.True(Step(await Dash(c), "tried_search"));
    }

    [Fact]
    public async Task A_search_by_meaning_ticks_it_too_even_when_it_fell_back_to_keywords()
    {
        var c = await factory.UserAsync();   // the AI service is off in this host, so this is the fallback
        await c.GetAsync("/api/v1/search/semantic?q=pooling").ExpectAsync(HttpStatusCode.OK);
        Assert.True(Step(await Dash(c), "tried_search"));
    }

    [Fact]
    public async Task Asking_a_question_ticks_its_step_but_a_refused_question_does_not()
    {
        var ai = new RecordingAi();
        using var web = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Outbox:Enabled", "false");
            b.ConfigureServices(s => s.AddSingleton<IAiClient>(ai));
        });
        var c = new TestClient(web);
        await c.RegisterAsync(TestClient.UniqueEmail());
        var session = await c.PostAsync("/api/v1/chat/sessions", new { }).ExpectAsync(HttpStatusCode.Created);
        var path = $"/api/v1/chat/sessions/{Id(session)}/messages";

        ai.ChatOpenFailure = () => new AiUnavailableException("down");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await c.PostAsync(path, new { content = "q" })).StatusCode);
        Assert.False(Step(await Dash(c), "asked_question"));

        ai.ChatOpenFailure = null;
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync(path, new { content = "q" })).StatusCode);
        Assert.True(Step(await Dash(c), "asked_question"));
    }

    [Fact]
    public async Task Dismissing_the_checklist_is_the_users_preference_and_the_dashboard_reports_it()
    {
        var c = await factory.UserAsync();
        await c.PatchAsync("/api/v1/me", new { preferences = new { onboarding_dismissed = true } }).ExpectAsync(HttpStatusCode.OK);
        Assert.True((await Dash(c)).GetProperty("onboarding").GetProperty("dismissed").GetBoolean());
    }

    [Fact]
    public async Task The_dashboard_needs_a_session()
    {
        await factory.NewClient().GetAsync("/api/v1/dashboard").ExpectAsync(HttpStatusCode.Unauthorized);
    }
}
