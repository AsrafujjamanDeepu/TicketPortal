using TicketPortal.Api.Realtime;
using Xunit;

namespace TicketPortal.Api.Tests.Unit
{
    // REALTIME_SIGNALR_PLAN.md, Chunk 3 — the changes a service reports after a bulk UPDATE
    // (RealtimeBulkChanges), pushed through the real router. Pure functions, no database and no
    // SignalR: this pins who hears about each kind of bulk change, including the rule that a trip's
    // public seat-map group only ever gets the anonymous-safe SeatAvailability signal.
    public class RealtimeBulkChangesTests
    {
        private static readonly Guid OperatorA = Guid.NewGuid();
        private static readonly Guid OperatorB = Guid.NewGuid();
        private static readonly Guid CustomerOne = Guid.NewGuid();
        private static readonly Guid CustomerTwo = Guid.NewGuid();

        private static Dictionary<string, List<RealtimeChange>> RouteOf(IEnumerable<CapturedChange> changes) =>
            RealtimeRouter.Route(changes.ToList());

        [Fact]
        public void Seats_GoToThePlatformAndTheTripsSeatMap_AndNobodyElse()
        {
            var tripId = Guid.NewGuid();

            var routes = RouteOf(RealtimeBulkChanges.Seats(new[] { tripId }));

            Assert.Equal(
                new[] { RealtimeGroups.Platform, RealtimeGroups.Trip(tripId) }.OrderBy(g => g).ToArray(),
                routes.Keys.OrderBy(g => g).ToArray());

            var platform = Assert.Single(routes[RealtimeGroups.Platform]);
            Assert.Equal("TripSeats", platform.Entity);
            Assert.Equal(tripId, platform.TripId);

            // The public group gets the pseudo-entity and nothing that identifies a row, an
            // operator or a customer.
            var publicSignal = Assert.Single(routes[RealtimeGroups.Trip(tripId)]);
            Assert.Equal(RealtimeEvents.SeatAvailability, publicSignal.Entity);
            Assert.Equal(tripId, publicSignal.TripId);
            Assert.Null(publicSignal.Id);
            Assert.Null(publicSignal.OperatorId);
        }

        [Fact]
        public void Seats_IgnoreEmptyAndRepeatedTripIds()
        {
            var tripId = Guid.NewGuid();

            var changes = RealtimeBulkChanges.Seats(new[] { tripId, tripId, Guid.Empty }).ToList();

            var change = Assert.Single(changes);
            Assert.Equal(tripId, change.TripId);
        }

        [Fact]
        public void SeatHolds_GoToThePlatformAndTheTripsSeatMap_AndNobodyElse()
        {
            var tripId = Guid.NewGuid();
            var holdId = Guid.NewGuid();

            var routes = RouteOf(RealtimeBulkChanges.SeatHolds(tripId, new[] { holdId }));

            Assert.Equal(
                new[] { RealtimeGroups.Platform, RealtimeGroups.Trip(tripId) }.OrderBy(g => g).ToArray(),
                routes.Keys.OrderBy(g => g).ToArray());

            var platform = Assert.Single(routes[RealtimeGroups.Platform]);
            Assert.Equal("SeatHolds", platform.Entity);
            Assert.Equal(holdId, platform.Id);

            Assert.All(routes[RealtimeGroups.Trip(tripId)], m => Assert.Equal(RealtimeEvents.SeatAvailability, m.Entity));
        }

        [Fact]
        public void SeatHolds_OfSeveralTrips_EachTellTheirOwnTripOnly()
        {
            var tripOne = Guid.NewGuid();
            var tripTwo = Guid.NewGuid();

            var routes = RouteOf(RealtimeBulkChanges.SeatHolds(new[]
            {
                (HoldId: Guid.NewGuid(), TripId: tripOne),
                (HoldId: Guid.NewGuid(), TripId: tripTwo)
            }));

            Assert.Single(routes[RealtimeGroups.Trip(tripOne)]);
            Assert.Single(routes[RealtimeGroups.Trip(tripTwo)]);
            Assert.Equal(2, routes[RealtimeGroups.Platform].Count);
        }

