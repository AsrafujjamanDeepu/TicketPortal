using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketPortal.Api.Migrations
{
    /// <inheritdoc />
    public partial class C7TicketIntegrityIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // C7-1 PREFLIGHT. A unique index cannot be built over rows that already break the rule,
            // and silently picking which duplicate ticket to cancel would be a financial decision
            // (refunds, ledger entries, boarding). So this migration NEVER deletes or cancels anything:
            // if two live tickets already share a seat it throws, EF rolls the whole migration back
            // (nothing changes), and the message says exactly what to do. List the offending rows with
            // scripts/preflight-active-ticket-duplicates.sql, resolve each seat deliberately through the
            // normal cancellation/refund workflow, then re-run the migration. "Live" uses the same
            // predicate as the index filter below (Status not Cancelled(5)/Refunded(6), not soft-deleted).
            migrationBuilder.Sql(@"
DECLARE @duplicateSeats int =
(
    SELECT COUNT(*) FROM
    (
        SELECT t.TripSeatId
        FROM Tickets AS t
        WHERE t.Status NOT IN (5, 6) AND t.IsDeleted = 0
        GROUP BY t.TripSeatId
        HAVING COUNT(*) > 1
    ) AS d
);
IF @duplicateSeats > 0
BEGIN
    DECLARE @message nvarchar(2048) = CONCAT(
        N'C7TicketIntegrityIndexes cannot run: ', @duplicateSeats,
        N' trip seat(s) already have more than one active ticket. Run scripts/preflight-active-ticket-duplicates.sql, ',
        N'cancel/refund the extra ticket(s) deliberately, then apply this migration again. Nothing was changed.');
    THROW 51000, @message, 1;
END");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_TripSeatId",
                table: "Tickets");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_QrCodePayload",
                table: "Tickets",
                column: "QrCodePayload");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TripSeatId",
                table: "Tickets",
                column: "TripSeatId",
                unique: true,
                filter: "[Status] <> 5 AND [Status] <> 6 AND [IsDeleted] = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tickets_QrCodePayload",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_TripSeatId",
                table: "Tickets");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TripSeatId",
                table: "Tickets",
                column: "TripSeatId",
                filter: "[Status] <> 5 AND [Status] <> 6");
        }
    }
}
