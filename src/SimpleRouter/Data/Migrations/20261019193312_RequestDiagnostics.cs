using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimpleRouter.Data.Migrations
{
    /// <inheritdoc />
    public partial class RequestDiagnostics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FailureDetail",
                table: "RequestUsages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureKind",
                table: "RequestUsages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsStreaming",
                table: "RequestUsages",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Outcome",
                table: "RequestUsages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SessionKey",
                table: "RequestUsages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StopReason",
                table: "RequestUsages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpstreamModelId",
                table: "RequestUsages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RequestUsages_SessionKey_Tier_Id",
                table: "RequestUsages",
                columns: new[] { "SessionKey", "Tier", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RequestUsages_SessionKey_Tier_Id",
                table: "RequestUsages");

            migrationBuilder.DropColumn(
                name: "FailureDetail",
                table: "RequestUsages");

            migrationBuilder.DropColumn(
                name: "FailureKind",
                table: "RequestUsages");

            migrationBuilder.DropColumn(
                name: "IsStreaming",
                table: "RequestUsages");

            migrationBuilder.DropColumn(
                name: "Outcome",
                table: "RequestUsages");

            migrationBuilder.DropColumn(
                name: "SessionKey",
                table: "RequestUsages");

            migrationBuilder.DropColumn(
                name: "StopReason",
                table: "RequestUsages");

            migrationBuilder.DropColumn(
                name: "UpstreamModelId",
                table: "RequestUsages");
        }
    }
}
