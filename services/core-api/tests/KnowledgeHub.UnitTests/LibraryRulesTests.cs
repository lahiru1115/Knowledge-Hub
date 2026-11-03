using System.Text.Json;
using KnowledgeHub.Application.Common;
using KnowledgeHub.Application.Library;
using KnowledgeHub.Domain;
using KnowledgeHub.Infrastructure.Library;
using Xunit;

namespace KnowledgeHub.UnitTests;

public class UrlNormalizerTests
{
    [Theory]
    [InlineData("https://Example.COM/Path", "https://example.com/Path")] // host is lowercased, path is not
    [InlineData("HTTP://example.com", "http://example.com")]
    [InlineData("https://example.com/a/", "https://example.com/a")]
    [InlineData("https://example.com/", "https://example.com")]
    [InlineData("https://example.com:443/a", "https://example.com/a")]
    [InlineData("http://example.com:80/a", "http://example.com/a")]
    [InlineData("https://example.com:8443/a", "https://example.com:8443/a")]
    [InlineData("https://example.com/a#frag", "https://example.com/a")]
    [InlineData("https://example.com/a?utm_source=x&id=7&utm_medium=y", "https://example.com/a?id=7")]
    [InlineData("https://example.com/a?UTM_Campaign=x", "https://example.com/a")]
    [InlineData("  https://example.com/a  ", "https://example.com/a")]
    public void Normalises_to_one_form(string input, string expected)
    {
        Assert.True(UrlNormalizer.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void Keeps_other_query_parameters_and_their_order()
    {
        Assert.True(UrlNormalizer.TryNormalize("https://example.com/s?b=2&a=1&utm_term=x", out var n));
        Assert.Equal("https://example.com/s?b=2&a=1", n);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("example.com")]
    [InlineData("ftp://example.com/file")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>")]
    [InlineData("https://")]
    [InlineData("//example.com")]
    [InlineData("http://E0\u00e8\U00016E1D\U00011897I\u00eb")]   // a host that is not a valid internationalised name once threw while it was read
    public void Rejects_anything_that_is_not_an_http_or_https_address(string? input)
    {
        Assert.False(UrlNormalizer.IsValid(input));
        Assert.False(UrlNormalizer.TryNormalize(input, out _));
    }

    [Fact]
    public void Accepts_2048_characters_and_rejects_2049()
    {
        var url = "https://example.com/" + new string('a', 2048 - "https://example.com/".Length);
        Assert.Equal(2048, url.Length);
        Assert.True(UrlNormalizer.IsValid(url));
        Assert.False(UrlNormalizer.IsValid(url + "a"));
    }

    [Fact]
    public void Two_spellings_of_the_same_page_have_the_same_normal_form()
    {
        UrlNormalizer.TryNormalize("HTTPS://News.Example.com:443/story/?utm_source=tw#comments", out var a);
        UrlNormalizer.TryNormalize("https://news.example.com/story", out var b);
        Assert.Equal(b, a);
    }
}

public class TagNameTests
{
    [Theory]
    [InlineData("Machine Learning", "machine learning")]
    [InlineData("  padded  ", "padded")]
    [InlineData("many    spaces   here", "many spaces here")]
    [InlineData("C#", "c#")]
    [InlineData("node.js", "node.js")]
    [InlineData("c++", "c++")]
    [InlineData("snake_case-and-kebab", "snake_case-and-kebab")]
    [InlineData("Ünïcode ÉÈ", "ünïcode éè")]
    [InlineData("日本語", "日本語")]
    [InlineData("2026", "2026")]
    public void Valid_names_are_trimmed_lowercased_and_have_spaces_collapsed(string input, string expected)
    {
        Assert.True(TagNames.TryNormalize(input, out var name));
        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("a;b")]
    [InlineData("a,b")]
    [InlineData("<script>")]
    [InlineData("tag\nnewline")]
    [InlineData("emoji 😀")]
    public void Invalid_names_are_refused(string? input) => Assert.False(TagNames.TryNormalize(input, out _));

    [Fact]
    public void The_length_limit_is_50_characters()
    {
        Assert.True(TagNames.TryNormalize(new string('a', 50), out _));
        Assert.False(TagNames.TryNormalize(new string('a', 51), out _));
    }
}

public class PatchTests
{
    private static readonly JsonSerializerOptions s_options = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private sealed record Body(Patch<string?> Title, Patch<Guid?> CollectionId, string? Other);

    [Fact]
    public void An_absent_field_is_not_set()
    {
        var body = JsonSerializer.Deserialize<Body>("{}", s_options)!;
        Assert.False(body.Title.IsSet);
        Assert.False(body.CollectionId.IsSet);
    }

    [Fact]
    public void A_null_field_is_set_to_nothing_which_means_clear()
    {
        var body = JsonSerializer.Deserialize<Body>("""{"title":null,"collection_id":null}""", s_options)!;
        Assert.True(body.Title.IsSet);
        Assert.Null(body.Title.Value);
        Assert.True(body.CollectionId.IsSet);
        Assert.Null(body.CollectionId.Value);
    }

    [Fact]
    public void A_value_is_set_to_that_value()
    {
        var id = Guid.NewGuid();
        var body = JsonSerializer.Deserialize<Body>($$"""{"title":"Hello","collection_id":"{{id}}"}""", s_options)!;
        Assert.Equal((true, "Hello"), (body.Title.IsSet, body.Title.Value));
        Assert.Equal((true, id), (body.CollectionId.IsSet, body.CollectionId.Value));
    }

    [Fact]
    public void Other_fields_are_unaffected()
    {
        var body = JsonSerializer.Deserialize<Body>("""{"other":"x"}""", s_options)!;
        Assert.Equal("x", body.Other);
        Assert.False(body.Title.IsSet);
    }
}

public class SearchQueryTests
{
    [Theory]
    [InlineData("postgres", "postgres:*")]
    [InlineData("Redis Cluster", "redis & cluster:*")]
    [InlineData("  spaced   out  ", "spaced & out:*")]
    [InlineData("node.js tips", "node & js & tips:*")]
    [InlineData("café", "café:*")]
    public void Words_are_anded_and_the_last_is_a_prefix(string input, string expected) =>
        Assert.Equal(expected, ResourceSearch.BuildTsQuery(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!! ??? ...")]
    public void Nothing_searchable_means_list_everything(string? input) => Assert.Null(ResourceSearch.BuildTsQuery(input));

    [Theory]
    [InlineData("a & b | !c", "a & b & c:*")]
    [InlineData("foo:* bar", "foo & bar:*")]
    [InlineData("'; DROP TABLE x; --", "drop & table & x:*")]
    [InlineData("(((", null)]
    public void Query_syntax_in_the_input_is_stripped_not_interpreted(string input, string? expected) =>
        Assert.Equal(expected, ResourceSearch.BuildTsQuery(input));

    [Fact]
    public void Only_the_first_eight_words_are_used()
    {
        var q = ResourceSearch.BuildTsQuery("a b c d e f g h i j")!;
        Assert.Equal(8, q.Split(" & ").Length);
        Assert.EndsWith("h:*", q, StringComparison.Ordinal);
    }
}

public class CollectionColorTests
{
    [Fact]
    public void The_palette_is_the_eight_design_system_names()
    {
        Assert.Equal(["slate", "teal", "blue", "violet", "rose", "amber", "green", "orange"], CollectionColors.All);
    }
}

public class ResourceDtoTests
{
    [Fact]
    public void A_resources_tags_are_listed_in_name_order()
    {
        var r = new Resource { Url = "https://e.com" };
        foreach (var n in new[] { "zeta", "alpha", "mid" })
            r.ResourceTags.Add(new ResourceTag { Tag = new Tag { Name = n } });
        Assert.Equal(["alpha", "mid", "zeta"], ResourceService.ToDto(r).Tags);
    }

    [Fact]
    public void Every_problem_the_library_can_raise_has_a_message()
    {
        foreach (var code in new[] { ProblemCode.NotFound, ProblemCode.CollectionNameTaken, ProblemCode.TagNameTaken, ProblemCode.EditConflict, ProblemCode.ValidationFailed })
            Assert.False(string.IsNullOrWhiteSpace(ProblemCatalogue.Title(code)));
    }
}
