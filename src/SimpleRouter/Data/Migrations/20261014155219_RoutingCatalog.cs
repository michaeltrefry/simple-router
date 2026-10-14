using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimpleRouter.Data.Migrations
{
    /// <inheritdoc />
    public partial class RoutingCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Models",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    UpstreamModelId = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Models", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SessionPins",
                columns: table => new
                {
                    SessionKey = table.Column<string>(type: "TEXT", nullable: false),
                    Tier = table.Column<string>(type: "TEXT", nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", nullable: false),
                    ClientSessionId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionPins", x => new { x.SessionKey, x.Tier });
                });

            migrationBuilder.CreateTable(
                name: "TierAliases",
                columns: table => new
                {
                    Pattern = table.Column<string>(type: "TEXT", nullable: false),
                    Tier = table.Column<string>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TierAliases", x => x.Pattern);
                });

            migrationBuilder.CreateTable(
                name: "TierCursors",
                columns: table => new
                {
                    Tier = table.Column<string>(type: "TEXT", nullable: false),
                    Next = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TierCursors", x => x.Tier);
                });

            migrationBuilder.CreateTable(
                name: "TierMembers",
                columns: table => new
                {
                    Tier = table.Column<string>(type: "TEXT", nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TierMembers", x => new { x.Tier, x.ModelId });
                    table.ForeignKey(
                        name: "FK_TierMembers_Models_ModelId",
                        column: x => x.ModelId,
                        principalTable: "Models",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SessionPins_ClientSessionId",
                table: "SessionPins",
                column: "ClientSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_TierMembers_ModelId",
                table: "TierMembers",
                column: "ModelId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SessionPins");

            migrationBuilder.DropTable(
                name: "TierAliases");

            migrationBuilder.DropTable(
                name: "TierCursors");

            migrationBuilder.DropTable(
                name: "TierMembers");

            migrationBuilder.DropTable(
                name: "Models");
        }
    }
}
