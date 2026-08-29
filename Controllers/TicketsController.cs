using TicketPortal.Api.Data;
using TicketPortal.Api.DTO;
using TicketPortal.Api.Extensions;
using TicketPortal.Api.Models.Bookings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace TicketPortal.Api.Controllers
{
    // Read-only on purpose. Tickets are issued exclusively by PaymentConfirmationService the
    // moment an online payment is confirmed (see Services/PaymentConfirmationService.cs) — the
    // old generic Create/Update here let a client mint an "Issued" ticket at any Fare, with no
    // link to a real payment at all. Cancelling a ticket belongs to the CancellationRequest
    // workflow, not a raw field edit, so Delete has also been removed.
    //
    // Access is three-tiered, same pattern as PaymentsController/RefundsController (Ticket
    // carries no BusOperatorId directly, so scoping joins through Booking.BusOperatorId):
    // platform Admin/Staff see every ticket; an operator's own Staff/Operator account only
    // sees tickets against that operator's own bookings; a plain Customer only sees their own.
    // Previously ANY Staff account saw every operator's tickets, and "Operator" was silently
    // excluded entirely.
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class TicketsController(AppDbContext db) : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var query = db.Tickets.AsQueryable();

            if (User.IsInRole("Admin") || User.IsInRole("Staff") || User.IsInRole("Operator"))
            {
                var callerOperatorId = await User.GetBusOperatorIdAsync(db);
                if (callerOperatorId != null)
                {
                    query = query.Where(t => db.Bookings.Any(b =>
                        b.Id == t.BookingId && b.BusOperatorId == callerOperatorId));
                }
                // else: platform Admin/Staff — no filter, see everything.
            }
            else
            {
                var userId = GetCurrentUserId();
                query = query.Where(t => db.Bookings.Any(b =>
                    b.Id == t.BookingId && b.CustomerProfile != null && b.CustomerProfile.UserId == userId));
            }

            var items = await query.OrderByDescending(t => t.CreatedAtUtc).ToListAsync();
            return Ok(items.Select(ToResponseDto));
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(Guid id)
        {
            var item = await db.Tickets.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null) return NotFound();
            if (!await CanAccessAsync(item)) return Forbid();
            return Ok(ToResponseDto(item));
        }

        private Guid? GetCurrentUserId()
        {
            var claim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(claim, out var id) ? id : null;
        }

        private async Task<bool> CanAccessAsync(Ticket item)
        {
            if (User.IsInRole("Admin") || User.IsInRole("Staff") || User.IsInRole("Operator"))
            {
                var operatorId = await db.Bookings
                    .Where(b => b.Id == item.BookingId)
                    .Select(b => (Guid?)b.BusOperatorId)
                    .FirstOrDefaultAsync();
                return operatorId != null && await User.CanManageOperatorAsync(db, operatorId.Value);
            }

            var userId = GetCurrentUserId();
            if (userId == null) return false;

            return await db.Bookings.AnyAsync(b =>
                b.Id == item.BookingId && b.CustomerProfile != null && b.CustomerProfile.UserId == userId);
        }

        private static TicketResponseDto ToResponseDto(Ticket x) => new()
        {
            Id = x.Id,
            BookingId = x.BookingId,
            BookingPassengerId = x.BookingPassengerId,
            TripId = x.TripId,
            TripSeatId = x.TripSeatId,
            TicketNumber = x.TicketNumber,
            ExternalTicketKey = x.ExternalTicketKey,
            SeatNumberSnapshot = x.SeatNumberSnapshot,
            QrCodePayload = x.QrCodePayload,
            Fare = x.Fare,
            DiscountAmount = x.DiscountAmount,
            FinalFare = x.FinalFare,
            Status = x.Status,
            IssuedAtUtc = x.IssuedAtUtc,
            CheckedInAtUtc = x.CheckedInAtUtc,
            CancelledAtUtc = x.CancelledAtUtc,
            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc,
            RowVersion = x.RowVersion,
        };
    }
}
