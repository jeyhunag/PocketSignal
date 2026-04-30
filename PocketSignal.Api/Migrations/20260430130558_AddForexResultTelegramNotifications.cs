using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PocketSignal.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddForexResultTelegramNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastNotificationError",
                table: "ForexTradeResults",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastNotifiedAtUtc",
                table: "ForexTradeResults",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastNotifiedResult",
                table: "ForexTradeResults",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastNotificationError",
                table: "ForexTradeResults");

            migrationBuilder.DropColumn(
                name: "LastNotifiedAtUtc",
                table: "ForexTradeResults");

            migrationBuilder.DropColumn(
                name: "LastNotifiedResult",
                table: "ForexTradeResults");
        }
    }
}
