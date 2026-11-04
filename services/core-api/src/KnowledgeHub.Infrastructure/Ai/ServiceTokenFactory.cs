using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace KnowledgeHub.Infrastructure.Ai;

/// <summary>Signs the short-lived token that goes with every call to the AI service (Doc 02 §5.6).</summary>
public sealed class ServiceTokenFactory(IOptions<AiOptions> options, TimeProvider time)
{
    public const string System = "system";

    private readonly JsonWebTokenHandler _handler = new();

    /// <summary>A token for one user's call. The AI service takes the user only from here.</summary>
    public string ForUser(Guid userId) => Create(userId.ToString(), null);

    /// <summary>A token for a call that is not about one user, with the scope it needs.</summary>
    public string ForSystem(string scope) => Create(System, scope);

    private string Create(string subject, string? scope)
    {
        var o = options.Value;
        var key = Encoding.UTF8.GetBytes(o.JwtSecret);
        if (key.Length < 32) throw new InvalidOperationException("AiService:JwtSecret must be at least 32 bytes.");
        var now = time.GetUtcNow().UtcDateTime;
        var claims = new Dictionary<string, object> { ["sub"] = subject, ["jti"] = Guid.NewGuid().ToString("N") };
        if (scope is not null) claims["scope"] = scope;
        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = o.Issuer,
            Audience = o.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddSeconds(o.TokenLifetimeSeconds),
            Claims = claims,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256),
        });
    }
}
