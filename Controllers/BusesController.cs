using TicketPortal.Api.Data;
using TicketPortal.Api.DTO;
using TicketPortal.Api.Extensions;
using TicketPortal.Api.Models.BusFleet;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace TicketPortal.Api.Controllers
{
    // Master = Bus, Details = Seat (a bus's seat layout, created/replaced together).
    // This controller is the "reference" one — every other controller in this project follows
    // the exact same skeleton (GetAll -> GetById -> Create -> Update -> Delete -> UploadImage)
    // and several of them point their comments back here instead of repeating explanations.
    //
    // Read stays open to any logged-in user (buses show up in search results/booking screens
    // regardless of who's looking). Writes are operator-scoped: platform Admin/Staff can touch
    // any Bus, an operator's own staff only their own operator's buses, and everyone else is
    // refused. Previously this controller had NO role/ownership check at all — any logged-in
    // customer could create or edit a Bus under any operator's BusOperatorId.
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class BusesController(AppDbContext db, IWebHostEnvironment env) : ControllerBase
    {
        // IMPORTANT: .ToListAsync() runs FIRST, materializing real Bus entities from the database.
        // ToResponseDto() then runs as plain in-memory LINQ-to-Objects. Putting ToResponseDto
        // straight inside .Select() on the IQueryable would make EF Core try to translate that
        // method call into SQL — which it can't do for a hand-written mapping method — and throw
        // an InvalidOperationException at request time (this is a runtime failure, so a project
        // that BUILDS fine can still crash the first time this endpoint is actually called).
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var buses = await db.Buses
                .Include(b => b.Seats)
                .Include(b => b.Images)
                .ToListAsync();

            return Ok(buses.Select(ToResponseDto));
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(Guid id)
        {
            var bus = await db.Buses
                .Include(b => b.Seats)
                .Include(b => b.Images)
                .FirstOrDefaultAsync(b => b.Id == id);

            return bus == null ? NotFound() : Ok(ToResponseDto(bus));
        }

        [HttpPost]
        public async Task<IActionResult> Create(BusCreateDto dto)
        {
            // ----------------------------------------
            // 1. Authorization — operator scoping
            // ----------------------------------------
            if (!await User.CanManageOperatorAsync(db, dto.BusOperatorId))
            {
                return Forbid();
            }

            // ----------------------------------------
            // 2. Validate BusOperatorId exists
            // ----------------------------------------
            var operatorExists = await db.BusOperators.AnyAsync(o => o.Id == dto.BusOperatorId);
            if (!operatorExists)
            {
                return BadRequest(new { message = "BusOperatorId does not exist.", busOperatorId = dto.BusOperatorId });
            }

            var bus = new Bus
            {
                BusOperatorId = dto.BusOperatorId,
                RegistrationNumber = dto.RegistrationNumber,
                CoachNumber = dto.CoachNumber,
                Brand = dto.Brand,
                Model = dto.Model,
                RegistrationDate = dto.RegistrationDate,
                BusType = dto.BusType,
                TotalSeats = dto.TotalSeats,
                HasWifi = dto.HasWifi,
                HasToilet = dto.HasToilet,
                Seats = dto.Seats.Select(s => new Seat
                {
                    SeatNumber = s.SeatNumber,
                    RowNumber = s.RowNumber,
                    ColumnNumber = s.ColumnNumber,
                    DeckLevel = s.DeckLevel,
                    SeatType = s.SeatType,
                    IsWindow = s.IsWindow,
                    ExtraFare = s.ExtraFare
                }).ToList()
            };

            db.Buses.Add(bus);
            await db.SaveChangesAsync();

            return CreatedAtAction(nameof(GetById), new { id = bus.Id }, ToResponseDto(bus));
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(Guid id, BusUpdateDto dto)
        {
            // ----------------------------------------
            // 1. Load Bus + existing Seats
            // ----------------------------------------
            var bus = await db.Buses
                .Include(b => b.Seats)
                .FirstOrDefaultAsync(b => b.Id == id);

            if (bus == null)
            {
                return NotFound(new { message = "Bus not found." });
            }

            // ----------------------------------------
            // 1a. Authorization — operator scoping, checked against both the Bus's current
            // operator and dto.BusOperatorId (in case of an attempted reassignment)
            // ----------------------------------------
            if (!await User.CanManageOperatorAsync(db, bus.BusOperatorId) || !await User.CanManageOperatorAsync(db, dto.BusOperatorId))
            {
                return Forbid();
            }

            var operatorExists = await db.BusOperators.AnyAsync(o => o.Id == dto.BusOperatorId);
            if (!operatorExists)
            {
                return BadRequest(new { message = "BusOperatorId does not exist.", busOperatorId = dto.BusOperatorId });
            }

            // ----------------------------------------
            // 2. Validate RowVersion
            // ----------------------------------------
            if (dto.RowVersion == null || dto.RowVersion.Length == 0)
            {
                return BadRequest(new
                { message = "RowVersion is required. " + "GET the Bus first and send the latest RowVersion." });
            }

            // ----------------------------------------
            // 3. Set original RowVersion
            // ----------------------------------------
            db.Entry(bus)
                .Property(b => b.RowVersion)
                .OriginalValue = dto.RowVersion;

            // ----------------------------------------
            // 4. Update Bus
            // ----------------------------------------
            bus.BusOperatorId = dto.BusOperatorId;
            bus.RegistrationNumber = dto.RegistrationNumber;
            bus.CoachNumber = dto.CoachNumber;
            bus.Brand = dto.Brand;
            bus.Model = dto.Model;
            bus.RegistrationDate = dto.RegistrationDate;
            bus.BusType = dto.BusType;
            bus.TotalSeats = dto.TotalSeats;
            bus.HasWifi = dto.HasWifi;
            bus.HasToilet = dto.HasToilet;
            bus.IsActive = dto.IsActive;

            await using var transaction = await db.Database.BeginTransactionAsync();

            try
            {
                // ----------------------------------------
                // 5. Delete old Seats
                // ----------------------------------------
                var oldSeats = await db.Seats.Where(s => s.BusId == id).ToListAsync();

                if (oldSeats.Count > 0)
                {
                    db.Seats.RemoveRange(oldSeats);
                }

                // ----------------------------------------
                // 6. Create new Seats
                // ----------------------------------------
                var newSeats = dto.Seats.Select(s => new Seat
                {
                    BusId = id,
                    SeatNumber = s.SeatNumber,
                    RowNumber = s.RowNumber,
                    ColumnNumber = s.ColumnNumber,
                    DeckLevel = s.DeckLevel,
                    SeatType = s.SeatType,
                    IsWindow = s.IsWindow,
                    ExtraFare = s.ExtraFare
                }).ToList();

                if (newSeats.Count > 0)
                {
                    await db.Seats.AddRangeAsync(newSeats);
                }

                // ----------------------------------------
                // 7. Save Bus + Seats together
                // ----------------------------------------
                await db.SaveChangesAsync();

                // ----------------------------------------
                // 8. Commit transaction
                // ----------------------------------------
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
                    message = "This Bus was changed by another request. " + "GET the latest Bus and retry the update.", entries
                });
            }
            catch (DbUpdateException ex)
            {
                await transaction.RollbackAsync();

                return Conflict(new
                { message = "Could not save Bus update.", detail = ex.InnerException?.Message });
            }

            // ----------------------------------------
            // 9. Reload fresh Bus + Seats
            // ----------------------------------------
            var updatedBus = await db.Buses.Include(b => b.Seats).FirstOrDefaultAsync(b => b.Id == id);

            if (updatedBus == null)
            {
                return NotFound(new { message = "Bus was updated but could not be loaded again." });
            }

            // ----------------------------------------
            // 10. Return response
            // ----------------------------------------
            return Ok(ToResponseDto(updatedBus));
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(Guid id)
        {
            var bus = await db.Buses
                .Include(b => b.Seats)
                .Include(b => b.Images)
                .Include(b => b.AmenityMappings)
                .Include(b => b.MaintenanceLogs)
                .FirstOrDefaultAsync(b => b.Id == id);
            if (bus == null) return NotFound();

            if (!await User.CanManageOperatorAsync(db, bus.BusOperatorId)) return Forbid();

            var hasTrips = await db.Trips.AnyAsync(t => t.BusId == id);
            var hasSchedules = await db.Schedules.AnyAsync(s => s.BusId == id);

            if (hasTrips || hasSchedules)
            {
                // Don't cascade-delete: those Trips/Schedules can have Bookings (real customer
                // purchases) hanging off them, and DELETE tearing through that would destroy
                // financial and customer history. Soft-delete instead — hides this Bus from every
                // normal query without removing a single row.
                bus.IsActive = false;
                bus.MarkDeleted();

                try
                {
                    await db.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    return Conflict("This Bus was already modified or deleted by another request.");
                }

                return Ok(new
                {
                    message = "This Bus has Trips or Schedules against it, so it can't be permanently deleted without destroying that history. It has been deactivated instead — hidden from all normal queries — but nothing was destroyed.",
                    softDeleted = true
                });
            }

            // Seat, BusImage, BusAmenityMapping, and BusMaintenanceLog are all pure details of this
            // Bus — none of them have independent meaning without it, so it's safe to hard-delete
            // them alongside the Bus itself. Restrict never cascades any of these on its own, so
            // each one is cleared explicitly before removing the Bus row.
            db.Seats.RemoveRange(bus.Seats);
            db.BusImages.RemoveRange(bus.Images);
            db.BusAmenityMappings.RemoveRange(bus.AmenityMappings);
            db.BusMaintenanceLogs.RemoveRange(bus.MaintenanceLogs);
            db.Buses.Remove(bus);

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Conflict("This Bus was already modified or deleted by another request.");
            }
            catch (DbUpdateException)
            {
                // Safety net for anything outside the checks above that still references this Bus.
                return Conflict("Cannot delete this Bus — something still references it. Try again shortly; if this persists, contact support.");
            }

            return NoContent();
        }

        // Stores the uploaded photo as a BusImage row (the entity that already exists for this),
        // marking it primary and demoting any previous primary image.
        [HttpPost("{id}/images")]
        public async Task<IActionResult> UploadImage(Guid id, IFormFile file)
        {
            var bus = await db.Buses.Include(b => b.Images).FirstOrDefaultAsync(b => b.Id == id);
            if (bus == null) return NotFound();
            if (!await User.CanManageOperatorAsync(db, bus.BusOperatorId)) return Forbid();

            var validationError = TicketPortal.Api.Extensions.FileUploadValidation.Validate(file);
            if (validationError != null) return validationError;

            var fileName = $"bus_{id}_{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";
            var path = Path.Combine(env.WebRootPath!, "images", fileName);
            await using (var stream = System.IO.File.Create(path))
            {
                await file.CopyToAsync(stream);
            }

            foreach (var existing in bus.Images)
            {
                existing.IsPrimary = false;
            }

            db.BusImages.Add(new BusImage
            {
                BusId = id,
                ImageUrl = $"/images/{fileName}",
                IsPrimary = true
            });

            await db.SaveChangesAsync();
            return Ok(new { imageUrl = $"/images/{fileName}" });
        }

        private static BusResponseDto ToResponseDto(Bus bus) => new()
        {
            Id = bus.Id,
            BusOperatorId = bus.BusOperatorId,
            RegistrationNumber = bus.RegistrationNumber,
            CoachNumber = bus.CoachNumber,
            CreatedAtUtc = bus.CreatedAtUtc,
            UpdatedAtUtc = bus.UpdatedAtUtc,
            DeletedAtUtc = bus.DeletedAtUtc,
            Brand = bus.Brand,
            Model = bus.Model,
            RegistrationDate = bus.RegistrationDate,
            BusType = bus.BusType,
            TotalSeats = bus.TotalSeats,
            HasWifi = bus.HasWifi,
            HasToilet = bus.HasToilet,
            IsActive = bus.IsActive,

            PrimaryImageUrl = bus.Images
                .FirstOrDefault(i => i.IsPrimary)?.ImageUrl,

            Seats = bus.Seats.Select(s => new SeatResponseDto
            {
                Id = s.Id,
                SeatNumber = s.SeatNumber,
                RowNumber = s.RowNumber,
                ColumnNumber = s.ColumnNumber,
                DeckLevel = s.DeckLevel,
                SeatType = s.SeatType,
                IsWindow = s.IsWindow,
                ExtraFare = s.ExtraFare,
                IsActive = s.IsActive
            }).ToList(),

            // IMPORTANT: return current RowVersion
            RowVersion = bus.RowVersion
        };

        // Operator-scoping auth helper used to be duplicated per-controller here — it's now
        // the single User.CanManageOperatorAsync(db, ...) extension in
        // Extensions/ClaimsPrincipalExtensions.cs (Piece 2), used above and by every other
        // controller that needs the same check.
    }
}
