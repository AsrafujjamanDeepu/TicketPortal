using TicketPortal.Api.Data;
using TicketPortal.Api.Models.Bookings;
using TicketPortal.Api.Models.Enums;
using TicketPortal.Api.Models.Finance;
using TicketPortal.Api.Models.Payments;
using Microsoft.EntityFrameworkCore;

namespace TicketPortal.Api.Services
{
    public enum PaymentConfirmationOutcome
    {
        Confirmed,
        PaidButSeatsLost
    }

    public class PaymentConfirmationResult
    {
        public PaymentConfirmationOutcome Outcome { get; set; }
        public Payment Payment { get; set; } = default!;
        public Booking? Booking { get; set; }
        public List<Ticket> Tickets { get; set; } = new();
        public Refund? AutoRefund { get; set; }

        // Set only if the booking/ticket side fully succeeded but the ledger posting failed
        // (e.g. missing CommissionRule config). Never block a paying customer's ticket on a
        // finance configuration problem — surface it here instead so it can be reconciled.
        public string? LedgerWarning { get; set; }
    }

    // This is the ONLY place in the codebase allowed to call
    // SeatHoldService.ConvertHoldToBookingAsync and FinanceLedgerService.PostOnlineSaleAsync
    // for an online payment — together, every time — so a booking can never end up confirmed
    // without a ledger entry, or vice versa. Before this class existed, PaymentsController
    // wrote straight to the Payments table and never called either service.
    public class PaymentConfirmationService
    {
        private readonly AppDbContext _db;
        private readonly SeatHoldService _seatHoldService;
        private readonly FinanceLedgerService _financeLedgerService;

        public PaymentConfirmationService(
            AppDbContext db,
            SeatHoldService seatHoldService,
            FinanceLedgerService financeLedgerService)
        {
            _db = db;
            _seatHoldService = seatHoldService;
            _financeLedgerService = financeLedgerService;
        }

        // Step 3 of checkout: the customer picked a payment method for a booking that came
        // from an active hold. Amount is always taken from the booking's own GrandTotal —
        // never from the caller — so a request can't just declare its own price.
        //
        // Note: Booking doesn't currently get its SeatHoldId set by BookingsController.Create,
        // so we take the hold token directly from the caller (the front-end already has it in
        // the checkout session from the SeatHolds step) rather than looking it up via the
        // booking. We still cross-check it against the booking's TripId below.
        public async Task<Payment> InitiatePaymentAsync(
            Guid bookingId,
            string holdToken,
            PaymentMethod method,
            Guid? paymentProviderId)
        {
            var booking = await _db.Bookings.FirstOrDefaultAsync(b => b.Id == bookingId)
                ?? throw new InvalidOperationException($"Booking {bookingId} does not exist.");

            if (booking.Status != BookingStatus.PendingPayment && booking.Status != BookingStatus.Draft)
            {
                throw new InvalidOperationException(
                    $"Booking {bookingId} is {booking.Status} and can no longer accept a payment.");
            }

            var hold = await _db.SeatHolds.FirstOrDefaultAsync(h => h.HoldToken == holdToken)
                ?? throw new InvalidOperationException("This hold token is invalid.");

            if (hold.TripId != booking.TripId)
            {
                throw new InvalidOperationException("This hold does not belong to the same trip as the booking.");
            }

            var gateway = PaymentGateway.None;
            if (paymentProviderId.HasValue)
            {
                gateway = await _db.PaymentProviders
                    .Where(p => p.Id == paymentProviderId.Value)
                    .Select(p => p.Gateway)
                    .FirstOrDefaultAsync();
            }

            var payment = new Payment
            {
                BookingId = booking.Id,
                PaymentProviderId = paymentProviderId,
                Method = method,
                Gateway = gateway,
                CollectedBy = booking.MoneyCollectedBy,
                Amount = booking.GrandTotal,
                Currency = booking.Currency,
                Status = PaymentStatus.Initiated,
                TransactionDateUtc = DateTime.UtcNow,
            };

            _db.Payments.Add(payment);
            await _db.SaveChangesAsync();

            _db.PaymentHistories.Add(new PaymentHistory
            {
                PaymentId = payment.Id,
                Status = PaymentStatus.Initiated,
                Remarks = "Payment attempt started."
            });
            await _db.SaveChangesAsync();

            return payment;
        }

