using TicketPortal.Api.Data;
using TicketPortal.Api.Models.Bookings;
using TicketPortal.Api.Models.Enums;
using TicketPortal.Api.Models.Scheduling;
using TicketPortal.Api.Realtime;
using Microsoft.EntityFrameworkCore;

namespace TicketPortal.Api.Services
{
    public class SeatsUnavailableException : Exception
    {
        public SeatsUnavailableException(string message) : base(message) { }
    }

    // Chunk 3 task 1: HoldSeatsAsync used to only check that the trip existed — a cancelled,
    // departed, or otherwise-finished trip (correctly hidden from search) could still be held
    // by calling POST /api/seatholds directly. Kept separate from SeatsUnavailableException
    // (a per-seat race) since the failure reason and the right caller-facing message are
    // different: "this trip isn't bookable" vs. "someone just took that seat".
    public class TripNotBookableException : Exception
    {
        public TripNotBookableException(string message) : base(message) { }
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
        private readonly IConfiguration _configuration;
        private readonly IRealtimeNotifier _notifier;

        // The notifier is optional on purpose: DemoDataSeeder builds this class by hand
        // (`new SeatHoldService(db, configuration)`), and that call site must keep compiling and
        // simply announce nothing. With DI the registered notifier is injected.
        public SeatHoldService(AppDbContext db, IConfiguration configuration, IRealtimeNotifier? notifier = null)
        {
            _db = db;
            _configuration = configuration;
            _notifier = notifier ?? NullRealtimeNotifier.Instance;
        }

        // Realtime Chunk 3 (REALTIME_SIGNALR_PLAN.md).
        //
        // Every seat change in this class is a bulk ExecuteUpdateAsync, which EF Core's change
        // tracker (and therefore Chunk 2's SaveChanges capture) never sees. So each method reports
        // what it changed through RealtimeBulkChanges, using AnnounceAsync below. Two things make
        // that safe and quiet:
        //   * Every report is made while the method's transaction is still open. The notifier parks
        //     it until that transaction commits and drops it if it rolls back, so a client never
        //     re-fetches before the change is visible, and never hears about one that was undone.
        //   * HoldSeatsAsync, ReleaseHoldAsync and ConvertHoldToBookingAsync also save a tracked
        //     SeatHold in the same transaction, which Chunk 2 already announces. The parked bulk
        //     change and those saved rows are released together as ONE batch, and the router sends
        //     one SeatAvailability per trip per batch — so a seat-map viewer gets a single signal,
        //     not two. What the bulk report adds there is the TripSeats signal for the admin pages.
        private async Task AnnounceAsync(IEnumerable<CapturedChange> changes)
        {
            try
            {
                await _notifier.EntityChangedAsync(_db, changes);
            }
            catch (Exception)
            {
                // Best-effort by design — a realtime problem must never change a booking outcome.
            }
        }

