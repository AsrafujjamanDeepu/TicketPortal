using Microsoft.EntityFrameworkCore;
using TicketPortal.Api.Data;
using TicketPortal.Api.Models.Enums;
using TicketPortal.Api.Models.Payments;
using TicketPortal.Api.Services;
using TicketPortal.Api.Tests.Infrastructure;
using Xunit;

namespace TicketPortal.Api.Tests.Integration;

[Collection(SharedApiCollection.Name)]
public sealed class RefundCapTests(TicketPortalWebApplicationFactory factory)
{
    [Fact]
    public async Task Approval_RejectsCumulativeRefundsAboveCapturedPayment()
    {
        Guid paymentId;
        Guid bookingId;
        decimal capturedAmount;
        var firstRefundId = Guid.NewGuid();
        var secondRefundId = Guid.NewGuid();

        using (var scope = factory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var payment = await db.Payments
                .Where(item => item.Status == PaymentStatus.Succeeded && item.Amount >= 100m)
                .Where(item => !db.Refunds.Any(refund => refund.PaymentId == item.Id))
                .Select(item => new { item.Id, item.BookingId, item.Amount })
                .FirstAsync();
            paymentId = payment.Id;
            bookingId = payment.BookingId;
            capturedAmount = payment.Amount;

            db.Refunds.Add(NewRefund(firstRefundId, bookingId, paymentId, decimal.Round(capturedAmount * 0.60m, 2)));
            await db.SaveChangesAsync();
        }

        try
        {
            using var scope = factory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var service = scope.ServiceProvider.GetRequiredService<RefundProcessingService>();

            await service.ApproveAsync(firstRefundId, "first reservation");

            db.Refunds.Add(NewRefund(secondRefundId, bookingId, paymentId, decimal.Round(capturedAmount * 0.50m, 2)));
            await db.SaveChangesAsync();
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.ApproveAsync(secondRefundId, "exceeds captured amount"));
            Assert.Contains("captured", exception.Message, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(RefundStatus.Approved,
                await db.Refunds.Where(item => item.Id == firstRefundId).Select(item => item.Status).SingleAsync());
            Assert.Equal(RefundStatus.Requested,
                await db.Refunds.Where(item => item.Id == secondRefundId).Select(item => item.Status).SingleAsync());
        }
        finally
        {
            using var cleanupScope = factory.CreateScope();
            var cleanupDb = cleanupScope.ServiceProvider.GetRequiredService<AppDbContext>();
            cleanupDb.RefundHistories.RemoveRange(cleanupDb.RefundHistories.Where(history =>
                history.RefundId == firstRefundId || history.RefundId == secondRefundId));
            cleanupDb.Refunds.RemoveRange(cleanupDb.Refunds.Where(refund =>
                refund.Id == firstRefundId || refund.Id == secondRefundId));
            await cleanupDb.SaveChangesAsync();
        }
    }

    private static Refund NewRefund(Guid id, Guid bookingId, Guid paymentId, decimal amount) => new()
    {
        Id = id,
        BookingId = bookingId,
        PaymentId = paymentId,
        Amount = amount,
        Status = RefundStatus.Requested,
        Reason = "Refund cap integration test",
        Currency = "BDT",
    };
}
