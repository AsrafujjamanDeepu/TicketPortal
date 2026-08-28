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
    // Refunds are never created directly through this API — PaymentConfirmationService
    // creates one automatically when held seats are lost after payment (see
    // Services/PaymentConfirmationService.cs), and the CancellationRequest workflow will be
    // the other source once that's wired up. From there, a refund only moves forward through
    // Approve → Process, or stops at Reject — never a raw Status edit. A guest booking's
    // refund (no CustomerProfile to credit) makes one extra stop at PendingManualPayout after
    // Process, and only leaves it via ManualPayout.
    //
    // Access is three-tiered (Piece 1): platform Admin/Staff (no StaffProfile.BusOperatorId,
    // or Admin) see every refund; an operator's own Staff/Operator account only sees refunds
    // against that operator's own bookings; a plain Customer only sees refunds against their
    // own bookings. Refund itself doesn't carry BusOperatorId, so operator-scoping always
    // joins through Booking.BusOperatorId — see CanAccessAsync/GetAll below.
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class RefundsController(AppDbContext db, RefundProcessingService refundProcessingService) : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var query = db.Refunds.AsQueryable();

            if (User.IsInRole("Admin") || User.IsInRole("Staff") || User.IsInRole("Operator"))
            {
                var callerOperatorId = await User.GetBusOperatorIdAsync(db);
                if (callerOperatorId != null)
                {
                    query = query.Where(r => db.Bookings.Any(b =>
                        b.Id == r.BookingId && b.BusOperatorId == callerOperatorId));
                }
                // else: platform Admin/Staff — no filter, see everything.
            }
            else
            {
                var userId = GetCurrentUserId();
                query = query.Where(r => db.Bookings.Any(b =>
                    b.Id == r.BookingId && b.CustomerProfile != null && b.CustomerProfile.UserId == userId));
            }

            var items = await query.OrderByDescending(r => r.RequestedAtUtc).ToListAsync();
            return Ok(items.Select(ToResponseDto));
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(Guid id)
        {
            var item = await db.Refunds.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null) return NotFound();
            if (!await CanAccessAsync(item)) return Forbid();
            return Ok(ToResponseDto(item));
        }

        // Staff review gate — Requested -> Approved. No money moves yet.
        [HttpPost("{id}/approve")]
        public async Task<IActionResult> Approve(Guid id, RefundApproveDto dto)
        {
            if (!await CanManageAsync(id)) return Forbid();

            try
            {
                await refundProcessingService.ApproveAsync(id, dto.Remarks);
                return NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("{id}/reject")]
        public async Task<IActionResult> Reject(Guid id, RefundRejectDto dto)
        {
            if (!await CanManageAsync(id)) return Forbid();

            try
            {
                await refundProcessingService.RejectAsync(id, dto.Reason);
                return NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // Approved -> actually moves money: posts to the commission ledger and credits the
        // customer's wallet. This is the one action on this controller that used to be a raw
        // PUT letting any client just declare Status = Succeeded with a made-up
        // GatewayRefundReference — no money ever moved.
        [HttpPost("{id}/process")]
        public async Task<IActionResult> Process(Guid id)
        {
            if (!await CanManageAsync(id)) return Forbid();

            try
            {
                await refundProcessingService.ProcessAsync(id);
                var item = await db.Refunds.FirstOrDefaultAsync(x => x.Id == id);
                return Ok(ToResponseDto(item!));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // The only way a guest refund (no CustomerProfile, so ProcessAsync above leaves it at
        // PendingManualPayout instead of Succeeded) can finish — staff confirms the guest was
        // actually paid back by hand and records proof, the same way OperatorPayoutsController
        // requires a real BankTransactionReference before a payout counts as done.
        [HttpPost("{id}/manual-payout")]
        public async Task<IActionResult> CompleteManualPayout(Guid id, RefundManualPayoutDto dto)
        {
            if (!await CanManageAsync(id)) return Forbid();

            try
            {
                await refundProcessingService.CompleteManualPayoutAsync(id, dto.ManualPayoutReference);
                var item = await db.Refunds.FirstOrDefaultAsync(x => x.Id == id);
                return Ok(ToResponseDto(item!));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // No generic POST/PUT/DELETE on purpose — see the class comment above.

        private Guid? GetCurrentUserId()
        {
            var claim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(claim, out var id) ? id : null;
        }

        // Refund doesn't carry BusOperatorId itself — resolves it via the parent Booking so
        // every other check here can go through the one shared CanManageOperatorAsync.
        private async Task<Guid?> GetOperatorIdAsync(Guid bookingId)
        {
            return await db.Bookings
                .Where(b => b.Id == bookingId)
                .Select(b => (Guid?)b.BusOperatorId)
                .FirstOrDefaultAsync();
        }

        // Read access (GetAll/GetById): Admin/platform-Staff see everything, an operator's own
        // Staff/Operator sees only that operator's refunds, a Customer sees only their own.
        private async Task<bool> CanAccessAsync(Refund refund)
        {
            if (User.IsInRole("Admin") || User.IsInRole("Staff") || User.IsInRole("Operator"))
            {
                var operatorId = await GetOperatorIdAsync(refund.BookingId);
                return operatorId != null && await User.CanManageOperatorAsync(db, operatorId.Value);
            }

            var userId = GetCurrentUserId();
            if (userId == null) return false;

            return await db.Bookings.AnyAsync(b =>
                b.Id == refund.BookingId && b.CustomerProfile != null && b.CustomerProfile.UserId == userId);
        }

        // Action endpoints (Approve/Reject/Process/CompleteManualPayout) are staff-only —
        // a Customer never reaches this regardless of whose refund it is. Resolves the refund's
        // Booking.BusOperatorId first, then defers to the same shared CanManageOperatorAsync
        // used everywhere else, so both problems close through one mechanism (Piece 2).
        private async Task<bool> CanManageAsync(Guid refundId)
        {
            var bookingId = await db.Refunds
                .Where(r => r.Id == refundId)
                .Select(r => (Guid?)r.BookingId)
                .FirstOrDefaultAsync();
            if (bookingId == null) return false;

            var operatorId = await GetOperatorIdAsync(bookingId.Value);
            return operatorId != null && await User.CanManageOperatorAsync(db, operatorId.Value);
        }

        private static RefundResponseDto ToResponseDto(Refund x) => new()
        {
            Id = x.Id,
            BookingId = x.BookingId,
            PaymentId = x.PaymentId,
            CancellationRequestId = x.CancellationRequestId,
            Amount = x.Amount,
            Currency = x.Currency,
            Status = x.Status,
            Reason = x.Reason,
            GatewayRefundReference = x.GatewayRefundReference,
            ManualPayoutReference = x.ManualPayoutReference,
            RequestedAtUtc = x.RequestedAtUtc,
            RefundedAtUtc = x.RefundedAtUtc,
            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc,
            RowVersion = x.RowVersion,
        };
    }
}
