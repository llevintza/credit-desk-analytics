using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Data.App.Migrations
{
    /// <inheritdoc />
    public partial class AuthSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sessions",
                schema: "auth",
                columns: table => new
                {
                    sid = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    rid = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    previous_rid = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    rotated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sessions", x => x.sid);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sessions_expires_at",
                schema: "auth",
                table: "sessions",
                column: "expires_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sessions",
                schema: "auth");
        }
    }
}
