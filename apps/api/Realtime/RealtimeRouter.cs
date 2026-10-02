namespace TicketPortal.Api.Realtime
{
    // REALTIME_SIGNALR_PLAN.md, Chunk 2 — the "Who receives what" table as code.
    //
    // Pure function of already-resolved changes (no database, no SignalR), so the whole routing
    // matrix is unit-testable. Returns, per SignalR group name, the messages that group gets.
    //
    //   platform        every change (admins and platform staff)
    //   operator-{id}   changes whose row belongs to that operator, for tables the catalog marks Operator
    //   staff           shared reference data owned by no operator (terminals, currencies, ...)
    //   customer-{id}   changes to that customer's OWN rows, for tables the catalog marks Customer
    //   trip-{id}       nothing but the SeatAvailability pseudo-entity for that trip
    public static class RealtimeRouter
    {
        // A single save touching more rows than this in one table is collapsed to one message per
        // (table, operator, customer) instead of one per row.
        public const int CollapseThreshold = 100;

        // Upper bound on SeatAvailability signals from one batch. Nobody can be watching hundreds
        // of freshly created trips, and the cap keeps a bulk trip-generation from fanning out.
        public const int MaxSeatTripsPerBatch = 200;

        public static Dictionary<string, List<RealtimeChange>> Route(IReadOnlyList<CapturedChange> changes)
        {
            var routes = new Dictionary<string, List<RealtimeChange>>(StringComparer.Ordinal);

            void Add(string group, RealtimeChange message)
            {
                if (!routes.TryGetValue(group, out var list))
                {
                    list = new List<RealtimeChange>();
                    routes[group] = list;
                }

                list.Add(message);
            }

            if (changes.Count == 0)
                return routes;

            // Seat signals come from the raw changes, BEFORE collapsing, so a bulk insert of
            // thousands of seats still tells each affected trip's viewers.
            var at = changes.Max(c => c.AtUtc);
            var seatTrips = changes
                .Where(c => c.TripId is not null && RealtimeEntityCatalog.SeatAffecting.Contains(c.Entity))
                .Select(c => c.TripId!.Value)
                .Distinct()
                .Take(MaxSeatTripsPerBatch);

            foreach (var tripId in seatTrips)
            {
                Add(
                    RealtimeGroups.Trip(tripId),
                    new RealtimeChange(RealtimeEvents.SeatAvailability, RealtimeActions.Updated, null, tripId, null, at));
            }

            foreach (var change in Collapse(changes))
            {
                var message = new RealtimeChange(
                    change.Entity, change.Action, change.Id, change.TripId, change.OperatorId, change.AtUtc);

                Add(RealtimeGroups.Platform, message);

                // The catalog decides who besides the platform may hear about this table; a table
                // it does not list is platform-only (fail closed).
                var audience = RealtimeEntityCatalog.AudienceOf(change.Entity);

                if (audience.HasFlag(RealtimeAudience.Operator) && change.OperatorId is { } operatorId)
                    Add(RealtimeGroups.Operator(operatorId), message);
                else if (audience.HasFlag(RealtimeAudience.Shared)
                    && change.OperatorId is null
                    && !change.ScopeUncertain)
                {
                    Add(RealtimeGroups.Staff, message);
                }

                if (audience.HasFlag(RealtimeAudience.Customer) && change.CustomerProfileId is { } customerProfileId)
                    Add(RealtimeGroups.Customer(customerProfileId), message);
            }

            return routes;
        }

        // Tables with more than CollapseThreshold changed rows become one "bulk" entry per
        // (operator, customer) pair. Small tables pass through untouched.
        public static IReadOnlyList<CapturedChange> Collapse(IReadOnlyList<CapturedChange> changes)
        {
            var bulkTables = changes
                .GroupBy(c => c.Entity)
                .Where(g => g.Count() > CollapseThreshold)
                .Select(g => g.Key)
                .ToHashSet(StringComparer.Ordinal);

            if (bulkTables.Count == 0)
                return changes;

            var result = new List<CapturedChange>(changes.Count);
            result.AddRange(changes.Where(c => !bulkTables.Contains(c.Entity)));

            var groups = changes
                .Where(c => bulkTables.Contains(c.Entity))
                .GroupBy(c => (c.Entity, c.OperatorId, c.CustomerProfileId, c.ScopeUncertain));

            foreach (var group in groups)
            {
                result.Add(new CapturedChange
                {
                    Entity = group.Key.Entity,
                    Action = RealtimeActions.Bulk,
                    Id = null,
                    OperatorId = group.Key.OperatorId,
                    CustomerProfileId = group.Key.CustomerProfileId,
                    ScopeUncertain = group.Key.ScopeUncertain,
                    AtUtc = group.Max(c => c.AtUtc)
                });
            }

            return result;
        }
    }
}
