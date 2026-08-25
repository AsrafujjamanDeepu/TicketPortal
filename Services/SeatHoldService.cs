using TicketPortal.Api.Data;
using TicketPortal.Api.Models.Bookings;
using TicketPortal.Api.Models.Enums;
using TicketPortal.Api.Models.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace TicketPortal.Api.Services
{
    public class SeatsUnavailableException : Exception
    {
        public SeatsUnavailableException(string message) : base(message) { }
    }

    // This class is the ONLY place in the whole codebase allowed to change TripSeat.Status or
    // SeatHold.Status. That rule exists to protect against the platform's single biggest risk:
    // two customers being sold the same physical seat.
    //
    // The simple way to write this ("read the seat, check if it's free, then save it as held")
    // is NOT safe, because two customers could both do the "check if it's free" step at almost
    // the exact same moment, both see it as free, and both go ahead and hold it. Instead, every
    // method below asks the database to check-and-change a seat in ONE single instruction
    // ("update this seat to Held, but only if it's still Available right now"). The database
    // itself guarantees only one of those two customers can win that race — there's no gap in
    // time where both could slip through.
    //
    // Every method here either fully succeeds (seats moved + hold recorded) or fully rolls
    // back — never leaves things half-done.
    public class SeatHoldService
    {
        private readonly AppDbContext _db;

        public SeatHoldService(AppDbContext db)
        {
            _db = db;
        }

        // Step 1 of checkout: the customer has picked their seats on the seat map, and we now
        // reserve them for a few minutes so nobody else can grab them while payment is happening.
        //
        // Note on concurrency: TripSeat also has AuditableEntity's [Timestamp] RowVersion, EF
        // Core's normal built-in protection against two edits clashing. We deliberately don't
        // rely on that here — instead we send one single "UPDATE ... WHERE Status = Available"
        // instruction, so two customers trying to hold the same seat at once literally cannot
        // both succeed; the database's own row locking decides the winner, no error-catching or
        // retrying required. RowVersion is still there as a backup for any OTHER, non-hold way a
        // seat might get edited later (e.g. an admin fixing a seat's fare from a back-office screen).
        public async Task<SeatHold> HoldSeatsAsync(
            Guid tripId,
            IReadOnlyCollection<Guid> tripSeatIds,
            int holdMinutes,
            Guid? heldByUserId,
            string? clientIpAddress,
            string? userAgent)
        {
            if (tripSeatIds.Count == 0)
            {
                throw new ArgumentException("At least one seat must be selected.", nameof(tripSeatIds));
            }

            // Without this check, a bad/typo'd tripId doesn't fail until the SaveChangesAsync
            // below, as a raw foreign-key-violation DbUpdateException — much harder to turn
            // into a clean 4xx response than a check we control right here.
            var tripExists = await _db.Trips.AnyAsync(t => t.Id == tripId);
            if (!tripExists)
            {
                throw new InvalidOperationException($"Trip {tripId} does not exist.");
            }

            var now = DateTime.UtcNow;

            // Create the hold "envelope" first — the actual timer (3/5 minutes, from holdMinutes).
            var hold = new SeatHold
            {
                TripId = tripId,
                HeldByUserId = heldByUserId,
                HoldToken = Guid.NewGuid().ToString("N"),
                HoldStartedAtUtc = now,
                HoldExpiresAtUtc = now.AddMinutes(holdMinutes),
                Status = SeatHoldStatus.Active,
                ClientIpAddress = clientIpAddress,
                UserAgent = userAgent
            };

            await using var transaction = await _db.Database.BeginTransactionAsync();

            _db.SeatHolds.Add(hold);

            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                // Covers anything the upfront checks above didn't catch (e.g. HeldByUserId
                // pointing at a user that no longer exists) — turns a raw SQL exception into
                // something a controller can translate to a clean 4xx instead of a 500.
                throw new InvalidOperationException(
                    "Could not create the seat hold — one of the referenced records may no longer exist.", ex);
            }

            // The important line: try to flip every requested seat to Held, but only the ones
            // that are still Available. If "affected" comes back smaller than the number of
            // seats requested, someone else beat us to at least one seat — so we give up on the
            // whole hold and undo everything, rather than give the customer half their seats.
            var affected = await _db.TripSeats
                .Where(ts => ts.TripId == tripId
                    && tripSeatIds.Contains(ts.Id)
                    && ts.Status == TripSeatStatus.Available)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(ts => ts.Status, TripSeatStatus.Held)
                    .SetProperty(ts => ts.CurrentSeatHoldId, hold.Id));

            if (affected != tripSeatIds.Count)
            {
                await transaction.RollbackAsync();
                throw new SeatsUnavailableException(
                    "One or more selected seats were just taken by another customer. Please reselect.");
            }

            // Now that the hold has definitely won all the seats, record each one, with a
            // frozen copy of today's price so it can't change under the customer mid-checkout.
            var seatFares = await _db.TripSeats
                .Where(ts => tripSeatIds.Contains(ts.Id))
                .Select(ts => new { ts.Id, ts.Fare })
                .ToListAsync();

            foreach (var seat in seatFares)
            {
                _db.SeatHoldItems.Add(new SeatHoldItem
                {
                    SeatHoldId = hold.Id,
                    TripSeatId = seat.Id,
                    FareAtHold = seat.Fare
                });
            }

            await _db.SaveChangesAsync();
            await transaction.CommitAsync();

            return hold;
        }

        // The customer closes the tab, goes back, or deselects seats before paying — let the
        // seats go straight back to Available instead of waiting out the full timer.
        public async Task ReleaseHoldAsync(string holdToken)
        {
            await using var transaction = await _db.Database.BeginTransactionAsync();

            var hold = await _db.SeatHolds.FirstOrDefaultAsync(h => h.HoldToken == holdToken);
            if (hold is null || hold.Status != SeatHoldStatus.Active)
            {
                return; // Already released/expired/converted elsewhere — nothing left to do.
            }

            await _db.TripSeats
                .Where(ts => ts.CurrentSeatHoldId == hold.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(ts => ts.Status, TripSeatStatus.Available)
                    .SetProperty(ts => ts.CurrentSeatHoldId, (Guid?)null));

            hold.Status = SeatHoldStatus.Released;
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        // Step 2 of checkout: payment has succeeded, so turn the temporary hold into a real,
        // permanent Booking. We re-check the timer even here, inside the same transaction,
        // because the background sweep job (see ExpireOverdueHoldsAsync) could theoretically
        // have reclaimed these exact seats a split second before this payment confirmation arrived.
        public async Task ConvertHoldToBookingAsync(string holdToken, Guid bookingId)
        {
            await using var transaction = await _db.Database.BeginTransactionAsync();

            var hold = await _db.SeatHolds.FirstOrDefaultAsync(h => h.HoldToken == holdToken);
            if (hold is null || hold.Status != SeatHoldStatus.Active || hold.HoldExpiresAtUtc <= DateTime.UtcNow)
            {
                throw new InvalidOperationException(
                    "This seat hold has expired. The seats must be reselected and paid for again.");
            }

            var affected = await _db.TripSeats
                .Where(ts => ts.CurrentSeatHoldId == hold.Id && ts.Status == TripSeatStatus.Held)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(ts => ts.Status, TripSeatStatus.Booked)
                    .SetProperty(ts => ts.BookingId, bookingId)
                    .SetProperty(ts => ts.CurrentSeatHoldId, (Guid?)null));

            if (affected == 0)
            {
                // Money was taken but the seats slipped away in the meantime — this has to be
                // treated as a refund case by whatever calls this method, not silently ignored.
                throw new InvalidOperationException("Held seats are no longer available. Payment must be refunded.");
            }

            hold.Status = SeatHoldStatus.ConvertedToBooking;
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        // This is the automatic "clean-up" job that makes the 3/5 minute timer actually mean
        // something. Meant to be called on a schedule (e.g. every 30 seconds) by a background
        // worker. It finds every hold whose time ran out without a completed payment, and puts
        // those seats back on sale. Works across ALL trips at once in one batch — this is
        // exactly why the SeatHold(Status, HoldExpiresAtUtc) index exists in AppDbContext, so
        // this search stays fast even with a huge number of trips running at once.
        public async Task<int> ExpireOverdueHoldsAsync(int batchSize = 200)
        {
            var now = DateTime.UtcNow;

            var expiredHoldIds = await _db.SeatHolds
                .Where(h => h.Status == SeatHoldStatus.Active && h.HoldExpiresAtUtc <= now)
                .OrderBy(h => h.HoldExpiresAtUtc)
                .Take(batchSize)
                .Select(h => h.Id)
                .ToListAsync();

            if (expiredHoldIds.Count == 0)
            {
                return 0;
            }

            await using var transaction = await _db.Database.BeginTransactionAsync();

            // Free up the seats first...
            await _db.TripSeats
                .Where(ts => ts.CurrentSeatHoldId != null && expiredHoldIds.Contains(ts.CurrentSeatHoldId.Value))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(ts => ts.Status, TripSeatStatus.Available)
                    .SetProperty(ts => ts.CurrentSeatHoldId, (Guid?)null));

            // ...then mark the holds themselves as expired, so there's a permanent record of
            // what happened and when.
            await _db.SeatHolds
                .Where(h => expiredHoldIds.Contains(h.Id))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(h => h.Status, SeatHoldStatus.Expired));

            await transaction.CommitAsync();
            return expiredHoldIds.Count;
        }
    }
}
