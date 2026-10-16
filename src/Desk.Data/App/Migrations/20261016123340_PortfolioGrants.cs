using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Data.App.Migrations
{
    /// <inheritdoc />
    public partial class PortfolioGrants : Migration
    {
        // ADR-0021 model B: per-user portfolio grants. Additive (a new table; no #121 migration and no core index changes),
        // so it is safe to run before the app version that reads it (README §14.4). The only FK is to auth.users: core is
        // truncated by the seeder and may move to its own database, so portfolio_id has none. Empty until grants are
        // loaded, and nothing reads it while ENTITLEMENTS_MODE is `all` (the default).
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "portfolio_grant",
                schema: "app",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    portfolio_id = table.Column<int>(type: "integer", nullable: false),
                    granted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    granted_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_portfolio_grant", x => new { x.user_id, x.portfolio_id });
                    table.ForeignKey(
                        name: "FK_portfolio_grant_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "auth",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_portfolio_grant_portfolio_id",
                schema: "app",
                table: "portfolio_grant",
                column: "portfolio_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "portfolio_grant",
                schema: "app");
        }
    }
}
