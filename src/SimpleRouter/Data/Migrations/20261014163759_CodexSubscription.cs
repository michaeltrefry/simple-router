using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimpleRouter.Data.Migrations
{
    /// <inheritdoc />
    public partial class CodexSubscription : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CodexCredentials",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    ProtectedRefreshToken = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedAccessToken = table.Column<string>(type: "TEXT", nullable: true),
                    AccessTokenExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ProtectedAccountId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CodexCredentials", x => x.Id);
                });

            // Codex models were seeded disabled only because no upstream served them yet.
            migrationBuilder.Sql("UPDATE Models SET Enabled = 1 WHERE Provider = 'codex' AND Id IN ('gpt-6-astra', 'gpt-6.1-sol', 'gpt-5.6-terra', 'gpt-6-luna');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CodexCredentials");
        }
    }
}
