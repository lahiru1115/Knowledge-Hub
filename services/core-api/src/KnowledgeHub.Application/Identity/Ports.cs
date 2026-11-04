using KnowledgeHub.Domain;

namespace KnowledgeHub.Application.Identity;

public interface ITokenService
{
    AccessToken CreateAccessToken(User user);
    /// <summary>A new random 256-bit token, URL-safe.</summary>
    string NewRefreshToken();
    /// <summary>SHA-256, hex. Only this is stored.</summary>
    string HashToken(string rawToken);
}

public interface ICommonPasswords
{
    bool IsCommon(string password);
}
