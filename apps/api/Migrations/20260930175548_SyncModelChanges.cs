using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketPortal.Api.Migrations
{
    /// <inheritdoc />
    public partial class SyncModelChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tickets_TripSeatId",
                table: "Tickets");

            migrationBuilder.CreateTable(
                name: "StaffSalesCounterAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StaffProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SalesCounterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    EffectiveFromUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EffectiveToUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffSalesCounterAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffSalesCounterAssignments_SalesCounters_SalesCounterId",
                        column: x => x.SalesCounterId,
                        principalTable: "SalesCounters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StaffSalesCounterAssignments_StaffProfiles_StaffProfileId",
                        column: x => x.StaffProfileId,
                        principalTable: "StaffProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TripSeatId",
                table: "Tickets",
                column: "TripSeatId",
                filter: "[Status] <> 5 AND [Status] <> 6");

            migrationBuilder.CreateIndex(
                name: "IX_StaffSalesCounterAssignments_SalesCounterId_IsActive",
                table: "StaffSalesCounterAssignments",
                columns: new[] { "SalesCounterId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_StaffSalesCounterAssignments_StaffProfileId_IsActive",
                table: "StaffSalesCounterAssignments",
                columns: new[] { "StaffProfileId", "IsActive" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StaffSalesCounterAssignments");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_TripSeatId",
                table: "Tickets");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TripSeatId",
                table: "Tickets",
                column: "TripSeatId",
                unique: true);
        }
    }
}
