using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimpleRouter.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOpenRouter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CostSource",
                table: "RequestUsages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GenerationId",
                table: "RequestUsages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolvedModelId",
                table: "RequestUsages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OpenRouter",
                table: "Models",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProviderCredentials",
                columns: table => new
                {
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedApiKey = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderCredentials", x => x.Provider);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProviderCredentials");

            migrationBuilder.DropColumn(
                name: "CostSource",
                table: "RequestUsages");

            migrationBuilder.DropColumn(
                name: "GenerationId",
                table: "RequestUsages");

            migrationBuilder.DropColumn(
                name: "ResolvedModelId",
                table: "RequestUsages");

            migrationBuilder.DropColumn(
                name: "OpenRouter",
                table: "Models");
        }
    }
}
