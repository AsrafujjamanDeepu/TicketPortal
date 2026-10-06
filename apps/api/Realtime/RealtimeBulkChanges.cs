using TicketPortal.Api.Data;

namespace TicketPortal.Api.Realtime
{
    // docs/02-Project-Concept-and-Solution.md (Real-time updates), Chunk 3 — builds the CapturedChange entries a service reports (via
    // IRealtimeNotifier.EntityChangedAsync) after changing rows with bulk SQL.
    //
    // Each change carries the scope it belongs to, so RealtimeScopeResolver finds nothing to look
    // up and RealtimeRouter sends it to the right groups:
    //
    //   TripSeats        platform; also makes the router send SeatAvailability to the trip's group
    //   SeatHolds        platform; likewise SeatAvailability for the trip
    //   Bookings         platform + the booking's operator + the booking's customer
    //   OperatorWallets  platform + that operator
    //   CustomerProfiles platform + that customer
    //
    // Entity names are the DbSet names (= table names, the same spelling Chunk 2's capture uses).
    // nameof() keeps them tied to AppDbContext at compile time.
    public static class RealtimeBulkChanges
    {
        // What a bulk Booking update needs to remember BEFORE it runs: once the status has moved,
        // the query that found the rows no longer matches them.
        public sealed record BookingScope(Guid Id, Guid TripId, Guid BusOperatorId, Guid? CustomerProfileId);

        public static IEnumerable<CapturedChange> Seats(IEnumerable<Guid> tripIds) =>
            tripIds
                .Where(id => id != Guid.Empty)
                .Distinct()
                .Select(tripId => new CapturedChange
                {
                    Entity = nameof(AppDbContext.TripSeats),
                    Action = RealtimeActions.Updated,
                    TripId = tripId
                });

        public static IEnumerable<CapturedChange> SeatHolds(Guid tripId, IEnumerable<Guid> seatHoldIds) =>
            seatHoldIds
                .Distinct()
                .Select(holdId => new CapturedChange
                {
                    Entity = nameof(AppDbContext.SeatHolds),
                    Action = RealtimeActions.Updated,
                    Id = holdId,
                    TripId = tripId
                });

        public static IEnumerable<CapturedChange> SeatHolds(IEnumerable<(Guid HoldId, Guid TripId)> holds) =>
            holds
                .Select(hold => new CapturedChange
                {
                    Entity = nameof(AppDbContext.SeatHolds),
                    Action = RealtimeActions.Updated,
                    Id = hold.HoldId,
                    TripId = hold.TripId
                });

        public static IEnumerable<CapturedChange> Bookings(IEnumerable<BookingScope> bookings) =>
            bookings
                .Select(booking => new CapturedChange
                {
                    Entity = nameof(AppDbContext.Bookings),
                    Action = RealtimeActions.Updated,
                    Id = booking.Id,
                    TripId = booking.TripId,
                    OperatorId = booking.BusOperatorId,
                    CustomerProfileId = booking.CustomerProfileId
                });

        public static IEnumerable<CapturedChange> OperatorWallet(Guid busOperatorId) =>
            new[]
            {
                new CapturedChange
                {
                    Entity = nameof(AppDbContext.OperatorWallets),
                    Action = RealtimeActions.Updated,
                    OperatorId = busOperatorId
                }
            };

        public static IEnumerable<CapturedChange> CustomerProfile(Guid customerProfileId) =>
            new[]
            {
                new CapturedChange
                {
                    Entity = nameof(AppDbContext.CustomerProfiles),
                    Action = RealtimeActions.Updated,
                    Id = customerProfileId,
                    CustomerProfileId = customerProfileId
                }
            };
    }
}
