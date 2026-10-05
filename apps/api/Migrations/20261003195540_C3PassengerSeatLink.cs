using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketPortal.Api.Migrations
{
    /// <inheritdoc />
    public partial class C3PassengerSeatLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TripSeatId",
                table: "BookingPassengers",
                type: "uniqueidentifier",
                nullable: true);

            // Existing passengers can only be linked confidently through an issued ticket.
            // Keep unticketed historical rows null instead of guessing from passenger order.
            migrationBuilder.Sql(@"
;WITH TicketSeatByPassenger AS
(
    SELECT t.BookingPassengerId, t.TripSeatId,
           ROW_NUMBER() OVER (PARTITION BY t.BookingPassengerId ORDER BY t.IssuedAtUtc DESC, t.Id DESC) AS RowNum
    FROM Tickets AS t
)
UPDATE bp
SET bp.TripSeatId = tsp.TripSeatId
FROM BookingPassengers AS bp
INNER JOIN TicketSeatByPassenger AS tsp ON tsp.BookingPassengerId = bp.Id AND tsp.RowNum = 1;");

            migrationBuilder.CreateIndex(
                name: "IX_BookingPassengers_TripSeatId",
                table: "BookingPassengers",
                column: "TripSeatId");

            migrationBuilder.AddForeignKey(
                name: "FK_BookingPassengers_TripSeats_TripSeatId",
                table: "BookingPassengers",
                column: "TripSeatId",
                principalTable: "TripSeats",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BookingPassengers_TripSeats_TripSeatId",
                table: "BookingPassengers");

            migrationBuilder.DropIndex(
                name: "IX_BookingPassengers_TripSeatId",
                table: "BookingPassengers");

            migrationBuilder.DropColumn(
                name: "TripSeatId",
                table: "BookingPassengers");
        }
    }
}
