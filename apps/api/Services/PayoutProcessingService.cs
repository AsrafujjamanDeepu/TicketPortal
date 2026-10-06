using System.Text.Json;
using TicketPortal.Api.Data;
using TicketPortal.Api.Models.Diagnostics;
using TicketPortal.Api.Models.Enums;
using TicketPortal.Api.Models.Finance;
using TicketPortal.Api.Realtime;
using Microsoft.EntityFrameworkCore;

namespace TicketPortal.Api.Services
{
    // Chunk 5 (C5-1): a payout request that failed a business rule. Derives from
    // InvalidOperationException so every existing `catch (InvalidOperationException)` that turns it
    // into a 400 keeps working; Code is the stable machine-readable reason (tests and the audit
    // trail key off it, the message is for humans and may be reworded).
    public sealed class PayoutRejectedException(string code, string message) : InvalidOperationException(message)
    {
        public string Code { get; } = code;
    }

    // Who asked for the payout, for the audit trail. Optional everywhere so the demo seeder and
    // background callers that have no HTTP request still work.
    public sealed record PayoutAuditActor(Guid? UserId, string? IpAddress, string? UserAgent);

    // Orchestrates an OperatorPayout from Pending through to Paid/Failed/Cancelled. Before this
    // existed, OperatorPayoutsController let any authenticated user set Status straight to Paid
    // with a made-up BankTransactionReference and no check against what was actually available
    // to pay out — this is what makes a payout's status mean "money really left the account".
    //
    // The available balance is RESERVED the moment a payout is created (moved out of
    // AvailablePayoutBalance immediately, atomically, so two staff members can't both create a
    // payout against the same money) and only turns into WithdrawnAmount once the transfer is
    // actually confirmed. If it fails or is cancelled first, the reservation is given back.
    //
    // Chunk 5 (C5-1) — settlement-linked payouts are validated BEFORE any money is reserved: the
    // settlement must exist for this operator, be Approved, be a platform-pays-operator
    // settlement in the same currency, and the request must fit inside what is still unpaid on it
    // (its NetAmount minus every Pending/Processing/Paid payout already raised against it). The
    // settlement row is locked for the length of the transaction so two concurrent requests for
    // the same settlement are serialized and the second one sees the first one's payout. Every
    // accepted AND rejected request leaves an AuditLog row.
    //
    // DECISION D8 (see SettlementGenerationService): money owed to an operator reaches
    // AvailablePayoutBalance only when its settlement is APPROVED, so even a payout with no
    // settlement reference can never be funded from a Draft, unreviewed settlement.
    public class PayoutProcessingService
    {
        private readonly AppDbContext _db;
        private readonly IRealtimeNotifier _notifier;

        // Optional so a hand-built instance (DemoDataSeeder) keeps compiling and announces nothing.
        public PayoutProcessingService(AppDbContext db, IRealtimeNotifier? notifier = null)
        {
            _db = db;
            _notifier = notifier ?? NullRealtimeNotifier.Instance;
        }

        // Realtime Chunk 3: create / complete / fail / cancel all move OperatorWallet numbers with a
        // bulk UPDATE that EF's change tracker never sees (the OperatorPayout row itself is
        // announced by Chunk 2's capture). Called inside each method's transaction, so the
        // announcement is parked until it commits and dropped if it rolls back.
        private async Task AnnounceWalletChangedAsync(Guid busOperatorId)
        {
            try
            {
                await _notifier.EntityChangedAsync(_db, RealtimeBulkChanges.OperatorWallet(busOperatorId));
            }
            catch (Exception)
            {
                // Best-effort by design — never fail a payout over a realtime problem.
            }
        }

