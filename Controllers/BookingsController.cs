using TicketPortal.Api.Data;
using TicketPortal.Api.DTO;
using TicketPortal.Api.Models.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TicketPortal.Api.Models.Bookings;
using System.Security.Claims;

namespace TicketPortal.Api.Controllers
{
    // Master = Booking, Details = BookingPassenger.
    // BusOperatorId is deliberately NOT on the create DTO — it's resolved server-side from the
    // Trip, exactly like a real booking flow would (the caller shouldn't get to pick the operator).
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class BookingsController(AppDbContext db, IWebHostEnvironment env) : ControllerBase
    {
        // See BusesController.GetAll for why materializing (.ToListAsync()) has to happen
        // BEFORE mapping with ToResponseDto — EF Core can't translate that method into SQL.
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var bookings = await db.Bookings.Include(b => b.Passengers).ToListAsync();
            return Ok(bookings.Select(ToResponseDto));
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(Guid id)
        {
            var booking = await db.Bookings.Include(b => b.Passengers).FirstOrDefaultAsync(b => b.Id == id);
            return booking == null ? NotFound() : Ok(ToResponseDto(booking));
        }

        [HttpPost]
        public async Task<IActionResult> Create(BookingCreateDto dto)
        {
            var trip = await db.Trips.FindAsync(dto.TripId);
            if (trip == null) return BadRequest($"Trip {dto.TripId} does not exist.");

            var booking = new Booking
            {
                CustomerProfileId = await ResolveOrCreateCustomerProfileIdAsync(),
                BusOperatorId = trip.BusOperatorId,
                TripId = dto.TripId,
                BoardingTerminalId = dto.BoardingTerminalId,
                DroppingTerminalId = dto.DroppingTerminalId,
                Pnr = GeneratePnr(),
                ContactName = dto.ContactName,
                ContactPhone = dto.ContactPhone,
                ContactEmail = dto.ContactEmail,
                SubTotal = dto.SubTotal,
                DiscountAmount = dto.DiscountAmount,
                TaxAmount = dto.TaxAmount,
                ServiceChargeAmount = dto.ServiceChargeAmount,
                GrandTotal = dto.GrandTotal,
                RequiresExternalConfirmation = dto.RequiresExternalConfirmation,
                ExpiresAtUtc = dto.ExpiresAtUtc,
                Passengers = dto.Passengers.Select(p => new BookingPassenger
                {
                    FullName = p.FullName,
                    Phone = p.Phone,
                    Email = p.Email,
                    Gender = p.Gender,
                    PassengerType = p.PassengerType,
                    Age = p.Age,
                    NationalIdNumber = p.NationalIdNumber
                }).ToList()
            };

            db.Bookings.Add(booking);
            await db.SaveChangesAsync();

            return CreatedAtAction(nameof(GetById), new { id = booking.Id }, ToResponseDto(booking));
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(Guid id, BookingUpdateDto dto)
        {
            var booking = await db.Bookings
                .Include(b => b.Passengers)
                .FirstOrDefaultAsync(b => b.Id == id);

            if (booking == null)
            {
                return NotFound(new
                {
                    message = "Booking not found."
                });
            }

            // ----------------------------------------
            // RowVersion validation
            // ----------------------------------------
            if (dto.RowVersion == null || dto.RowVersion.Length == 0)
            {
                return BadRequest(new
                {
                    message =
                        "RowVersion is required. " +
                        "GET the Booking first and send the latest RowVersion."
                });
            }

            // Tell EF which version the client originally loaded
            db.Entry(booking)
                .Property(b => b.RowVersion)
                .OriginalValue = dto.RowVersion;

            // ----------------------------------------
            // Validate passengers
            // ----------------------------------------
            if (dto.Passengers == null || dto.Passengers.Count == 0)
            {
                return BadRequest(new
                {
                    message = "At least one passenger is required."
                });
            }

            // ----------------------------------------
            // Update Booking
            // ----------------------------------------
            booking.BoardingTerminalId = dto.BoardingTerminalId;
            booking.DroppingTerminalId = dto.DroppingTerminalId;

            booking.ContactName = dto.ContactName;
            booking.ContactPhone = dto.ContactPhone;
            booking.ContactEmail = dto.ContactEmail;

            booking.SubTotal = dto.SubTotal;
            booking.DiscountAmount = dto.DiscountAmount;
            booking.TaxAmount = dto.TaxAmount;
            booking.ServiceChargeAmount = dto.ServiceChargeAmount;
            booking.GrandTotal = dto.GrandTotal;
            booking.RequiresExternalConfirmation = dto.RequiresExternalConfirmation;
            booking.ExpiresAtUtc = dto.ExpiresAtUtc;

            booking.Status = dto.Status;

            // ----------------------------------------
            // Replace passengers
            // ----------------------------------------
            db.BookingPassengers.RemoveRange(booking.Passengers);

            var newPassengers = dto.Passengers
                .Select(p => new BookingPassenger
                {
                    BookingId = booking.Id,

                    FullName = p.FullName,
                    Phone = p.Phone,
                    Email = p.Email,
                    Gender = p.Gender,
                    PassengerType = p.PassengerType,
                    Age = p.Age,
                    NationalIdNumber = p.NationalIdNumber
                })
                .ToList();

            await db.BookingPassengers.AddRangeAsync(newPassengers);

            // ----------------------------------------
            // Save
            // ----------------------------------------
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Conflict(new
                {
                    message =
                        "This Booking was changed by another request. " +
                        "GET the latest Booking and retry the update."
                });
            }
            catch (DbUpdateException ex)
            {
                return Conflict(new
                {
                    message =
                        "Could not save this Booking update — " +
                        "check boardingTerminalId/droppingTerminalId are valid.",

                    detail = ex.InnerException?.Message
                });
            }

            // ----------------------------------------
            // Reload fresh data
            // ----------------------------------------
            var updatedBooking = await db.Bookings
                .Include(b => b.Passengers)
                .FirstOrDefaultAsync(b => b.Id == id);

            if (updatedBooking == null)
            {
                return NotFound(new
                {
                    message = "Booking was updated but could not be loaded again."
                });
            }

            return Ok(ToResponseDto(updatedBooking));
        }
        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(Guid id)
        {
            var booking = await db.Bookings.Include(b => b.Passengers).FirstOrDefaultAsync(b => b.Id == id);
            if (booking == null) return NotFound();

            // BookingPassenger is a pure detail of this Booking — Restrict never cascades it.
            db.BookingPassengers.RemoveRange(booking.Passengers);
            db.Bookings.Remove(booking);

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Conflict("This Booking was already modified or deleted by another request.");
            }
            catch (DbUpdateException)
            {
                // e.g. a Payment or Ticket row still references this Booking under Restrict behavior.
                return Conflict("Cannot delete this Booking — related records (Payments, Tickets, etc.) still reference it.");
            }

            return NoContent();
        }

