using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimpleRouter.Data.Migrations
{
    /// <inheritdoc />
    public partial class ModelMaxPromptTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxPromptTokens",
                table: "Models",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxPromptTokens",
                table: "Models");
        }
    }
}
