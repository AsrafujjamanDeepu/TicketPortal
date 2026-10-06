using Microsoft.EntityFrameworkCore;
using TicketPortal.Api.Data;

namespace TicketPortal.Api.Realtime
{
    // docs/02-Project-Concept-and-Solution.md (Real-time updates), Chunk 2 — after a commit, works out which operator / trip /
    // customer each changed row belongs to, so the router can pick the right SignalR groups.
    //
    // Many rows carry their scope directly (Booking, Trip, Bus, ...). The rest point at a parent
    // (a Payment at its Booking, a Ticket at its Booking, a PaymentHistory at its Payment) and are
    // resolved through that parent. To keep this cheap:
    //   * parents that were themselves part of the same save are read from the batch, for free —
    //     a typical "hold seats" or "confirm payment" save needs no query at all;
    //   * everything else is fetched with ONE batched query per parent table (ids are chunked so
    //     a huge save can never exceed SQL Server's parameter limit);
    //   * only parents that can still change the answer are fetched (a row that already has an
    //     operator does not go looking for one).
    // Lookups ignore the soft-delete filter, so a soft-deleted parent still resolves.
    public static class RealtimeScopeResolver
    {
        private const int LookupChunkSize = 500;

        // What we know about one parent row. Only the fields that parent can supply are filled.
        // Missing = the parent row could not be found (deleted since, or never existed).
        private readonly record struct ParentInfo(
            Guid? OperatorId,
            Guid? TripId,
            Guid? CustomerProfileId,
            Guid? BookingId,
            bool Missing = false);

        // Entity (table) name -> the kind of parent it can be. Lets rows in the same batch answer
        // for each other without a database round-trip.
        private static readonly Dictionary<string, RealtimeRef> KindByTable = new(StringComparer.Ordinal)
        {
            ["Bookings"] = RealtimeRef.Booking,
            ["Payments"] = RealtimeRef.Payment,
            ["Trips"] = RealtimeRef.Trip,
            ["Buses"] = RealtimeRef.Bus,
            ["OperatorRoutes"] = RealtimeRef.OperatorRoute,
            ["StaffProfiles"] = RealtimeRef.StaffProfile,
            ["OperatorInvoices"] = RealtimeRef.OperatorInvoice,
            ["OperatorSettlements"] = RealtimeRef.OperatorSettlement,
            ["SalesCounters"] = RealtimeRef.SalesCounter,
            ["CancellationPolicies"] = RealtimeRef.CancellationPolicy,
            ["SeatHolds"] = RealtimeRef.SeatHold,
            ["TripSeats"] = RealtimeRef.TripSeat
        };

        // Only work out what the catalog says this table may be routed by: a platform-only table
        // never needs an operator or customer, so it never triggers a lookup.
        private static bool NeedsOperator(CapturedChange change) =>
            change.OperatorId is null && RealtimeEntityCatalog.AllowsOperator(change.Entity);

        private static bool NeedsTrip(CapturedChange change) =>
            change.TripId is null && RealtimeEntityCatalog.SeatAffecting.Contains(change.Entity);

        private static bool NeedsCustomer(CapturedChange change) =>
            change.CustomerProfileId is null && RealtimeEntityCatalog.AllowsCustomer(change.Entity);

        // Would looking up this parent kind change the answer for this row?
        private static bool Wants(CapturedChange change, RealtimeRef kind) => kind switch
        {
            RealtimeRef.Booking => NeedsOperator(change) || NeedsTrip(change) || NeedsCustomer(change),
            RealtimeRef.SeatHold => NeedsTrip(change),
            // A hold item knows both its hold and its seat; the hold alone is enough to find the trip.
            RealtimeRef.TripSeat => NeedsTrip(change)
                && !(change.Refs is not null && change.Refs.ContainsKey(RealtimeRef.SeatHold)),
            RealtimeRef.Payment => false, // Payments are only a stepping stone to a Booking (step A).
            _ => NeedsOperator(change)    // Trip, Bus, OperatorRoute, StaffProfile, ... supply an operator.
        };

