using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimpleRouter.Data.Migrations
{
    /// <inheritdoc />
    public partial class SystemOneDecisionSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Decisions",
                table: "Models",
                type: "INTEGER",
                nullable: false,
                // DecisionSupport.All: existing SystemOne models keep accepting everything they were forwarded before.
                defaultValue: 63);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Decisions",
                table: "Models");
        }
    }
}
