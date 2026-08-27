using TicketPortal.Api.Data;
using TicketPortal.Api.Models.Enums;
using TicketPortal.Api.Models.Payments;
using Microsoft.EntityFrameworkCore;

namespace TicketPortal.Api.Services
{
    // Orchestrates a Refund from Requested through to Succeeded/Failed. Before this existed,
    // RefundsController let any authenticated user set Status straight to Succeeded with a
    // made-up GatewayRefundReference — this is what makes a refund's status actually mean
    // "money really moved", by routing it through FinanceLedgerService (adjusts what the
    // operator is owed) and CustomerWalletService (actually gives the customer their money
    // back — the only real "send money to a customer" mechanism this project has today, since
    // no payment gateway is wired in to refund the original card/mobile-banking payment).
    public class RefundProcessingService
    {
        private readonly AppDbContext _db;
        private readonly FinanceLedgerService _financeLedgerService;
        private readonly CustomerWalletService _customerWalletService;

        public RefundProcessingService(
            AppDbContext db,
            FinanceLedgerService financeLedgerService,
            CustomerWalletService customerWalletService)
        {
            _db = db;
            _financeLedgerService = financeLedgerService;
            _customerWalletService = customerWalletService;
        }

        // A staff review step before any money moves — matches the business plan's "financing
        // and accounting" checks rather than letting a refund pay itself out unreviewed.
        public async Task ApproveAsync(Guid refundId, string? remarks)
        {
            var refund = await _db.Refunds.FirstOrDefaultAsync(r => r.Id == refundId)
                ?? throw new InvalidOperationException($"Refund {refundId} does not exist.");

            if (refund.Status != RefundStatus.Requested)
            {
                throw new InvalidOperationException(
                    $"Refund {refundId} is {refund.Status}; only a Requested refund can be approved.");
            }

            refund.Status = RefundStatus.Approved;
            refund.UpdatedAtUtc = DateTime.UtcNow;

            _db.RefundHistories.Add(new RefundHistory
            {
                RefundId = refund.Id,
                Status = RefundStatus.Approved,
                Remarks = remarks ?? "Refund approved."
            });

            await _db.SaveChangesAsync();
        }

        public async Task RejectAsync(Guid refundId, string reason)
        {
            var refund = await _db.Refunds.FirstOrDefaultAsync(r => r.Id == refundId)
                ?? throw new InvalidOperationException($"Refund {refundId} does not exist.");

            if (refund.Status is RefundStatus.Succeeded or RefundStatus.Rejected)
            {
                throw new InvalidOperationException(
                    $"Refund {refundId} is already {refund.Status} and cannot be rejected.");
            }

            refund.Status = RefundStatus.Rejected;
            refund.UpdatedAtUtc = DateTime.UtcNow;

            _db.RefundHistories.Add(new RefundHistory
            {
                RefundId = refund.Id,
                Status = RefundStatus.Rejected,
                Remarks = reason
            });

            await _db.SaveChangesAsync();
        }