        // Fills OperatorId / TripId / CustomerProfileId on every change it can. Never throws for a
        // missing parent (that row simply keeps a null scope and is routed to the platform group
        // only); a database failure propagates so the caller can decide how to degrade.
        public static async Task ResolveAsync(
            AppDbContext db,
            IReadOnlyList<CapturedChange> changes,
            CancellationToken cancellationToken = default)
        {
            if (changes.Count == 0)
                return;

            var known = SeedKnown(changes);

            await ResolvePaymentsAsync(db, changes, known, cancellationToken);
            await FetchMissingParentsAsync(db, changes, known, cancellationToken);
            Apply(changes, known);
            await ResolveOperatorViaTripAsync(db, changes, known, cancellationToken);
        }

        // Step C: a row that ended up with a trip but still no operator (a SeatHoldItem knows its
        // hold's trip but not its operator) asks the trip who owns it. Rare — most rows either
        // carry their operator or got it from their Booking in step B.
        private static async Task ResolveOperatorViaTripAsync(
            AppDbContext db,
            IReadOnlyList<CapturedChange> changes,
            Dictionary<(RealtimeRef Kind, Guid Id), ParentInfo> known,
            CancellationToken cancellationToken)
        {
            var stillUnscoped = changes
                .Where(c => NeedsOperator(c)
                    && c.TripId is not null
                    && !(c.Refs is not null && c.Refs.ContainsKey(RealtimeRef.Trip)))
                .ToList();

            if (stillUnscoped.Count == 0)
                return;

            foreach (var change in stillUnscoped)
            {
                change.Refs ??= new Dictionary<RealtimeRef, Guid>();
                change.Refs[RealtimeRef.Trip] = change.TripId!.Value;
            }

            var missing = stillUnscoped
                .Select(c => c.TripId!.Value)
                .Distinct()
                .Where(id => !known.ContainsKey((RealtimeRef.Trip, id)))
                .ToList();

            await FetchIntoAsync(db, RealtimeRef.Trip, missing, known, cancellationToken);
            Apply(stillUnscoped, known);
        }

        // Rows in this very batch are the first source of truth.
        private static Dictionary<(RealtimeRef Kind, Guid Id), ParentInfo> SeedKnown(IReadOnlyList<CapturedChange> changes)
        {
            var known = new Dictionary<(RealtimeRef Kind, Guid Id), ParentInfo>();

            foreach (var change in changes)
            {
                if (change.Id is { } id && KindByTable.TryGetValue(change.Entity, out var kind))
                {
                    Guid? bookingId = null;
                    if (change.Refs is not null && change.Refs.TryGetValue(RealtimeRef.Booking, out var booking))
                        bookingId = booking;

                    known[(kind, id)] = new ParentInfo(change.OperatorId, change.TripId, change.CustomerProfileId, bookingId);
                }

                // Any row that carries both a trip and an operator tells us who owns that trip.
                if (change.TripId is { } tripId && change.OperatorId is { } operatorId)
                    known[(RealtimeRef.Trip, tripId)] = new ParentInfo(operatorId, tripId, null, null);
            }

            return known;
        }

        // Step A: a row that only knows its Payment (PaymentHistories, PaymentWebhookEvents, ...)
        // gets the Payment's BookingId, which step B then resolves like any other Booking ref.
        private static async Task ResolvePaymentsAsync(
            AppDbContext db,
            IReadOnlyList<CapturedChange> changes,
            Dictionary<(RealtimeRef Kind, Guid Id), ParentInfo> known,
            CancellationToken cancellationToken)
        {
            var viaPayment = changes
                .Where(c => c.Refs is not null
                    && c.Refs.ContainsKey(RealtimeRef.Payment)
                    && !c.Refs.ContainsKey(RealtimeRef.Booking)
                    && (NeedsOperator(c) || NeedsTrip(c) || NeedsCustomer(c)))
                .ToList();

            if (viaPayment.Count == 0)
                return;

            var missing = viaPayment
                .Select(c => c.Refs![RealtimeRef.Payment])
                .Distinct()
                .Where(id => !known.ContainsKey((RealtimeRef.Payment, id)))
                .ToList();

            await FetchIntoAsync(db, RealtimeRef.Payment, missing, known, cancellationToken);

            foreach (var change in viaPayment)
            {
                var paymentId = change.Refs![RealtimeRef.Payment];
                if (known.TryGetValue((RealtimeRef.Payment, paymentId), out var payment) && payment.BookingId is { } bookingId)
                    change.Refs[RealtimeRef.Booking] = bookingId;
            }
        }

