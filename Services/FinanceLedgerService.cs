using TicketPortal.Api.Data;
using TicketPortal.Api.Models.Enums;
using TicketPortal.Api.Models.Finance;
using Microsoft.EntityFrameworkCore;

namespace TicketPortal.Api.Services
{
    // Thrown when a posting method is called for a booking whose SaleChannel/MoneyCollectedBy
    // doesn't match what that method assumes — this catches a "wrong method called for this
    // booking" mistake immediately, instead of quietly writing the wrong numbers into the books.
    public class LedgerChannelMismatchException : Exception
    {
        public LedgerChannelMismatchException(string message) : base(message) { }
    }

    // This class is the ONLY place in the codebase allowed to write to PlatformLedger (the
    // master money diary) or change OperatorWallet's numbers (the fast "current balance" cache).
    // Every method below does both things together, in one database transaction, every time —
    // so the cached balance can never end up disagreeing with what the diary actually says happened.
    public class FinanceLedgerService
    {
        private readonly AppDbContext _db;

        public FinanceLedgerService(AppDbContext db)
        {
            _db = db;
        }

        // Call this once, right after an ONLINE booking's payment is confirmed successful.
        // This is the heart of the commission calculation from the business plan. Example:
        //   Customer pays 1000 BDT online. Our commission is 10%. The gateway's own fee is 20 BDT,
        //   and the operator's contract says THEY bear that gateway fee.
        //     -> Diary: Credit 1000 (OnlineTicketSale)   = we now owe the operator this, before deductions.
        //     -> Diary: Debit   100 (PlatformCommission) = our cut, subtracted from what we owe them.
        //     -> Diary: Debit    20 (GatewayCharge)       = only written if the OPERATOR is the one paying this fee.
        //   End result: what we actually owe the operator for this one booking is 1000 - 100 - 20 = 880 BDT.
        public async Task PostOnlineSaleAsync(
            Guid bookingId,
            Guid busOperatorId,
            decimal grossFare,
            decimal platformCommission,
            decimal gatewayCharge,
            GatewayFeeBearer gatewayFeeBearer,
            string currency = "BDT")
        {
            // Safety check first: make sure this booking really was an online/platform sale
            // before we post it that way.
            await EnsureMoneyCollectedByAsync(bookingId, MoneyCollectedBy.Platform, nameof(PostOnlineSaleAsync));

            await using var transaction = await _db.Database.BeginTransactionAsync();

            var entries = new List<PlatformLedger>
            {
                NewEntry(bookingId, busOperatorId, StatementItemType.OnlineTicketSale,
                    SaleChannel.Online, credit: grossFare, debit: 0m, currency,
                    "Gross online ticket sale collected on operator's behalf"),

                NewEntry(bookingId, busOperatorId, StatementItemType.PlatformCommission,
                    SaleChannel.Online, credit: 0m, debit: platformCommission, currency,
                    "Platform commission on online sale")
            };

            // Only charge the operator for the gateway fee if their contract actually says
            // it's their responsibility — otherwise the platform or customer absorbs it elsewhere.
            if (gatewayFeeBearer == GatewayFeeBearer.Operator && gatewayCharge > 0)
            {
                entries.Add(NewEntry(bookingId, busOperatorId, StatementItemType.GatewayCharge,
                    SaleChannel.Online, credit: 0m, debit: gatewayCharge, currency,
                    "Payment gateway fee charged to operator"));
            }

            _db.PlatformLedgers.AddRange(entries);

            var netDeltaToOperator = entries.Sum(e => e.CreditAmount - e.DebitAmount);
            await ApplyWalletDeltaAsync(busOperatorId, netDeltaToOperator, totalOnlineSalesDelta: grossFare);

            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        // Call this once per ticket sold at an operator's CASH COUNTER, for operators using our
        // ERP there. No actual money passes through the platform for this — the cash stays with
        // the operator — this only records the small fee they owe us for using our system, which
        // gets netted against whatever we already owe them from online sales.
        public async Task PostCounterSaleCommissionAsync(
            Guid bookingId,
            Guid busOperatorId,
            decimal commissionAmount,
            string currency = "BDT")
        {
            await EnsureMoneyCollectedByAsync(bookingId, MoneyCollectedBy.Operator, nameof(PostCounterSaleCommissionAsync));

            await using var transaction = await _db.Database.BeginTransactionAsync();

            var entry = NewEntry(bookingId, busOperatorId, StatementItemType.CounterSaleCommission,
                SaleChannel.Counter, credit: 0m, debit: commissionAmount, currency,
                "Commission for counter ticket sold via platform ERP");

            _db.PlatformLedgers.Add(entry);
            await ApplyWalletDeltaAsync(busOperatorId, -commissionAmount, totalCounterSalesDelta: commissionAmount);

            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        // Call this when a refund is approved and actually processed for an online booking.
        public async Task PostRefundAsync(
            Guid bookingId,
            Guid? refundId,
            Guid busOperatorId,
            decimal refundAmount,
            string currency = "BDT")
        {
            await using var transaction = await _db.Database.BeginTransactionAsync();

            var entry = NewEntry(bookingId, busOperatorId, StatementItemType.Refund,
                SaleChannel.Online, credit: 0m, debit: refundAmount, currency,
                "Refund issued to customer");
            entry.RefundId = refundId;

            _db.PlatformLedgers.Add(entry);
            await ApplyWalletDeltaAsync(busOperatorId, -refundAmount);

            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        // A trust check: works out an operator's balance completely from scratch by re-adding
        // every diary row, ignoring the cached OperatorWallet numbers entirely. Good for a
        // nightly job that confirms the cache hasn't quietly drifted from reality.
        public async Task<decimal> ComputeOperatorBalanceFromLedgerAsync(Guid busOperatorId)
        {
            return await _db.PlatformLedgers
                .Where(l => l.BusOperatorId == busOperatorId)
                .SumAsync(l => l.CreditAmount - l.DebitAmount);
        }

        // A guard rail: confirms the booking's real MoneyCollectedBy value actually matches
        // what the calling method expects, before writing anything. Cheap to check, and it
        // catches "the wrong posting method got called for this sale" mistakes immediately,
        // instead of letting incorrect numbers land quietly in the diary.
        private async Task EnsureMoneyCollectedByAsync(Guid bookingId, MoneyCollectedBy expected, string methodName)
        {
            var actual = await _db.Bookings
                .Where(b => b.Id == bookingId)
                .Select(b => b.MoneyCollectedBy)
                .SingleOrDefaultAsync();

            if (actual != expected)
            {
                throw new LedgerChannelMismatchException(
                    $"{methodName} expects MoneyCollectedBy = {expected}, but booking {bookingId} has {actual}. " +
                    "Check which posting method the caller invoked for this sale channel.");
            }
        }

        // Small helper so every ledger row above is built the same consistent way.
        private static PlatformLedger NewEntry(
            Guid bookingId,
            Guid busOperatorId,
            StatementItemType itemType,
            SaleChannel saleChannel,
            decimal credit,
            decimal debit,
            string currency,
            string description) => new()
        {
            BookingId = bookingId,
            BusOperatorId = busOperatorId,
            LedgerNo = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(),
            ItemType = itemType,
            SaleChannel = saleChannel,
            CreditAmount = credit,
            DebitAmount = debit,
            Currency = currency,
            Description = description
        };

        // Updates OperatorWallet's cached numbers with a single "add this delta" database
        // instruction, instead of loading the wallet, changing it in C#, and saving it back.
        // That matters because several bookings for the same operator could be posting at
        // the exact same moment — a direct "add to the existing number" update stays correct
        // no matter how many of these happen at once, while a load-then-save approach could
        // lose one of the updates.
        private async Task ApplyWalletDeltaAsync(
            Guid busOperatorId,
            decimal receivableDelta,
            decimal totalOnlineSalesDelta = 0m,
            decimal totalCounterSalesDelta = 0m)
        {
            var affected = await _db.OperatorWallets
                .Where(w => w.BusOperatorId == busOperatorId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(w => w.OperatorReceivableFromPlatform,
                        w => w.OperatorReceivableFromPlatform + (receivableDelta > 0 ? receivableDelta : 0))
                    .SetProperty(w => w.PlatformReceivableFromOperator,
                        w => w.PlatformReceivableFromOperator + (receivableDelta < 0 ? -receivableDelta : 0))
                    .SetProperty(w => w.PendingSettlementBalance, w => w.PendingSettlementBalance + receivableDelta)
                    .SetProperty(w => w.TotalOnlineSalesAmount, w => w.TotalOnlineSalesAmount + totalOnlineSalesDelta)
                    .SetProperty(w => w.TotalCounterSalesAmount, w => w.TotalCounterSalesAmount + totalCounterSalesDelta));

            if (affected == 0)
            {
                throw new InvalidOperationException(
                    $"No OperatorWallet exists for operator {busOperatorId}. Create one when the operator is onboarded.");
            }
        }
    }
}
