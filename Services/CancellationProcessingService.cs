using TicketPortal.Api.Data;
using TicketPortal.Api.Models.Bookings;
using TicketPortal.Api.Models.Enums;
using TicketPortal.Api.Models.Payments;
using TicketPortal.Api.Models.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace TicketPortal.Api.Services
{
    // Thrown when a request would step on an already-in-progress cancellation for the same
    // booking/ticket. Mirrors SeatsUnavailableException's role in SeatHoldService: a
    // predictable "someone already did this" case that a controller should turn into 409,
    // not the generic 400 used for plain validation failures.
    public class CancellationConflictException : Exception
    {
        public CancellationConflictException(string message) : base(message) { }
    }

    // Orchestrates a CancellationRequest from Requested through Approved/Rejected to
    // Completed. Before this existed, CancellationRequestsController let a customer submit a
    // cancellation AND set their own ApprovedRefundAmount/Status in the same raw POST — this
    // is what makes an approval actually mean "staff reviewed it, and the refund amount came
    // from the real CancellationPolicy", the same way RefundProcessingService did for refunds
    // themselves.
    //
    // On approval this creates the Refund row and stops — it deliberately does NOT call
    // RefundProcessingService.ApproveAsync/ProcessAsync itself. Refunds still only move
    // through RefundsController's own Approve/Process actions, exactly like the Refund that
    // PaymentConfirmationService creates automatically when held seats are lost after
    // payment (see Services/PaymentConfirmationService.cs) — one single place creates a
    // Refund at Requested, and RefundProcessingService is the only thing that ever advances
    // it from there. That keeps this service from duplicating any of that logic.
    public class CancellationProcessingService
    {
        private readonly AppDbContext _db;

        public CancellationProcessingService(AppDbContext db)
        {
            _db = db;
        }

        // Customer-initiated (or staff, on the customer's behalf) — ties the request to the
        // trip's real CancellationPolicy and computes the refund percentage/fee from how close
        // to departure this is, rather than trusting whatever amount the client sends.
        public async Task<CancellationRequest> RequestAsync(
            Guid bookingId, Guid? ticketId, Guid? requestedByUserId, string reason)
        {
            var booking = await _db.Bookings
                .Include(b => b.Tickets)
                .FirstOrDefaultAsync(b => b.Id == bookingId)
                ?? throw new InvalidOperationException($"Booking {bookingId} does not exist.");

            Ticket? ticket = null;
            if (ticketId.HasValue)
            {
                ticket = booking.Tickets.FirstOrDefault(t => t.Id == ticketId.Value)
                    ?? throw new InvalidOperationException(
                        $"Ticket {ticketId} does not belong to booking {bookingId}.");

                if (ticket.Status is TicketStatus.Cancelled or TicketStatus.Refunded)
                {
                    throw new InvalidOperationException(
                        $"Ticket {ticketId} is already {ticket.Status} and cannot be cancelled again.");
                }
            }
            else if (booking.Status is BookingStatus.Cancelled or BookingStatus.Refunded or BookingStatus.Completed)
            {
                throw new InvalidOperationException(
                    $"Booking {bookingId} is {booking.Status} and cannot be cancelled.");
            }

            // Without this, a customer could spam duplicate requests for the same
            // booking/ticket while an earlier one is still Requested/Approved.
            var alreadyOpen = await _db.CancellationRequests.AnyAsync(cr =>
                cr.BookingId == bookingId
                && (ticketId == null ? cr.TicketId == null : cr.TicketId == ticketId)
                && (cr.Status == CancellationRequestStatus.Requested || cr.Status == CancellationRequestStatus.Approved));

            if (alreadyOpen)
            {
                throw new CancellationConflictException(
                    "A cancellation request for this booking/ticket is already in progress.");
            }

            var trip = await _db.Trips.FirstOrDefaultAsync(t => t.Id == booking.TripId)
                ?? throw new InvalidOperationException($"Trip for booking {bookingId} no longer exists.");

            if (trip.DepartureTimeUtc <= DateTime.UtcNow)
            {
                throw new InvalidOperationException(
                    "This trip has already departed; it can no longer be cancelled.");
            }

            var policy = await ResolveCancellationPolicyAsync(trip);
            var hoursBeforeDeparture = (trip.DepartureTimeUtc - DateTime.UtcNow).TotalHours;

            // Pick the tightest-fitting tier (closest to departure that still qualifies), the
            // same way a "cancel within 24h = X%, within 72h = Y%" ladder is meant to be read.
            var rule = policy?.Rules
                .Where(r => hoursBeforeDeparture >= r.MinHoursBeforeDeparture
                    && (r.MaxHoursBeforeDeparture == null || hoursBeforeDeparture <= r.MaxHoursBeforeDeparture))
                .OrderByDescending(r => r.MinHoursBeforeDeparture)
                .FirstOrDefault();

            var baseAmount = ticket?.FinalFare ?? booking.GrandTotal;

            // No policy configured at all, or no rule covers this window, both fall back to a
            // 0% refund rather than blocking the request outright — staff can still override
            // the amount at Approve time (e.g. a goodwill exception), so this only affects the
            // default a customer sees, never the final say.
            var requestedRefundAmount = rule == null
                ? 0m
                : Math.Max(0m, baseAmount * (rule.RefundPercentage / 100m) - rule.FixedCancellationFee);

            var cancellationRequest = new CancellationRequest
            {
                BookingId = bookingId,
                TicketId = ticketId,
                RequestedByUserId = requestedByUserId,
                Status = CancellationRequestStatus.Requested,
                Reason = reason,
                RequestedRefundAmount = requestedRefundAmount,
                RequestedAtUtc = DateTime.UtcNow,
            };

            _db.CancellationRequests.Add(cancellationRequest);
            await _db.SaveChangesAsync();

            return cancellationRequest;
        }

        // Staff review step. Sets the cancellation itself to Approved, reflects the
        // cancellation on the Ticket/Booking, and creates the Refund row — but does not touch
        // the Refund's own Approve/Process flow, which stays RefundsController's job.
        public async Task ApproveAsync(
            Guid cancellationRequestId, Guid? approvedByUserId, decimal? approvedRefundAmountOverride, string? remarks)
        {
            var cr = await _db.CancellationRequests
                .Include(c => c.Booking).ThenInclude(b => b.Tickets)
                .FirstOrDefaultAsync(c => c.Id == cancellationRequestId)
                ?? throw new InvalidOperationException($"CancellationRequest {cancellationRequestId} does not exist.");

            if (cr.Status != CancellationRequestStatus.Requested)
            {
                throw new InvalidOperationException(
                    $"CancellationRequest {cancellationRequestId} is {cr.Status}; only a Requested cancellation can be approved.");
            }

            var booking = cr.Booking;
            var ceiling = cr.TicketId.HasValue
                ? booking.Tickets.FirstOrDefault(t => t.Id == cr.TicketId.Value)?.FinalFare ?? 0m
                : booking.GrandTotal;

            var approvedAmount = approvedRefundAmountOverride ?? cr.RequestedRefundAmount;
            if (approvedAmount < 0 || approvedAmount > ceiling)
            {
                throw new InvalidOperationException(
                    $"Approved refund amount must be between 0 and {ceiling} for this " +
                    $"{(cr.TicketId.HasValue ? "ticket" : "booking")}.");
            }

            var payment = await _db.Payments
                .Where(p => p.BookingId == booking.Id && p.Status == PaymentStatus.Succeeded)
                .OrderByDescending(p => p.PaidAtUtc)
                .FirstOrDefaultAsync()
                ?? throw new InvalidOperationException(
                    $"No successful payment found for booking {booking.Id}; nothing to refund.");

            cr.Status = CancellationRequestStatus.Approved;
            cr.ApprovedByUserId = approvedByUserId;
            cr.ApprovedRefundAmount = approvedAmount;
            cr.ApprovedAtUtc = DateTime.UtcNow;
            cr.UpdatedAtUtc = DateTime.UtcNow;

            // Reflect the cancellation on the ticket/booking themselves — this is what actually
            // takes the seat out of "sold" state from the customer's point of view.
            //
            // Deliberately NOT touching TripSeat.Status here: SeatHoldService's own header
            // comment states it is the ONLY place in the codebase allowed to change that field,
            // and none of its existing methods cover "release a seat that was already converted
            // to a paid Booking" — only hold-time transitions (Available/Held/Booked/Expired).
            // Until a method for that exists there, a cancelled/refunded ticket's seat stays
            // marked Booked and won't be reoffered for resale. Flagged as a follow-up for
            // whoever extends SeatHoldService next, rather than reaching around that invariant
            // from here.
            if (cr.TicketId.HasValue)
            {
                var ticket = booking.Tickets.First(t => t.Id == cr.TicketId.Value);
                ticket.Status = TicketStatus.Cancelled;
                ticket.CancelledAtUtc = DateTime.UtcNow;

                var allCancelled = booking.Tickets.All(t => t.Status == TicketStatus.Cancelled);
                if (allCancelled)
                {
                    booking.Status = BookingStatus.Cancelled;
                    booking.CancelledAtUtc = DateTime.UtcNow;
                    booking.CancellationReason = cr.Reason;
                }
                else if (booking.Status != BookingStatus.Cancelled)
                {
                    booking.Status = BookingStatus.PartiallyCancelled;
                }
            }
            else
            {
                foreach (var t in booking.Tickets.Where(t => t.Status != TicketStatus.Cancelled))
                {
                    t.Status = TicketStatus.Cancelled;
                    t.CancelledAtUtc = DateTime.UtcNow;
                }

                booking.Status = BookingStatus.Cancelled;
                booking.CancelledAtUtc = DateTime.UtcNow;
                booking.CancellationReason = cr.Reason;
            }

            // Hand off to RefundProcessingService's world from here on — this only creates the
            // Refund at Requested, exactly like PaymentConfirmationService's own automatic-
            // refund path does. Approve/Process (and the RefundHistory trail that comes with
            // them) stay RefundProcessingService's job; this service never duplicates that logic.
            var refund = new Refund
            {
                BookingId = booking.Id,
                PaymentId = payment.Id,
                CancellationRequestId = cr.Id,
                Amount = approvedAmount,
                Currency = payment.Currency,
                Status = RefundStatus.Requested,
                Reason = string.IsNullOrWhiteSpace(remarks)
                    ? $"Cancellation approved: {cr.Reason}"
                    : $"Cancellation approved: {cr.Reason} ({remarks})",
                RequestedAtUtc = DateTime.UtcNow,
            };

            _db.Refunds.Add(refund);

            await _db.SaveChangesAsync();
        }

        public async Task RejectAsync(Guid cancellationRequestId, string rejectedReason)
        {
            var cr = await _db.CancellationRequests.FirstOrDefaultAsync(c => c.Id == cancellationRequestId)
                ?? throw new InvalidOperationException($"CancellationRequest {cancellationRequestId} does not exist.");

            if (cr.Status != CancellationRequestStatus.Requested)
            {
                throw new InvalidOperationException(
                    $"CancellationRequest {cancellationRequestId} is {cr.Status}; only a Requested cancellation can be rejected.");
            }

            cr.Status = CancellationRequestStatus.Rejected;
            cr.RejectedReason = rejectedReason;
            cr.UpdatedAtUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync();
        }

        // Explicit closing step, called once staff have taken the linked Refund all the way to
        // Succeeded through RefundsController's own Approve/Process actions. Kept as its own
        // step (rather than something RefundProcessingService.ProcessAsync triggers
        // automatically) because that service is shared, already-shipped code — see its own
        // file — and isn't touched here to reach back into this workflow.
        public async Task CompleteAsync(Guid cancellationRequestId)
        {
            var cr = await _db.CancellationRequests.FirstOrDefaultAsync(c => c.Id == cancellationRequestId)
                ?? throw new InvalidOperationException($"CancellationRequest {cancellationRequestId} does not exist.");

            if (cr.Status != CancellationRequestStatus.Approved)
            {
                throw new InvalidOperationException(
                    $"CancellationRequest {cancellationRequestId} is {cr.Status}; only an Approved cancellation can be completed.");
            }

            var refundSucceeded = await _db.Refunds.AnyAsync(r =>
                r.CancellationRequestId == cr.Id && r.Status == RefundStatus.Succeeded);

            if (!refundSucceeded)
            {
                throw new InvalidOperationException(
                    "The linked refund has not succeeded yet — approve and process it through RefundsController first.");
            }

            cr.Status = CancellationRequestStatus.Completed;
            cr.CompletedAtUtc = DateTime.UtcNow;
            cr.UpdatedAtUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync();
        }

        private async Task<CancellationPolicy?> ResolveCancellationPolicyAsync(Trip trip)
        {
            if (trip.CancellationPolicyId.HasValue)
            {
                var tripPolicy = await _db.CancellationPolicies
                    .Include(p => p.Rules)
                    .FirstOrDefaultAsync(p => p.Id == trip.CancellationPolicyId.Value && p.IsActive);

                if (tripPolicy != null) return tripPolicy;
            }

            // Fall back to the platform-wide default (BusOperatorId == null) if the trip
            // doesn't have its own policy, or its own has since been deactivated.
            return await _db.CancellationPolicies
                .Include(p => p.Rules)
                .Where(p => p.BusOperatorId == null && p.IsActive)
                .OrderByDescending(p => p.CreatedAtUtc)
                .FirstOrDefaultAsync();
        }
    }
}
