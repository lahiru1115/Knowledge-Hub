using KnowledgeHub.Application.Identity;
using KnowledgeHub.Infrastructure.Security;
using Xunit;

namespace KnowledgeHub.UnitTests;

public class ValidatorTests
{
    private readonly RegisterRequestValidator _validator = new(new CommonPasswords());

    private static RegisterRequest Valid() => new("ada@example.com", "a long unusual passphrase", "Ada");

    [Fact]
    public void A_valid_request_passes() => Assert.True(_validator.Validate(Valid()).IsValid);

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    public void Bad_emails_fail(string email) =>
        Assert.False(_validator.Validate(Valid() with { Email = email }).IsValid);

    [Theory]
    [InlineData("short")]
    [InlineData("elevenchars")]
    public void Passwords_under_twelve_characters_fail(string password) =>
        Assert.False(_validator.Validate(Valid() with { Password = password }).IsValid);

    [Fact]
    public void Passwords_over_128_characters_fail() =>
        Assert.False(_validator.Validate(Valid() with { Password = new string('a', 129) }).IsValid);

    [Fact]
    public void Common_passwords_fail() =>
        Assert.False(_validator.Validate(Valid() with { Password = "PasswordPassword" }).IsValid);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_display_names_fail(string name) =>
        Assert.False(_validator.Validate(Valid() with { DisplayName = name }).IsValid);

    [Fact]
    public void Display_names_over_60_characters_fail() =>
        Assert.False(_validator.Validate(Valid() with { DisplayName = new string('x', 61) }).IsValid);
}
