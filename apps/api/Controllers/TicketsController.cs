using TicketPortal.Api.Data;
using TicketPortal.Api.DTO;
using TicketPortal.Api.Extensions;
using TicketPortal.Api.Models.Bookings;
using TicketPortal.Api.Models.Enums;
using TicketPortal.Api.Models.People;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace TicketPortal.Api.Controllers
{
    // Read-only on purpose. Tickets are issued exclusively by PaymentConfirmationService the
    // moment an online payment is confirmed — the old generic Create/Update here let a client
    // mint an "Issued" ticket at any Fare, with no link to a real payment at all. Cancelling a
    // ticket belongs to the CancellationRequest workflow, not a raw field edit, so Delete has
    // also been removed.
    //
    // Access is three-tiered, same pattern as PaymentsController/RefundsController (Ticket
    // carries no BusOperatorId directly, so scoping joins through Booking.BusOperatorId):
    // platform Admin/Staff see every ticket; an operator's own Staff/Operator account only
    // sees tickets against that operator's own bookings; a plain Customer only sees their own.
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class TicketsController(AppDbContext db) : ControllerBase
    {
        // A conductor or customer needs to check that a ticket is currently usable without
        // seeing passenger contact details, payment details, or a full booking. This exposes
        // the minimum operational data required at boarding.
        [AllowAnonymous]
        [HttpGet("verify/{ticketNumber}")]
        public async Task<IActionResult> VerifyByTicketNumber(string ticketNumber)
        {
            var normalizedTicketNumber = ticketNumber.Trim();
            if (string.IsNullOrWhiteSpace(normalizedTicketNumber))
            {
                return NotFound(new { message = "Ticket not found." });
            }

            var ticket = await db.Tickets
                .AsNoTracking()
                .Include(t => t.Trip)
                    .ThenInclude(trip => trip.BusOperator)
                .Include(t => t.Trip)
                    .ThenInclude(trip => trip.DepartureTerminal)
                .Include(t => t.Trip)
                    .ThenInclude(trip => trip.ArrivalTerminal)
                .FirstOrDefaultAsync(t => t.TicketNumber == normalizedTicketNumber);

            if (ticket is null)
            {
                return NotFound(new { message = "Ticket not found." });
            }

            var trip = ticket.Trip;
            var validForBoarding = ticket.Status is TicketStatus.Issued or TicketStatus.CheckedIn;

            return Ok(new
            {
                ticketNumber = ticket.TicketNumber,
                status = ticket.Status,
                validForBoarding,
                seatNumber = ticket.SeatNumberSnapshot,
                tripCode = trip.TripCode,
                operatorName = trip.BusOperator?.Name,
                departureTerminal = trip.DepartureTerminal?.Name,
                arrivalTerminal = trip.ArrivalTerminal?.Name,
                departureTimeUtc = trip.DepartureTimeUtc
            });
        }

        // =========================================================
        // GET: /api/tickets
        // Role-scoped list of tickets
        // =========================================================
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var query = db.Tickets
                .Include(t => t.Booking)
                    .ThenInclude(b => b.Passengers)
                .Include(t => t.Booking)
                    .ThenInclude(b => b.Trip)
                        .ThenInclude(tr => tr.Bus)
                .Include(t => t.Booking)
                    .ThenInclude(b => b.Trip)
                        .ThenInclude(tr => tr.BusOperator)
                .Include(t => t.Booking)
                    .ThenInclude(b => b.Trip)
                        .ThenInclude(tr => tr.DepartureTerminal)
                .Include(t => t.Booking)
                    .ThenInclude(b => b.Trip)
                        .ThenInclude(tr => tr.ArrivalTerminal)
                .Include(t => t.Booking)
                    .ThenInclude(b => b.BoardingTerminal)
                .Include(t => t.Booking)
                    .ThenInclude(b => b.DroppingTerminal)
                .Include(t => t.TripSeat)
                .AsQueryable();

            // ============ Role-based scoping ============
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
                    b.Id == t.BookingId &&
                    b.CustomerProfile != null &&
                    b.CustomerProfile.UserId == userId));
            }

            var items = await query
                .OrderByDescending(t => t.CreatedAtUtc)
                .ToListAsync();

            return Ok(items.Select(ToResponseDto));
        }

        // =========================================================
        // GET: /api/tickets/{id}
        // Full ticket details with related booking/trip/bus/passenger
        // =========================================================
        [HttpGet("{id:guid}")]
        public async Task<IActionResult> GetById(Guid id)
        {
            var item = await db.Tickets
                .Include(t => t.Booking)
                    .ThenInclude(b => b.Passengers)
                .Include(t => t.Booking)
                    .ThenInclude(b => b.Trip)
                        .ThenInclude(tr => tr.Bus)
                .Include(t => t.Booking)
                    .ThenInclude(b => b.Trip)
                        .ThenInclude(tr => tr.BusOperator)
                .Include(t => t.Booking)
                    .ThenInclude(b => b.Trip)
                        .ThenInclude(tr => tr.DepartureTerminal)
                .Include(t => t.Booking)
                    .ThenInclude(b => b.Trip)
                        .ThenInclude(tr => tr.ArrivalTerminal)
                .Include(t => t.Booking)
                    .ThenInclude(b => b.BoardingTerminal)
                .Include(t => t.Booking)
                    .ThenInclude(b => b.DroppingTerminal)
                .Include(t => t.TripSeat)
                .FirstOrDefaultAsync(x => x.Id == id);

            if (item == null) return NotFound();
            if (!await CanAccessAsync(item)) return Forbid();

            return Ok(ToResponseDto(item));
        }

        // =========================================================
        // Auth helpers
        // =========================================================
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

                return operatorId != null &&
                       await User.CanManageOperatorAsync(db, operatorId.Value);
            }

            var userId = GetCurrentUserId();
            if (userId == null) return false;

            return await db.Bookings.AnyAsync(b =>
                b.Id == item.BookingId &&
                b.CustomerProfile != null &&
                b.CustomerProfile.UserId == userId);
        }

        // =========================================================
        // Map Ticket entity → TicketResponseDto
        // =========================================================
        private static TicketResponseDto ToResponseDto(Ticket x)
        {
            var booking = x.Booking;
            var trip = booking?.Trip;
            var tripSeat = x.TripSeat;

            // ⭐ Find matching passenger for this ticket
            BookingPassenger? passenger = null;

            if (booking?.Passengers != null)
            {
                // Try match by BookingPassengerId first (exact)
                if (x.BookingPassengerId != Guid.Empty)
                {
                    passenger = booking.Passengers
                        .FirstOrDefault(p => p.Id == x.BookingPassengerId);
                }

                // Fallback: first passenger (single-passenger bookings)
                passenger ??= booking.Passengers.FirstOrDefault();
            }

            return new TicketResponseDto
            {
                // ============ TICKET ============
                Id = x.Id,
                TicketNumber = x.TicketNumber,
                ExternalTicketKey = x.ExternalTicketKey,
                Fare = x.Fare,
                DiscountAmount = x.DiscountAmount,
                FinalFare = x.FinalFare,
                Status = x.Status,
                IssuedAtUtc = x.IssuedAtUtc,
                CheckedInAtUtc = x.CheckedInAtUtc,
                CancelledAtUtc = x.CancelledAtUtc,
                QrCodePayload = x.QrCodePayload,

                // ============ BOOKING ============
                BookingId = x.BookingId,
                Pnr = booking?.Pnr,

                // ============ PASSENGER ============
                BookingPassengerId = x.BookingPassengerId,
                PassengerName = passenger?.FullName,
                PassengerPhone = passenger?.Phone,
                PassengerGender = passenger?.Gender,
                PassengerAge = passenger?.Age,

                // ============ SEAT ============
                TripSeatId = x.TripSeatId,
                SeatNumberSnapshot = x.SeatNumberSnapshot ?? string.Empty,
                SeatNumber = x.SeatNumberSnapshot ?? tripSeat?.SeatNumber,

                // ============ TRIP ============
                TripId = x.TripId,
                TripCode = trip?.TripCode,
                DepartureTimeUtc = trip?.DepartureTimeUtc,
                ArrivalTimeUtc = trip?.ArrivalTimeUtc,

                // ============ BUS ============
                BusId = trip?.BusId,
                BusName = trip?.Bus != null
                    ? $"{trip.Bus.Brand} {trip.Bus.Model}".Trim()
                    : null,
                BusBrand = trip?.Bus?.Brand,
                BusModel = trip?.Bus?.Model,
                BusCoachNumber = trip?.Bus?.CoachNumber,
                BusRegistrationNumber = trip?.Bus?.RegistrationNumber,
                BusHasWifi = trip?.Bus?.HasWifi ?? false,
                BusHasToilet = trip?.Bus?.HasToilet ?? false,

                // ============ OPERATOR ============
                BusOperatorId = trip?.BusOperatorId,
                BusOperatorName = trip?.BusOperator?.Name,

                // ============ TERMINALS ============
                DepartureTerminalId = trip?.DepartureTerminalId,
                DepartureTerminalName = trip?.DepartureTerminal?.Name,
                DepartureCity = trip?.DepartureTerminal?.City,
                ArrivalTerminalId = trip?.ArrivalTerminalId,
                ArrivalTerminalName = trip?.ArrivalTerminal?.Name,
                ArrivalCity = trip?.ArrivalTerminal?.City,

                // ============ BOARDING / DROPPING ============
                BoardingTerminalName = booking?.BoardingTerminal?.Name,
                DroppingTerminalName = booking?.DroppingTerminal?.Name,

                // ============ META ============
                CreatedAtUtc = x.CreatedAtUtc,
                UpdatedAtUtc = x.UpdatedAtUtc,
                RowVersion = x.RowVersion,
            };
        }
    }
}
