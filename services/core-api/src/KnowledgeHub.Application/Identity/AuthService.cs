using System.Text.Json;
using FluentValidation;
using KnowledgeHub.Application.Common;
using KnowledgeHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KnowledgeHub.Application.Identity;

public sealed class AuthService(
    IAppDbContext db,
    IPasswordHasher hasher,
    ITokenService tokens,
    IClock clock,
    IRequestContext request,
    IOptions<AuthOptions> options,
    IValidator<RegisterRequest> registerValidator,
    IValidator<LoginRequest> loginValidator)
{
    private readonly AuthOptions _options = options.Value;

    // Verified against when the email is unknown, so unknown and known emails take similar time.
    private string DecoyHash => _decoyHash ??= hasher.Hash("decoy-password-for-timing");
    private string? _decoyHash;

    public async Task<AuthResult> RegisterAsync(RegisterRequest input, CancellationToken ct)
    {
        await ValidateAsync(registerValidator, input, ct);
        var email = input.Email.Trim().ToLowerInvariant();

        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            throw new AppException(ProblemCode.EmailTaken);

        var now = clock.UtcNow;
        var user = new User
        {
            Email = email,
            PasswordHash = hasher.Hash(input.Password),
            DisplayName = input.DisplayName.Trim(),
            Role = string.Equals(email, _options.AdminEmail?.Trim(), StringComparison.OrdinalIgnoreCase) ? UserRole.Admin : UserRole.Member,
            CreatedAt = now,
            UpdatedAt = now,
            LastLoginAt = now,
        };
        db.Users.Add(user);
        var issued = IssueTokens(user, Guid.CreateVersion7(), now);
        Audit("auth.register", user.Id);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            throw new AppException(ProblemCode.EmailTaken); // lost a race with a concurrent registration
        }

        return new AuthResult(UserDto.From(user), issued);
    }

    public async Task<AuthResult> LoginAsync(LoginRequest input, CancellationToken ct)
    {
        await ValidateAsync(loginValidator, input, ct);
        var email = input.Email.Trim().ToLowerInvariant();
        var now = clock.UtcNow;
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

        if (user?.PasswordHash is null)
        {
            hasher.Verify(input.Password, DecoyHash);
            Audit("auth.login_failed", user?.Id, new { reason = user is null ? "unknown_email" : "no_password" });
            await db.SaveChangesAsync(ct);
            throw new AppException(ProblemCode.InvalidCredentials);
        }

        if (user.IsLocked(now))
        {
            Audit("auth.login_failed", user.Id, new { reason = "locked" });
            await db.SaveChangesAsync(ct);
            var wait = (int)Math.Ceiling((user.LockedUntil!.Value - now).TotalSeconds);
            var locked = new AppException(ProblemCode.AccountLocked);
            locked.Extensions["retry_after"] = wait;
            throw locked;
        }

        if (!hasher.Verify(input.Password, user.PasswordHash))
        {
            user.RegisterFailedLogin(now,
                _options.LockoutThreshold,
                TimeSpan.FromMinutes(_options.LockoutWindowMinutes),
                TimeSpan.FromMinutes(_options.LockoutMinutes));
            Audit("auth.login_failed", user.Id, new { reason = "bad_password", locked = user.IsLocked(now) });
            await db.SaveChangesAsync(ct);
            throw new AppException(ProblemCode.InvalidCredentials);
        }

        if (user.IsDisabled)
        {
            Audit("auth.login_failed", user.Id, new { reason = "disabled" });
            await db.SaveChangesAsync(ct);
            throw new AppException(ProblemCode.AccountDisabled);
        }

        if (hasher.NeedsRehash(user.PasswordHash))
            user.PasswordHash = hasher.Hash(input.Password);

        user.RegisterSuccessfulLogin(now);
        var issued = IssueTokens(user, Guid.CreateVersion7(), now);
        Audit("auth.login", user.Id);
        await db.SaveChangesAsync(ct);
        return new AuthResult(UserDto.From(user), issued);
    }

    /// <summary>Rotates the refresh token. Presenting a revoked token revokes the whole family (FR-AUTH-04).</summary>
    public async Task<IssuedTokens> RefreshAsync(string? rawToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(rawToken)) throw new AppException(ProblemCode.Unauthenticated);

        var now = clock.UtcNow;
        var hash = tokens.HashToken(rawToken);
        var current = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct)
            ?? throw new AppException(ProblemCode.Unauthenticated);

        if (current.RevokedAt is not null)
        {
            await RevokeFamilyAsync(current.FamilyId, now, ct);
            Audit("auth.refresh_reuse", current.UserId);
            await db.SaveChangesAsync(ct);
            throw new AppException(ProblemCode.Unauthenticated);
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == current.UserId, ct);
        if (current.IsExpired(now) || user is null || user.IsDisabled)
            throw new AppException(ProblemCode.Unauthenticated);

        var issued = IssueTokens(user, current.FamilyId, now, out var next);
        current.RevokedAt = now;
        current.ReplacedById = next.Id;
        await db.SaveChangesAsync(ct);
        return issued;
    }

    public async Task LogoutAsync(Guid userId, string? rawRefreshToken, CancellationToken ct)
    {
        var now = clock.UtcNow;
        if (!string.IsNullOrEmpty(rawRefreshToken))
        {
            var hash = tokens.HashToken(rawRefreshToken);
            var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash && t.UserId == userId, ct);
            if (token is not null) await RevokeFamilyAsync(token.FamilyId, now, ct);
        }

        Audit("auth.logout", userId);
        await db.SaveChangesAsync(ct);
    }

    public async Task<UserDto> GetMeAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new AppException(ProblemCode.Unauthenticated);
        return UserDto.From(user);
    }

    private IssuedTokens IssueTokens(User user, Guid familyId, DateTimeOffset now) => IssueTokens(user, familyId, now, out _);

    private IssuedTokens IssueTokens(User user, Guid familyId, DateTimeOffset now, out RefreshToken refresh)
    {
        var access = tokens.CreateAccessToken(user);
        var raw = tokens.NewRefreshToken();
        refresh = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = tokens.HashToken(raw),
            FamilyId = familyId,
            CreatedAt = now,
            ExpiresAt = now.AddDays(_options.RefreshTokenDays),
        };
        db.RefreshTokens.Add(refresh);
        return new IssuedTokens(access.Value, access.ExpiresAt, raw, refresh.ExpiresAt);
    }

    private async Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken ct)
    {
        var live = await db.RefreshTokens.Where(t => t.FamilyId == familyId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var t in live) t.RevokedAt = now;
    }

    private void Audit(string type, Guid? userId, object? data = null)
    {
        var payload = new Dictionary<string, object?> { ["trace_id"] = request.TraceId };
        if (data is not null)
            foreach (var p in data.GetType().GetProperties())
                payload[p.Name] = p.GetValue(data);
        db.AuditEvents.Add(new AuditEvent
        {
            UserId = userId,
            Type = type,
            Ip = System.Net.IPAddress.TryParse(request.Ip, out var ip) ? ip : null,
            UserAgent = request.UserAgent is { Length: > 512 } ua ? ua[..512] : request.UserAgent,
            Data = JsonSerializer.Serialize(payload),
            CreatedAt = clock.UtcNow,
        });
    }

    private static async Task ValidateAsync<T>(IValidator<T> validator, T value, CancellationToken ct)
    {
        var result = await validator.ValidateAsync(value, ct);
        if (result.IsValid) return;
        throw new AppException(ProblemCode.ValidationFailed, "One or more fields are not valid.")
        {
            Errors = result.Errors
                .GroupBy(e => ToSnake(e.PropertyName))
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()),
        };
    }

    private static string ToSnake(string name) => JsonNamingPolicy.SnakeCaseLower.ConvertName(name);

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException?.GetType().GetProperty("SqlState")?.GetValue(ex.InnerException) as string == "23505";
}
