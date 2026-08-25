using TicketPortal.Api.Data;
using TicketPortal.Api.DTO;
using TicketPortal.Api.Models.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace TicketPortal.Api.Controllers
{
    // Read-only. This is the append-only trail of a Refund's own status changes — written
    // exclusively by RefundProcessingService (see Services/RefundProcessingService.cs), the
    // same way PaymentHistory tracks a Payment. A trail that a client can also POST/PUT/DELETE
    // to isn't a trail.
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class RefundHistoriesController(AppDbContext db) : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var query = db.RefundHistories.AsQueryable();

            if (!User.IsInRole("Admin") && !User.IsInRole("Staff"))
            {
                var userId = GetCurrentUserId();
                query = query.Where(h => db.Refunds.Any(r =>
                    r.Id == h.RefundId && db.Bookings.Any(b =>
                        b.Id == r.BookingId && b.CustomerProfile != null && b.CustomerProfile.UserId == userId)));
            }

            var items = await query.OrderByDescending(h => h.ChangedAtUtc).ToListAsync();
            return Ok(items.Select(ToResponseDto));
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(Guid id)
        {
            var item = await db.RefundHistories.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null) return NotFound();

            if (!User.IsInRole("Admin") && !User.IsInRole("Staff"))
            {
                var userId = GetCurrentUserId();
                var owns = await db.Refunds.AnyAsync(r =>
                    r.Id == item.RefundId && db.Bookings.Any(b =>
                        b.Id == r.BookingId && b.CustomerProfile != null && b.CustomerProfile.UserId == userId));
                if (!owns) return Forbid();
            }

            return Ok(ToResponseDto(item));
        }

        private Guid? GetCurrentUserId()
        {
            var claim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(claim, out var id) ? id : null;
        }

        private static RefundHistoryResponseDto ToResponseDto(RefundHistory x) => new()
        {
            Id = x.Id,
            RefundId = x.RefundId,
            Status = x.Status,
            ChangedAtUtc = x.ChangedAtUtc,
            Remarks = x.Remarks,
            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc,
            RowVersion = x.RowVersion,
        };
    }
}
