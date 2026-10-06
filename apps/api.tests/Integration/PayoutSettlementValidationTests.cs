using Microsoft.EntityFrameworkCore;
using TicketPortal.Api.Data;
using TicketPortal.Api.Models.CompanyNetwork;
using TicketPortal.Api.Models.Enums;
using TicketPortal.Api.Models.Finance;
using TicketPortal.Api.Services;
using TicketPortal.Api.Tests.Infrastructure;
using Xunit;

namespace TicketPortal.Api.Tests.Integration
{
    // Chunk 5 (C5-1 + decision D8): a settlement-linked payout is only accepted for an Approved,
    // platform-pays-operator settlement of the SAME operator, in the SAME currency, and only up to
    // what is still unpaid on it — and money owed to an operator becomes payable at approval, not
    // at generation. Every test builds its own fresh operator + wallet + one settlement whose
    // numbers are hand-checkable: one online sale of 1,000 with 100 commission -> net 900.
    [Collection(SharedApiCollection.Name)]
    public class PayoutSettlementValidationTests
    {
        private const decimal Net = 900m;

        private readonly TicketPortalWebApplicationFactory _factory;

        public PayoutSettlementValidationTests(TicketPortalWebApplicationFactory factory)
        {
            _factory = factory;
        }

        private static async Task<Guid> CreateFreshOperatorWithWalletAsync(AppDbContext db)
        {
            var op = new BusOperator { Name = $"Payout Test Operator {Guid.NewGuid():N}" };
            db.BusOperators.Add(op);
            db.OperatorWallets.Add(new OperatorWallet { BusOperatorId = op.Id });
            await db.SaveChangesAsync();
            return op.Id;
        }

        // Posts one 1,000 online sale (100 commission, no operator-borne gateway fee) for the
        // operator and generates the settlement for today. The borrowed seeded booking only has to
        // exist and be a Platform-collected sale — see FinanceLedgerServiceTests for why.
        private static async Task<OperatorSettlement> CreateDraftSettlementAsync(AppDbContext db, Guid operatorId)
        {
            var bookingId = await db.Bookings
                .Where(b => b.MoneyCollectedBy == MoneyCollectedBy.Platform)
                .Select(b => b.Id)
                .FirstAsync();

            await new FinanceLedgerService(db).PostOnlineSaleAsync(
                bookingId, operatorId, 1000m, 100m, 0m, GatewayFeeBearer.Platform);

            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            return await new SettlementGenerationService(db).GenerateSettlementAsync(operatorId, today, today);
        }

        private static async Task<(Guid OperatorId, Guid SettlementId)> CreateApprovedSettlementAsync(AppDbContext db)
        {
            var operatorId = await CreateFreshOperatorWithWalletAsync(db);
            var settlement = await CreateDraftSettlementAsync(db, operatorId);
            await new SettlementGenerationService(db).ApproveAsync(settlement.Id, "Test approval.");
            return (operatorId, settlement.Id);
        }

        private static Task<OperatorWallet> WalletAsync(AppDbContext db, Guid operatorId) =>
            db.OperatorWallets.AsNoTracking().SingleAsync(w => w.BusOperatorId == operatorId);

        private static Task<int> PayoutCountAsync(AppDbContext db, Guid operatorId) =>
            db.OperatorPayouts.CountAsync(p => p.BusOperatorId == operatorId);

        private static async Task SetSettlementStatusAsync(AppDbContext db, Guid settlementId, SettlementStatus status)
        {
            await db.OperatorSettlements
                .Where(s => s.Id == settlementId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.Status, status));
        }

