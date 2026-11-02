using System.Text.Json;
using FluentValidation;
using KnowledgeHub.Api.Http;
using KnowledgeHub.Api.Identity;
using KnowledgeHub.Api.Library;
using KnowledgeHub.Application.Common;
using KnowledgeHub.Application.Identity;
using KnowledgeHub.Application.Library;
using KnowledgeHub.Infrastructure;
using KnowledgeHub.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Configuration: Auth__SigningKey etc. come from the environment (NFR-SEC-09).
// ADMIN_EMAIL (named in the SRS) is accepted as an alias for Auth:AdminEmail.
builder.Configuration["Auth:AdminEmail"] ??= builder.Configuration["ADMIN_EMAIL"];
builder.Services.AddOptions<AuthOptions>()
    .Bind(builder.Configuration.GetSection(AuthOptions.Section))
    .Validate(o => System.Text.Encoding.UTF8.GetByteCount(o.SigningKey) >= 32, "Auth:SigningKey must be at least 32 bytes.")
    .ValidateOnStart();

builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    // Enums travel as lowercase names only: a number such as 2 must not silently mean the third value.
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
});
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemExceptionHandler>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddValidatorsFromAssemblyContaining<RegisterRequestValidator>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<SessionIssuer>();
builder.Services.AddScoped<CollectionService>();
builder.Services.AddScoped<ResourceService>();
builder.Services.AddScoped<TagService>();
builder.Services.AddScoped<AuditLog>();
builder.Services.AddScoped<IRequestContext, HttpRequestContext>();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddScoped<SecurityStampValidator>();
builder.Services.AddScoped<ISessionCache>(sp => sp.GetRequiredService<SecurityStampValidator>());

// Access token JWT, read from the kh_at cookie (Doc 02 §3.3, §9.1).
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<AuthOptions>>((jwt, auth) =>
    {
        var a = auth.Value;
        var keys = new List<SecurityKey> { TokenService.KeyFor(a.SigningKey) };
        if (!string.IsNullOrEmpty(a.PreviousSigningKey)) keys.Add(TokenService.KeyFor(a.PreviousSigningKey));

        jwt.MapInboundClaims = false;
        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = a.Issuer,
            ValidateAudience = false,
            IssuerSigningKeys = keys,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.FromSeconds(10),
            NameClaimType = "sub",
            RoleClaimType = "role",
        };
        jwt.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                ctx.Token = ctx.Request.Cookies[AuthCookies.Access];
                return Task.CompletedTask;
            },
            OnTokenValidated = async ctx =>
            {
                var validator = ctx.HttpContext.RequestServices.GetRequiredService<SecurityStampValidator>();
                var ok = Guid.TryParse(ctx.Principal?.FindFirst("sub")?.Value, out var id)
                    && await validator.IsValidAsync(id, ctx.Principal?.FindFirst("stamp")?.Value, ctx.HttpContext.RequestAborted);
                if (!ok) ctx.Fail("The session is no longer valid.");
            },
        };
    });
builder.Services.AddAuthorizationBuilder().AddPolicy("admin", p => p.RequireClaim("role", "admin"));
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, AuthorizationResultHandler>();
builder.Services.AddKnowledgeHubRateLimiting();

var app = builder.Build();

app.UseExceptionHandler();
app.UseMiddleware<CsrfMiddleware>();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

// Health endpoints sit outside /api/v1 (Doc 02 §8.2, IR-17). They give no internal details.
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (IAppDbContext db, HttpContext http, CancellationToken ct) =>
{
    if (await db.Database.CanConnectAsync(ct)) return Results.Ok(new { status = "ready" });
    await ProblemWriter.WriteAsync(http, ProblemCode.ServiceUnavailable);
    return Results.Empty;
});

var api = app.MapGroup("/api/v1");
api.MapIdentity();
api.MapLibrary();

app.Run();

public partial class Program;
