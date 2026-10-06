using TicketPortal.Api.Data;
using TicketPortal.Api.Models.Bookings;
using TicketPortal.Api.Models.Enums;
using TicketPortal.Api.Models.Finance;
using Microsoft.EntityFrameworkCore;

namespace TicketPortal.Api.Services
{
    // Chunk 5 (C5-2 / C5-3 / D3): the ONE place that decides which CommissionRule applies to a
    // sale and what amount it produces. Before this, PaymentConfirmationService (online + counter
    // sales) and FinanceReconciliationService (re-posting a missed ledger entry) each carried
    // their own copy of the lookup, and both derived "today" from DateTime.UtcNow. Nothing
    // stopped the copies drifting, and the UTC date is the wrong business date for Dhaka for six
    // hours every day. Everything here is a stateless static so the selection rules can be unit
    // tested without a database (see CommissionRuleResolverTests).
    //
    // DECISION D3 (recorded in docs/03-Remaining-Fix-Plan.md): a FixedAmount rule is a flat
    // amount PER TICKET, not per booking. That is what the model, the enum comment and the demo
    // data always described ("flat 20 BDT per ticket"); the old code applied it once per booking,
    // which under-charged every multi-seat booking. Percentage rules are unchanged: a share of
    // (SubTotal - DiscountAmount).
    //
    // RULE ORDER (deterministic, so two rules can never "randomly" win):
    //   1. only active, non-deleted rules for this operator + sales channel whose inclusive
    //      [EffectiveFrom, EffectiveTo] window contains the Dhaka sale date are candidates;
    //   2. a rule scoped to the trip's own route beats an operator-wide (BusRouteId == null) one;
    //   3. within the same scope the latest EffectiveFrom wins, then the most recently created,
    //      then the lowest Id (a pure tie-breaker so the result never depends on row order).
    // Creating or editing a rule that would overlap another active rule of the same operator,
    // channel and route scope is rejected (FindOverlap), so step 3 only ever matters for legacy
    // rows saved before that validation existed.
    public static class CommissionRuleResolver
    {
        // Pure selection over an in-memory list — the unit-testable core.
        public static CommissionRule? Select(
            IEnumerable<CommissionRule> rules,
            Guid busOperatorId,
            SaleChannel channel,
            Guid? busRouteId,
            DateOnly dhakaDate)
        {
            var candidates = rules
                .Where(r => !r.IsDeleted
                    && r.IsActive
                    && r.BusOperatorId == busOperatorId
                    && r.SaleChannel == channel
                    && r.EffectiveFrom <= dhakaDate
                    && (r.EffectiveTo == null || r.EffectiveTo >= dhakaDate))
                .ToList();

            var routeSpecific = busRouteId.HasValue
                ? candidates.Where(r => r.BusRouteId == busRouteId)
                : Enumerable.Empty<CommissionRule>();

            return PickLatest(routeSpecific) ?? PickLatest(candidates.Where(r => r.BusRouteId == null));
        }

        private static CommissionRule? PickLatest(IEnumerable<CommissionRule> rules) => rules
            .OrderByDescending(r => r.EffectiveFrom)
            .ThenByDescending(r => r.CreatedAtUtc)
            .ThenBy(r => r.Id)
            .FirstOrDefault();

        // Database-backed lookup used by every caller that posts commission. `dhakaDate` is
        // supplied by the caller (derived once from DhakaClock) rather than read here, so one
        // sale uses one date for every lookup it makes.
        public static async Task<CommissionRule> ResolveAsync(
            AppDbContext db, Booking booking, SaleChannel channel, DateOnly dhakaDate)
        {
            var busRouteId = await db.Trips
                .Where(t => t.Id == booking.TripId)
                .Select(t => (Guid?)t.BusRouteId)
                .FirstOrDefaultAsync();

            var rules = await db.CommissionRules
                .Where(r => r.BusOperatorId == booking.BusOperatorId
                    && r.SaleChannel == channel
                    && r.IsActive
                    && r.EffectiveFrom <= dhakaDate
                    && (r.EffectiveTo == null || r.EffectiveTo >= dhakaDate))
                .ToListAsync();

            return Select(rules, booking.BusOperatorId, channel, busRouteId, dhakaDate)
                ?? throw new InvalidOperationException(
                    $"No active {channel} CommissionRule configured for operator {booking.BusOperatorId} " +
                    $"on {dhakaDate:yyyy-MM-dd}. Add one on the Commission Rules screen, then retry.");
        }

        // How many tickets the sale covered — the multiplier for a FixedAmount rule (D3).
        // Counts every non-deleted ticket on the booking regardless of its current status: the
        // commission is posted for the sale as it was made, and a ticket cancelled later is
        // handled by the proportional reversal in FinanceLedgerService, not by shrinking this
        // number retroactively (which would make a late re-post disagree with an on-time one).
        public static Task<int> CountTicketsAsync(AppDbContext db, Guid bookingId) =>
            db.Tickets.CountAsync(t => t.BookingId == bookingId);

        public static decimal Compute(CommissionRule rule, decimal taxableBase, int ticketCount) =>
            rule.CommissionType switch
            {
                CommissionType.Percentage => Math.Round(taxableBase * (rule.CommissionValue / 100m), 2),
                CommissionType.FixedAmount => Math.Round(rule.CommissionValue * Math.Max(0, ticketCount), 2),
                _ => 0m,
            };

        // The amount a booking's commission is calculated on (Percentage rules only).
        public static decimal TaxableBase(Booking booking) =>
            Math.Max(0m, booking.SubTotal - booking.DiscountAmount);

        // ---- Overlap validation (C5-3) ----

        // Inclusive on both ends, open-ended when EffectiveTo is null.
        public static bool WindowsOverlap(
            DateOnly fromA, DateOnly? toA, DateOnly fromB, DateOnly? toB) =>
            (toB is null || fromA <= toB.Value) && (toA is null || fromB <= toA.Value);

        // The existing active rule (if any) that the candidate would collide with: same
        // operator, same sales channel, same route scope (both operator-wide, or both the same
        // route) and intersecting date windows. A route-specific rule next to an operator-wide
        // one is NOT a collision — that pairing is the intended "route overrides default" setup.
        // An inactive candidate can never collide, because it never applies to any sale.
        public static CommissionRule? FindOverlap(
            IEnumerable<CommissionRule> existing, CommissionRule candidate)
        {
            if (!candidate.IsActive)
            {
                return null;
            }

            return existing.FirstOrDefault(other =>
                other.Id != candidate.Id
                && !other.IsDeleted
                && other.IsActive
                && other.BusOperatorId == candidate.BusOperatorId
                && other.SaleChannel == candidate.SaleChannel
                && other.BusRouteId == candidate.BusRouteId
                && WindowsOverlap(candidate.EffectiveFrom, candidate.EffectiveTo, other.EffectiveFrom, other.EffectiveTo));
        }
    }
}
