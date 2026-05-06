using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PocketSignal.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddBinaryTradeResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BinaryTradeResults",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Direction = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ExpiryMinutes = table.Column<int>(type: "int", nullable: false),
                    Confidence = table.Column<int>(type: "int", nullable: false),
                    Grade = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    EntryPrice = table.Column<decimal>(type: "decimal(18,5)", precision: 18, scale: 5, nullable: false),
                    ExitPrice = table.Column<decimal>(type: "decimal(18,5)", precision: 18, scale: 5, nullable: true),
                    Difference = table.Column<decimal>(type: "decimal(18,5)", precision: 18, scale: 5, nullable: true),
                    Result = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    SignalMessage = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ExpiryReason = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DueAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CheckedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResultNotifiedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResultNotificationMessage = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BinaryTradeResults", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ForexTradeResults_CreatedAtUtc",
                table: "ForexTradeResults",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ForexTradeResults_Result",
                table: "ForexTradeResults",
                column: "Result");

            migrationBuilder.CreateIndex(
                name: "IX_ForexTradeResults_Symbol",
                table: "ForexTradeResults",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_BinaryTradeResults_CreatedAtUtc",
                table: "BinaryTradeResults",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_BinaryTradeResults_DueAtUtc",
                table: "BinaryTradeResults",
                column: "DueAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_BinaryTradeResults_Result",
                table: "BinaryTradeResults",
                column: "Result");

            migrationBuilder.CreateIndex(
                name: "IX_BinaryTradeResults_Symbol",
                table: "BinaryTradeResults",
                column: "Symbol");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BinaryTradeResults");

            migrationBuilder.DropIndex(
                name: "IX_ForexTradeResults_CreatedAtUtc",
                table: "ForexTradeResults");

            migrationBuilder.DropIndex(
                name: "IX_ForexTradeResults_Result",
                table: "ForexTradeResults");

            migrationBuilder.DropIndex(
                name: "IX_ForexTradeResults_Symbol",
                table: "ForexTradeResults");
        }
    }
}
