using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Desk.Data.App.Migrations
{
    /// <inheritdoc />
    public partial class AccessRequests : Migration
    {
        // ADR-0029, #538: the demo access-request queue. Expand only (README §14.4): a new table and the citext extension,
        // nothing the running app reads, so it is safe before the version that writes it. No seed rows.
        // At most one pending request per address, whatever its case: an expression index, so raw SQL (EF can't model it).
        internal const string PendingEmailIndexSql =
            "CREATE UNIQUE INDEX IF NOT EXISTS " + AppDbContext.PendingEmailIndex
            + " ON app.access_request (lower(email)) WHERE status = 'pending'";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:citext", ",,");

            migrationBuilder.CreateTable(
                name: "access_request",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    email = table.Column<string>(type: "citext", nullable: false),
                    display_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    purpose = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    account_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_access_request", x => x.id);
                    table.CheckConstraint("ck_access_request_email_length", "char_length(email) BETWEEN 3 AND 254");
                    table.CheckConstraint("ck_access_request_status", "status IN ('pending', 'approved', 'rejected')");
                    table.ForeignKey(
                        name: "FK_access_request_users_account_id",
                        column: x => x.account_id,
                        principalSchema: "auth",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_access_request_account_id",
                schema: "app",
                table: "access_request",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "IX_access_request_status_created_at",
                schema: "app",
                table: "access_request",
                columns: new[] { "status", "created_at" });

            migrationBuilder.Sql(PendingEmailIndexSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropped with the table; IF EXISTS keeps a retried Down safe.
            migrationBuilder.Sql("DROP INDEX IF EXISTS app." + AppDbContext.PendingEmailIndex);

            migrationBuilder.DropTable(
                name: "access_request",
                schema: "app");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,");
        }
    }
}
