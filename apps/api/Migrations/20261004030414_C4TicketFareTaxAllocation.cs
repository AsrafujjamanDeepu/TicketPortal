using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketPortal.Api.Migrations
{
    /// <inheritdoc />
    public partial class C4TicketFareTaxAllocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "TaxAmount",
                table: "Tickets",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            // Historical tickets predate ticket-level discount/tax allocation. Rebuild their
            // breakdown from the immutable booking totals using largest-remainder cents so
            // old paid tickets also have refund bases that add up to the original charge.
            migrationBuilder.Sql(@"
;WITH TicketBase AS
(
    SELECT t.Id, t.BookingId, t.Fare, b.DiscountAmount, b.TaxAmount, b.ServiceChargeAmount,
           SUM(t.Fare) OVER (PARTITION BY t.BookingId) AS GrossFareTotal,
           CONVERT(bigint, ROUND(b.DiscountAmount * 100, 0)) AS DiscountCents
    FROM Tickets t
    INNER JOIN Bookings b ON b.Id = t.BookingId
),
DiscountShare AS
(
    SELECT *,
           CASE WHEN GrossFareTotal > 0 THEN DiscountCents * Fare / GrossFareTotal ELSE 0 END AS DiscountExactCents
    FROM TicketBase
),
DiscountBase AS
(
    SELECT *, CONVERT(bigint, FLOOR(DiscountExactCents)) AS DiscountBaseCents,
           ROW_NUMBER() OVER (PARTITION BY BookingId ORDER BY DiscountExactCents - FLOOR(DiscountExactCents) DESC, Id) AS DiscountRemainderRank,
           SUM(CONVERT(bigint, FLOOR(DiscountExactCents))) OVER (PARTITION BY BookingId) AS DiscountBaseCentsTotal
    FROM DiscountShare
),
DiscountAlloc AS
(
    SELECT *, DiscountBaseCents + CASE WHEN DiscountRemainderRank <= DiscountCents - DiscountBaseCentsTotal THEN 1 ELSE 0 END AS AllocatedDiscountCents
    FROM DiscountBase
),
NetBase AS
(
    SELECT *, CASE WHEN SUM(Fare - AllocatedDiscountCents / 100.0) OVER (PARTITION BY BookingId) > 0
                   THEN Fare - AllocatedDiscountCents / 100.0 ELSE Fare END AS AllocationWeight
    FROM DiscountAlloc
),
TaxShare AS
(
    SELECT *, SUM(AllocationWeight) OVER (PARTITION BY BookingId) AS WeightTotal,
           CONVERT(bigint, ROUND(TaxAmount * 100, 0)) AS TaxCents,
           CASE WHEN SUM(AllocationWeight) OVER (PARTITION BY BookingId) > 0
                THEN ROUND(TaxAmount * 100, 0) * AllocationWeight / SUM(AllocationWeight) OVER (PARTITION BY BookingId)
                ELSE 0 END AS TaxExactCents
    FROM NetBase
),
TaxBase AS
(
    SELECT *, CONVERT(bigint, FLOOR(TaxExactCents)) AS TaxBaseCents,
           ROW_NUMBER() OVER (PARTITION BY BookingId ORDER BY TaxExactCents - FLOOR(TaxExactCents) DESC, Id) AS TaxRemainderRank,
           SUM(CONVERT(bigint, FLOOR(TaxExactCents))) OVER (PARTITION BY BookingId) AS TaxBaseCentsTotal
    FROM TaxShare
),
TaxAlloc AS
(
    SELECT *, TaxBaseCents + CASE WHEN TaxRemainderRank <= TaxCents - TaxBaseCentsTotal THEN 1 ELSE 0 END AS AllocatedTaxCents
    FROM TaxBase
),
ServiceShare AS
(
    SELECT *, CONVERT(bigint, ROUND(ServiceChargeAmount * 100, 0)) AS ServiceCents,
           CASE WHEN WeightTotal > 0
                THEN ROUND(ServiceChargeAmount * 100, 0) * AllocationWeight / WeightTotal
                ELSE 0 END AS ServiceExactCents
    FROM TaxAlloc
),
ServiceBase AS
(
    SELECT *, CONVERT(bigint, FLOOR(ServiceExactCents)) AS ServiceBaseCents,
           ROW_NUMBER() OVER (PARTITION BY BookingId ORDER BY ServiceExactCents - FLOOR(ServiceExactCents) DESC, Id) AS ServiceRemainderRank,
           SUM(CONVERT(bigint, FLOOR(ServiceExactCents))) OVER (PARTITION BY BookingId) AS ServiceBaseCentsTotal
    FROM ServiceShare
),
FinalAlloc AS
(
    SELECT Id,
           AllocatedDiscountCents / 100.0 AS AllocatedDiscount,
           AllocatedTaxCents / 100.0 AS AllocatedTax,
           (ServiceBaseCents + CASE WHEN ServiceRemainderRank <= ServiceCents - ServiceBaseCentsTotal THEN 1 ELSE 0 END) / 100.0 AS AllocatedService
    FROM ServiceBase
)
UPDATE t
SET t.DiscountAmount = a.AllocatedDiscount,
    t.TaxAmount = a.AllocatedTax,
    t.FinalFare = t.Fare - a.AllocatedDiscount + a.AllocatedTax + a.AllocatedService
FROM Tickets t
INNER JOIN FinalAlloc a ON a.Id = t.Id;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TaxAmount",
                table: "Tickets");
        }
    }
}
