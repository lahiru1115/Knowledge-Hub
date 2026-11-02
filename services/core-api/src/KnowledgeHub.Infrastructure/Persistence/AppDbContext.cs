using System.Text.Json;
using System.Text.Json.Serialization;
using KnowledgeHub.Application.Common;
using KnowledgeHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace KnowledgeHub.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IAppDbContext
{
    public const string Schema = "core";

    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<Resource> Resources => Set<Resource>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<ResourceTag> ResourceTags => Set<ResourceTag>();

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("users", t => t.HasCheckConstraint("ck_users_role", "role IN ('member','admin')"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasColumnType("citext").HasMaxLength(254);
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.DisplayName).HasMaxLength(60);
            e.Property(x => x.Role).HasConversion(r => r == UserRole.Admin ? "admin" : "member", s => s == "admin" ? UserRole.Admin : UserRole.Member);
            e.Property(x => x.Preferences)
                .HasColumnType("jsonb")
                .HasConversion(
                    p => JsonSerializer.Serialize(p, s_json),
                    s => JsonSerializer.Deserialize<UserPreferences>(s, s_json) ?? new UserPreferences(),
                    new ValueComparer<UserPreferences>(
                        (a, c) => JsonSerializer.Serialize(a, s_json) == JsonSerializer.Serialize(c, s_json),
                        v => JsonSerializer.Serialize(v, s_json).GetHashCode(StringComparison.Ordinal),
                        v => JsonSerializer.Deserialize<UserPreferences>(JsonSerializer.Serialize(v, s_json), s_json)!));
        });

        modelBuilder.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.FamilyId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PasswordResetToken>(e =>
        {
            e.ToTable("password_reset_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasMaxLength(100);
            e.Property(x => x.Ip).HasColumnType("inet");
            e.Property(x => x.UserAgent).HasMaxLength(512);
            e.Property(x => x.Data).HasColumnType("jsonb");
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasIndex(x => x.CreatedAt);
            // No foreign key: the account.deleted row outlives the user (Doc 02 §7.2).
        });

        LibraryModel.Configure(modelBuilder);
    }
}
