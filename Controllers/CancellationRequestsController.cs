using TicketPortal.Api.Data;
using TicketPortal.Api.DTO;
using TicketPortal.Api.Models.Bookings;
using TicketPortal.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace TicketPortal.Api.Controllers
{
    // Cancellations only ever move through CancellationProcessingService from here:
    // Create -> RequestAsync (prices the refund from the trip's real CancellationPolicy),
    // Approve/Reject are staff-only, and Complete closes the loop once the linked Refund has
    // actually succeeded through RefundsController. The old generic CRUD let a customer submit
    // a cancellation and approve their own refund amount in the same request.
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class CancellationRequestsController(
        AppDbContext db, CancellationProcessingService cancellationProcessingService) : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var query = db.CancellationRequests.AsQueryable();

            if (!User.IsInRole("Admin") && !User.IsInRole("Staff"))
            {
                var userId = GetCurrentUserId();
                query = query.Where(cr => db.Bookings.Any(b =>
                    b.Id == cr.BookingId && b.CustomerProfile != null && b.CustomerProfile.UserId == userId));
            }

            var items = await query.OrderByDescending(cr => cr.RequestedAtUtc).ToListAsync();
            return Ok(items.Select(ToResponseDto));
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(Guid id)
        {
            var item = await db.CancellationRequests.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null) return NotFound();
            if (!await CanAccessAsync(item)) return Forbid();
            return Ok(ToResponseDto(item));
        }

        // Customer-initiated (or staff, on the customer's behalf). RequestedByUserId always
        // comes from the caller's own claim, never the request body.
        [HttpPost]
        public async Task<IActionResult> Create(CancellationRequestCreateDto dto)
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff"))
            {
                var userId = GetCurrentUserId();
                var ownsBooking = await db.Bookings.AnyAsync(b =>
                    b.Id == dto.BookingId && b.CustomerProfile != null && b.CustomerProfile.UserId == userId);
                if (!ownsBooking) return Forbid();
            }

            try
            {
                var item = await cancellationProcessingService.RequestAsync(
                    dto.BookingId, dto.TicketId, GetCurrentUserId(), dto.Reason);

                return CreatedAtAction(nameof(GetById), new { id = item.Id }, ToResponseDto(item));
            }
            catch (CancellationConflictException ex)
            {
                return Conflict(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // Staff review gate — Requested -> Approved. Creates the linked Refund (at Requested);
        // that Refund only moves forward from RefundsController's own Approve/Process actions.
        [HttpPost("{id}/approve")]
        public async Task<IActionResult> Approve(Guid id, CancellationApproveDto dto)
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff")) return Forbid();

            try
            {
                await cancellationProcessingService.ApproveAsync(
                    id, GetCurrentUserId(), dto.ApprovedRefundAmount, dto.Remarks);

                var item = await db.CancellationRequests.FirstOrDefaultAsync(x => x.Id == id);
                return Ok(ToResponseDto(item!));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("{id}/reject")]
        public async Task<IActionResult> Reject(Guid id, CancellationRejectDto dto)
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff")) return Forbid();

            try
            {
                await cancellationProcessingService.RejectAsync(id, dto.RejectedReason);
                return NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // Closing step — only succeeds once the linked Refund has actually reached Succeeded
        // through RefundsController's own Approve/Process actions.
        [HttpPost("{id}/complete")]
        public async Task<IActionResult> Complete(Guid id)
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff")) return Forbid();

            try
            {
                await cancellationProcessingService.CompleteAsync(id);
                var item = await db.CancellationRequests.FirstOrDefaultAsync(x => x.Id == id);
                return Ok(ToResponseDto(item!));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // No generic PUT/DELETE on purpose — see the class comment above.

        private Guid? GetCurrentUserId()
        {
            var claim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(claim, out var id) ? id : null;
        }

        private async Task<bool> CanAccessAsync(CancellationRequest item)
        {
            if (User.IsInRole("Admin") || User.IsInRole("Staff")) return true;

            var userId = GetCurrentUserId();
            if (userId == null) return false;

            return await db.Bookings.AnyAsync(b =>
                b.Id == item.BookingId && b.CustomerProfile != null && b.CustomerProfile.UserId == userId);
        }

        private static CancellationRequestResponseDto ToResponseDto(CancellationRequest x) => new()
        {
            Id = x.Id,
            BookingId = x.BookingId,
            TicketId = x.TicketId,
            RequestedByUserId = x.RequestedByUserId,
            ApprovedByUserId = x.ApprovedByUserId,
            Status = x.Status,
            Reason = x.Reason,
            RejectedReason = x.RejectedReason,
            RequestedRefundAmount = x.RequestedRefundAmount,
            ApprovedRefundAmount = x.ApprovedRefundAmount,
            RequestedAtUtc = x.RequestedAtUtc,
            ApprovedAtUtc = x.ApprovedAtUtc,
            CompletedAtUtc = x.CompletedAtUtc,
            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc,
            RowVersion = x.RowVersion,
        };
    }
}
