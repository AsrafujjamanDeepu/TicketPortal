using TicketPortal.Api.Data;
using TicketPortal.Api.Authorization;
using TicketPortal.Api.DTO;
using TicketPortal.Api.Extensions;
using TicketPortal.Api.Models.Bookings;
using TicketPortal.Api.Models.Enums;
using TicketPortal.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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
    public class SeatHoldsController(
        AppDbContext db,
        SeatHoldService seatHoldService,
        IConfiguration configuration,
        ExternalBookingSyncService externalSync,
        ICurrentActorService currentActor,
        ILogger<SeatHoldsController> logger) : ControllerBase
    {
        // The "3 to 5 minute timer" from the concept (§5) — server-side, so a client can never
        // request its own (much longer) hold window. Chunk 3 task 3: now configurable via
        // SeatHold:Minutes (appsettings.json / SeatHold__Minutes env var) instead of a fixed
        // constant, but still clamped to the concept's allowed 3–5 minute range here — a bad or
        // malicious config value can't hand out an hour-long hold.
        private int HoldDurationMinutes => Math.Clamp(configuration.GetValue("SeatHold:Minutes", 5), 3, 5);

        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var actor = await currentActor.ResolveAsync(User);
            var query = db.SeatHolds.AsQueryable();

            if (actor.IsAdmin || actor.HasPermission(Permissions.BookingRead))
            {
                if (actor.BusOperatorId != null)
                {
                    var operatorId = actor.BusOperatorId.Value;
                    query = query.Where(h => db.Trips.Any(t => t.Id == h.TripId && t.BusOperatorId == operatorId));
                }
            }
            else if (actor.Type == ActorType.Customer)
            {
                query = query.Where(h => h.HeldByUserId == actor.UserId);
            }
            else return Forbid();

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
        [EnableRateLimiting("holds")]
        public async Task<IActionResult> Create(SeatHoldCreateDto dto)
        {
            var actor = await currentActor.ResolveAsync(User);
            if (actor.Type != ActorType.Customer) return Forbid();
            if (dto.TripSeatIds == null || dto.TripSeatIds.Count == 0)
            {
                return BadRequest(new { message = "Select at least one seat." });
            }

            // Chunk 6 / C6-2: the two checks that need no database (repeated seat, too many seats
            // for one hold) are made first, so a refused request never costs an ERP call or
            // touches any inventory. SeatHoldService repeats them as the authoritative check.
            var limits = SeatHoldLimits.FromConfiguration(configuration);
            var requestProblem = SeatHoldLimits.CheckRequest(dto.TripSeatIds, limits);
            if (requestProblem is not null)
            {
                return BadRequest(new { message = requestProblem.Message, code = requestProblem.Kind.ToString() });
            }

            // Chunk 8 task 5: for a trip whose operator's own ERP is the source of truth
            // (ExternalApiManaged), our own TripSeat.Status can be stale — the operator may have
            // sold this exact seat through a channel we don't see. Ask their GetSeatAvailability
            // endpoint before ever taking the hold. This is an extra check on top of
            // SeatHoldService's own race-safe locking (and Chunk 3's TripNotBookableException
            // checks below), never a replacement for either.
            //
            // Chunk 6 / C6-4 (decision D7): what happens when that check CANNOT be completed is
            // Integrations:AvailabilityFailureMode. "Closed" (the default) refuses the hold with a
            // 503 — we do not know whether the seat is free; "Open" lets it through on our own
            // seat map and logs a warning. See ExternalAvailabilityPolicy for the trade-off.
            var trip = await db.Trips.FirstOrDefaultAsync(t => t.Id == dto.TripId);
            if (trip == null)
            {
                return BadRequest(new { message = "Trip not found." });
            }

            if (trip.InventoryMode == OperatorInventoryMode.ExternalApiManaged)
            {
                var requestedSeatNumbers = await db.TripSeats
                    .Where(ts => dto.TripSeatIds.Contains(ts.Id))
                    .Select(ts => ts.SeatNumber)
                    .ToListAsync();

                var availability = await externalSync.CheckSeatAvailabilityAsync(trip, HttpContext.RequestAborted);
                if (!availability.Success)
                {
                    if (ExternalAvailabilityPolicy.GetFailureMode(configuration) == AvailabilityFailureMode.Closed)
                    {
                        return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                        {
                            message = ExternalAvailabilityPolicy.ClosedCustomerMessage,
                            code = "OperatorAvailabilityUnavailable"
                        });
                    }

                    logger.LogWarning(
                        "Seat availability for Trip {TripId} could not be verified ({Reason}); AvailabilityFailureMode is Open, so the hold proceeds on our own seat map.",
                        trip.Id, availability.ErrorMessage);
                }
                else
                {
                    var conflictingSeats = requestedSeatNumbers
                        .Where(availability.SoldSeatNumbers.Contains)
                        .ToList();

                    if (conflictingSeats.Count > 0)
                    {
                        return Conflict(new
                        {
                            message = $"The operator's system reports seat(s) {string.Join(", ", conflictingSeats)} " +
                                "already sold. Please choose different seats.",
                            seatNumbers = conflictingSeats,
                        });
                    }
                }
            }

            try
            {
                var hold = await seatHoldService.HoldSeatsAsync(
                    dto.TripId,
                    dto.TripSeatIds,
                    HoldDurationMinutes,
                    GetCurrentUserId(),
                    HttpContext.Connection.RemoteIpAddress?.ToString(),
                    Request.Headers.UserAgent.ToString(),
                    limits: limits);

                return CreatedAtAction(nameof(GetById), new { id = hold.Id }, ToResponseDto(hold));
            }
            // Chunk 6 / C6-2: a hoarding/sanity rule refused the request before any seat was
            // touched. Too many active holds is a conflict with the user's own current state
            // (409, with a message telling them what to do); a bad seat list is a 400.
            catch (SeatHoldLimitException ex)
            {
                var body = new { message = ex.Message, code = ex.Kind.ToString() };
                return ex.Kind == SeatHoldLimitKind.TooManyActiveHolds ? Conflict(body) : BadRequest(body);
            }
            catch (SeatsUnavailableException ex)
            {
                return Conflict(new { message = ex.Message });
            }
            // Chunk 3 task 1: the trip's state changed out from under a stale seat map (someone
            // cancelled it, or it departed) between the customer loading the page and clicking
            // Hold. Same 409-and-refresh treatment as SeatsUnavailableException above, rather
            // than a generic 400, so the Angular seat map handles both the same way.
            catch (TripNotBookableException ex)
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
        [EnableRateLimiting("holds")]
        public async Task<IActionResult> Release(Guid id)
        {
            var item = await db.SeatHolds.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null) return NotFound();
            var actor = await currentActor.ResolveAsync(User);
            var isOwner = actor.Type == ActorType.Customer && item.HeldByUserId == actor.UserId;
            var operatorId = await db.Trips.Where(t => t.Id == item.TripId)
                .Select(t => (Guid?)t.BusOperatorId).FirstOrDefaultAsync();
            var canManage = actor.HasPermission(Permissions.BookingManage) && operatorId != null
                && actor.CanManageOperator(operatorId.Value);
            if (!isOwner && !canManage) return Forbid();

            await seatHoldService.ReleaseHoldAsync(item.HoldToken);
            return NoContent();
        }

        private Guid? GetCurrentUserId()
        {
            var claim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(claim, out var id) ? id : null;
        }

        private async Task<bool> CanAccessAsync(SeatHold item)
        {
            var actor = await currentActor.ResolveAsync(User);
            if (actor.IsAdmin || actor.HasPermission(Permissions.BookingRead))
            {
                var operatorId = await db.Trips
                    .Where(t => t.Id == item.TripId)
                    .Select(t => (Guid?)t.BusOperatorId)
                    .FirstOrDefaultAsync();
                return operatorId != null && actor.CanManageOperator(operatorId.Value);
            }

            return actor.Type == ActorType.Customer && item.HeldByUserId == actor.UserId;
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
