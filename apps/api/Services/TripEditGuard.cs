using Microsoft.EntityFrameworkCore;
using TicketPortal.Api.Data;
using TicketPortal.Api.DTO;
using TicketPortal.Api.Models.Enums;
using TicketPortal.Api.Models.Scheduling;

namespace TicketPortal.Api.Services
{
    // Chunk 6 / C6-1 (decision D6) — what may be edited on a Trip once customers have a stake in it.
    //
    // THE RULE
    //   Once any seat on a trip is Held (someone is mid-checkout) or Booked, or any non-cancelled
    //   ticket exists for it, the things a customer relied on when they chose it are LOCKED:
    //   operator, route, bus, departure/arrival terminal, departure/arrival time, currency, and
    //   the fare of every Held/Booked seat. Everything else stays editable — TripCode,
    //   BaseFare, the fare of seats nobody has taken, wheelchair flag, status (subject to
    //   TripStatusTransitionRules), DelayReason.
    //
    // WHY BLOCK instead of "reschedule and notify"
    //   This project has no customer-notification sender (no email/SMS/push pipeline), so a
    //   "silently move your bus to 3 a.m." edit would leave customers believing the old time.
    //   A delay is expressed honestly by moving the trip to Delayed with a DelayReason, which
    //   every customer-facing screen already shows; a genuine change of time/bus/route is done
    //   by cancelling the trip (POST /api/trips/{id}/cancel — refunds every booking and
    //   releases every hold) and creating a new one. If a notification channel is added later,
    //   relaxing this guard is a one-place change.
    //
    // Everything below is deliberately pure (no database access) except GetSalesSnapshotAsync, so
    // the rules can be unit-tested directly.
    public sealed record TripSalesSnapshot(int HeldSeats, int BookedSeats, int ActiveTickets)
    {
        public bool HasSales => HeldSeats > 0 || BookedSeats > 0 || ActiveTickets > 0;
    }

    public sealed class TripSeatPlan
    {
        // Seats that exist on the trip but are no longer in the request.
        public List<TripSeat> ToRemove { get; } = new();

        // Seats present on both sides — only the fare can change; id, number, status, block
        // reason and external seat key are left exactly as they are.
        public List<(TripSeat Existing, TripSeatCreateDto Requested)> ToKeep { get; } = new();

        // Seats in the request that the trip does not have yet.
        public List<TripSeatCreateDto> ToAdd { get; } = new();
    }

    public static class TripEditGuard
    {
        // Edit forms (<input type="datetime-local">) hold minute precision, while a stored time may
        // carry seconds. Two instants less than a minute apart count as "the same time" so a plain
        // status or TripCode edit is never refused for a rounding difference; the controller keeps
        // the stored value in that case, so this is not a way to nudge a trip's time either.
        public static readonly TimeSpan TimeTolerance = TimeSpan.FromMinutes(1);

        public static async Task<TripSalesSnapshot> GetSalesSnapshotAsync(AppDbContext db, Trip trip)
        {
            var held = trip.TripSeats.Count(s => s.Status == TripSeatStatus.Held);
            var booked = trip.TripSeats.Count(s => s.Status == TripSeatStatus.Booked);

            var tickets = await db.Tickets.CountAsync(t =>
                t.TripId == trip.Id
                && t.Status != TicketStatus.Cancelled
                && t.Status != TicketStatus.Refunded);

            return new TripSalesSnapshot(held, booked, tickets);
        }