        public async Task<OperatorPayout> CreateAsync(
            Guid busOperatorId, decimal amount, string currency, Guid? operatorSettlementId, string? notes,
            PayoutAuditActor? actor = null)
        {
            try
            {
                return await CreateCoreAsync(busOperatorId, amount, currency, operatorSettlementId, notes, actor);
            }
            catch (PayoutRejectedException rejection)
            {
                // The transaction inside CreateCoreAsync has already rolled back, so nothing was
                // reserved. The rejection itself is still worth a permanent record (who tried to
                // pay out what against which settlement, and why it was refused).
                await RecordRejectionAsync(busOperatorId, amount, currency, operatorSettlementId, actor, rejection);
                throw;
            }
        }

        private async Task<OperatorPayout> CreateCoreAsync(
            Guid busOperatorId, decimal amount, string currency, Guid? operatorSettlementId, string? notes,
            PayoutAuditActor? actor)
        {
            if (amount <= 0)
            {
                throw new PayoutRejectedException("invalid_amount", "Payout amount must be positive.");
            }

            currency = (currency ?? string.Empty).Trim().ToUpperInvariant();
            if (currency.Length != 3)
            {
                throw new PayoutRejectedException("invalid_currency", "Payout currency must be a 3-letter currency code.");
            }

            await using var transaction = await _db.Database.BeginTransactionAsync();

            // Lock order is always settlement first, then wallet, so two payout requests can never
            // deadlock each other.
            if (operatorSettlementId.HasValue)
            {
                await ValidateSettlementAsync(busOperatorId, amount, currency, operatorSettlementId.Value);
            }

            // Atomic check-and-reserve: the WHERE clause only lets the update through if there's
            // still enough available balance, so two concurrent payout requests for the same
            // operator can't both succeed against money that's only there once.
            var reserved = await _db.OperatorWallets
                .Where(w => w.BusOperatorId == busOperatorId && w.AvailablePayoutBalance >= amount)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(w => w.AvailablePayoutBalance, w => w.AvailablePayoutBalance - amount));

            if (reserved == 0)
            {
                var walletExists = await _db.OperatorWallets.AnyAsync(w => w.BusOperatorId == busOperatorId);
                throw walletExists
                    ? new PayoutRejectedException("insufficient_balance",
                        $"Operator {busOperatorId} does not have {amount} {currency} available to pay out.")
                    : new PayoutRejectedException("wallet_missing",
                        $"No OperatorWallet exists for operator {busOperatorId}.");
            }

            await AnnounceWalletChangedAsync(busOperatorId);

            var payout = new OperatorPayout
            {
                BusOperatorId = busOperatorId,
                OperatorSettlementId = operatorSettlementId,
                PayoutNo = $"PYT-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
                Amount = amount,
                Currency = currency,
                Status = PayoutStatus.Pending,
                Notes = notes,
            };
            _db.OperatorPayouts.Add(payout);

            // Same transaction as the reservation: either the payout, the reserved money and its
            // audit row all exist, or none of them do.
            _db.AuditLogs.Add(NewAuditLog(actor, payout.Id.ToString(), "Created", oldValues: null, newValues: new
            {
                payout.BusOperatorId,
                payout.OperatorSettlementId,
                payout.PayoutNo,
                payout.Amount,
                payout.Currency,
            }));

            await _db.SaveChangesAsync();
            await transaction.CommitAsync();

            return payout;
        }

        // Everything a settlement-linked payout must satisfy. Runs inside CreateCoreAsync's
        // transaction. Throws PayoutRejectedException with a specific code for each rule.
        private async Task ValidateSettlementAsync(
            Guid busOperatorId, decimal amount, string currency, Guid settlementId)
        {
            // A real (value-changing) write takes an exclusive row lock that is held until this
            // transaction ends. A second request for the same settlement blocks right here until
            // the first one commits or rolls back, and then reads the committed payouts below —
            // so the "unpaid remainder" check cannot be raced. It also doubles as the ownership
            // check: the WHERE clause only matches a live settlement of THIS operator.
            var lockedAtUtc = DateTime.UtcNow;
            var locked = await _db.OperatorSettlements
                .Where(s => s.Id == settlementId && s.BusOperatorId == busOperatorId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.UpdatedAtUtc, s => lockedAtUtc));