        // Step B: one batched query per parent table still needed.
        private static async Task FetchMissingParentsAsync(
            AppDbContext db,
            IReadOnlyList<CapturedChange> changes,
            Dictionary<(RealtimeRef Kind, Guid Id), ParentInfo> known,
            CancellationToken cancellationToken)
        {
            var wanted = new Dictionary<RealtimeRef, HashSet<Guid>>();

            foreach (var change in changes)
            {
                if (change.Refs is null)
                    continue;

                foreach (var (kind, id) in change.Refs)
                {
                    if (!Wants(change, kind) || known.ContainsKey((kind, id)))
                        continue;

                    if (!wanted.TryGetValue(kind, out var ids))
                    {
                        ids = new HashSet<Guid>();
                        wanted[kind] = ids;
                    }

                    ids.Add(id);
                }
            }

            foreach (var (kind, ids) in wanted)
                await FetchIntoAsync(db, kind, ids.ToList(), known, cancellationToken);
        }

        private static void Apply(
            IReadOnlyList<CapturedChange> changes,
            Dictionary<(RealtimeRef Kind, Guid Id), ParentInfo> known)
        {
            foreach (var change in changes)
            {
                if (change.Refs is null)
                    continue;

                foreach (var (kind, id) in change.Refs)
                {
                    if (!known.TryGetValue((kind, id), out var info))
                        continue;

                    if (info.Missing)
                    {
                        // The parent is gone: nothing to inherit, and we must not guess.
                        change.ScopeUncertain = true;
                        continue;
                    }

                    switch (kind)
                    {
                        case RealtimeRef.Booking:
                            if (NeedsOperator(change)) change.OperatorId = info.OperatorId;
                            if (NeedsTrip(change)) change.TripId = info.TripId;
                            if (NeedsCustomer(change)) change.CustomerProfileId = info.CustomerProfileId;
                            break;

                        case RealtimeRef.SeatHold:
                        case RealtimeRef.TripSeat:
                            if (NeedsTrip(change)) change.TripId = info.TripId;
                            break;

                        case RealtimeRef.Payment:
                            break; // Already turned into a Booking ref by step A.

                        default:
                            if (NeedsOperator(change)) change.OperatorId = info.OperatorId;
                            break;
                    }
                }
            }
        }

        // Looks the ids up (skipping ones already known) and records every id — a parent that no
        // longer exists is remembered as "empty" so it is never asked for twice.
        private static async Task FetchIntoAsync(
            AppDbContext db,
            RealtimeRef kind,
            List<Guid> ids,
            Dictionary<(RealtimeRef Kind, Guid Id), ParentInfo> known,
            CancellationToken cancellationToken)
        {
            if (ids.Count == 0)
                return;

            var found = await FetchAsync(db, kind, ids, cancellationToken);
            foreach (var id in ids)
            {
                known[(kind, id)] = found.TryGetValue(id, out var info)
                    ? info
                    : new ParentInfo(null, null, null, null, Missing: true);
            }
        }

