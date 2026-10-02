using TicketPortal.Api.Realtime;
using Xunit;

namespace TicketPortal.Api.Tests.Unit
{
    // REALTIME_SIGNALR_PLAN.md, Chunk 2 — the "Who receives what" table, tested as a pure
    // function (no database, no SignalR). Chunk 7 adds the connection-level version of the same
    // matrix; these tests pin the routing rules themselves.
    public class RealtimeRouterTests
    {
        private static readonly Guid OperatorA = Guid.NewGuid();
        private static readonly Guid OperatorB = Guid.NewGuid();
        private static readonly Guid CustomerOne = Guid.NewGuid();
        private static readonly Guid CustomerTwo = Guid.NewGuid();

        private static CapturedChange Change(
            string entity,
            Guid? operatorId = null,
            Guid? tripId = null,
            Guid? customerProfileId = null,
            string action = RealtimeActions.Updated) =>
            new()
            {
                Entity = entity,
                Action = action,
                Id = Guid.NewGuid(),
                OperatorId = operatorId,
                TripId = tripId,
                CustomerProfileId = customerProfileId
            };

        private static bool Received(Dictionary<string, List<RealtimeChange>> routes, string group, string entity) =>
            routes.TryGetValue(group, out var list) && list.Any(m => m.Entity == entity);

        [Fact]
        public void EveryChange_GoesToThePlatformGroup()
        {
            var routes = RealtimeRouter.Route(new[]
            {
                Change("Bookings", operatorId: OperatorA, customerProfileId: CustomerOne),
                Change("ActivityLogs"),
                Change("Terminals")
            });

            Assert.Equal(3, routes[RealtimeGroups.Platform].Count);
        }

        [Fact]
        public void OperatorScopedRow_GoesToThatOperatorOnly()
        {
            var routes = RealtimeRouter.Route(new[] { Change("Trips", operatorId: OperatorA) });

            Assert.True(Received(routes, RealtimeGroups.Operator(OperatorA), "Trips"));
            Assert.False(routes.ContainsKey(RealtimeGroups.Operator(OperatorB)));
        }

        [Fact]
        public void UnscopedRow_ThatIsNotSharedReference_GoesToPlatformOnly()
        {
            var routes = RealtimeRouter.Route(new[] { Change("ActivityLogs") });

            Assert.Equal(new[] { RealtimeGroups.Platform }, routes.Keys.ToArray());
        }

        [Fact]
        public void SharedReferenceData_WithNoOperator_GoesToTheStaffGroup()
        {
            var routes = RealtimeRouter.Route(new[] { Change("Terminals") });

            Assert.True(Received(routes, RealtimeGroups.Staff, "Terminals"));
        }

        [Fact]
        public void SharedReferenceData_ScopedToAnOperator_GoesToThatOperatorNotTheStaffGroup()
        {
            // A fare rule that belongs to Operator A is Operator A's business, not every
            // operator's.
            var routes = RealtimeRouter.Route(new[] { Change("FareRules", operatorId: OperatorA) });

            Assert.True(Received(routes, RealtimeGroups.Operator(OperatorA), "FareRules"));
            Assert.False(routes.ContainsKey(RealtimeGroups.Staff));
        }

        [Fact]
        public void OperatorAndSharedTable_WithNoOperator_GoesToTheStaffGroup()
        {
            // A global cancellation-policy rule (policy owned by nobody) is for every operator.
            var routes = RealtimeRouter.Route(new[] { Change("CancellationPolicyRules") });

            Assert.True(Received(routes, RealtimeGroups.Staff, "CancellationPolicyRules"));
        }

        [Fact]
        public void CustomerVisibleRow_GoesToThatCustomerOnly()
        {
            var routes = RealtimeRouter.Route(new[]
            {
                Change("Bookings", operatorId: OperatorA, customerProfileId: CustomerOne)
            });

            Assert.True(Received(routes, RealtimeGroups.Customer(CustomerOne), "Bookings"));
            Assert.False(routes.ContainsKey(RealtimeGroups.Customer(CustomerTwo)));
        }

        [Fact]
        public void PlatformOnlyTable_NeverReachesAnOperatorOrCustomer_EvenWithFullScope()
        {
            // A platform-ledger row links to an operator, a booking and the customer, but the
            // catalog says finance-internal tables are platform-only.
            var routes = RealtimeRouter.Route(new[]
            {
                Change("PlatformLedgers", operatorId: OperatorA, customerProfileId: CustomerOne)
            });

            Assert.Equal(new[] { RealtimeGroups.Platform }, routes.Keys.ToArray());
        }

        [Fact]
        public void UnknownTable_FailsClosed_ToThePlatformGroupOnly()
        {
            var routes = RealtimeRouter.Route(new[]
            {
                Change("SomeFutureTable", operatorId: OperatorA, customerProfileId: CustomerOne)
            });

            Assert.Equal(new[] { RealtimeGroups.Platform }, routes.Keys.ToArray());
        }

        [Fact]
        public void SharedReferenceChange_WhoseOwnerCouldNotBeResolved_DoesNotReachTheStaffGroup()
        {
            // "No operator found" must not be mistaken for "belongs to no operator".
            var uncertain = Change("CancellationPolicyRules");
            uncertain.ScopeUncertain = true;

            var routes = RealtimeRouter.Route(new[] { uncertain });

            Assert.Equal(new[] { RealtimeGroups.Platform }, routes.Keys.ToArray());
        }