            if (locked == 0)
            {
                // Same message whether the settlement is missing or belongs to another operator, so
                // the response can't be used to probe which settlement ids exist; the audit row
                // still records which of the two it was.
                var existsElsewhere = await _db.OperatorSettlements.AnyAsync(s => s.Id == settlementId);
                throw new PayoutRejectedException(
                    existsElsewhere ? "settlement_operator_mismatch" : "settlement_not_found",
                    $"Settlement {settlementId} was not found for this operator.");
            }

            var settlement = await _db.OperatorSettlements.AsNoTracking().FirstAsync(s => s.Id == settlementId);

            if (settlement.Direction != SettlementDirection.PlatformPaysOperator || settlement.NetAmount <= 0)
            {
                throw new PayoutRejectedException("settlement_not_payable",
                    $"Settlement {settlement.SettlementNo} does not have a positive amount owed to the operator, so there is nothing to pay out against it.");
            }

            if (settlement.Status != SettlementStatus.Approved)
            {
                throw new PayoutRejectedException("settlement_not_approved",
                    $"Settlement {settlement.SettlementNo} is {settlement.Status}; only an Approved settlement can be paid out.");
            }

            var settlementCurrency = await _db.PlatformLedgers
                .AsNoTracking()
                .Where(l => l.OperatorSettlementId == settlementId)
                .Select(l => l.Currency)
                .FirstOrDefaultAsync();
            if (settlementCurrency != null
                && !string.Equals(settlementCurrency, currency, StringComparison.OrdinalIgnoreCase))
            {
                throw new PayoutRejectedException("currency_mismatch",
                    $"Settlement {settlement.SettlementNo} is in {settlementCurrency}, but the payout is in {currency}.");
            }

            // Failed and Cancelled payouts gave their reservation back, so they don't count; a
            // Pending/Processing payout is money already spoken for, a Paid one is money gone.
            var alreadyCommitted = await _db.OperatorPayouts
                .Where(p => p.OperatorSettlementId == settlementId
                    && (p.Status == PayoutStatus.Pending
                        || p.Status == PayoutStatus.Processing
                        || p.Status == PayoutStatus.Paid))
                .SumAsync(p => (decimal?)p.Amount) ?? 0m;
            var remaining = settlement.NetAmount - alreadyCommitted;

            if (remaining <= 0)
            {
                throw new PayoutRejectedException("settlement_fully_paid",
                    $"Settlement {settlement.SettlementNo} has already been paid out in full ({settlement.NetAmount} {currency}).");
            }