        private async Task AssertRejectedAsync(
            Guid operatorId, Guid? settlementId, decimal amount, string currency, string expectedCode)
        {
            using var scope = _factory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var service = scope.ServiceProvider.GetRequiredService<PayoutProcessingService>();

            var before = await WalletAsync(db, operatorId);
            var ex = await Assert.ThrowsAsync<PayoutRejectedException>(() =>
                service.CreateAsync(operatorId, amount, currency, settlementId, "should be rejected"));
            Assert.Equal(expectedCode, ex.Code);

            // Nothing reserved, nothing created.
            var after = await WalletAsync(db, operatorId);
            Assert.Equal(before.AvailablePayoutBalance, after.AvailablePayoutBalance);
            Assert.Equal(0, await PayoutCountAsync(db, operatorId));

            // ...but the refusal is on the audit trail.
            var audit = await db.AuditLogs.AsNoTracking()
                .Where(a => a.EntityName == "OperatorPayout" && a.Action == "PayoutRejected"
                    && a.NewValuesJson != null && a.NewValuesJson.Contains(expectedCode)
                    && a.NewValuesJson.Contains(operatorId.ToString()))
                .CountAsync();
            Assert.True(audit >= 1, $"Expected a PayoutRejected audit row with reason '{expectedCode}'.");
        }

        // ---------- Rejections ----------

        [Fact]
        public async Task ADraftSettlement_CannotBePaidOut()
        {
            Guid operatorId, settlementId;
            using (var scope = _factory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                operatorId = await CreateFreshOperatorWithWalletAsync(db);
                settlementId = (await CreateDraftSettlementAsync(db, operatorId)).Id;
            }

            await AssertRejectedAsync(operatorId, settlementId, 100m, "BDT", "settlement_not_approved");
        }

        [Theory]
        [InlineData(SettlementStatus.Cancelled)]   // the "rejected" end of the lifecycle
        [InlineData(SettlementStatus.Invoiced)]
        [InlineData(SettlementStatus.Paid)]
        public async Task OnlyAnApprovedSettlement_CanBePaidOut(SettlementStatus status)
        {
            Guid operatorId, settlementId;
            using (var scope = _factory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                (operatorId, settlementId) = await CreateApprovedSettlementAsync(db);
                await SetSettlementStatusAsync(db, settlementId, status);
            }

            await AssertRejectedAsync(operatorId, settlementId, 100m, "BDT", "settlement_not_approved");
        }

        [Fact]
        public async Task ASettlementOfAnotherOperator_IsRejected_WithTheSameMessageAsAMissingOne()
        {
            Guid ownerId, settlementId, otherOperatorId;
            using (var scope = _factory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                (ownerId, settlementId) = await CreateApprovedSettlementAsync(db);
                otherOperatorId = await CreateFreshOperatorWithWalletAsync(db);
                // Give the other operator real, spendable money so only the ownership rule can refuse.
                await db.OperatorWallets
                    .Where(w => w.BusOperatorId == otherOperatorId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(w => w.AvailablePayoutBalance, 5000m));
            }

            await AssertRejectedAsync(otherOperatorId, settlementId, 100m, "BDT", "settlement_operator_mismatch");

            using var scope2 = _factory.CreateScope();
            var service = scope2.ServiceProvider.GetRequiredService<PayoutProcessingService>();
            var foreign = await Assert.ThrowsAsync<PayoutRejectedException>(() =>
                service.CreateAsync(otherOperatorId, 100m, "BDT", settlementId, null));
            var missing = await Assert.ThrowsAsync<PayoutRejectedException>(() =>
                service.CreateAsync(otherOperatorId, 100m, "BDT", Guid.NewGuid(), null));
            Assert.Equal("settlement_not_found", missing.Code);
            Assert.EndsWith("was not found for this operator.", foreign.Message);
            Assert.EndsWith("was not found for this operator.", missing.Message);

            // The owner's money is untouched.
            var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(Net, (await WalletAsync(db2, ownerId)).AvailablePayoutBalance);
        }

        [Fact]
        public async Task AWrongCurrency_IsRejected()
        {
            Guid operatorId, settlementId;
            using (var scope = _factory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                (operatorId, settlementId) = await CreateApprovedSettlementAsync(db);
            }

            await AssertRejectedAsync(operatorId, settlementId, 100m, "USD", "currency_mismatch");
        }