        // Step 4: the gateway (or, until a real one is wired in, whatever calls this — see the
        // TODO on PaymentsController.Confirm) says the money arrived. From here, three things
        // must happen together: the hold becomes a real booking, tickets get issued, and the
        // sale gets posted to the commission ledger.
        public async Task<PaymentConfirmationResult> ConfirmOnlinePaymentAsync(
            Guid paymentId,
            string holdToken,
            string? gatewayTransactionId,
            decimal gatewayFeeAmount,
            string? gatewayResponseJson)
        {
            var payment = await _db.Payments.FirstOrDefaultAsync(p => p.Id == paymentId)
                ?? throw new InvalidOperationException($"Payment {paymentId} does not exist.");

            if (payment.Status != PaymentStatus.Initiated && payment.Status != PaymentStatus.Pending)
            {
                throw new InvalidOperationException(
                    $"Payment {paymentId} is already {payment.Status}; it cannot be confirmed again.");
            }

            var booking = await _db.Bookings.FirstOrDefaultAsync(b => b.Id == payment.BookingId)
                ?? throw new InvalidOperationException($"Booking for payment {paymentId} no longer exists.");

            // Record the money as received first. This is the source-of-truth fact and stays
            // true even in the seat-loss branch below — the payment really did succeed; what
            // happens next is a separate question.
            await using (var tx = await _db.Database.BeginTransactionAsync())
            {
                payment.Status = PaymentStatus.Succeeded;
                payment.PaidAtUtc = DateTime.UtcNow;
                payment.GatewayTransactionId = gatewayTransactionId;
                payment.GatewayFeeAmount = gatewayFeeAmount;
                payment.NetReceivedAmount = payment.Amount - gatewayFeeAmount;
                payment.GatewayResponseJson = gatewayResponseJson;
                payment.UpdatedAtUtc = DateTime.UtcNow;

                _db.PaymentHistories.Add(new PaymentHistory
                {
                    PaymentId = payment.Id,
                    Status = PaymentStatus.Succeeded,
                    Remarks = "Gateway confirmed payment."
                });

                await _db.SaveChangesAsync();
                await tx.CommitAsync();
            }

            var result = new PaymentConfirmationResult { Payment = payment, Booking = booking };

            try
            {
                await _seatHoldService.ConvertHoldToBookingAsync(holdToken, booking.Id);
            }
            catch (InvalidOperationException ex)
            {
                // Matches the rule already documented on SeatHoldService: money in, seats gone
                // = refund case, never silently ignored. We stop here — no tickets, no ledger
                // entry — for a booking that has no real seats behind it.
                var refund = new Refund
                {
                    BookingId = booking.Id,
                    PaymentId = payment.Id,
                    Amount = payment.Amount,
                    Currency = payment.Currency,
                    Status = RefundStatus.Requested,
                    Reason = $"Automatic refund: seats were lost before payment confirmation completed ({ex.Message})",
                    RequestedAtUtc = DateTime.UtcNow,
                };
                _db.Refunds.Add(refund);
                await _db.SaveChangesAsync();

                result.Outcome = PaymentConfirmationOutcome.PaidButSeatsLost;
                result.AutoRefund = refund;
                return result;
            }

            // Seats are confirmed as this booking's — now lock in the booking itself and issue
            // one ticket per passenger.
            await using (var tx = await _db.Database.BeginTransactionAsync())
            {
                booking.Confirm(); // Booking's own method — throws if it's not in a confirmable state.

                // BookingPassenger and TripSeat don't have a direct FK to each other in this
                // schema (BookingCreateDto doesn't collect a per-passenger seat choice either),
                // so passengers are paired to this booking's now-Booked seats in a fixed,
                // deterministic order. Fine for the current demo-level checkout; a real
                // "passenger picks seat X" flow would need an explicit link instead.
                var passengers = await _db.BookingPassengers
                    .Where(p => p.BookingId == booking.Id)
                    .OrderBy(p => p.CreatedAtUtc)
                    .ToListAsync();

                var bookedSeats = await _db.TripSeats
                    .Where(ts => ts.BookingId == booking.Id)
                    .OrderBy(ts => ts.SeatNumber)
                    .ToListAsync();

                if (passengers.Count != bookedSeats.Count)
                {
                    throw new InvalidOperationException(
                        $"Booking {booking.Id} has {passengers.Count} passenger(s) but {bookedSeats.Count} " +
                        "booked seat(s) — cannot safely pair them into tickets.");
                }

                var tickets = new List<Ticket>();
                for (int i = 0; i < passengers.Count; i++)
                {
                    var seat = bookedSeats[i];
                    tickets.Add(new Ticket
                    {
                        BookingId = booking.Id,
                        BookingPassengerId = passengers[i].Id,
                        TripId = booking.TripId,
                        TripSeatId = seat.Id,
                        TicketNumber = GenerateTicketNumber(),
                        SeatNumberSnapshot = seat.SeatNumber,
                        QrCodePayload = $"{booking.Pnr}|{seat.SeatNumber}|{Guid.NewGuid():N}",
                        Fare = seat.Fare,
                        DiscountAmount = 0m,
                        FinalFare = seat.Fare,
                        Status = TicketStatus.Issued,
                        IssuedAtUtc = DateTime.UtcNow,
                    });
                }

                _db.Tickets.AddRange(tickets);
                await _db.SaveChangesAsync();
                await tx.CommitAsync();

                result.Tickets = tickets;
            }

            result.Outcome = PaymentConfirmationOutcome.Confirmed;

            // Ledger posting last, isolated in its own try/catch. A missing commission-rule
            // configuration is a real problem, but it must never be the reason a customer who
            // has genuinely paid walks away without a ticket — flag it for finance to fix by
            // hand instead of blocking checkout.
            try
            {
                var (commission, gatewayFeeBearer) = await ResolveCommissionAsync(booking);
                await _financeLedgerService.PostOnlineSaleAsync(
                    booking.Id,
                    booking.BusOperatorId,
                    payment.Amount,
                    commission,
                    payment.GatewayFeeAmount,
                    gatewayFeeBearer,
                    payment.Currency);
            }
            catch (Exception ex)
            {
                result.LedgerWarning =
                    $"Ticket issued and booking confirmed, but the commission ledger entry failed: " +
                    $"{ex.Message}. Needs manual reconciliation.";
            }

            return result;
        }

