using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataAccessLayer.Migrations
{
    /// <inheritdoc />
    public partial class AddTokenTrackingAndHasContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TokensUsed",
                table: "Users",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "HasContext",
                table: "ChatHistories",
                type: "bit",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "EmbeddingModels",
                keyColumn: "EmbeddingModelId",
                keyValue: 1,
                columns: new[] { "Description", "ModelName" },
                values: new object[] { "Google Gemini gemini-embedding-001 (768 dims)", "gemini-embedding-001" });

            migrationBuilder.UpdateData(
                table: "Users",
                keyColumn: "UserId",
                keyValue: 1,
                column: "TokensUsed",
                value: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TokensUsed",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "HasContext",
                table: "ChatHistories");

            migrationBuilder.UpdateData(
                table: "EmbeddingModels",
                keyColumn: "EmbeddingModelId",
                keyValue: 1,
                columns: new[] { "Description", "ModelName" },
                values: new object[] { "Google Gemini text-embedding-004 (768 dims)", "text-embedding-004" });
        }
    }
}