        [Fact]
        public async Task AmountsAboveTheUnpaidRemainder_AreRejected()
        {
            Guid operatorId, settlementId;
            using (var scope = _factory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                (operatorId, settlementId) = await CreateApprovedSettlementAsync(db);
                // Wallet holds extra spendable money, so only the settlement's own remainder can refuse.
                await db.OperatorWallets
                    .Where(w => w.BusOperatorId == operatorId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(w => w.AvailablePayoutBalance, w => w.AvailablePayoutBalance + 5000m));
            }

            await AssertRejectedAsync(operatorId, settlementId, Net + 0.01m, "BDT", "exceeds_settlement_remainder");
        }

        // ---------- Repeated requests ----------

        [Fact]
        public async Task RepeatedPayouts_CannotReuseTheSameRemainder()
        {
            Guid operatorId, settlementId;
            using (var scope = _factory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                (operatorId, settlementId) = await CreateApprovedSettlementAsync(db);
                await db.OperatorWallets
                    .Where(w => w.BusOperatorId == operatorId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(w => w.AvailablePayoutBalance, w => w.AvailablePayoutBalance + 5000m));
            }

            using var scope2 = _factory.CreateScope();
            var service = scope2.ServiceProvider.GetRequiredService<PayoutProcessingService>();

            await service.CreateAsync(operatorId, 600m, "BDT", settlementId, "first part");

            // 900 - 600 = 300 left; asking for 400 must fail and name the real remainder.
            var tooMuch = await Assert.ThrowsAsync<PayoutRejectedException>(() =>
                service.CreateAsync(operatorId, 400m, "BDT", settlementId, "too much"));
            Assert.Equal("exceeds_settlement_remainder", tooMuch.Code);
            Assert.Contains("300", tooMuch.Message);

            await service.CreateAsync(operatorId, 300m, "BDT", settlementId, "the rest");

            var fullyPaid = await Assert.ThrowsAsync<PayoutRejectedException>(() =>
                service.CreateAsync(operatorId, 1m, "BDT", settlementId, "nothing left"));
            Assert.Equal("settlement_fully_paid", fullyPaid.Code);
        }

        [Fact]
        public async Task AFailedPayout_GivesItsPartOfTheRemainderBack()
        {
            Guid operatorId, settlementId;
            using (var scope = _factory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                (operatorId, settlementId) = await CreateApprovedSettlementAsync(db);
            }

            using var scope2 = _factory.CreateScope();
            var service = scope2.ServiceProvider.GetRequiredService<PayoutProcessingService>();

            var first = await service.CreateAsync(operatorId, Net, "BDT", settlementId, "full amount");
            await Assert.ThrowsAsync<PayoutRejectedException>(() =>
                service.CreateAsync(operatorId, 1m, "BDT", settlementId, "blocked while the first is live"));

            await service.FailAsync(first.Id, "Bank rejected the transfer.");

            // The reservation and the settlement remainder both came back, so the same amount can be raised again.
            var retry = await service.CreateAsync(operatorId, Net, "BDT", settlementId, "retry");
            Assert.Equal(PayoutStatus.Pending, retry.Status);
        }

        // ---------- Concurrency ----------

