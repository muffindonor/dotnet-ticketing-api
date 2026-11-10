using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ticketing.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PaymentTimeoutAndOutboxCleanup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // From now on a row in the outbox means "not sent yet", so sent rows must go first.
            migrationBuilder.Sql("DELETE FROM [OutboxMessages] WHERE [PublishedAt] IS NOT NULL");

            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_CreatedAt",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "PublishedAt",
                table: "OutboxMessages");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PaymentStartedAt",
                table: "Orders",
                type: "datetimeoffset",
                nullable: true);

            // Payments already in flight need a start time, or they could never time out.
            migrationBuilder.Sql("UPDATE [Orders] SET [PaymentStartedAt] = SYSDATETIMEOFFSET() WHERE [Status] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_CreatedAt",
                table: "OutboxMessages",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_CreatedAt",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "PaymentStartedAt",
                table: "Orders");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PublishedAt",
                table: "OutboxMessages",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_CreatedAt",
                table: "OutboxMessages",
                column: "CreatedAt",
                filter: "[PublishedAt] IS NULL");
        }
    }
}
