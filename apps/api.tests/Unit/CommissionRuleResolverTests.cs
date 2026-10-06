using TicketPortal.Api.Models.Enums;
using TicketPortal.Api.Models.Finance;
using TicketPortal.Api.Services;
using Xunit;

namespace TicketPortal.Api.Tests.Unit
{
    // Chunk 5 (C5-2 / C5-3 / D3): the commission rule order, the Dhaka business date, the
    // per-ticket fixed amount and the overlap rule — all pure functions, so these run without a
    // database and pin the finance examples in docs/01-Run-and-Manual-Test-Guide.md (section 10.12).
    public class CommissionRuleResolverTests
    {
        private static readonly Guid OperatorA = Guid.Parse("11111111-1111-1111-1111-111111111111");
        private static readonly Guid OperatorB = Guid.Parse("22222222-2222-2222-2222-222222222222");
        private static readonly Guid RouteX = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        private static readonly Guid RouteY = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        private static CommissionRule Rule(
            DateOnly from, DateOnly? to = null, Guid? route = null,
            SaleChannel channel = SaleChannel.Online, Guid? op = null,
            CommissionType type = CommissionType.Percentage, decimal value = 10m,
            bool active = true, DateTime? createdAtUtc = null) => new()
        {
            BusOperatorId = op ?? OperatorA,
            BusRouteId = route,
            SaleChannel = channel,
            CommissionType = type,
            CommissionValue = value,
            EffectiveFrom = from,
            EffectiveTo = to,
            IsActive = active,
            CreatedAtUtc = createdAtUtc ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        private static DateOnly D(int year, int month, int day) => new(year, month, day);

        // ---------- C5-2: Dhaka business dates ----------

        [Fact]
        public void DhakaDate_RollsToTheNextDay_SixHoursBeforeUtcMidnight()
        {
            // 17:59:59 UTC on 30 Sep is 23:59:59 in Dhaka — still 30 Sep.
            Assert.Equal(D(2026, 9, 30), DhakaClock.DateOf(new DateTime(2026, 9, 30, 17, 59, 59, DateTimeKind.Utc)));
            // 18:00:00 UTC on 30 Sep is 00:00:00 on 1 Oct in Dhaka.
            Assert.Equal(D(2026, 10, 1), DhakaClock.DateOf(new DateTime(2026, 9, 30, 18, 0, 0, DateTimeKind.Utc)));
            // 20:00 UTC on 30 Sep is 02:00 on 1 Oct in Dhaka.
            Assert.Equal(D(2026, 10, 1), DhakaClock.DateOf(new DateTime(2026, 9, 30, 20, 0, 0, DateTimeKind.Utc)));
        }

        [Fact]
        public void ASaleNearUtcMidnight_UsesTheRuleThatStartsOnTheDhakaDate()
        {
            var septemberRule = Rule(D(2026, 1, 1), to: D(2026, 9, 30), value: 10m);
            var octoberRule = Rule(D(2026, 10, 1), value: 12m);
            var rules = new[] { septemberRule, octoberRule };

            // 20:00 UTC on 30 Sep: the UTC date is still 30 Sep (old, wrong behaviour would pick
            // the 10% rule), but the Dhaka date is 1 Oct, so the 12% rule must win.
            var saleInstant = new DateTime(2026, 9, 30, 20, 0, 0, DateTimeKind.Utc);
            var utcDate = DateOnly.FromDateTime(saleInstant);
            var dhakaDate = DhakaClock.DateOf(saleInstant);

            Assert.Same(septemberRule, CommissionRuleResolver.Select(rules, OperatorA, SaleChannel.Online, null, utcDate));
            Assert.Same(octoberRule, CommissionRuleResolver.Select(rules, OperatorA, SaleChannel.Online, null, dhakaDate));
        }

        [Fact]
        public void EffectivePeriod_IsInclusiveOnTheFirstAndLastDay()
        {
            var rule = Rule(D(2026, 10, 1), to: D(2026, 10, 31));
            var rules = new[] { rule };

            Assert.Null(CommissionRuleResolver.Select(rules, OperatorA, SaleChannel.Online, null, D(2026, 9, 30)));
            Assert.Same(rule, CommissionRuleResolver.Select(rules, OperatorA, SaleChannel.Online, null, D(2026, 10, 1)));
            Assert.Same(rule, CommissionRuleResolver.Select(rules, OperatorA, SaleChannel.Online, null, D(2026, 10, 31)));
            Assert.Null(CommissionRuleResolver.Select(rules, OperatorA, SaleChannel.Online, null, D(2026, 11, 1)));
        }

        // ---------- C5-3: deterministic rule order ----------

        [Fact]
        public void ARouteSpecificRule_BeatsTheOperatorWideRule_EvenWhenTheWideRuleIsNewer()
        {
            var routeRule = Rule(D(2026, 1, 1), route: RouteX, value: 8m);
            var wideRule = Rule(D(2026, 6, 1), route: null, value: 10m);
            var rules = new[] { wideRule, routeRule };

            Assert.Same(routeRule, CommissionRuleResolver.Select(rules, OperatorA, SaleChannel.Online, RouteX, D(2026, 10, 5)));
            // A trip on a different route falls back to the operator-wide rule.
            Assert.Same(wideRule, CommissionRuleResolver.Select(rules, OperatorA, SaleChannel.Online, RouteY, D(2026, 10, 5)));
        }

        [Fact]
        public void WhenLegacyRulesOverlap_TheLatestEffectiveFromWins_ThenTheNewestCreated_RegardlessOfListOrder()
        {
            var older = Rule(D(2026, 1, 1), value: 10m);
            var newer = Rule(D(2026, 3, 1), value: 11m);
            var sameStartLaterCreated = Rule(D(2026, 3, 1), value: 12m,
                createdAtUtc: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

            var forward = new[] { older, newer, sameStartLaterCreated };
            var reversed = new[] { sameStartLaterCreated, newer, older };

            Assert.Same(sameStartLaterCreated, CommissionRuleResolver.Select(forward, OperatorA, SaleChannel.Online, null, D(2026, 10, 5)));
            Assert.Same(sameStartLaterCreated, CommissionRuleResolver.Select(reversed, OperatorA, SaleChannel.Online, null, D(2026, 10, 5)));
        }

        [Fact]
        public void Select_IgnoresInactiveDeletedOtherOperatorAndOtherChannelRules()
        {
            var inactive = Rule(D(2026, 1, 1), active: false);
            var deleted = Rule(D(2026, 1, 1));
            deleted.IsDeleted = true;
            var otherOperator = Rule(D(2026, 1, 1), op: OperatorB);
            var otherChannel = Rule(D(2026, 1, 1), channel: SaleChannel.Counter);
            var rules = new[] { inactive, deleted, otherOperator, otherChannel };

            Assert.Null(CommissionRuleResolver.Select(rules, OperatorA, SaleChannel.Online, null, D(2026, 10, 5)));
        }

        // ---------- D3: percentage vs fixed-per-ticket amounts ----------

        [Fact]
        public void PercentageCommission_IsAShareOfTheDiscountedSubtotal_RoundedToTwoDecimals()
        {
            var rule = Rule(D(2026, 1, 1), type: CommissionType.Percentage, value: 10m);

            Assert.Equal(100m, CommissionRuleResolver.Compute(rule, 1000m, ticketCount: 1));
            Assert.Equal(100m, CommissionRuleResolver.Compute(rule, 1000m, ticketCount: 4)); // ticket count is irrelevant
            Assert.Equal(33.33m, CommissionRuleResolver.Compute(Rule(D(2026, 1, 1), value: 10m), 333.333m, 2));
        }

        [Theory]
        [InlineData(1, 10)]
        [InlineData(3, 30)]
        [InlineData(5, 50)]
        public void FixedCommission_IsChargedPerTicket(int tickets, int expected)
        {
            var rule = Rule(D(2026, 1, 1), channel: SaleChannel.Counter, type: CommissionType.FixedAmount, value: 10m);

            Assert.Equal((decimal)expected, CommissionRuleResolver.Compute(rule, taxableBase: 9999m, ticketCount: tickets));
        }

        [Fact]
        public void FixedCommission_WithNoTickets_IsZero()
        {
            var rule = Rule(D(2026, 1, 1), type: CommissionType.FixedAmount, value: 10m);

            Assert.Equal(0m, CommissionRuleResolver.Compute(rule, 1000m, ticketCount: 0));
        }

        // ---------- C5-3: overlap validation ----------

        [Fact]
        public void TwoActiveRules_ForTheSameOperatorChannelAndScope_WithIntersectingDates_Overlap()
        {
            var existing = Rule(D(2026, 1, 1), to: D(2026, 12, 31));
            var candidate = Rule(D(2026, 6, 1), to: D(2027, 3, 31));

            Assert.Same(existing, CommissionRuleResolver.FindOverlap(new[] { existing }, candidate));
        }

        [Fact]
        public void TouchingOnASharedDay_IsAnOverlap_ButConsecutiveDaysAreNot()
        {
            var existing = Rule(D(2026, 1, 1), to: D(2026, 9, 30));

            // Both rules claim 30 Sep — inclusive end dates make that a real collision.
            Assert.Same(existing, CommissionRuleResolver.FindOverlap(new[] { existing }, Rule(D(2026, 9, 30))));
            // Starting the day after the other ends is the clean hand-over.
            Assert.Null(CommissionRuleResolver.FindOverlap(new[] { existing }, Rule(D(2026, 10, 1))));
        }

        [Fact]
        public void AnOpenEndedRule_OverlapsEverythingThatStartsAfterIt()
        {
            var openEnded = Rule(D(2026, 1, 1), to: null);

            Assert.Same(openEnded, CommissionRuleResolver.FindOverlap(new[] { openEnded }, Rule(D(2030, 1, 1), to: D(2030, 12, 31))));
            Assert.Null(CommissionRuleResolver.FindOverlap(new[] { openEnded }, Rule(D(2025, 1, 1), to: D(2025, 12, 31))));
        }

        [Fact]
        public void ARouteRule_NextToAnOperatorWideRule_IsNotAnOverlap_ButTwoRulesForTheSameRouteAre()
        {
            var wide = Rule(D(2026, 1, 1));
            var routeX = Rule(D(2026, 1, 1), route: RouteX);

            Assert.Null(CommissionRuleResolver.FindOverlap(new[] { wide }, routeX));
            Assert.Null(CommissionRuleResolver.FindOverlap(new[] { routeX }, Rule(D(2026, 1, 1), route: RouteY)));
            Assert.Same(routeX, CommissionRuleResolver.FindOverlap(new[] { routeX }, Rule(D(2026, 5, 1), route: RouteX)));
        }

        [Fact]
        public void DifferentChannelsOrOperators_NeverOverlap()
        {
            var existing = Rule(D(2026, 1, 1));

            Assert.Null(CommissionRuleResolver.FindOverlap(new[] { existing }, Rule(D(2026, 1, 1), channel: SaleChannel.Counter)));
            Assert.Null(CommissionRuleResolver.FindOverlap(new[] { existing }, Rule(D(2026, 1, 1), op: OperatorB)));
        }

        [Fact]
        public void InactiveRules_NeverCollide_AndARuleNeverCollidesWithItself()
        {
            var existing = Rule(D(2026, 1, 1));
            var inactiveExisting = Rule(D(2026, 1, 1), active: false);

            Assert.Null(CommissionRuleResolver.FindOverlap(new[] { existing }, Rule(D(2026, 1, 1), active: false)));
            Assert.Null(CommissionRuleResolver.FindOverlap(new[] { inactiveExisting }, Rule(D(2026, 1, 1))));
            Assert.Null(CommissionRuleResolver.FindOverlap(new[] { existing }, existing)); // editing a rule in place
        }
    }
}
