using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataAccessLayer.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MonthlyQuestionCount",
                table: "Users",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "QuotaResetDate",
                table: "Users",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ShortTermQuestionCount",
                table: "Users",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ShortTermResetDate",
                table: "Users",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SubscriptionExpiry",
                table: "Users",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubscriptionPlan",
                table: "Users",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.UpdateData(
                table: "AiModels",
                keyColumn: "AiModelId",
                keyValue: 1,
                columns: new[] { "Description", "ModelName" },
                values: new object[] { "Google Gemini 2.0 Flash Lite — higher rate limit (30 RPM)", "gemini-2.0-flash-lite" });

            migrationBuilder.UpdateData(
                table: "Users",
                keyColumn: "UserId",
                keyValue: 1,
                columns: new[] { "MonthlyQuestionCount", "QuotaResetDate", "ShortTermQuestionCount", "ShortTermResetDate", "SubscriptionExpiry", "SubscriptionPlan" },
                values: new object[] { 0, null, 0, null, null, "Basic" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MonthlyQuestionCount",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "QuotaResetDate",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ShortTermQuestionCount",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ShortTermResetDate",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SubscriptionExpiry",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SubscriptionPlan",
                table: "Users");

            migrationBuilder.UpdateData(
                table: "AiModels",
                keyColumn: "AiModelId",
                keyValue: 1,
                columns: new[] { "Description", "ModelName" },
                values: new object[] { "Google Gemini 2.0 Flash — fast & cost-effective", "gemini-2.0-flash" });
        }
    }
}