        // The step that actually moves money. Two separate service calls, each with its own
        // transaction (FinanceLedgerService and CustomerWalletService each manage their own,
        // by design — see their own file comments) — so this isn't fully atomic across both.
        // Rather than one catch-all around both calls, each stage below has its own: a failure
        // in Stage 1 (ledger post) means nothing has moved yet, so a plain Failed is accurate.
        // A failure in Stage 2 (wallet credit) is a genuinely worse, different situation — the
        // operator's side is already adjusted while the customer hasn't been paid — so it gets
        // its own RefundStatus.ReconciliationNeeded instead of being folded into the same
        // Failed bucket as "nothing happened." A real production system would still want an
        // outbox/saga pattern to close this gap automatically; for now, ReconciliationNeeded is
        // a queryable flag (`WHERE Status = ReconciliationNeeded`) for staff or a nightly job to
        // find and reconcile by hand, rather than a fact buried inside a Failed row's remark text.
        //
        // Guest checkout (no CustomerProfile, so no wallet to credit) can't reach Succeeded
        // here at all — there's no payment-gateway refund integration yet, so the only real way
        // to pay a guest back today is a manual bank/mobile-banking transfer. Once the ledger
        // side is posted, a guest refund parks at PendingManualPayout until staff calls
        // CompleteManualPayoutAsync with proof the transfer actually happened.
        public async Task ProcessAsync(Guid refundId)
        {
            var refund = await _db.Refunds.FirstOrDefaultAsync(r => r.Id == refundId)
                ?? throw new InvalidOperationException($"Refund {refundId} does not exist.");

            if (refund.Status != RefundStatus.Approved)
            {
                throw new InvalidOperationException(
                    $"Refund {refundId} is {refund.Status}; only an Approved refund can be processed.");
            }

            var booking = await _db.Bookings.FirstOrDefaultAsync(b => b.Id == refund.BookingId)
                ?? throw new InvalidOperationException($"Booking for refund {refundId} no longer exists.");

            refund.Status = RefundStatus.Processing;
            _db.RefundHistories.Add(new RefundHistory
            {
                RefundId = refund.Id,
                Status = RefundStatus.Processing,
                Remarks = "Refund processing started."
            });
            await _db.SaveChangesAsync();

            // Stage 1 — post to the commission ledger. If this throws, nothing has moved yet:
            // a plain Failed status is fully accurate and there is nothing to reconcile.
            try
            {
                await _financeLedgerService.PostRefundAsync(
                    refund.BookingId, refund.Id, booking.BusOperatorId, refund.Amount, refund.Currency);
            }
            catch (Exception ex)
            {
                refund.Status = RefundStatus.Failed;
                _db.RefundHistories.Add(new RefundHistory
                {
                    RefundId = refund.Id,
                    Status = RefundStatus.Failed,
                    Remarks = $"Refund failed before any money moved (ledger post): {ex.Message}"
                });
                await _db.SaveChangesAsync();
                throw;
            }

            // Stage 2 — pay the customer back. The ledger side is already committed at this
            // point, so a failure here leaves the two sides out of sync rather than leaving
            // nothing moved — that's the case ReconciliationNeeded exists to flag.
            if (booking.CustomerProfileId.HasValue)
            {
                try
                {
                    await _customerWalletService.CreditAsync(
                        booking.CustomerProfileId.Value,
                        refund.Amount,
                        CustomerWalletTransactionType.RefundCredit,
                        bookingId: booking.Id,
                        refundId: refund.Id,
                        description: $"Refund for booking {booking.Pnr}",
                        currency: refund.Currency);

                    refund.Status = RefundStatus.Succeeded;
                    refund.RefundedAtUtc = DateTime.UtcNow;
                    _db.RefundHistories.Add(new RefundHistory
                    {
                        RefundId = refund.Id,
                        Status = RefundStatus.Succeeded,
                        Remarks = "Refund completed."
                    });
                }
                catch (Exception ex)
                {
                    refund.Status = RefundStatus.ReconciliationNeeded;
                    _db.RefundHistories.Add(new RefundHistory
                    {
                        RefundId = refund.Id,
                        Status = RefundStatus.ReconciliationNeeded,
                        Remarks = $"Ledger posted but wallet credit failed: {ex.Message}. " +
                            "Operator-side books are already adjusted; customer has not been paid. Needs manual reconciliation."
                    });
                    await _db.SaveChangesAsync();
                    throw;
                }
            }
            else
            {
                // Guest checkout: no wallet to credit and no gateway to push money back
                // through. The ledger side is posted, but the refund is NOT done yet — it
                // cannot silently reach Succeeded without a real manual payout on record.
                refund.Status = RefundStatus.PendingManualPayout;
                _db.RefundHistories.Add(new RefundHistory
                {
                    RefundId = refund.Id,
                    Status = RefundStatus.PendingManualPayout,
                    Remarks = "Ledger posted. Guest booking has no wallet — awaiting manual payout confirmation."
                });
            }

            await _db.SaveChangesAsync();
        }

        // The only way a guest refund (no CustomerProfile) can finish moving from
        // PendingManualPayout to Succeeded — requires proof staff actually paid the guest back
        // by hand, the same way PayoutProcessingService.CompleteAsync requires a real
        // BankTransactionReference before an operator payout counts as done.
        public async Task CompleteManualPayoutAsync(Guid refundId, string manualPayoutReference)
        {
            if (string.IsNullOrWhiteSpace(manualPayoutReference))
            {
                throw new InvalidOperationException("A manual payout reference is required to complete this refund.");
            }

            var refund = await _db.Refunds.FirstOrDefaultAsync(r => r.Id == refundId)
                ?? throw new InvalidOperationException($"Refund {refundId} does not exist.");

            if (refund.Status != RefundStatus.PendingManualPayout)
            {
                throw new InvalidOperationException(
                    $"Refund {refundId} is {refund.Status}; only a PendingManualPayout refund can be completed this way.");
            }

            var booking = await _db.Bookings.FirstOrDefaultAsync(b => b.Id == refund.BookingId)
                ?? throw new InvalidOperationException($"Booking for refund {refundId} no longer exists.");

            // Defense in depth: this field, and this path to Succeeded, only ever apply to a
            // guest booking — a booking with a CustomerProfile is paid back through the wallet
            // in ProcessAsync instead and should never reach PendingManualPayout in the first
            // place, but the guard stays cheap insurance against that invariant breaking later.
            if (booking.CustomerProfileId.HasValue)
            {
                throw new InvalidOperationException(
                    $"Booking for refund {refundId} has a CustomerProfile; manual payout does not apply.");
            }

            refund.Status = RefundStatus.Succeeded;
            refund.ManualPayoutReference = manualPayoutReference;
            refund.RefundedAtUtc = DateTime.UtcNow;
            refund.UpdatedAtUtc = DateTime.UtcNow;

            _db.RefundHistories.Add(new RefundHistory
            {
                RefundId = refund.Id,
                Status = RefundStatus.Succeeded,
                Remarks = $"Manual payout confirmed (reference: {manualPayoutReference})."
            });

            await _db.SaveChangesAsync();
        }
    }
}
