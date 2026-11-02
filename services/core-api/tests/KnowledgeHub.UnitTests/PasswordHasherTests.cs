using KnowledgeHub.Infrastructure.Security;
using Xunit;

namespace KnowledgeHub.UnitTests;

public class PasswordHasherTests
{
    private readonly Argon2idPasswordHasher _hasher = new();

    [Fact]
    public void Hash_uses_argon2id_with_the_documented_parameters_and_never_contains_the_password()
    {
        var hash = _hasher.Hash("correct horse battery staple");

        Assert.StartsWith("$argon2id$v=19$m=19456,t=2,p=1$", hash, StringComparison.Ordinal);
        Assert.DoesNotContain("correct", hash, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_accepts_the_right_password_and_rejects_others()
    {
        var hash = _hasher.Hash("correct horse battery staple");
        Assert.True(_hasher.Verify("correct horse battery staple", hash));
        Assert.False(_hasher.Verify("Correct horse battery staple", hash));
    }

    [Fact]
    public void The_same_password_hashes_differently_each_time()
    {
        Assert.NotEqual(_hasher.Hash("same password 123"), _hasher.Hash("same password 123"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("$argon2id$v=19$m=x,t=2,p=1$AAAA$AAAA")]
    public void Verify_returns_false_for_malformed_hashes(string hash)
    {
        Assert.False(_hasher.Verify("anything", hash));
    }

    [Fact]
    public void NeedsRehash_flags_weaker_parameters_only()
    {
        Assert.False(_hasher.NeedsRehash(_hasher.Hash("a password 123")));
        Assert.True(_hasher.NeedsRehash("$argon2id$v=19$m=4096,t=1,p=1$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"));
        Assert.True(_hasher.NeedsRehash("garbage"));
    }
}
