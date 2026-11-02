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

public class AccountValidatorTests
{
    private static readonly CommonPasswords s_common = new();

    [Fact]
    public void Password_reset_confirm_applies_the_same_password_rules_as_registration()
    {
        var validator = new PasswordResetConfirmValidator(s_common);

        Assert.True(validator.Validate(new PasswordResetConfirmRequest("token", "a long unusual passphrase")).IsValid);
        var short_ = validator.Validate(new PasswordResetConfirmRequest("token", "short"));
        Assert.Contains(short_.Errors, e => e.PropertyName == "NewPassword");
        Assert.False(validator.Validate(new PasswordResetConfirmRequest("token", "PasswordPassword")).IsValid);
        Assert.False(validator.Validate(new PasswordResetConfirmRequest("", "a long unusual passphrase")).IsValid);
    }

    [Fact]
    public void Change_password_needs_the_current_password_and_a_valid_new_one()
    {
        var validator = new ChangePasswordValidator(s_common);

        Assert.True(validator.Validate(new ChangePasswordRequest("old password", "a long unusual passphrase")).IsValid);
        Assert.False(validator.Validate(new ChangePasswordRequest("", "a long unusual passphrase")).IsValid);
        Assert.False(validator.Validate(new ChangePasswordRequest("old password", new string('a', 129))).IsValid);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("Ada", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void Update_me_checks_the_display_name_only_when_present(string? name, bool valid)
    {
        Assert.Equal(valid, new UpdateMeValidator().Validate(new UpdateMeRequest(name, null)).IsValid);
    }
}