        // Names (as the API's JSON property names) of the trip-level fields the request would
        // change but that are locked once the trip has sales. Empty = the edit is fine.
        public static IReadOnlyList<string> FindLockedFieldChanges(Trip trip, TripUpdateDto dto)
        {
            var changed = new List<string>();

            if (dto.BusOperatorId != trip.BusOperatorId) changed.Add("busOperatorId");
            if (dto.BusRouteId != trip.BusRouteId) changed.Add("busRouteId");
            if (dto.BusId != trip.BusId) changed.Add("busId");
            if (dto.DepartureTerminalId != trip.DepartureTerminalId) changed.Add("departureTerminalId");
            if (dto.ArrivalTerminalId != trip.ArrivalTerminalId) changed.Add("arrivalTerminalId");

            if (!SameInstant(dto.DepartureTimeUtc, trip.DepartureTimeUtc)) changed.Add("departureTimeUtc");
            if (!SameInstant(dto.ArrivalTimeUtc, trip.ArrivalTimeUtc)) changed.Add("arrivalTimeUtc");

            if (!string.Equals(dto.Currency?.Trim(), trip.Currency?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                changed.Add("currency");
            }

            return changed;
        }

        // Held/Booked seats that the request would drop, or whose fare it would change. Returned
        // as human-readable fragments ("A1 (fare)", "B2 (removed)") for the error message.
        public static IReadOnlyList<string> FindLockedSeatChanges(Trip trip, IEnumerable<TripSeatCreateDto> requestedSeats)
        {
            var requestedBySeatId = requestedSeats
                .GroupBy(s => s.SeatId)
                .ToDictionary(g => g.Key, g => g.First());

            var changed = new List<string>();

            foreach (var existing in trip.TripSeats.Where(s =>
                         s.Status == TripSeatStatus.Held || s.Status == TripSeatStatus.Booked))
            {
                if (!requestedBySeatId.TryGetValue(existing.SeatId, out var requested))
                {
                    changed.Add($"{existing.SeatNumber} (removed)");
                }
                else if (requested.Fare != existing.Fare)
                {
                    changed.Add($"{existing.SeatNumber} (fare)");
                }
            }

            return changed;
        }

        // Splits the request into remove / keep-and-reprice / add, matching on the physical SeatId.
        public static TripSeatPlan PlanSeatChanges(Trip trip, IEnumerable<TripSeatCreateDto> requestedSeats)
        {
            var plan = new TripSeatPlan();
            var requestedBySeatId = requestedSeats.ToDictionary(s => s.SeatId);
            var existingBySeatId = trip.TripSeats.ToDictionary(s => s.SeatId);

            foreach (var existing in trip.TripSeats)
            {
                if (requestedBySeatId.TryGetValue(existing.SeatId, out var requested))
                {
                    plan.ToKeep.Add((existing, requested));
                }
                else
                {
                    plan.ToRemove.Add(existing);
                }
            }

            foreach (var requested in requestedBySeatId.Values)
            {
                if (!existingBySeatId.ContainsKey(requested.SeatId))
                {
                    plan.ToAdd.Add(requested);
                }
            }

            return plan;
        }

        // Of the seats that would be removed, those that cannot be (they appear in a hold, a
        // ticket, a booking passenger or an external seat mapping — all Restrict foreign keys,
        // so deleting them would fail the whole update). Returns their seat numbers.
        public static async Task<IReadOnlyList<string>> FindSeatsThatCannotBeRemovedAsync(
            AppDbContext db, IReadOnlyCollection<TripSeat> toRemove)
        {
            if (toRemove.Count == 0)
            {
                return Array.Empty<string>();
            }

            var ids = toRemove.Select(s => s.Id).ToList();

            var referenced = new HashSet<Guid>();
            referenced.UnionWith(await db.SeatHoldItems.Where(i => ids.Contains(i.TripSeatId)).Select(i => i.TripSeatId).ToListAsync());
            referenced.UnionWith(await db.Tickets.Where(t => ids.Contains(t.TripSeatId)).Select(t => t.TripSeatId).ToListAsync());
            referenced.UnionWith(await db.BookingPassengers.Where(p => p.TripSeatId != null && ids.Contains(p.TripSeatId.Value)).Select(p => p.TripSeatId!.Value).ToListAsync());
            referenced.UnionWith(await db.ExternalSeatMappings.Where(m => ids.Contains(m.TripSeatId)).Select(m => m.TripSeatId).ToListAsync());

            return toRemove
                .Where(s => referenced.Contains(s.Id))
                .Select(s => s.SeatNumber)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool SameInstant(DateTime a, DateTime b) => (a - b).Duration() < TimeTolerance;
    }
}