            if (amount > remaining)
            {
                throw new PayoutRejectedException("exceeds_settlement_remainder",
                    $"Settlement {settlement.SettlementNo} has only {remaining} {currency} left unpaid, but {amount} {currency} was requested.");
            }
        }

        private async Task RecordRejectionAsync(
            Guid busOperatorId, decimal amount, string currency, Guid? settlementId,
            PayoutAuditActor? actor, PayoutRejectedException rejection)
        {
            try
            {
                _db.AuditLogs.Add(NewAuditLog(actor, settlementId?.ToString() ?? busOperatorId.ToString(),
                    "PayoutRejected", oldValues: null, newValues: new
                    {
                        BusOperatorId = busOperatorId,
                        OperatorSettlementId = settlementId,
                        Amount = amount,
                        Currency = currency,
                        Reason = rejection.Code,
                    }));
                await _db.SaveChangesAsync();
            }
            catch (Exception)
            {
                // Best-effort by design: failing to write the rejection record must never hide
                // the real, user-facing reason the payout was refused.
            }
        }

        private static AuditLog NewAuditLog(
            PayoutAuditActor? actor, string entityId, string action, object? oldValues, object? newValues) => new()
        {
            UserId = actor?.UserId,
            EntityName = "OperatorPayout",
            EntityId = entityId,
            Action = action,
            OldValuesJson = oldValues == null ? null : JsonSerializer.Serialize(oldValues),
            NewValuesJson = newValues == null ? null : JsonSerializer.Serialize(newValues),
            IpAddress = Truncate(actor?.IpAddress, 80),
            UserAgent = Truncate(actor?.UserAgent, 300),
            CreatedAtUtc = DateTime.UtcNow,
        };

        private static string? Truncate(string? value, int max) =>
            value is { Length: > 0 } && value.Length > max ? value[..max] : value;

        // Staff confirms they've actually started the bank transfer. No wallet change — the
        // amount was already reserved at Create.
        public async Task MarkProcessingAsync(Guid payoutId)
        {
            var payout = await _db.OperatorPayouts.FirstOrDefaultAsync(p => p.Id == payoutId)
                ?? throw new InvalidOperationException($"Payout {payoutId} does not exist.");

            if (payout.Status != PayoutStatus.Pending)
            {
                throw new InvalidOperationException(
                    $"Payout {payoutId} is {payout.Status}; only a Pending payout can move to Processing.");
            }

            payout.Status = PayoutStatus.Processing;
            payout.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        // The step that actually confirms money left the account — requires a real bank
        // reference, matching the plan's "should only move to Completed once there's a real
        // bank reference" requirement.
        public async Task CompleteAsync(Guid payoutId, string bankTransactionReference)
        {
            if (string.IsNullOrWhiteSpace(bankTransactionReference))
            {
                throw new InvalidOperationException("A bank transaction reference is required to complete a payout.");
            }

            var payout = await _db.OperatorPayouts.FirstOrDefaultAsync(p => p.Id == payoutId)
                ?? throw new InvalidOperationException($"Payout {payoutId} does not exist.");

            if (payout.Status is not (PayoutStatus.Pending or PayoutStatus.Processing))
            {
                throw new InvalidOperationException(
                    $"Payout {payoutId} is {payout.Status}; only a Pending or Processing payout can be completed.");
            }

            await using var transaction = await _db.Database.BeginTransactionAsync();

            payout.Status = PayoutStatus.Paid;
            payout.PaidAtUtc = DateTime.UtcNow;
            payout.BankTransactionReference = bankTransactionReference;
            payout.UpdatedAtUtc = DateTime.UtcNow;

            await _db.OperatorWallets
                .Where(w => w.BusOperatorId == payout.BusOperatorId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(w => w.WithdrawnAmount, w => w.WithdrawnAmount + payout.Amount));

            await AnnounceWalletChangedAsync(payout.BusOperatorId);

            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        // Gives the reserved amount back to AvailablePayoutBalance — used for both Fail and
        // Cancel, since in both cases the money never actually left the account.
        public Task FailAsync(Guid payoutId, string reason) => ReleaseAsync(payoutId, PayoutStatus.Failed, reason);

        public Task CancelAsync(Guid payoutId, string reason) => ReleaseAsync(payoutId, PayoutStatus.Cancelled, reason);

        private async Task ReleaseAsync(Guid payoutId, PayoutStatus terminalStatus, string reason)
        {
            var payout = await _db.OperatorPayouts.FirstOrDefaultAsync(p => p.Id == payoutId)
                ?? throw new InvalidOperationException($"Payout {payoutId} does not exist.");

            if (payout.Status is not (PayoutStatus.Pending or PayoutStatus.Processing))
            {
                throw new InvalidOperationException(
                    $"Payout {payoutId} is {payout.Status}; only a Pending or Processing payout can be {terminalStatus}.");
            }

            await using var transaction = await _db.Database.BeginTransactionAsync();

            payout.Status = terminalStatus;
            payout.UpdatedAtUtc = DateTime.UtcNow;
            payout.Notes = string.IsNullOrWhiteSpace(payout.Notes) ? reason : $"{payout.Notes} | {reason}";

            await _db.OperatorWallets
                .Where(w => w.BusOperatorId == payout.BusOperatorId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(w => w.AvailablePayoutBalance, w => w.AvailablePayoutBalance + payout.Amount));

            await AnnounceWalletChangedAsync(payout.BusOperatorId);

            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
    }
}
