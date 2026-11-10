using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ticketing.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class OutboxOrderLinkAndSeatOrderIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OrderId",
                table: "OutboxMessages",
                type: "uniqueidentifier",
                nullable: true);

            // Links requests still waiting to be sent, so a timeout can cancel them too.
            migrationBuilder.Sql(
                "UPDATE [OutboxMessages] SET [OrderId] = TRY_CONVERT(uniqueidentifier, JSON_VALUE([Payload], '$.OrderId'))");

            migrationBuilder.CreateIndex(
                name: "IX_Seats_CurrentOrderId",
                table: "Seats",
                column: "CurrentOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_OrderId",
                table: "OutboxMessages",
                column: "OrderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Seats_CurrentOrderId",
                table: "Seats");

            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_OrderId",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "OrderId",
                table: "OutboxMessages");
        }
    }
}
