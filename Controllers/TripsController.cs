using TicketPortal.Api.Data;
using TicketPortal.Api.DTO;
using TicketPortal.Api.Models.Scheduling;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TicketPortal.Api.Models.Enums;
using System.Security.Claims;

namespace TicketPortal.Api.Controllers
{
    // Master = Trip, Details = TripSeat (per-trip seat availability and pricing).
    // [Authorize] on the class means every action below requires a valid Bearer token except
    // where explicitly overridden — there is no override here, so all six endpoints are protected.
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class TripsController(AppDbContext db, IWebHostEnvironment env) : ControllerBase
    {
        // See BusesController.GetAll for why materializing (.ToListAsync()) has to happen
        // BEFORE mapping with ToResponseDto — EF Core can't translate that method into SQL.
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var trips = await db.Trips.Include(t => t.TripSeats).ToListAsync();
            return Ok(trips.Select(ToResponseDto));
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(Guid id)
        {
            var trip = await db.Trips.Include(t => t.TripSeats).FirstOrDefaultAsync(t => t.Id == id);
            return trip == null ? NotFound() : Ok(ToResponseDto(trip));
        }

        [HttpPost]
        public async Task<IActionResult> Create(TripCreateDto dto)
        {
            // =========================================================
            // 1. Basic validation
            // =========================================================

            if (dto == null)
            {
                return BadRequest(new
                {
                    message = "Request body is required."
                });
            }

            if (dto.TripSeats == null || dto.TripSeats.Count == 0)
            {
                return BadRequest(new
                {
                    message = "At least one TripSeat is required."
                });
            }

            // =========================================================
            // 2. Validate Bus + BusOperator
            // =========================================================

            var bus = await db.Buses
                .AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == dto.BusId);

            if (bus == null)
            {
                return BadRequest(new
                {
                    message = "The specified Bus does not exist.",
                    busId = dto.BusId
                });
            }

            if (bus.BusOperatorId != dto.BusOperatorId)
            {
                return BadRequest(new
                {
                    message =
                        "The specified Bus does not belong to the specified BusOperator.",

                    busId = dto.BusId,
                    actualBusOperatorId = bus.BusOperatorId,
                    requestedBusOperatorId = dto.BusOperatorId
                });
            }

            // =========================================================
            // 3. Validate BusOperator exists
            // =========================================================

            var operatorExists = await db.BusOperators
                .AnyAsync(o => o.Id == dto.BusOperatorId);

            if (!operatorExists)
            {
                return BadRequest(new
                {
                    message = "BusOperatorId does not exist.",
                    busOperatorId = dto.BusOperatorId
                });
            }

            // =========================================================
            // 4. Validate BusRoute
            // =========================================================

            var busRouteExists = await db.BusRoutes
                .AnyAsync(r => r.Id == dto.BusRouteId);

            if (!busRouteExists)
            {
                return BadRequest(new
                {
                    message = "BusRouteId does not exist.",
                    busRouteId = dto.BusRouteId
                });
            }

            // =========================================================
            // 5. Validate Departure Terminal
            // =========================================================

            var departureTerminalExists = await db.Terminals
                .AnyAsync(t => t.Id == dto.DepartureTerminalId);

            if (!departureTerminalExists)
            {
                return BadRequest(new
                {
                    message = "DepartureTerminalId does not exist.",
                    departureTerminalId = dto.DepartureTerminalId
                });
            }

            // =========================================================
            // 6. Validate Arrival Terminal
            // =========================================================

            var arrivalTerminalExists = await db.Terminals
                .AnyAsync(t => t.Id == dto.ArrivalTerminalId);

            if (!arrivalTerminalExists)
            {
                return BadRequest(new
                {
                    message = "ArrivalTerminalId does not exist.",
                    arrivalTerminalId = dto.ArrivalTerminalId
                });
            }

            // =========================================================
            // 7. Departure and Arrival cannot be same
            // =========================================================

            if (dto.DepartureTerminalId == dto.ArrivalTerminalId)
            {
                return BadRequest(new
                {
                    message =
                        "DepartureTerminalId and ArrivalTerminalId cannot be the same."
                });
            }

            // =========================================================
            // 8. Validate Date / Time
            // =========================================================

            if (dto.ArrivalTimeUtc <= dto.DepartureTimeUtc)
            {
                return BadRequest(new
                {
                    message =
                        "ArrivalTimeUtc must be greater than DepartureTimeUtc."
                });
            }

            // =========================================================
            // 9. Get physical Seats of selected Bus
            // =========================================================

            var physicalSeats = await db.Seats
                .Where(s => s.BusId == dto.BusId)
                .ToDictionaryAsync(s => s.Id);

            if (physicalSeats.Count == 0)
            {
                return BadRequest(new
                {
                    message = "The selected Bus has no physical seats."
                });
            }

            // =========================================================
            // 10. Validate duplicate SeatId
            // =========================================================

            var duplicateSeatIds = dto.TripSeats
                .GroupBy(s => s.SeatId)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (duplicateSeatIds.Count > 0)
            {
                return BadRequest(new
                {
                    message =
                        "The same physical seat cannot be added to a Trip more than once.",

                    duplicateSeatIds
                });
            }

            // =========================================================
            // 11. Validate every TripSeat belongs to this Bus
            // =========================================================

            foreach (var seatDto in dto.TripSeats)
            {
                if (!physicalSeats.TryGetValue(
                        seatDto.SeatId,
                        out var physicalSeat))
                {
                    return BadRequest(new
                    {
                        message =
                            "One or more TripSeats do not belong to the selected Bus.",

                        invalidSeatId = seatDto.SeatId
                    });
                }

                // Don't trust client SeatNumber — it must match the physical seat's real number.
                if (!string.Equals(
                        physicalSeat.SeatNumber,
                        seatDto.SeatNumber,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(new
                    {
                        message =
                            $"SeatNumber mismatch for SeatId {seatDto.SeatId}.",

                        expectedSeatNumber = physicalSeat.SeatNumber,

                        receivedSeatNumber = seatDto.SeatNumber
                    });
                }
            }

            // =========================================================
            // 12. Create Trip
            // =========================================================

            // Each TripSeat.SeatId below must reference a Seat that already exists on BusId's
            // physical layout — a trip prices/tracks existing seats, it doesn't invent new ones.
            // SeatNumber/SeatType are taken from the physical Seat (physicalSeats), never from the
            // client, for the same reason Update() below does it — only Fare is trip-specific.
            var trip = new Trip
            {
                BusOperatorId = dto.BusOperatorId,
                BusRouteId = dto.BusRouteId,
                BusId = dto.BusId,
                DepartureTerminalId = dto.DepartureTerminalId,
                ArrivalTerminalId = dto.ArrivalTerminalId,
                TripCode = dto.TripCode,
                DepartureTimeUtc = dto.DepartureTimeUtc,
                ArrivalTimeUtc = dto.ArrivalTimeUtc,
                BaseFare = dto.BaseFare,
                Currency = dto.Currency,
                IsWheelchairAccessible = dto.IsWheelchairAccessible,
                TripSeats = dto.TripSeats
                    .Select(s =>
                    {
                        var physicalSeat = physicalSeats[s.SeatId];

                        return new TripSeat
                        {
                            SeatId = physicalSeat.Id,
                            SeatNumber = physicalSeat.SeatNumber,
                            SeatType = physicalSeat.SeatType,
                            Fare = s.Fare
                        };
                    })
                    .ToList()
            };

            // =========================================================
            // 13. Save
            // =========================================================

            try
            {
                db.Trips.Add(trip);

                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                return Conflict(new
                {
                    message = "Could not create this Trip.",

                    detail = ex.InnerException?.Message,

                    innerDetail =
                        ex.InnerException?.InnerException?.Message
                });
            }

            // =========================================================
            // 14. Reload Trip + TripSeats
            // =========================================================

            var createdTrip = await db.Trips
                .Include(t => t.TripSeats)
                .FirstOrDefaultAsync(t => t.Id == trip.Id);

            if (createdTrip == null)
            {
                return StatusCode(500, new
                {
                    message = "Trip was created but could not be loaded again."
                });
            }

            // =========================================================
            // 15. Return response
            // =========================================================

            return CreatedAtAction(nameof(GetById), new { id = createdTrip.Id }, ToResponseDto(createdTrip));
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(Guid id, TripUpdateDto dto)
        {
            // =========================================================
            // 1. Load Trip + existing TripSeats
            // =========================================================

            var trip = await db.Trips
                .Include(t => t.TripSeats)
                .FirstOrDefaultAsync(t => t.Id == id);

            if (trip == null)
            {
                return NotFound(new
                {
                    message = "Trip not found."
                });
            }

            // Captured before any of the fields below get overwritten — used after the update
            // to decide whether a TripStatusHistory row actually needs writing.
            var previousStatus = trip.Status;


            // =========================================================
            // 2. Validate RowVersion
            // =========================================================

            if (dto.RowVersion == null || dto.RowVersion.Length == 0)
            {
                return BadRequest(new
                {
                    message =
                        "RowVersion is required. " +
                        "GET the Trip first and send the latest RowVersion."
                });
            }


            // =========================================================
            // 3. Set original RowVersion for concurrency checking
            // =========================================================

            db.Entry(trip)
                .Property(t => t.RowVersion)
                .OriginalValue = dto.RowVersion;


            // =========================================================
            // 4. Validate TripSeats
            // =========================================================

            if (dto.TripSeats == null || dto.TripSeats.Count == 0)
            {
                return BadRequest(new
                {
                    message = "At least one TripSeat is required."
                });
            }


            // =========================================================
            // 5. Check Bus exists
            // =========================================================

            var bus = await db.Buses
                .AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == dto.BusId);

            if (bus == null)
            {
                return BadRequest(new
                {
                    message = "The specified Bus does not exist.",
                    busId = dto.BusId
                });
            }


            // =========================================================
            // 6. Check Bus belongs to selected BusOperator
            // =========================================================

            if (bus.BusOperatorId != dto.BusOperatorId)
            {
                return BadRequest(new
                {
                    message =
                        "The specified Bus does not belong to the specified BusOperator.",

                    busId = dto.BusId,
                    actualBusOperatorId = bus.BusOperatorId,
                    requestedBusOperatorId = dto.BusOperatorId
                });
            }


            // =========================================================
            // 7. Get all physical Seats of this Bus
            // =========================================================

            var busSeatIds = await db.Seats
                .Where(s => s.BusId == dto.BusId)
                .Select(s => s.Id)
                .ToListAsync();


            // =========================================================
            // 8. Validate TripSeats belong to selected Bus
            // =========================================================

            var invalidSeatIds = dto.TripSeats
                .Where(s => !busSeatIds.Contains(s.SeatId))
                .Select(s => s.SeatId)
                .Distinct()
                .ToList();

            if (invalidSeatIds.Count > 0)
            {
                return BadRequest(new
                {
                    message =
                        "One or more TripSeats do not belong to the selected Bus.",

                    invalidSeatIds
                });
            }


            // =========================================================
            // 9. Prevent duplicate SeatId
            // =========================================================

            var duplicateSeatIds = dto.TripSeats
                .GroupBy(s => s.SeatId)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (duplicateSeatIds.Count > 0)
            {
                return BadRequest(new
                {
                    message =
                        "The same physical seat cannot be added " +
                        "to a Trip more than once.",

                    duplicateSeatIds
                });
            }


            // =========================================================
            // 10. Validate TripSeat information against physical Seat
            // =========================================================

            var physicalSeats = await db.Seats
                .Where(s => s.BusId == dto.BusId)
                .ToDictionaryAsync(s => s.Id);

            foreach (var seatDto in dto.TripSeats)
            {
                if (!physicalSeats.TryGetValue(seatDto.SeatId, out var physicalSeat))
                {
                    return BadRequest(new
                    {
                        message = $"Seat {seatDto.SeatId} does not belong to this Bus."
                    });
                }

                // Optional but recommended:
                // Don't allow client to send a wrong SeatNumber.
                if (!string.Equals(
                        physicalSeat.SeatNumber,
                        seatDto.SeatNumber,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(new
                    {
                        message =
                            $"SeatNumber mismatch for SeatId {seatDto.SeatId}.",

                        expectedSeatNumber = physicalSeat.SeatNumber,
                        receivedSeatNumber = seatDto.SeatNumber
                    });
                }
            }


            // =========================================================
            // 11. Validate Departure Terminal
            // =========================================================

            var departureTerminalExists =
                await db.Terminals.AnyAsync(t =>
                    t.Id == dto.DepartureTerminalId);

            if (!departureTerminalExists)
            {
                return BadRequest(new
                {
                    message = "DepartureTerminalId does not exist.",
                    departureTerminalId = dto.DepartureTerminalId
                });
            }


            // =========================================================
            // 12. Validate Arrival Terminal
            // =========================================================

            var arrivalTerminalExists =
                await db.Terminals.AnyAsync(t =>
                    t.Id == dto.ArrivalTerminalId);

            if (!arrivalTerminalExists)
            {
                return BadRequest(new
                {
                    message = "ArrivalTerminalId does not exist.",
                    arrivalTerminalId = dto.ArrivalTerminalId
                });
            }


            // =========================================================
            // 13. Departure and Arrival terminal cannot be same
            // =========================================================

            if (dto.DepartureTerminalId == dto.ArrivalTerminalId)
            {
                return BadRequest(new
                {
                    message =
                        "DepartureTerminalId and ArrivalTerminalId cannot be the same."
                });
            }


            // =========================================================
            // 14. Validate BusRoute
            // =========================================================

            var busRouteExists =
                await db.BusRoutes.AnyAsync(r =>
                    r.Id == dto.BusRouteId);

            if (!busRouteExists)
            {
                return BadRequest(new
                {
                    message = "BusRouteId does not exist.",
                    busRouteId = dto.BusRouteId
                });
            }


            // =========================================================
            // 15. Validate Date/Time
            // =========================================================

            if (dto.ArrivalTimeUtc <= dto.DepartureTimeUtc)
            {
                return BadRequest(new
                {
                    message =
                        "ArrivalTimeUtc must be greater than DepartureTimeUtc."
                });
            }


            // =========================================================
            // 16. Update Trip master
            // =========================================================

            trip.BusOperatorId = dto.BusOperatorId;
            trip.BusRouteId = dto.BusRouteId;
            trip.BusId = dto.BusId;

            trip.DepartureTerminalId = dto.DepartureTerminalId;
            trip.ArrivalTerminalId = dto.ArrivalTerminalId;

            trip.TripCode = dto.TripCode;

            trip.DepartureTimeUtc = dto.DepartureTimeUtc;
            trip.ArrivalTimeUtc = dto.ArrivalTimeUtc;

            trip.BaseFare = dto.BaseFare;
            trip.Currency = dto.Currency;
            trip.IsWheelchairAccessible = dto.IsWheelchairAccessible;

            trip.Status = dto.Status;
            trip.DelayReason = dto.DelayReason;

            if (dto.Status != previousStatus)
            {
                db.TripStatusHistories.Add(new TripStatusHistory
                {
                    TripId = trip.Id,
                    ChangedByUserId = GetCurrentUserId(),
                    Status = dto.Status,
                    ChangedAtUtc = DateTime.UtcNow,
                    Remarks = dto.DelayReason,
                });
            }


            // =========================================================
            // 17. Transaction
            // =========================================================

            // NOTE: this still deletes-and-recreates TripSeats rather than diff-reconciling them
            // (unlike BusOperatorsController.Update(), which reconciles OperatorRoutes in place).
            // That's safe from a data-integrity standpoint — TripSeat rows that are Held/Booked
            // are protected by Restrict FKs from SeatHoldItem/Booking, so the delete below simply
            // fails with a Conflict instead of destroying an in-progress hold or a paid seat. The
            // trade-off is that editing ANY field on a Trip (e.g. just DepartureTimeUtc) is
            // blocked once a single seat on it has been held or booked. If that turns out to be
            // too restrictive in practice, apply the same reconcile-in-place pattern used for
            // OperatorRoutes here too.
            await using var transaction =
                await db.Database.BeginTransactionAsync();

            try
            {
                // -----------------------------------------------------
                // Remove existing TripSeats
                // -----------------------------------------------------

                if (trip.TripSeats.Any())
                {
                    db.TripSeats.RemoveRange(trip.TripSeats);
                }


                // -----------------------------------------------------
                // Create new TripSeats
                // -----------------------------------------------------

                var newTripSeats = dto.TripSeats
                    .Select(s =>
                    {
                        var physicalSeat = physicalSeats[s.SeatId];

                        return new TripSeat
                        {
                            TripId = trip.Id,

                            SeatId = physicalSeat.Id,

                            // Take these from physical Seat instead of
                            // trusting the client.
                            SeatNumber = physicalSeat.SeatNumber,

                            SeatType = physicalSeat.SeatType,

                            Fare = s.Fare
                        };
                    })
                    .ToList();


                // -----------------------------------------------------
                // Add TripSeats
                // -----------------------------------------------------

                await db.TripSeats.AddRangeAsync(newTripSeats);


                // -----------------------------------------------------
                // Save
                // -----------------------------------------------------

                await db.SaveChangesAsync();


                // -----------------------------------------------------
                // Commit
                // -----------------------------------------------------

                await transaction.CommitAsync();
            }
            catch (DbUpdateConcurrencyException ex)
            {
                await transaction.RollbackAsync();

                var entries = ex.Entries.Select(e => new
                {
                    Entity = e.Entity.GetType().Name,
                    State = e.State.ToString()
                });

                return Conflict(new
                {
                    message =
                        "This Trip was changed by another request. " +
                        "GET the latest Trip and retry the update.",

                    entries
                });
            }
            catch (DbUpdateException ex)
            {
                await transaction.RollbackAsync();

                return Conflict(new
                {
                    message = "Could not save this Trip update. If seats on this Trip are " +
                               "already Held or Booked, release/cancel them first.",

                    detail = ex.InnerException?.Message,

                    innerDetail =
                        ex.InnerException?.InnerException?.Message
                });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();

                return StatusCode(500, new
                {
                    message =
                        "An unexpected error occurred while updating the Trip.",

                    detail = ex.Message
                });
            }


            // =========================================================
            // 18. Reload updated Trip
            // =========================================================

            var updatedTrip = await db.Trips
                .Include(t => t.TripSeats)
                .FirstOrDefaultAsync(t => t.Id == id);

            if (updatedTrip == null)
            {
                return NotFound(new
                {
                    message =
                        "Trip was updated but could not be loaded again."
                });
            }


            // =========================================================
            // 19. Return response
            // =========================================================

            return Ok(ToResponseDto(updatedTrip));
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(Guid id)
        {
            var trip = await db.Trips.Include(t => t.TripSeats).FirstOrDefaultAsync(t => t.Id == id);
            if (trip == null) return NotFound();

            var hasBookings = await db.Bookings.AnyAsync(b => b.TripId == id);

            if (hasBookings)
            {
                // Don't cascade-delete: Bookings are real customer purchases. Soft-delete + cancel
                // the trip instead — every existing Booking, and the customer who holds it, is
                // completely untouched; the trip just stops showing up in "browse trips" listings.
                var previousStatus = trip.Status;
                trip.Status = TripStatus.Cancelled;
                trip.MarkDeleted();

                if (previousStatus != TripStatus.Cancelled)
                {
                    db.TripStatusHistories.Add(new TripStatusHistory
                    {
                        TripId = trip.Id,
                        ChangedByUserId = GetCurrentUserId(),
                        Status = TripStatus.Cancelled,
                        ChangedAtUtc = DateTime.UtcNow,
                        Remarks = "Trip has existing bookings; soft-deleted and cancelled instead of removed.",
                    });
                }

                try
                {
                    await db.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    return Conflict("This Trip was already modified or deleted by another request.");
                }

                return Ok(new
                {
                    message = "This Trip has Bookings against it, so it can't be permanently deleted without destroying customer booking history. It has been marked Cancelled and hidden from all normal queries instead — every existing Booking is untouched.",
                    softDeleted = true
                });
            }

            // TripSeat is a pure detail of this Trip — Restrict never cascades it, so clear it explicitly.
            db.TripSeats.RemoveRange(trip.TripSeats);
            db.Trips.Remove(trip);

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Conflict("This Trip was already modified or deleted by another request.");
            }
            catch (DbUpdateException)
            {
                // Safety net — e.g. a TripStatusHistory/Review/SeatHold row that also references it.
                return Conflict("Cannot delete this Trip — something still references it. Try again shortly; if this persists, contact support.");
            }

            return NoContent();
        }

        // Trip carries a single CoverImageUrl field directly — simplest of the 5 image endpoints.
        [HttpPost("{id}/images")]
        public async Task<IActionResult> UploadImage(Guid id, IFormFile file)
        {
            var trip = await db.Trips.FindAsync(id);

            if (trip == null)
            {
                return NotFound(new { message = "Trip not found." });
            }

            if (file == null || file.Length == 0)
            {
                return BadRequest(new { message = "No image uploaded." });
            }

            var allowedExtensions = new[]
            {
                ".jpg",
                ".jpeg",
                ".png",
                ".gif",
                ".webp",
                ".bmp"
            };

            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!allowedExtensions.Contains(extension))
            {
                return BadRequest(new
                {
                    message = "Only JPG, JPEG, PNG, GIF, WEBP and BMP images are allowed."
                });
            }

            const long maxFileSize = 5 * 1024 * 1024;

            if (file.Length > maxFileSize)
            {
                return BadRequest(new { message = "Image size cannot exceed 5 MB." });
            }

            var imageFolder = Path.Combine( env.WebRootPath!, "images" );
            Directory.CreateDirectory(imageFolder);

            var fileName = $"trip_{id}_{Guid.NewGuid()}{extension}";

            var filePath = Path.Combine( imageFolder, fileName );

            await using (var stream = new FileStream( filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }
            trip.CoverImageUrl = $"/images/{fileName}";
            await db.SaveChangesAsync();
            return Ok(new
            {
                message = "Trip image uploaded successfully.",
                imageUrl = trip.CoverImageUrl
            });
        }

        // Used only to stamp ChangedByUserId on the TripStatusHistory rows added above/below —
        // every other action on this controller was already fine without touching claims.
        private Guid? GetCurrentUserId()
        {
            var claim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(claim, out var id) ? id : null;
        }

        private static TripResponseDto ToResponseDto(Trip trip)
        {
            return new TripResponseDto
            {
                Id = trip.Id,

                BusOperatorId = trip.BusOperatorId,
                BusRouteId = trip.BusRouteId,
                BusId = trip.BusId,

                DepartureTerminalId = trip.DepartureTerminalId,
                ArrivalTerminalId = trip.ArrivalTerminalId,

                TripCode = trip.TripCode,

                DepartureTimeUtc = trip.DepartureTimeUtc,

                ArrivalTimeUtc = trip.ArrivalTimeUtc,

                BaseFare = trip.BaseFare,

                Currency = trip.Currency,
                IsWheelchairAccessible = trip.IsWheelchairAccessible,

                Status = trip.Status,
                CreatedAtUtc = trip.CreatedAtUtc,
                UpdatedAtUtc = trip.UpdatedAtUtc,
                DeletedAtUtc = trip.DeletedAtUtc,

                DelayReason = trip.DelayReason,

                CoverImageUrl = trip.CoverImageUrl,

                TripSeats = trip.TripSeats
                    .Select(s => new TripSeatResponseDto
                    {
                        Id = s.Id,

                        SeatId = s.SeatId,

                        SeatNumber = s.SeatNumber,

                        SeatType = s.SeatType,

                        Fare = s.Fare,

                        Status = s.Status
                    })
                    .ToList(),

                RowVersion = trip.RowVersion
            };
        }
    }
}
