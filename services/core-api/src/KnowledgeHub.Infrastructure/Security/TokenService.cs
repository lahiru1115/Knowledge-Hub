using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using KnowledgeHub.Application.Common;
using KnowledgeHub.Application.Identity;
using KnowledgeHub.Domain;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace KnowledgeHub.Infrastructure.Security;

public sealed class TokenService(IOptions<AuthOptions> options, IClock clock) : ITokenService
{
    private readonly AuthOptions _o = options.Value;

    public AccessToken CreateAccessToken(User user)
    {
        var now = clock.UtcNow;
        var expires = now.AddMinutes(_o.AccessTokenMinutes);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _o.Issuer,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new Claim("role", user.Role == UserRole.Admin ? "admin" : "member"),
                new Claim("stamp", user.SecurityStamp.ToString()),
            ]),
            SigningCredentials = new SigningCredentials(KeyFor(_o.SigningKey), SecurityAlgorithms.HmacSha256),
        };
        return new AccessToken(new JsonWebTokenHandler().CreateToken(descriptor), expires);
    }

    public string NewRefreshToken() => Microsoft.AspNetCore.WebUtilities.Base64UrlTextEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    public string HashToken(string rawToken) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    public static SymmetricSecurityKey KeyFor(string key) => new(Encoding.UTF8.GetBytes(key));
}
