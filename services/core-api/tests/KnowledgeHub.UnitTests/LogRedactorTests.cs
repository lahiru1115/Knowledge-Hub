using KnowledgeHub.Application.Common;
using Xunit;

namespace KnowledgeHub.UnitTests;

public class LogRedactorTests
{
    [Theory]
    [InlineData("Sign-in failed for ada@example.com", "Sign-in failed for a***@example.com")]
    [InlineData("a.b+tag@sub.example.co.uk and x@y.io", "a***@sub.example.co.uk and x***@y.io")]
    [InlineData("no address here", "no address here")]
    public void Email_addresses_are_masked(string input, string expected) =>
        Assert.Equal(expected, LogRedactor.Redact(input));

    [Theory]
    [InlineData("Cookie: kh_at=abc.def.ghi; kh_csrf=123", "Cookie: [redacted]; kh_csrf=[redacted]")]
    [InlineData("set-cookie: kh_rt=secretvalue; path=/api", "set-cookie: [redacted]; path=/api")]
    [InlineData("X-CSRF-Token: 0123456789abcdef", "X-CSRF-Token: [redacted]")]
    [InlineData("Authorization: Bearer abc123", "Authorization: [redacted]")]
    [InlineData("sent Bearer abc123def to the AI service", "sent Bearer [redacted] to the AI service")]
    public void Credentials_in_headers_and_cookies_are_removed(string input, string expected) =>
        Assert.Equal(expected, LogRedactor.Redact(input));

    [Fact]
    public void A_json_web_token_is_removed_wherever_it_appears()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjMifQ.c2lnbmF0dXJl";
        var result = LogRedactor.Redact($"token was {jwt} at the time, again {jwt}.");
        Assert.DoesNotContain("eyJ", result, StringComparison.Ordinal);
        Assert.Contains(LogRedactor.Redacted, result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("password=hunter2 ok", "password=[redacted] ok")]
    [InlineData("{\"password\":\"hunter 2\",\"x\":1}", "{\"password\":\"[redacted]\",\"x\":1}")]
    [InlineData("new_password: correct horse", "new_password: [redacted] horse")]
    [InlineData("?token=abc123&page=2", "?token=[redacted]&page=2")]
    [InlineData("{\"notes\":\"my private \\\"quoted\\\" thought\"}", "{\"notes\":\"[redacted]\"}")]
    [InlineData("apiKey=sk-123", "apiKey=[redacted]")]
    [InlineData("content=What did I save", "content=[redacted] did I save")]
    public void Secrets_and_content_fields_are_removed(string input, string expected) =>
        Assert.Equal(expected, LogRedactor.Redact(input));

    [Fact]
    public void Text_that_is_fine_is_left_alone()
    {
        const string fine = "Outbox message 0193a000 of type resource.created failed on attempt 2; retry in 40 s";
        Assert.Equal(fine, LogRedactor.Redact(fine));
        Assert.Equal("", LogRedactor.Redact(null));
        Assert.Equal("", LogRedactor.Redact(""));
    }

    [Fact]
    public void A_word_that_merely_contains_a_sensitive_name_is_not_touched()
    {
        Assert.Equal("the context is large", LogRedactor.Redact("the context is large"));
        Assert.Equal("tokenizer ready", LogRedactor.Redact("tokenizer ready"));
    }
}
