using TicketPortal.Api.Data;
using TicketPortal.Api.Models.Enums;
using TicketPortal.Api.Models.People;
using Microsoft.EntityFrameworkCore;

namespace TicketPortal.Api.Services
{
    // The only place in the codebase allowed to change CustomerProfile.WalletBalance. Every
    // top-up or spend goes through here, and every single change writes BOTH the new balance
    // AND a CustomerWalletTransaction row explaining it, in the same save — so the balance
    // number can always be double-checked by re-adding up that customer's transaction history.
    public class CustomerWalletService
    {
        private readonly AppDbContext _db;

        public CustomerWalletService(AppDbContext db)
        {
            _db = db;
        }

        // Add money to a customer's wallet (a top-up, or a refund paid back into the wallet
        // instead of the original payment method).
        public Task CreditAsync(Guid customerProfileId, decimal amount, CustomerWalletTransactionType type,
            Guid? bookingId = null, Guid? refundId = null, string? description = null, string currency = "BDT")
        {
            if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount), "Credit amount must be positive.");
            return ApplyAsync(customerProfileId, amount, type, bookingId, refundId, description, currency);
        }

        // Take money out of a customer's wallet (paying for a booking with wallet balance).
        public Task DebitAsync(Guid customerProfileId, decimal amount, CustomerWalletTransactionType type,
            Guid? bookingId = null, string? description = null, string currency = "BDT")
        {
            if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount), "Debit amount must be positive.");
            return ApplyAsync(customerProfileId, -amount, type, bookingId, null, description, currency);
        }

        private async Task ApplyAsync(
            Guid customerProfileId,
            decimal signedAmount,
            CustomerWalletTransactionType type,
            Guid? bookingId,
            Guid? refundId,
            string? description,
            string currency)
        {
            await using var transaction = await _db.Database.BeginTransactionAsync();

            // We check the balance first, then save it. That's a slightly less strict pattern
            // than SeatHoldService uses for seats — and that's fine here, because a wallet
            // debit only ever affects ONE customer's own row, not something many strangers are
            // all racing to grab at once the way a seat is. The worst case (two clicks from the
            // very same customer at almost the same instant) is rare and low-stakes compared to
            // double-selling a physical seat.
            // FirstOrDefaultAsync + explicit null check instead of SingleAsync(): SingleAsync
            // throws "Sequence contains no elements" (via InvalidOperationException) if the
            // CustomerProfileId doesn't exist, which the global handler still returns as a 400,
            // but with a message that doesn't say what actually went wrong. This gives a clear,
            // actionable message instead.
            var current = await _db.CustomerProfiles
                .Where(c => c.Id == customerProfileId)
                .Select(c => (decimal?)c.WalletBalance)
                .FirstOrDefaultAsync();

            if (current is null)
            {
                throw new InvalidOperationException($"CustomerProfile {customerProfileId} does not exist.");
            }

            var newBalance = current.Value + signedAmount;
            if (newBalance < 0)
            {
                throw new InvalidOperationException("Insufficient wallet balance.");
            }

            await _db.CustomerProfiles
                .Where(c => c.Id == customerProfileId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(c => c.WalletBalance, newBalance));

            // The paper trail: exactly why the balance just changed.
            _db.CustomerWalletTransactions.Add(new CustomerWalletTransaction
            {
                CustomerProfileId = customerProfileId,
                BookingId = bookingId,
                RefundId = refundId,
                TransactionType = type,
                Amount = signedAmount,
                BalanceAfter = newBalance,
                Currency = currency,
                Description = description
            });

            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
    }
}
