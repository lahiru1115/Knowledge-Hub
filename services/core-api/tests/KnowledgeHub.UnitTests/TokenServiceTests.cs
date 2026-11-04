using KnowledgeHub.Application.Common;
using KnowledgeHub.Application.Identity;
using KnowledgeHub.Domain;
using KnowledgeHub.Infrastructure.Security;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace KnowledgeHub.UnitTests;

public class TokenServiceTests
{
    private sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow => now; }

    private static readonly DateTimeOffset s_now = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);
    private const string Key = "unit-test-signing-key-that-is-long-enough-0123456789";

    private static TokenService Create() =>
        new(Options.Create(new AuthOptions { SigningKey = Key }), new FixedClock(s_now));

    [Fact]
    public async Task Access_token_carries_the_documented_claims_and_lives_15_minutes()
    {
        var user = new User { Role = UserRole.Admin };
        var token = Create().CreateAccessToken(user);

        Assert.Equal(s_now.AddMinutes(15), token.ExpiresAt);
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token.Value, new TokenValidationParameters
        {
            ValidIssuer = "knowledge-hub",
            ValidateAudience = false,
            ValidateLifetime = false,
            IssuerSigningKey = TokenService.KeyFor(Key),
        });
        Assert.True(result.IsValid);
        var jwt = (JsonWebToken)result.SecurityToken;
        Assert.Equal(user.Id.ToString(), jwt.Subject);
        Assert.Equal("admin", jwt.GetClaim("role").Value);
        Assert.Equal(user.SecurityStamp.ToString(), jwt.GetClaim("stamp").Value);
        Assert.Equal("HS256", jwt.Alg);
    }

    [Fact]
    public async Task Access_token_signed_with_another_key_is_rejected()
    {
        var token = Create().CreateAccessToken(new User());
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token.Value, new TokenValidationParameters
        {
            ValidIssuer = "knowledge-hub",
            ValidateAudience = false,
            ValidateLifetime = false,
            IssuerSigningKey = TokenService.KeyFor("a-completely-different-key-of-sufficient-length-99"),
        });
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Refresh_tokens_are_256_bit_random_and_hash_to_sha256_hex()
    {
        var service = Create();
        var a = service.NewRefreshToken();
        var b = service.NewRefreshToken();

        Assert.NotEqual(a, b);
        Assert.Equal(43, a.Length); // 32 bytes, base64url without padding
        var hash = service.HashToken(a);
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, service.HashToken(a));
        Assert.NotEqual(a, hash);
    }
}
