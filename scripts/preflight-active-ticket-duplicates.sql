/*
  C7-1 preflight: find trip seats that already have MORE THAN ONE active ticket.

  Run this against a disposable COPY of the database (restore a backup, or a throwaway
  LocalDB database) BEFORE applying migration 20261005104758_C7TicketIntegrityIndexes.
  The migration refuses to run while any row below exists, and it never deletes or cancels
  anything by itself - choosing which ticket stays valid is a business decision.

  "Active" means exactly what the new unique index means:
      Status NOT IN (5 = Cancelled, 6 = Refunded)  AND  IsDeleted = 0

  Usage (sqlcmd):  sqlcmd -S "(localdb)\MSSQLLocalDB" -d TicketPortalDB -i scripts/preflight-active-ticket-duplicates.sql
  Expected result on a healthy database: both result sets are empty.
*/
SET NOCOUNT ON;

-- 1) One row per offending seat.
SELECT t.TripSeatId,
       COUNT(*)                         AS ActiveTicketCount,
       MIN(t.IssuedAtUtc)               AS FirstIssuedAtUtc,
       MAX(t.IssuedAtUtc)               AS LastIssuedAtUtc
FROM Tickets AS t
WHERE t.Status NOT IN (5, 6) AND t.IsDeleted = 0
GROUP BY t.TripSeatId
HAVING COUNT(*) > 1
ORDER BY ActiveTicketCount DESC, t.TripSeatId;

-- 2) The tickets involved, with enough context to decide which one is the real one.
SELECT t.TripSeatId, t.Id AS TicketId, t.TicketNumber, t.Status, t.IssuedAtUtc,
       t.BookingId, b.Pnr, b.Status AS BookingStatus, t.SeatNumberSnapshot, t.FinalFare
FROM Tickets AS t
JOIN Bookings AS b ON b.Id = t.BookingId
WHERE t.Status NOT IN (5, 6) AND t.IsDeleted = 0
  AND t.TripSeatId IN (
        SELECT TripSeatId FROM Tickets
        WHERE Status NOT IN (5, 6) AND IsDeleted = 0
        GROUP BY TripSeatId HAVING COUNT(*) > 1)
ORDER BY t.TripSeatId, t.IssuedAtUtc, t.TicketNumber;

/*
  Resolving a duplicate (do this deliberately, one seat at a time, on the REAL database):
  keep the ticket whose booking is the one that really owns the seat (TripSeats.BookingId) and
  whose payment/ledger entries exist, then cancel + refund the others through the normal
  CancellationRequest / Refund workflow so money and ledgers stay consistent. Do not run a bulk
  UPDATE ... SET Status = 5 here - that would skip the refund and ledger entries.
*/