        // Customer abandoned checkout, or the gateway reported failure — free the seats right
        // away instead of making the next customer wait out the full hold timer.
        public async Task FailPaymentAsync(Guid paymentId, string holdToken, string? reason)
        {
            var payment = await _db.Payments.FirstOrDefaultAsync(p => p.Id == paymentId)
                ?? throw new InvalidOperationException($"Payment {paymentId} does not exist.");

            if (payment.Status != PaymentStatus.Initiated && payment.Status != PaymentStatus.Pending)
            {
                throw new InvalidOperationException(
                    $"Payment {paymentId} is already {payment.Status}; it cannot be failed.");
            }

            payment.Status = PaymentStatus.Failed;
            payment.FailedAtUtc = DateTime.UtcNow;
            payment.UpdatedAtUtc = DateTime.UtcNow;

            _db.PaymentHistories.Add(new PaymentHistory
            {
                PaymentId = payment.Id,
                Status = PaymentStatus.Failed,
                Remarks = reason ?? "Payment failed."
            });

            await _db.SaveChangesAsync();
            await _seatHoldService.ReleaseHoldAsync(holdToken);
        }

        // Finds the operator's active online-sale commission rule (preferring one scoped to
        // this trip's specific route over a general one) and their contract's gateway-fee-bearer
        // setting. Throws if nothing is configured — see the try/catch around the caller for why
        // that's safe to do this late in the flow.
        private async Task<(decimal commission, GatewayFeeBearer feeBearer)> ResolveCommissionAsync(Booking booking)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);

            var busRouteId = await _db.Trips
                .Where(t => t.Id == booking.TripId)
                .Select(t => t.BusRouteId)
                .FirstOrDefaultAsync();

            var candidates = await _db.CommissionRules
                .Where(cr => cr.BusOperatorId == booking.BusOperatorId
                    && cr.SaleChannel == SaleChannel.Online
                    && cr.IsActive
                    && cr.EffectiveFrom <= today
                    && (cr.EffectiveTo == null || cr.EffectiveTo >= today))
                .ToListAsync();

            var rule = candidates.FirstOrDefault(cr => cr.BusRouteId == busRouteId)
                ?? candidates.FirstOrDefault(cr => cr.BusRouteId == null)
                ?? throw new InvalidOperationException(
                    $"No active online CommissionRule configured for operator {booking.BusOperatorId}.");

            var commission = rule.CommissionType switch
            {
                CommissionType.Percentage => Math.Round(booking.GrandTotal * (rule.CommissionValue / 100m), 2),
                CommissionType.FixedAmount => rule.CommissionValue,
                _ => 0m
            };

            var feeBearer = await _db.OperatorContracts
                .Where(c => c.BusOperatorId == booking.BusOperatorId && c.IsActive)
                .Select(c => (GatewayFeeBearer?)c.GatewayFeeBearer)
                .FirstOrDefaultAsync() ?? GatewayFeeBearer.Platform;

            return (commission, feeBearer);
        }

        private static string GenerateTicketNumber() =>
            "TKT" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
    }
}
