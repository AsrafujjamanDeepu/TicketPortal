using TicketPortal.Api.Data;
using TicketPortal.Api.DTO;
using TicketPortal.Api.Extensions;
using TicketPortal.Api.Models.Bookings;
using TicketPortal.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace TicketPortal.Api.Controllers
{
    // CanAccess previously granted ANY Staff account unrestricted access to EVERY operator's
    // seat holds — including Release, a write action that frees another operator's active
    // hold out from under a customer mid-checkout. SeatHold carries no BusOperatorId directly;
    // it's resolved via Trip.BusOperatorId, same idea as RefundsController resolving through
    // Booking. "Staff" also silently excluded the "Operator" login role.
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class SeatHoldsController(AppDbContext db, SeatHoldService seatHoldService) : ControllerBase
    {
        // The "3 to 5 minute timer" from the business plan — fixed here, server-side, so a
        // client can never request its own (much longer) hold window. Move this to
        // appsettings/config if it needs to become tunable later.
        private const int HoldDurationMinutes = 5;

        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var query = db.SeatHolds.AsQueryable();

            // Admin/platform-Staff see every hold; an operator's own Staff/Operator account is
            // scoped to holds on that operator's own trips; everyone else sees only their own.
            if (User.IsInRole("Admin") || User.IsInRole("Staff") || User.IsInRole("Operator"))
            {
                var callerOperatorId = await User.GetBusOperatorIdAsync(db);
                if (callerOperatorId != null)
                {
                    query = query.Where(h => db.Trips.Any(t => t.Id == h.TripId && t.BusOperatorId == callerOperatorId));
                }
                // else: platform Admin/Staff — no filter, see everything.
            }
            else
            {
                var userId = GetCurrentUserId();
                query = query.Where(h => h.HeldByUserId == userId);
            }

            var items = await query.OrderByDescending(h => h.HoldStartedAtUtc).ToListAsync();
            return Ok(items.Select(ToResponseDto));
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(Guid id)
        {
            var item = await db.SeatHolds.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null) return NotFound();
            if (!await CanAccessAsync(item)) return Forbid();
            return Ok(ToResponseDto(item));
        }

        // The checkout page polls this by token (it doesn't have a database id yet at that
        // point) to drive the on-screen countdown.
        [HttpGet("by-token/{holdToken}")]
        public async Task<IActionResult> GetByToken(string holdToken)
        {
            var item = await db.SeatHolds.FirstOrDefaultAsync(x => x.HoldToken == holdToken);
            if (item == null) return NotFound();
            if (!await CanAccessAsync(item)) return Forbid();
            return Ok(ToResponseDto(item));
        }

        // Step 1 of checkout. This used to just insert a SeatHold row and never touch
        // TripSeat at all — meaning two customers could both "hold" and both convert the same
        // seat. It now delegates to SeatHoldService, which does the actual race-safe
        // "UPDATE TripSeat SET Status = Held WHERE Status = Available" locking.
        [HttpPost]
        public async Task<IActionResult> Create(SeatHoldCreateDto dto)
        {
            if (dto.TripSeatIds == null || dto.TripSeatIds.Count == 0)
            {
                return BadRequest(new { message = "Select at least one seat." });
            }

            try
            {
                var hold = await seatHoldService.HoldSeatsAsync(
                    dto.TripId,
                    dto.TripSeatIds,
                    HoldDurationMinutes,
                    GetCurrentUserId(),
                    HttpContext.Connection.RemoteIpAddress?.ToString(),
                    Request.Headers.UserAgent.ToString());

                return CreatedAtAction(nameof(GetById), new { id = hold.Id }, ToResponseDto(hold));
            }
            catch (SeatsUnavailableException ex)
            {
                return Conflict(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // Customer deselects seats or abandons checkout before the timer runs out — free the
        // seats immediately instead of making the next customer wait out the full window.
        // Replaces the old generic PUT, which let a client set Status to anything directly
        // (including straight to ConvertedToBooking, with no payment involved at all).
        [HttpPost("{id}/release")]
        public async Task<IActionResult> Release(Guid id)
        {
            var item = await db.SeatHolds.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null) return NotFound();
            if (!await CanAccessAsync(item)) return Forbid();

            await seatHoldService.ReleaseHoldAsync(item.HoldToken);
            return NoContent();
        }

        private Guid? GetCurrentUserId()
        {
            var claim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(claim, out var id) ? id : null;
        }

        // Admin/platform-Staff: any hold. Staff/Operator scoped to one operator: only holds on
        // that operator's own trips (resolved via Trip.BusOperatorId — SeatHold carries no
        // BusOperatorId directly). Everyone else: only a hold they themselves created.
        private async Task<bool> CanAccessAsync(SeatHold item)
        {
            if (User.IsInRole("Admin") || User.IsInRole("Staff") || User.IsInRole("Operator"))
            {
                var operatorId = await db.Trips
                    .Where(t => t.Id == item.TripId)
                    .Select(t => (Guid?)t.BusOperatorId)
                    .FirstOrDefaultAsync();
                return operatorId != null && await User.CanManageOperatorAsync(db, operatorId.Value);
            }

            var userId = GetCurrentUserId();
            return userId != null && item.HeldByUserId == userId;
        }

        private static SeatHoldResponseDto ToResponseDto(SeatHold x)
        {
            var secondsRemaining = (int)Math.Max(0, (x.HoldExpiresAtUtc - DateTime.UtcNow).TotalSeconds);

            return new SeatHoldResponseDto
            {
                Id = x.Id,
                TripId = x.TripId,
                HeldByUserId = x.HeldByUserId,
                HoldToken = x.HoldToken,
                HoldStartedAtUtc = x.HoldStartedAtUtc,
                HoldExpiresAtUtc = x.HoldExpiresAtUtc,
                Status = x.Status,
                SecondsRemaining = x.Status == Models.Enums.SeatHoldStatus.Active ? secondsRemaining : 0,
                ClientIpAddress = x.ClientIpAddress,
                UserAgent = x.UserAgent,
                CreatedAtUtc = x.CreatedAtUtc,
                UpdatedAtUtc = x.UpdatedAtUtc,
                RowVersion = x.RowVersion,
            };
        }
    }
}
