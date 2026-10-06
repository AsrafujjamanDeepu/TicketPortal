using TicketPortal.Api.Data;
using TicketPortal.Api.Models.Bookings;
using TicketPortal.Api.Models.Enums;
using TicketPortal.Api.Models.Finance;
using Microsoft.EntityFrameworkCore;

namespace TicketPortal.Api.Services
{
    // RBAC Amendment v3 / Chunk 7 task 4 ("Missing-rule visibility"):
    //
    //   "When no commission rule exists the booking still confirms and the ledger is skipped
    //    with a ledgerWarning (by design — see PaymentConfirmationService). Add a reconciliation
    //    list 'confirmed bookings with no ledger rows' (admin/finance) and a safe re-post
    //    action, so settlements can never be silently understated."
    //
    // That LedgerWarning (see PaymentsController's Confirm actions) is only ever returned once,
    // in the HTTP response to the confirm-payment call itself — it is never written anywhere
    // durable. Worse, PaymentConfirmationService's idempotency path means a RETRY of the same
    // confirm call after the gap happens returns the already-cached "Succeeded" result without
    // ever attempting the ledger post again (see BuildIdempotentResultAsync there) — so once a
    // booking hits this gap, it stays a gap forever unless something notices and fixes it by
    // hand. This service is that "notice and fix".
    //
    // GetLedgerGapsAsync needs no new column and no migration: a gap is simply a Confirmed (or
    // later) booking whose expected ledger row — OnlineTicketSale for an online sale,
    // CounterSaleCommission for a counter sale, see FinanceLedgerService.PostOnlineSaleAsync /
    // PostCounterSaleCommissionAsync — doesn't exist. That's computed live from PlatformLedger
    // every time, so it can never itself drift out of sync with reality.
    //
    // Deliberately its own small service/file rather than new endpoints bolted onto an existing
    // controller: this reads across Bookings/Payments/PlatformLedgers/CommissionRules/
    // OperatorContracts and doesn't belong to any one existing resource. It also deliberately
    // does NOT call into PaymentConfirmationService (out of Chunk 7's owner scope — see the
    // completion plan's ownership table: FinanceLedgerService/SettlementGenerationService/
    // Commission* controllers, not PaymentConfirmationService/PaymentsController). Chunk 5
    // replaced the two hand-kept copies of the CommissionRule lookup with one stateless
    // CommissionRuleResolver that both services call, so the rule order, the Dhaka business
    // date and the fixed-amount-per-ticket arithmetic (decision D3) are identical in a live
    // sale and in a re-post by construction instead of by careful copying.
    public class LedgerGap
    {
        public Guid BookingId { get; set; }
        public string Pnr { get; set; } = string.Empty;
        public Guid BusOperatorId { get; set; }
        public SaleChannel SaleChannel { get; set; }
        public decimal GrandTotal { get; set; }
        public string Currency { get; set; } = "BDT";
        public DateTime ConfirmedAtUtc { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    public class FinanceReconciliationService(AppDbContext db, FinanceLedgerService financeLedgerService)
    {
        private static readonly BookingStatus[] SettleableStatuses =
        [
            BookingStatus.Confirmed,
            BookingStatus.Completed,
            BookingStatus.PartiallyCancelled,
        ];

        public async Task<List<LedgerGap>> GetLedgerGapsAsync(Guid? busOperatorId = null)
        {
            var bookings = db.Bookings.Where(b => SettleableStatuses.Contains(b.Status));
            if (busOperatorId.HasValue)
            {
                bookings = bookings.Where(b => b.BusOperatorId == busOperatorId.Value);
            }

            var onlineGaps = await bookings
                .Where(b => b.MoneyCollectedBy == MoneyCollectedBy.Platform && !db.PlatformLedgers.Any(l =>
                    l.BookingId == b.Id && l.ItemType == StatementItemType.OnlineTicketSale))
                .Select(b => new LedgerGap
                {
                    BookingId = b.Id,
                    Pnr = b.Pnr,
                    BusOperatorId = b.BusOperatorId,
                    SaleChannel = b.SaleChannel,
                    GrandTotal = b.GrandTotal,
                    Currency = b.Currency,
                    ConfirmedAtUtc = b.UpdatedAtUtc ?? b.CreatedAtUtc,
                    Reason = "No OnlineTicketSale ledger entry for this online booking.",
                })
                .ToListAsync();

            var counterGaps = await bookings
                .Where(b => b.MoneyCollectedBy == MoneyCollectedBy.Operator && !db.PlatformLedgers.Any(l =>
                    l.BookingId == b.Id && l.ItemType == StatementItemType.CounterSaleCommission))
                .Select(b => new LedgerGap
                {
                    BookingId = b.Id,
                    Pnr = b.Pnr,
                    BusOperatorId = b.BusOperatorId,
                    SaleChannel = b.SaleChannel,
                    GrandTotal = b.GrandTotal,
                    Currency = b.Currency,
                    ConfirmedAtUtc = b.UpdatedAtUtc ?? b.CreatedAtUtc,
                    Reason = "No CounterSaleCommission ledger entry for this counter sale.",
                })
                .ToListAsync();

            return onlineGaps.Concat(counterGaps).OrderBy(g => g.ConfirmedAtUtc).ToList();
        }

        // Re-resolves the SAME CommissionRule lookup PaymentConfirmationService uses (see
        // ResolveCommissionRuleAsync there), then calls the one FinanceLedgerService method the
        // original confirm flow would have called. Re-checks the gap still exists immediately
        // before posting, so calling this twice for the same booking is always safe: the second
        // call sees the entry the first call just wrote and throws instead of posting again.
        public async Task RepostAsync(Guid bookingId)
        {
            var booking = await db.Bookings.FirstOrDefaultAsync(b => b.Id == bookingId)
                ?? throw new InvalidOperationException($"Booking {bookingId} does not exist.");

            if (!SettleableStatuses.Contains(booking.Status))
            {
                throw new InvalidOperationException(
                    $"Booking {bookingId} is {booking.Status}; only a Confirmed/Completed/PartiallyCancelled booking can have a ledger entry re-posted.");
            }

            if (booking.MoneyCollectedBy == MoneyCollectedBy.Platform)
            {
                var alreadyPosted = await db.PlatformLedgers.AnyAsync(l =>
                    l.BookingId == bookingId && l.ItemType == StatementItemType.OnlineTicketSale);
                if (alreadyPosted)
                {
                    throw new InvalidOperationException(
                        $"Booking {bookingId} already has an OnlineTicketSale ledger entry — nothing to re-post.");
                }

                var payment = await db.Payments
                    .Where(p => p.BookingId == bookingId && p.Status == PaymentStatus.Succeeded)
                    .OrderByDescending(p => p.PaidAtUtc)
                    .FirstOrDefaultAsync()
                    ?? throw new InvalidOperationException($"Booking {bookingId} has no succeeded Payment to re-post from.");

                var rule = await ResolveCommissionRuleAsync(booking, SaleChannel.Online);
                var commission = CommissionRuleResolver.Compute(
                    rule, CommissionRuleResolver.TaxableBase(booking),
                    await CommissionRuleResolver.CountTicketsAsync(db, booking.Id));

                var gatewayFeeBearer = await db.OperatorContracts
                    .Where(c => c.BusOperatorId == booking.BusOperatorId && c.IsActive)
                    .Select(c => (GatewayFeeBearer?)c.GatewayFeeBearer)
                    .FirstOrDefaultAsync() ?? GatewayFeeBearer.Platform;

                await financeLedgerService.PostOnlineSaleAsync(
                    booking.Id, booking.BusOperatorId, payment.Amount, commission,
                    payment.GatewayFeeAmount, gatewayFeeBearer, payment.Currency);
            }
            else if (booking.MoneyCollectedBy == MoneyCollectedBy.Operator)
            {
                var alreadyPosted = await db.PlatformLedgers.AnyAsync(l =>
                    l.BookingId == bookingId && l.ItemType == StatementItemType.CounterSaleCommission);
                if (alreadyPosted)
                {
                    throw new InvalidOperationException(
                        $"Booking {bookingId} already has a CounterSaleCommission ledger entry — nothing to re-post.");
                }

                var rule = await ResolveCommissionRuleAsync(booking, SaleChannel.Counter);
                var commission = CommissionRuleResolver.Compute(
                    rule, CommissionRuleResolver.TaxableBase(booking),
                    await CommissionRuleResolver.CountTicketsAsync(db, booking.Id));

                await financeLedgerService.PostCounterSaleCommissionAsync(
                    booking.Id, booking.BusOperatorId, commission, booking.Currency);
            }
            else
            {
                throw new InvalidOperationException(
                    $"Booking {bookingId} has MoneyCollectedBy = {booking.MoneyCollectedBy}, which this reconciliation flow doesn't handle.");
            }
        }

        // Same rule selection as PaymentConfirmationService — both call CommissionRuleResolver
        // (see this file's class comment). A re-post resolves the rule on TODAY's Dhaka business
        // date, not the original sale date: the documented recovery for a missing rule is "add
        // one on the Commission Rules screen, then retry", and a rule added today can't be
        // back-dated to the sale without rewriting history. The Dhaka date is derived once here.
        private Task<CommissionRule> ResolveCommissionRuleAsync(Booking booking, SaleChannel channel) =>
            CommissionRuleResolver.ResolveAsync(db, booking, channel, DhakaClock.Today());
    }
}
