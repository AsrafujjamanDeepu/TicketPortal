using TicketPortal.Api.Data;
using TicketPortal.Api.DTO;
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
    // Approve → Process, or stops at Reject — never a raw Status edit.
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class RefundsController(AppDbContext db, RefundProcessingService refundProcessingService) : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var query = db.Refunds.AsQueryable();

            if (!User.IsInRole("Admin") && !User.IsInRole("Staff"))
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
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff")) return Forbid();

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
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff")) return Forbid();

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
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff")) return Forbid();

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

        // No generic POST/PUT/DELETE on purpose — see the class comment above.

        private Guid? GetCurrentUserId()
        {
            var claim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(claim, out var id) ? id : null;
        }

        private async Task<bool> CanAccessAsync(Refund refund)
        {
            if (User.IsInRole("Admin") || User.IsInRole("Staff")) return true;

            var userId = GetCurrentUserId();
            if (userId == null) return false;

            return await db.Bookings.AnyAsync(b =>
                b.Id == refund.BookingId && b.CustomerProfile != null && b.CustomerProfile.UserId == userId);
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
            RequestedAtUtc = x.RequestedAtUtc,
            RefundedAtUtc = x.RefundedAtUtc,
            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc,
            RowVersion = x.RowVersion,
        };
    }
}
