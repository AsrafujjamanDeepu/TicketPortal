using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketPortal.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMasterDetailDataTypeFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // BusOperator: number (FoundedYear) + date (RegisteredOnUtc)
            migrationBuilder.AddColumn<int>(
                name: "FoundedYear",
                table: "BusOperators",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RegisteredOnUtc",
                table: "BusOperators",
                type: "datetime2",
                nullable: true);

            // Bus: date (RegistrationDate)
            migrationBuilder.AddColumn<DateTime>(
                name: "RegistrationDate",
                table: "Buses",
                type: "datetime2",
                nullable: true);

            // Trip: bool (IsWheelchairAccessible)
            migrationBuilder.AddColumn<bool>(
                name: "IsWheelchairAccessible",
                table: "Trips",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // CancellationPolicy: date (EffectiveFromUtc / EffectiveToUtc)
            migrationBuilder.AddColumn<DateTime>(
                name: "EffectiveFromUtc",
                table: "CancellationPolicies",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EffectiveToUtc",
                table: "CancellationPolicies",
                type: "datetime2",
                nullable: true);

            // NOTE: Booking.RequiresExternalConfirmation (bool) and Booking.ExpiresAtUtc (date)
            // already existed as columns from the InitialA migration — only the DTOs/controller
            // needed to expose them, so no schema change is required for Bookings here.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FoundedYear",
                table: "BusOperators");

            migrationBuilder.DropColumn(
                name: "RegisteredOnUtc",
                table: "BusOperators");

            migrationBuilder.DropColumn(
                name: "RegistrationDate",
                table: "Buses");

            migrationBuilder.DropColumn(
                name: "IsWheelchairAccessible",
                table: "Trips");

            migrationBuilder.DropColumn(
                name: "EffectiveFromUtc",
                table: "CancellationPolicies");

            migrationBuilder.DropColumn(
                name: "EffectiveToUtc",
                table: "CancellationPolicies");
        }
    }
}
