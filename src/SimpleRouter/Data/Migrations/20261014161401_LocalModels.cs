using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimpleRouter.Data.Migrations
{
    /// <inheritdoc />
    public partial class LocalModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BaseUrl",
                table: "Models",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ContextWindow",
                table: "Models",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProtectedApiKey",
                table: "Models",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SupportsTools",
                table: "Models",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "SupportsVision",
                table: "Models",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BaseUrl",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "ContextWindow",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "ProtectedApiKey",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "SupportsTools",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "SupportsVision",
                table: "Models");
        }
    }
}