        [Fact]
        public void Bookings_GoToThePlatform_TheirOperator_AndTheirOwnCustomerOnly()
        {
            var bookingId = Guid.NewGuid();
            var tripId = Guid.NewGuid();

            var routes = RouteOf(RealtimeBulkChanges.Bookings(new[]
            {
                new RealtimeBulkChanges.BookingScope(bookingId, tripId, OperatorA, CustomerOne)
            }));

            Assert.Contains(routes[RealtimeGroups.Platform], m => m.Entity == "Bookings" && m.Id == bookingId);
            Assert.Contains(routes[RealtimeGroups.Operator(OperatorA)], m => m.Entity == "Bookings" && m.Id == bookingId);
            Assert.Contains(routes[RealtimeGroups.Customer(CustomerOne)], m => m.Entity == "Bookings" && m.Id == bookingId);

            Assert.False(routes.ContainsKey(RealtimeGroups.Operator(OperatorB)));
            Assert.False(routes.ContainsKey(RealtimeGroups.Customer(CustomerTwo)));
            Assert.False(routes.ContainsKey(RealtimeGroups.Staff));
        }

        [Fact]
        public void Bookings_OfAGuestCheckout_ReachNoCustomerGroup()
        {
            var routes = RouteOf(RealtimeBulkChanges.Bookings(new[]
            {
                new RealtimeBulkChanges.BookingScope(Guid.NewGuid(), Guid.NewGuid(), OperatorA, null)
            }));

            Assert.True(routes.ContainsKey(RealtimeGroups.Operator(OperatorA)));
            Assert.DoesNotContain(routes.Keys, group => group.StartsWith("customer-", StringComparison.Ordinal));
        }

        [Fact]
        public void OperatorWallet_GoesToThePlatformAndThatOperatorOnly()
        {
            var routes = RouteOf(RealtimeBulkChanges.OperatorWallet(OperatorA));

            Assert.Equal(
                new[] { RealtimeGroups.Platform, RealtimeGroups.Operator(OperatorA) }.OrderBy(g => g).ToArray(),
                routes.Keys.OrderBy(g => g).ToArray());
            Assert.All(routes.Values.SelectMany(list => list), m =>
            {
                Assert.Equal("OperatorWallets", m.Entity);
                Assert.Equal(OperatorA, m.OperatorId);
            });
        }

        [Fact]
        public void CustomerProfile_GoesToThePlatformAndThatCustomerOnly()
        {
            var routes = RouteOf(RealtimeBulkChanges.CustomerProfile(CustomerOne));

            Assert.Equal(
                new[] { RealtimeGroups.Platform, RealtimeGroups.Customer(CustomerOne) }.OrderBy(g => g).ToArray(),
                routes.Keys.OrderBy(g => g).ToArray());
            Assert.False(routes.ContainsKey(RealtimeGroups.Customer(CustomerTwo)));

            var forCustomer = Assert.Single(routes[RealtimeGroups.Customer(CustomerOne)]);
            Assert.Equal("CustomerProfiles", forCustomer.Entity);
            Assert.Equal(CustomerOne, forCustomer.Id);
        }

        [Fact]
        public void EveryEntityNameUsed_IsAListedTableInTheCatalog()
        {
            // A typo in an entity name would silently fall back to "platform only" (fail closed) and
            // starve the screen that listens for it — so tie every name to the catalog.
            var names = RealtimeBulkChanges.Seats(new[] { Guid.NewGuid() })
                .Concat(RealtimeBulkChanges.SeatHolds(Guid.NewGuid(), new[] { Guid.NewGuid() }))
                .Concat(RealtimeBulkChanges.Bookings(new[]
                {
                    new RealtimeBulkChanges.BookingScope(Guid.NewGuid(), Guid.NewGuid(), OperatorA, CustomerOne)
                }))
                .Concat(RealtimeBulkChanges.OperatorWallet(OperatorA))
                .Concat(RealtimeBulkChanges.CustomerProfile(CustomerOne))
                .Select(c => c.Entity)
                .Distinct()
                .ToList();

            Assert.Equal(5, names.Count);
            Assert.All(names, name => Assert.Contains(name, RealtimeEntityCatalog.ListedTables));
        }

        [Fact]
        public void ABigExpiryBatch_CollapsesTheHoldRows_ButStillTellsEveryTripsSeatMap()
        {
            // ExpireOverdueHoldsAsync handles up to 200 holds per run. The platform list collapses
            // to one "bulk" message instead of 150, but every affected trip's viewers must still be
            // told (the router builds seat signals from the raw changes, before collapsing).
            var holds = Enumerable.Range(0, 150)
                .Select(_ => (HoldId: Guid.NewGuid(), TripId: Guid.NewGuid()))
                .ToList();

            var routes = RouteOf(RealtimeBulkChanges.SeatHolds(holds));

            var platform = Assert.Single(routes[RealtimeGroups.Platform]);
            Assert.Equal(RealtimeActions.Bulk, platform.Action);
            Assert.Equal("SeatHolds", platform.Entity);

            foreach (var hold in holds)
                Assert.Single(routes[RealtimeGroups.Trip(hold.TripId)]);
        }
    }
}