        [Fact]
        public async Task TwoSimultaneousPayouts_ForTheSameSettlementRemainder_OnlyOneSucceeds()
        {
            Guid operatorId, settlementId;
            using (var scope = _factory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                (operatorId, settlementId) = await CreateApprovedSettlementAsync(db);
                // Plenty of wallet money, so the wallet's own check can't be what stops the second request.
                await db.OperatorWallets
                    .Where(w => w.BusOperatorId == operatorId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(w => w.AvailablePayoutBalance, w => w.AvailablePayoutBalance + 5000m));
            }

            async Task<string> AttemptAsync()
            {
                using var scope = _factory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<PayoutProcessingService>();
                try
                {
                    await service.CreateAsync(operatorId, 600m, "BDT", settlementId, "race");
                    return "created";
                }
                catch (PayoutRejectedException ex)
                {
                    return ex.Code;
                }
            }

            var results = await Task.WhenAll(AttemptAsync(), AttemptAsync());

            Assert.Equal(1, results.Count(r => r == "created"));
            Assert.Equal(1, results.Count(r => r == "exceeds_settlement_remainder"));

            using var verifyScope = _factory.CreateScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await PayoutCountAsync(verifyDb, operatorId));
            // 900 released at approval + 5,000 top-up - exactly one 600 reservation.
            Assert.Equal(Net + 5000m - 600m, (await WalletAsync(verifyDb, operatorId)).AvailablePayoutBalance);
        }

        // ---------- Accepted payouts leave an audit row ----------

        [Fact]
        public async Task AnAcceptedPayout_IsAudited_WithTheActor()
        {
            Guid operatorId, settlementId;
            using (var scope = _factory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                (operatorId, settlementId) = await CreateApprovedSettlementAsync(db);
            }

            using var scope2 = _factory.CreateScope();
            var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
            var service = scope2.ServiceProvider.GetRequiredService<PayoutProcessingService>();

            var payout = await service.CreateAsync(
                operatorId, 400m, "bdt", settlementId, "audited",
                new PayoutAuditActor(null, "203.0.113.7", "xunit"));

            Assert.Equal("BDT", payout.Currency); // normalized
            var audit = await db2.AuditLogs.AsNoTracking()
                .SingleAsync(a => a.EntityName == "OperatorPayout" && a.EntityId == payout.Id.ToString() && a.Action == "Created");
            Assert.Equal("203.0.113.7", audit.IpAddress);
            Assert.Contains(payout.PayoutNo, audit.NewValuesJson);
        }

        // ---------- D8: money becomes payable at approval ----------

        [Fact]
        public async Task ADraftSettlementsMoney_IsNotPayable_UntilItIsApproved()
        {
            Guid operatorId, settlementId;
            using (var scope = _factory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                operatorId = await CreateFreshOperatorWithWalletAsync(db);
                settlementId = (await CreateDraftSettlementAsync(db, operatorId)).Id;
            }

            using var scope2 = _factory.CreateScope();
            var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
            var payouts = scope2.ServiceProvider.GetRequiredService<PayoutProcessingService>();
            var settlements = new SettlementGenerationService(db2);

            var draftWallet = await WalletAsync(db2, operatorId);
            Assert.Equal(Net, draftWallet.PendingSettlementBalance);
            Assert.Equal(0m, draftWallet.AvailablePayoutBalance);

            // Even a payout with NO settlement reference cannot reach unapproved money.
            var unlinked = await Assert.ThrowsAsync<PayoutRejectedException>(() =>
                payouts.CreateAsync(operatorId, 100m, "BDT", null, "no settlement link"));
            Assert.Equal("insufficient_balance", unlinked.Code);

            await settlements.ApproveAsync(settlementId, "Reviewed.");

            var approvedWallet = await WalletAsync(db2, operatorId);
            Assert.Equal(0m, approvedWallet.PendingSettlementBalance);
            Assert.Equal(Net, approvedWallet.AvailablePayoutBalance);
            await payouts.CreateAsync(operatorId, 100m, "BDT", null, "now payable");
        }

        [Fact]
        public async Task ApprovingTwice_NeverReleasesTheMoneyTwice()
        {
            Guid operatorId, settlementId;
            using (var scope = _factory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                operatorId = await CreateFreshOperatorWithWalletAsync(db);
                settlementId = (await CreateDraftSettlementAsync(db, operatorId)).Id;
            }

            async Task<bool> ApproveAsync()
            {
                using var scope = _factory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                try
                {
                    await new SettlementGenerationService(db).ApproveAsync(settlementId, "race");
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }

            var results = await Task.WhenAll(ApproveAsync(), ApproveAsync());
            Assert.Equal(1, results.Count(r => r));

            using var verifyScope = _factory.CreateScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var wallet = await WalletAsync(verifyDb, operatorId);
            Assert.Equal(Net, wallet.AvailablePayoutBalance);
            Assert.Equal(0m, wallet.PendingSettlementBalance);

            // A later, sequential re-approval is refused too.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new SettlementGenerationService(verifyDb).ApproveAsync(settlementId, "again"));
            Assert.Equal(Net, (await WalletAsync(verifyDb, operatorId)).AvailablePayoutBalance);
        }
    }
}
