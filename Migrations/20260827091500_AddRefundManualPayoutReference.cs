using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketPortal.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddRefundManualPayoutReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Refund: string (ManualPayoutReference) — proof of a manual bank/mobile-banking
            // payout for guest-checkout refunds (no CustomerProfile/wallet to credit). Only set
            // once RefundProcessingService.CompleteManualPayoutAsync moves a PendingManualPayout
            // refund to Succeeded.
            migrationBuilder.AddColumn<string>(
                name: "ManualPayoutReference",
                table: "Refunds",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ManualPayoutReference",
                table: "Refunds");
        }
    }
}
