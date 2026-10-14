using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimpleRouter.Data.Migrations
{
    /// <inheritdoc />
    public partial class ModelCooldowns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ModelCooldowns",
                columns: table => new
                {
                    ModelId = table.Column<string>(type: "TEXT", nullable: false),
                    Until = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelCooldowns", x => x.ModelId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ModelCooldowns");
        }
    }
}
