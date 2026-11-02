using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace KnowledgeHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Library : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "collections",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    color = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    icon = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    is_pinned = table.Column<bool>(type: "boolean", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_collections", x => x.id);
                    table.ForeignKey(
                        name: "fk_collections_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "core",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tags",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tags", x => x.id);
                    table.ForeignKey(
                        name: "fk_tags_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "core",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "resources",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    collection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    url_normalized = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    favicon_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    image_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    site_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    summary_source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    is_favourite = table.Column<bool>(type: "boolean", nullable: false),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false),
                    metadata_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    tags_text = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    search_vector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true, computedColumnSql: "setweight(to_tsvector('english', coalesce(title, '')), 'A') ||\nsetweight(to_tsvector('english', coalesce(tags_text, '')), 'B') ||\nsetweight(to_tsvector('english', coalesce(description, '')), 'C') ||\nsetweight(to_tsvector('english', coalesce(notes, '') || ' ' || regexp_replace(url, '[^[:alnum:]]+', ' ', 'g')), 'D')", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_resources", x => x.id);
                    table.CheckConstraint("ck_resources_metadata_status", "metadata_status IN ('pending','ready','failed')");
                    table.CheckConstraint("ck_resources_summary_source", "summary_source IN ('none','extracted','ai','user')");
                    table.CheckConstraint("ck_resources_type", "type IN ('article','video','pdf','course','repo','website','other')");
                    table.ForeignKey(
                        name: "fk_resources_collections_collection_id",
                        column: x => x.collection_id,
                        principalSchema: "core",
                        principalTable: "collections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_resources_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "core",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "resource_tags",
                schema: "core",
                columns: table => new
                {
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_resource_tags", x => new { x.resource_id, x.tag_id });
                    table.CheckConstraint("ck_resource_tags_source", "source IN ('user','ai')");
                    table.ForeignKey(
                        name: "fk_resource_tags_resources_resource_id",
                        column: x => x.resource_id,
                        principalSchema: "core",
                        principalTable: "resources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_resource_tags_tags_tag_id",
                        column: x => x.tag_id,
                        principalSchema: "core",
                        principalTable: "tags",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // One name per user regardless of case (FR-COL-01); an expression index, so EF cannot model it.
            migrationBuilder.Sql("CREATE UNIQUE INDEX ux_collections_user_id_lower_name ON core.collections (user_id, lower(name));");

            migrationBuilder.CreateIndex(
                name: "ix_collections_user_id_is_pinned_position",
                schema: "core",
                table: "collections",
                columns: new[] { "user_id", "is_pinned", "position" },
                descending: new[] { false, true, false });

            migrationBuilder.CreateIndex(
                name: "ix_resource_tags_tag_id",
                schema: "core",
                table: "resource_tags",
                column: "tag_id");

            migrationBuilder.CreateIndex(
                name: "ix_resources_collection_id",
                schema: "core",
                table: "resources",
                column: "collection_id");

            migrationBuilder.CreateIndex(
                name: "ix_resources_deleted_at",
                schema: "core",
                table: "resources",
                column: "deleted_at",
                filter: "deleted_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_resources_search_vector",
                schema: "core",
                table: "resources",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_resources_user_id_collection_id",
                schema: "core",
                table: "resources",
                columns: new[] { "user_id", "collection_id" },
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_resources_user_id_created_at_id",
                schema: "core",
                table: "resources",
                columns: new[] { "user_id", "created_at", "id" },
                descending: new[] { false, true, false },
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_resources_user_id_updated_at",
                schema: "core",
                table: "resources",
                columns: new[] { "user_id", "updated_at" },
                descending: new[] { false, true },
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_resources_user_id_url_normalized",
                schema: "core",
                table: "resources",
                columns: new[] { "user_id", "url_normalized" },
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tags_user_id_name",
                schema: "core",
                table: "tags",
                columns: new[] { "user_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "resource_tags",
                schema: "core");

            migrationBuilder.DropTable(
                name: "resources",
                schema: "core");

            migrationBuilder.DropTable(
                name: "tags",
                schema: "core");

            migrationBuilder.DropTable(
                name: "collections",
                schema: "core");
        }
    }
}
