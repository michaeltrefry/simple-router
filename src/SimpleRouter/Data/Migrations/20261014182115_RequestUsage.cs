using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimpleRouter.Data.Migrations
{
    /// <inheritdoc />
    public partial class RequestUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ModelPrices",
                columns: table => new
                {
                    ModelId = table.Column<string>(type: "TEXT", nullable: false),
                    InputPerMTok = table.Column<decimal>(type: "TEXT", nullable: false),
                    OutputPerMTok = table.Column<decimal>(type: "TEXT", nullable: false),
                    CacheReadPerMTok = table.Column<decimal>(type: "TEXT", nullable: false),
                    CacheWritePerMTok = table.Column<decimal>(type: "TEXT", nullable: false),
                    CacheWrite1hPerMTok = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelPrices", x => x.ModelId);
                });

            migrationBuilder.CreateTable(
                name: "RequestUsages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    At = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ClientSessionId = table.Column<string>(type: "TEXT", nullable: true),
                    ModelId = table.Column<string>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    Tier = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    InputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    OutputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheReadTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheWriteTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheWrite1hTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CostUsdMicros = table.Column<long>(type: "INTEGER", nullable: false),
                    LatencyMs = table.Column<long>(type: "INTEGER", nullable: false),
                    Effort = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestUsages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RequestUsages_ClientSessionId",
                table: "RequestUsages",
                column: "ClientSessionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ModelPrices");

            migrationBuilder.DropTable(
                name: "RequestUsages");
        }
    }
}