        // Image lives on the DETAIL row here, not the master — one passenger's ID photo,
        // not the whole booking. Route nests under both ids to make that explicit.
        [HttpPost("{bookingId}/passengers/{passengerId}/images")]
        public async Task<IActionResult> UploadPassengerIdPhoto(Guid bookingId, Guid passengerId, IFormFile file)
        {
            var passenger = await db.BookingPassengers
                .FirstOrDefaultAsync(p => p.Id == passengerId && p.BookingId == bookingId);
            if (passenger == null) return NotFound();
            if (file == null || file.Length == 0) return BadRequest("No file uploaded");

            var fileName = $"passenger_{passengerId}_{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";
            var path = Path.Combine(env.WebRootPath!, "images", fileName);
            await using (var stream = System.IO.File.Create(path))
            {
                await file.CopyToAsync(stream);
            }

            passenger.NationalIdPhotoUrl = $"/images/{fileName}";
            await db.SaveChangesAsync();
            return Ok(new { imageUrl = passenger.NationalIdPhotoUrl });
        }

        private static string GeneratePnr() =>
            "PNR" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        // A booking is always made by a customer, so this is the right place to lazily
        // provision a CustomerProfile — nothing does it at registration time, since
        // AccountController.Register is shared by customers and staff alike (see
        // ApplicationUser's comment: only one of CustomerProfile/StaffProfile gets filled in).
        // Without this, Booking.CustomerProfileId stayed null forever, so
        // PaymentsController/TicketsController could never scope "my own" records to a
        // real customer.
        private async Task<Guid?> ResolveOrCreateCustomerProfileIdAsync()
        {
            var claim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(claim, out var userId)) return null;

            var existingId = await db.CustomerProfiles
                .Where(cp => cp.UserId == userId)
                .Select(cp => (Guid?)cp.Id)
                .FirstOrDefaultAsync();

            if (existingId != null) return existingId;

            var profile = new Models.People.CustomerProfile { UserId = userId };
            db.CustomerProfiles.Add(profile);
            await db.SaveChangesAsync();
            return profile.Id;
        }

        private static BookingResponseDto ToResponseDto(Booking booking) => new()
        {
            Id = booking.Id,

            Pnr = booking.Pnr,

            TripId = booking.TripId,

            BoardingTerminalId = booking.BoardingTerminalId,
            DroppingTerminalId = booking.DroppingTerminalId,

            ContactName = booking.ContactName,
            ContactPhone = booking.ContactPhone,
            ContactEmail = booking.ContactEmail,
            CreatedAtUtc = booking.CreatedAtUtc,
            UpdatedAtUtc = booking.UpdatedAtUtc,
            DeletedAtUtc = booking.DeletedAtUtc,

            Status = booking.Status,

            GrandTotal = booking.GrandTotal,
            RequiresExternalConfirmation = booking.RequiresExternalConfirmation,
            ExpiresAtUtc = booking.ExpiresAtUtc,

            Currency = booking.Currency,

            Passengers = booking.Passengers
         .Select(p => new BookingPassengerResponseDto
         {
             Id = p.Id,
             FullName = p.FullName,
             Phone = p.Phone,
             Email = p.Email,
             Gender = p.Gender,
             PassengerType = p.PassengerType,
             Age = p.Age,
             NationalIdNumber = p.NationalIdNumber,
             NationalIdPhotoUrl = p.NationalIdPhotoUrl
         })
         .ToList(),

            RowVersion = booking.RowVersion
        };
    }
}