        // The Draft/PendingPayment bookings a bulk UPDATE is about to move (to Expired or
        // Cancelled) must be read BEFORE it runs: afterwards the status filter no longer matches
        // them. Skipped entirely (no query) when nobody is listening.
        private async Task<List<RealtimeBulkChanges.BookingScope>> SnapshotBookingsAsync(IQueryable<Booking> bookings)
        {
            if (!_notifier.IsLive)
            {
                return new List<RealtimeBulkChanges.BookingScope>();
            }

            try
            {
                var rows = await bookings
                    .AsNoTracking()
                    .Select(b => new { b.Id, b.TripId, b.BusOperatorId, b.CustomerProfileId })
                    .ToListAsync();

                return rows
                    .Select(b => new RealtimeBulkChanges.BookingScope(b.Id, b.TripId, b.BusOperatorId, b.CustomerProfileId))
                    .ToList();
            }
            catch (Exception)
            {
                // Best-effort by design: without the snapshot those booking screens simply catch
                // up on their next refresh.
                return new List<RealtimeBulkChanges.BookingScope>();
            }
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
            string? userAgent,
            bool skipSellabilityCheck = false)
        {
            if (tripSeatIds.Count == 0)
            {
                throw new ArgumentException("At least one seat must be selected.", nameof(tripSeatIds));
            }

            var now = DateTime.UtcNow;

            // Without this check, a bad/typo'd tripId doesn't fail until the SaveChangesAsync
            // below, as a raw foreign-key-violation DbUpdateException — much harder to turn
            // into a clean 4xx response than a check we control right here. Also loads exactly
            // what Chunk 3's trip-state gating needs (Status/DepartureTimeUtc), so this stays a
            // single round-trip instead of a second query just for the sellability check.
            var trip = await _db.Trips
                .Where(t => t.Id == tripId)
                .Select(t => new { t.Status, t.DepartureTimeUtc })
                .FirstOrDefaultAsync();
            if (trip is null)
            {
                throw new InvalidOperationException($"Trip {tripId} does not exist.");
            }

            // Chunk 3 task 1: refuse a hold on a trip that's Cancelled/Departed/Running/
            // Arrived/Completed, or whose departure (optionally minus a configurable stop-sales
            // window) has already passed — the same rule TripsController.Search and
            // BookingBusesController already use to keep such trips out of results in the first
            // place. skipSellabilityCheck exists ONLY for DemoDataSeeder backfilling a booking
            // onto an already-completed historical trip; every real caller (SeatHoldsController)
            // leaves it false.
            if (!skipSellabilityCheck)
            {
                var stopSalesWindow = TimeSpan.FromMinutes(
                    _configuration.GetValue("SeatHold:StopSalesMinutesBeforeDeparture", 0));

                if (!TripSellability.IsSellable(trip.Status, trip.DepartureTimeUtc, now, stopSalesWindow))
                {
                    throw new TripNotBookableException(
                        TripSellability.GetUnsellableReason(trip.Status, trip.DepartureTimeUtc, now, stopSalesWindow));
                }
            }

            // HeldByUserId is a foreign key to AspNetUsers. A signed-in browser can present a token
            // for a user that no longer exists (e.g. the dev database was re-created), which used
            // to surface only as the generic "referenced records" error below. Say what is wrong.
            if (heldByUserId.HasValue && !await _db.Users.AnyAsync(u => u.Id == heldByUserId.Value))
            {
                throw new InvalidOperationException(
                    "Your login belongs to an account that no longer exists. Please log out and log in again.");
            }

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

            // Realtime Chunk 3: the seats just moved Available -> Held with a bulk UPDATE.
            await AnnounceAsync(RealtimeBulkChanges.Seats(new[] { tripId }));

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

            // Realtime Chunk 3: the seats just went back to Available with a bulk UPDATE.
            await AnnounceAsync(RealtimeBulkChanges.Seats(new[] { hold.TripId }));

            hold.Status = SeatHoldStatus.Released;
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        // Step 2 of checkout: payment has succeeded, so turn the temporary hold into a real,
        // permanent Booking. We re-check the timer even here, inside the same transaction,
        // because the background sweep job (see ExpireOverdueHoldsAsync) could theoretically
        // have reclaimed these exact seats a split second before this payment confirmation arrived.
        //
        // expectedSeatHoldId is the caller's Booking.SeatHoldId (set once, at
        // BookingsController.Create, and never trusted from the client) — NOT derived from
        // holdToken itself. Without this cross-check, this method would happily reassign
        // whatever hold holdToken pointed to onto bookingId, with no verification the two ever
        // had anything to do with each other: a stale or mismatched token from the caller (a
        // reloaded checkout tab, a second unrelated hold from browsing another trip, or
        // deliberately supplied) would silently attach a completely different trip/seats/price
        // onto this booking. Same reasoning as the same-style fix in
        // PaymentConfirmationService.InitiatePaymentAsync.
        public async Task ConvertHoldToBookingAsync(string holdToken, Guid bookingId, Guid expectedSeatHoldId)
        {
            await using var transaction = await _db.Database.BeginTransactionAsync();

            var hold = await _db.SeatHolds.FirstOrDefaultAsync(h => h.HoldToken == holdToken);
            if (hold is null || hold.Status != SeatHoldStatus.Active || hold.HoldExpiresAtUtc <= DateTime.UtcNow)
            {
                throw new InvalidOperationException(
                    "This seat hold has expired. The seats must be reselected and paid for again.");
            }

            if (hold.Id != expectedSeatHoldId)
            {
                // Treated the same as an invalid/expired hold — the caller (see
                // PaymentConfirmationService.ConfirmOnlinePaymentAsync) already handles that as
                // a "seats lost, refund the payment" case, which is exactly the right outcome
                // here too: money was taken for booking X, but the seats this token points to
                // were never actually booking X's.
                throw new InvalidOperationException(
                    "This seat hold does not belong to the booking being confirmed. The seats must be reselected and paid for again.");
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

            // Realtime Chunk 3: the seats just moved Held -> Booked with a bulk UPDATE.
            await AnnounceAsync(RealtimeBulkChanges.Seats(new[] { hold.TripId }));

            hold.Status = SeatHoldStatus.ConvertedToBooking;
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        // Called once a booked seat's ticket has actually been cancelled/refunded (see
        // CancellationProcessingService.ApproveAsync) — the seat goes back on sale, the same
        // outcome as an expired or released hold. Scoped to a specific bookingId as well as the
        // seat ids themselves, so this can never be used to release a seat belonging to some
        // other booking by mistake. Only ever moves a seat that is currently Booked; anything
        // already Available/Held/Blocked is left untouched, and the returned count lets the
        // caller notice if fewer seats moved than it expected.
        //
        // A single ExecuteUpdateAsync call is already atomic on its own, so this doesn't open
        // its own transaction — callers that need it combined with other writes (e.g. the ticket
        // status change that triggers this) should wrap both in one transaction on their side,
        // the same way HoldSeatsAsync's caller-facing transaction covers everything it touches.
        public async Task<int> ReleaseCancelledSeatsAsync(Guid bookingId, IReadOnlyCollection<Guid> tripSeatIds)
        {
            if (tripSeatIds.Count == 0)
            {
                return 0;
            }

            var released = await _db.TripSeats
                .Where(ts => ts.BookingId == bookingId
                    && tripSeatIds.Contains(ts.Id)
                    && ts.Status == TripSeatStatus.Booked)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(ts => ts.Status, TripSeatStatus.Available)
                    .SetProperty(ts => ts.BookingId, (Guid?)null));

            // Realtime Chunk 3: the seats are back on sale, so tell that trip's seat-map viewers.
            // Both callers (CancellationProcessingService.ApproveAsync and ExternalBookingSyncService)
            // run this inside their own transaction; the announcement is parked until it commits
            // (see AnnounceAsync), so this method needs no knowledge of who owns the transaction.
            if (released > 0 && _notifier.IsLive)
            {
                try
                {
                    var tripIds = await _db.TripSeats
                        .AsNoTracking()
                        .Where(ts => tripSeatIds.Contains(ts.Id))
                        .Select(ts => ts.TripId)
                        .Distinct()
                        .ToListAsync();

                    await AnnounceAsync(RealtimeBulkChanges.Seats(tripIds));
                }
                catch (Exception)
                {
                    // Best-effort by design — never fail a cancellation over a realtime lookup.
                }
            }

            return released;
        }

        // Chunk 5's trip-cancel cascade needs this trip-scoped sibling to
        // ExpireOverdueHoldsAsync below — that one only reaches holds whose OWN timer has run
        // out; this one reaches EVERY still-Active hold on ONE trip regardless of its timer,
        // because once a trip is Cancelled its seats are never going to be sold, held, or paid
        // for again. Mirrors that method's same three-part shape (free the seats, close out
        // the holds, un-stick any Draft/PendingPayment Booking that was waiting on one of
        // them) so a cancelled trip can never be left with a seat that still reads Held, or a
        // booking stuck forever at PendingPayment for a trip that will now never run. Unlike
        // ExpireOverdueHoldsAsync, the affected bookings are set to Cancelled rather than
        // Expired — nothing "timed out" here, the trip itself was called off.
        public async Task<int> ReleaseActiveHoldsForTripAsync(Guid tripId, string? reason = null)
        {
            await using var transaction = await _db.Database.BeginTransactionAsync();

            var activeHoldIds = await _db.SeatHolds
                .Where(h => h.TripId == tripId && h.Status == SeatHoldStatus.Active)
                .Select(h => h.Id)
                .ToListAsync();

            if (activeHoldIds.Count == 0)
            {
                await transaction.CommitAsync();
                return 0;
            }

            await _db.TripSeats
                .Where(ts => ts.CurrentSeatHoldId != null && activeHoldIds.Contains(ts.CurrentSeatHoldId.Value))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(ts => ts.Status, TripSeatStatus.Available)
                    .SetProperty(ts => ts.CurrentSeatHoldId, (Guid?)null));

            await _db.SeatHolds
                .Where(h => activeHoldIds.Contains(h.Id))
                .ExecuteUpdateAsync(setters => setters
                    // Cancelled, not Released — Released is specifically "the customer backed
                    // out on their own"; this is an admin/system action closing out the hold
                    // because the trip itself was called off, which is exactly what
                    // SeatHoldStatus.Cancelled's own doc comment describes. (First real use of
                    // that status value — nothing else in the codebase sets it yet.)
                    .SetProperty(h => h.Status, SeatHoldStatus.Cancelled));

            var cancellationReason = string.IsNullOrWhiteSpace(reason)
                ? "Trip was cancelled by the operator."
                : reason;

            var stuckBookings = _db.Bookings
                .Where(b => b.SeatHoldId != null
                    && activeHoldIds.Contains(b.SeatHoldId.Value)
                    && (b.Status == BookingStatus.Draft || b.Status == BookingStatus.PendingPayment));

            var cancelledBookings = await SnapshotBookingsAsync(stuckBookings);

            await stuckBookings
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(b => b.Status, BookingStatus.Cancelled)
                    .SetProperty(b => b.CancelledAtUtc, DateTime.UtcNow)
                    .SetProperty(b => b.CancellationReason, cancellationReason));

            // Realtime Chunk 3: the freed seats, the closed holds and the cancelled bookings are
            // all bulk changes. Announced inside the transaction, so they go out only on commit.
            if (_notifier.IsLive)
            {
                await AnnounceAsync(RealtimeBulkChanges.Seats(new[] { tripId })
                    .Concat(RealtimeBulkChanges.SeatHolds(tripId, activeHoldIds))
                    .Concat(RealtimeBulkChanges.Bookings(cancelledBookings)));
            }

            await transaction.CommitAsync();
            return activeHoldIds.Count;
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

            // Realtime Chunk 3: each hold's TripId is read as well as its id, so the trips whose
            // seats this sweep frees can be announced to their seat-map viewers.
            var expiredHolds = await _db.SeatHolds
                .Where(h => h.Status == SeatHoldStatus.Active && h.HoldExpiresAtUtc <= now)
                .OrderBy(h => h.HoldExpiresAtUtc)
                .Take(batchSize)
                .Select(h => new { h.Id, h.TripId })
                .ToListAsync();

            if (expiredHolds.Count == 0)
            {
                return 0;
            }

            var expiredHoldIds = expiredHolds.Select(h => h.Id).ToList();

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

            // ...and finally, the Booking these holds belong to (if one was ever created —
            // BookingsController.Create makes the Booking BEFORE payment, linked back via
            // Booking.SeatHoldId). Without this, a customer who abandons checkout leaves a
            // Booking row stuck at Draft/PendingPayment forever, even though its seats and hold
            // are already gone — Confirmed/later bookings are untouched since their hold was
            // already ConvertedToBooking, not Active, so they were never in expiredHoldIds.
            var abandonedBookings = _db.Bookings
                .Where(b => b.SeatHoldId != null
                    && expiredHoldIds.Contains(b.SeatHoldId.Value)
                    && (b.Status == BookingStatus.Draft || b.Status == BookingStatus.PendingPayment));

            var expiredBookings = await SnapshotBookingsAsync(abandonedBookings);

            await abandonedBookings
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(b => b.Status, BookingStatus.Expired));

            // Realtime Chunk 3: announced inside the transaction, so it goes out only on commit.
            // Seats -> every affected trip's seat map; holds -> the admin Seat Holds page;
            // bookings -> the customer's booking views and the operator/admin Bookings lists.
            if (_notifier.IsLive)
            {
                await AnnounceAsync(RealtimeBulkChanges.Seats(expiredHolds.Select(h => h.TripId))
                    .Concat(RealtimeBulkChanges.SeatHolds(expiredHolds.Select(h => (HoldId: h.Id, h.TripId))))
                    .Concat(RealtimeBulkChanges.Bookings(expiredBookings)));
            }

            await transaction.CommitAsync();
            return expiredHoldIds.Count;
        }
    }
}