        private static async Task<Dictionary<Guid, ParentInfo>> FetchAsync(
            AppDbContext db,
            RealtimeRef kind,
            List<Guid> ids,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<Guid, ParentInfo>();

            foreach (var slice in ids.Chunk(LookupChunkSize))
            {
                // A List<Guid> (not the raw array) so EF sees the plain List.Contains translation.
                var chunk = slice.ToList();

                switch (kind)
                {
                    case RealtimeRef.Booking:
                    {
                        var rows = await db.Bookings.IgnoreQueryFilters().AsNoTracking()
                            .Where(x => chunk.Contains(x.Id))
                            .Select(x => new { x.Id, x.BusOperatorId, x.TripId, x.CustomerProfileId })
                            .ToListAsync(cancellationToken);
                        foreach (var row in rows)
                            result[row.Id] = new ParentInfo(row.BusOperatorId, row.TripId, row.CustomerProfileId, null);
                        break;
                    }

                    case RealtimeRef.Payment:
                    {
                        var rows = await db.Payments.IgnoreQueryFilters().AsNoTracking()
                            .Where(x => chunk.Contains(x.Id))
                            .Select(x => new { x.Id, x.BookingId })
                            .ToListAsync(cancellationToken);
                        foreach (var row in rows)
                            result[row.Id] = new ParentInfo(null, null, null, row.BookingId);
                        break;
                    }

                    case RealtimeRef.SeatHold:
                    {
                        var rows = await db.SeatHolds.IgnoreQueryFilters().AsNoTracking()
                            .Where(x => chunk.Contains(x.Id))
                            .Select(x => new { x.Id, x.TripId })
                            .ToListAsync(cancellationToken);
                        foreach (var row in rows)
                            result[row.Id] = new ParentInfo(null, row.TripId, null, null);
                        break;
                    }

                    case RealtimeRef.TripSeat:
                    {
                        var rows = await db.TripSeats.IgnoreQueryFilters().AsNoTracking()
                            .Where(x => chunk.Contains(x.Id))
                            .Select(x => new { x.Id, x.TripId })
                            .ToListAsync(cancellationToken);
                        foreach (var row in rows)
                            result[row.Id] = new ParentInfo(null, row.TripId, null, null);
                        break;
                    }

                    case RealtimeRef.Trip:
                        AddOperators(result, await db.Trips.IgnoreQueryFilters().AsNoTracking()
                            .Where(x => chunk.Contains(x.Id))
                            .Select(x => new { x.Id, Op = (Guid?)x.BusOperatorId })
                            .ToListAsync(cancellationToken),
                            x => (x.Id, x.Op));
                        break;

                    case RealtimeRef.Bus:
                        AddOperators(result, await db.Buses.IgnoreQueryFilters().AsNoTracking()
                            .Where(x => chunk.Contains(x.Id))
                            .Select(x => new { x.Id, Op = (Guid?)x.BusOperatorId })
                            .ToListAsync(cancellationToken),
                            x => (x.Id, x.Op));
                        break;

                    case RealtimeRef.OperatorRoute:
                        AddOperators(result, await db.OperatorRoutes.IgnoreQueryFilters().AsNoTracking()
                            .Where(x => chunk.Contains(x.Id))
                            .Select(x => new { x.Id, Op = (Guid?)x.BusOperatorId })
                            .ToListAsync(cancellationToken),
                            x => (x.Id, x.Op));
                        break;

                    case RealtimeRef.StaffProfile:
                        AddOperators(result, await db.StaffProfiles.IgnoreQueryFilters().AsNoTracking()
                            .Where(x => chunk.Contains(x.Id))
                            .Select(x => new { x.Id, Op = (Guid?)x.BusOperatorId })
                            .ToListAsync(cancellationToken),
                            x => (x.Id, x.Op));
                        break;

                    case RealtimeRef.OperatorInvoice:
                        AddOperators(result, await db.OperatorInvoices.IgnoreQueryFilters().AsNoTracking()
                            .Where(x => chunk.Contains(x.Id))
                            .Select(x => new { x.Id, Op = (Guid?)x.BusOperatorId })
                            .ToListAsync(cancellationToken),
                            x => (x.Id, x.Op));
                        break;

                    case RealtimeRef.OperatorSettlement:
                        AddOperators(result, await db.OperatorSettlements.IgnoreQueryFilters().AsNoTracking()
                            .Where(x => chunk.Contains(x.Id))
                            .Select(x => new { x.Id, Op = (Guid?)x.BusOperatorId })
                            .ToListAsync(cancellationToken),
                            x => (x.Id, x.Op));
                        break;

                    case RealtimeRef.SalesCounter:
                        AddOperators(result, await db.SalesCounters.IgnoreQueryFilters().AsNoTracking()
                            .Where(x => chunk.Contains(x.Id))
                            .Select(x => new { x.Id, Op = (Guid?)x.BusOperatorId })
                            .ToListAsync(cancellationToken),
                            x => (x.Id, x.Op));
                        break;

                    case RealtimeRef.CancellationPolicy:
                        AddOperators(result, await db.CancellationPolicies.IgnoreQueryFilters().AsNoTracking()
                            .Where(x => chunk.Contains(x.Id))
                            .Select(x => new { x.Id, Op = (Guid?)x.BusOperatorId })
                            .ToListAsync(cancellationToken),
                            x => (x.Id, x.Op));
                        break;
                }
            }

            return result;
        }

        // Shared tail of the "this parent only tells us its operator" lookups above.
        private static void AddOperators<TRow>(
            Dictionary<Guid, ParentInfo> result,
            List<TRow> rows,
            Func<TRow, (Guid Id, Guid? Operator)> selector)
        {
            foreach (var row in rows)
            {
                var (id, op) = selector(row);
                result[id] = new ParentInfo(op, null, null, null);
            }
        }
    }
}