        [Fact]
        public void CustomerOwnedRow_IsRoutedToTheCustomerAndNotTheOperator_WhenTheTableIsCustomerOnly()
        {
            var routes = RealtimeRouter.Route(new[]
            {
                Change("CustomerWalletTransactions", operatorId: OperatorA, customerProfileId: CustomerOne)
            });

            Assert.True(Received(routes, RealtimeGroups.Customer(CustomerOne), "CustomerWalletTransactions"));
            Assert.False(routes.ContainsKey(RealtimeGroups.Operator(OperatorA)));
        }

        [Fact]
        public void GuestBooking_WithNoCustomerProfile_ReachesNoCustomerGroup()
        {
            var routes = RealtimeRouter.Route(new[] { Change("Bookings", operatorId: OperatorA) });

            Assert.DoesNotContain(routes.Keys, key => key.StartsWith("customer-", StringComparison.Ordinal));
        }

        [Fact]
        public void SeatAffectingRow_ProducesAnAnonymousSafeSignalForItsTripGroup()
        {
            var tripId = Guid.NewGuid();

            var routes = RealtimeRouter.Route(new[] { Change("TripSeats", operatorId: OperatorA, tripId: tripId) });

            var message = Assert.Single(routes[RealtimeGroups.Trip(tripId)]);
            Assert.Equal(RealtimeEvents.SeatAvailability, message.Entity);
            Assert.Equal(tripId, message.TripId);
            Assert.Null(message.Id);          // No row id...
            Assert.Null(message.OperatorId);  // ...and no operator: nothing an anonymous viewer can learn from.
        }

        [Fact]
        public void TripGroups_NeverReceiveRealTableNames()
        {
            var tripId = Guid.NewGuid();

            var routes = RealtimeRouter.Route(new[]
            {
                Change("Bookings", operatorId: OperatorA, tripId: tripId, customerProfileId: CustomerOne),
                Change("Tickets", operatorId: OperatorA, tripId: tripId, customerProfileId: CustomerOne),
                Change("TripSeats", operatorId: OperatorA, tripId: tripId)
            });

            Assert.All(routes[RealtimeGroups.Trip(tripId)], m => Assert.Equal(RealtimeEvents.SeatAvailability, m.Entity));
        }

        [Fact]
        public void RowThatDoesNotAffectSeats_ProducesNoTripSignal_EvenWithATripId()
        {
            var tripId = Guid.NewGuid();

            var routes = RealtimeRouter.Route(new[] { Change("Reviews", tripId: tripId, customerProfileId: CustomerOne) });

            Assert.False(routes.ContainsKey(RealtimeGroups.Trip(tripId)));
        }

        [Fact]
        public void SeveralSeatChangesOnOneTrip_ProduceOneSignal()
        {
            var tripId = Guid.NewGuid();

            var routes = RealtimeRouter.Route(new[]
            {
                Change("TripSeats", tripId: tripId),
                Change("TripSeats", tripId: tripId),
                Change("SeatHolds", tripId: tripId)
            });

            Assert.Single(routes[RealtimeGroups.Trip(tripId)]);
        }

        [Fact]
        public void HugeBatch_IsCollapsedToOneBulkMessagePerOperator()
        {
            var changes = new List<CapturedChange>();
            for (var i = 0; i < RealtimeRouter.CollapseThreshold + 20; i++)
                changes.Add(Change("Bookings", operatorId: OperatorA));
            for (var i = 0; i < RealtimeRouter.CollapseThreshold + 20; i++)
                changes.Add(Change("Bookings", operatorId: OperatorB));

            var routes = RealtimeRouter.Route(changes);

            Assert.Equal(2, routes[RealtimeGroups.Platform].Count);
            Assert.All(routes[RealtimeGroups.Platform], m =>
            {
                Assert.Equal(RealtimeActions.Bulk, m.Action);
                Assert.Null(m.Id);
            });
            Assert.Single(routes[RealtimeGroups.Operator(OperatorA)]);
            Assert.Single(routes[RealtimeGroups.Operator(OperatorB)]);
        }

        [Fact]
        public void ABatchAtTheThreshold_IsNotCollapsed()
        {
            var changes = Enumerable.Range(0, RealtimeRouter.CollapseThreshold)
                .Select(_ => Change("Bookings", operatorId: OperatorA))
                .ToList();

            var routes = RealtimeRouter.Route(changes);

            Assert.Equal(RealtimeRouter.CollapseThreshold, routes[RealtimeGroups.Platform].Count);
        }

        [Fact]
        public void CollapsingABulkInsert_StillTellsEveryAffectedTripsSeatMap()
        {
            var tripIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            var changes = new List<CapturedChange>();
            foreach (var tripId in tripIds)
                for (var i = 0; i < RealtimeRouter.CollapseThreshold; i++)
                    changes.Add(Change("TripSeats", operatorId: OperatorA, tripId: tripId));

            var routes = RealtimeRouter.Route(changes);

            foreach (var tripId in tripIds)
                Assert.Single(routes[RealtimeGroups.Trip(tripId)]);

            // ...while the platform group got the collapsed table entry, not 300 rows.
            Assert.Single(routes[RealtimeGroups.Platform]);
        }

        [Fact]
        public void EmptyBatch_RoutesNothing()
        {
            Assert.Empty(RealtimeRouter.Route(Array.Empty<CapturedChange>()));
        }
    }
}
