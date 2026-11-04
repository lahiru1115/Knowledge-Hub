using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnowledgeHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Outbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "content_hash",
                schema: "core",
                table: "resources",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "metadata_error",
                schema: "core",
                table: "resources",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "user_edited_fields",
                schema: "core",
                table: "resources",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "outbox",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    traceparent = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox", x => x.id);
                    table.CheckConstraint("ck_outbox_status", "status IN ('pending','processing','done','dead')");
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_resources_metadata_error",
                schema: "core",
                table: "resources",
                sql: "metadata_error IS NULL OR metadata_error IN ('blocked_url','timeout','unreachable','http_error','not_html','too_large','too_many_redirects')");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_next_attempt_at",
                schema: "core",
                table: "outbox",
                column: "next_attempt_at",
                filter: "status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_status_processed_at",
                schema: "core",
                table: "outbox",
                columns: new[] { "status", "processed_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox",
                schema: "core");

            migrationBuilder.DropCheckConstraint(
                name: "ck_resources_metadata_error",
                schema: "core",
                table: "resources");

            migrationBuilder.DropColumn(
                name: "content_hash",
                schema: "core",
                table: "resources");

            migrationBuilder.DropColumn(
                name: "metadata_error",
                schema: "core",
                table: "resources");

            migrationBuilder.DropColumn(
                name: "user_edited_fields",
                schema: "core",
                table: "resources");
        }
    }
}
