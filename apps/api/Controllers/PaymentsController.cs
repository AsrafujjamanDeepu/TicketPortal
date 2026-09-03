using TicketPortal.Api.Data;
using TicketPortal.Api.DTO;
using TicketPortal.Api.Extensions;
using TicketPortal.Api.Models.Payments;
using TicketPortal.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace TicketPortal.Api.Controllers
{
    // Access is three-tiered, same pattern as RefundsController (Payment doesn't carry
    // BusOperatorId either, so scoping always joins through Booking.BusOperatorId): platform
    // Admin/Staff see every payment; an operator's own Staff/Operator account only sees
    // payments against that operator's own bookings; a plain Customer only sees their own.
    // CanAccessAsync previously granted ANY Staff account (and silently excluded the
    // "Operator" login role) full read/write access to every operator's gateway references,
    // fees, and amounts — fixed below via the shared ClaimsPrincipalExtensions helpers.
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class PaymentsController(AppDbContext db, PaymentConfirmationService paymentConfirmationService) : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var query = db.Payments.AsQueryable();

            if (User.IsInRole("Admin") || User.IsInRole("Staff") || User.IsInRole("Operator"))
            {
                var callerOperatorId = await User.GetBusOperatorIdAsync(db);
                if (callerOperatorId != null)
                {
                    query = query.Where(p => db.Bookings.Any(b =>
                        b.Id == p.BookingId && b.BusOperatorId == callerOperatorId));
                }
                // else: platform Admin/Staff — no filter, see everything.
            }
            else
            {
                var userId = GetCurrentUserId();
                query = query.Where(p => db.Bookings.Any(b =>
                    b.Id == p.BookingId && b.CustomerProfile != null && b.CustomerProfile.UserId == userId));
            }

            var items = await query.OrderByDescending(p => p.TransactionDateUtc).ToListAsync();
            return Ok(items.Select(ToResponseDto));
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(Guid id)
        {
            var item = await db.Payments.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null) return NotFound();
            if (!await CanAccessAsync(item)) return Forbid();
            return Ok(ToResponseDto(item));
        }

        // Step 3 of checkout: start a payment attempt. Amount always comes from the booking's
        // own GrandTotal, computed server-side inside PaymentConfirmationService — the old
        // version here let the client POST any Amount and any Status ("Succeeded") directly,
        // which was a straightforward free-ticket exploit.
        [HttpPost("initiate")]
        public async Task<IActionResult> Initiate(PaymentInitiateDto dto)
        {
            try
            {
                var payment = await paymentConfirmationService.InitiatePaymentAsync(
                    dto.BookingId, dto.HoldToken, dto.Method, dto.PaymentProviderId);

                return CreatedAtAction(nameof(GetById), new { id = payment.Id }, ToResponseDto(payment));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // Step 4: the gateway reports success. Converts the seat hold to the booking, issues
        // tickets, and posts the sale to the commission ledger — see PaymentConfirmationService.
        //
        // TODO before going live with a real gateway: this must verify the gateway's own
        // webhook signature (or be moved behind a signed return-URL/webhook handler) instead of
        // trusting whatever hits this endpoint. Until then, treat this as a stand-in for that
        // webhook, not as something safe to expose to arbitrary clients in production.
        [HttpPost("{id}/confirm")]
        public async Task<IActionResult> Confirm(Guid id, PaymentGatewayResultDto dto)
        {
            var payment = await db.Payments.FirstOrDefaultAsync(x => x.Id == id);
            if (payment == null) return NotFound();
            if (!await CanAccessAsync(payment)) return Forbid();

            try
            {
                var result = await paymentConfirmationService.ConfirmOnlinePaymentAsync(
                    id, dto.HoldToken, dto.GatewayTransactionId, dto.GatewayFeeAmount, dto.GatewayResponseJson);

                if (result.Outcome == PaymentConfirmationOutcome.PaidButSeatsLost)
                {
                    return Conflict(new
                    {
                        message = "Payment was received, but the held seats are no longer available. " +
                                  "A refund has automatically been requested.",
                        refundId = result.AutoRefund?.Id
                    });
                }

                return Ok(new
                {
                    payment = ToResponseDto(result.Payment),
                    bookingStatus = result.Booking?.Status,
                    ticketIds = result.Tickets.Select(t => t.Id),
                    ledgerWarning = result.LedgerWarning
                });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // Customer abandoned checkout, or the gateway reported failure — release the seats
        // immediately instead of making them wait out the full hold timer.
        [HttpPost("{id}/fail")]
        public async Task<IActionResult> Fail(Guid id, PaymentFailDto dto)
        {
            var payment = await db.Payments.FirstOrDefaultAsync(x => x.Id == id);
            if (payment == null) return NotFound();
            if (!await CanAccessAsync(payment)) return Forbid();

            try
            {
                await paymentConfirmationService.FailPaymentAsync(id, dto.HoldToken, dto.Reason);
                return NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // Counter-sale equivalent of Initiate+Confirm combined: staff at a physical counter
        // confirms cash (or a card) was collected in hand, right there — there's no gateway
        // round trip for this channel, so unlike the online flow, this is the only call needed.
        // Only valid against a Booking BookingsController.Create already created with
        // SalesCounterId set (SaleChannel.Counter) — see
        // PaymentConfirmationService.ConfirmCounterSaleAsync. Staff/Operator/Admin only — a
        // Customer has no reason to ever call this.
        [HttpPost("counter-sale/confirm")]
        public async Task<IActionResult> ConfirmCounterSale(CounterSaleConfirmDto dto)
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff") && !User.IsInRole("Operator"))
            {
                return Forbid();
            }

            var booking = await db.Bookings.FirstOrDefaultAsync(b => b.Id == dto.BookingId);
            if (booking == null) return NotFound(new { message = "Booking not found." });

            if (!await User.CanManageOperatorAsync(db, booking.BusOperatorId))
            {
                return Forbid();
            }

            try
            {
                var result = await paymentConfirmationService.ConfirmCounterSaleAsync(
                    dto.BookingId, dto.HoldToken, dto.Method);

                if (result.Outcome == PaymentConfirmationOutcome.PaidButSeatsLost)
                {
                    return Conflict(new
                    {
                        message = "Payment was collected, but the held seats are no longer available. " +
                                  "The platform never held this money — refund the customer directly at the counter."
                    });
                }

                return Ok(new
                {
                    payment = ToResponseDto(result.Payment),
                    bookingStatus = result.Booking?.Status,
                    ticketIds = result.Tickets.Select(t => t.Id),
                    ledgerWarning = result.LedgerWarning
                });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // No generic PUT/DELETE on purpose: a payment's Status only ever moves through
        // PaymentConfirmationService (Initiate → Confirm or Fail). Payments are never deleted —
        // that's a permanent financial record.

        private Guid? GetCurrentUserId()
        {
            var claim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(claim, out var id) ? id : null;
        }

        private async Task<bool> CanAccessAsync(Payment payment)
        {
            if (User.IsInRole("Admin") || User.IsInRole("Staff") || User.IsInRole("Operator"))
            {
                var operatorId = await db.Bookings
                    .Where(b => b.Id == payment.BookingId)
                    .Select(b => (Guid?)b.BusOperatorId)
                    .FirstOrDefaultAsync();
                return operatorId != null && await User.CanManageOperatorAsync(db, operatorId.Value);
            }

            var userId = GetCurrentUserId();
            if (userId == null) return false;

            return await db.Bookings.AnyAsync(b =>
                b.Id == payment.BookingId && b.CustomerProfile != null && b.CustomerProfile.UserId == userId);
        }

        private static PaymentResponseDto ToResponseDto(Payment x) => new()
        {
            Id = x.Id,
            BookingId = x.BookingId,
            PaymentProviderId = x.PaymentProviderId,
            Method = x.Method,
            Gateway = x.Gateway,
            CollectedBy = x.CollectedBy,
            GatewayTransactionId = x.GatewayTransactionId,
            MerchantInvoiceNumber = x.MerchantInvoiceNumber,
            Amount = x.Amount,
            GatewayFeeAmount = x.GatewayFeeAmount,
            NetReceivedAmount = x.NetReceivedAmount,
            Currency = x.Currency,
            Status = x.Status,
            TransactionDateUtc = x.TransactionDateUtc,
            PaidAtUtc = x.PaidAtUtc,
            FailedAtUtc = x.FailedAtUtc,
            GatewayResponseJson = x.GatewayResponseJson,
            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc,
            RowVersion = x.RowVersion,
        };
    }
}
