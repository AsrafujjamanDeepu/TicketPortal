// Piece 6 (People/HR & ERP Integrations) — operator scoping. 🟡 tier, same two-part gate as the
// rest of this HR bucket: customers blocked entirely; among Staff, an operator's own staff only
// see/write their OWN operator's staff profiles; platform Staff/Admin see everything.
//
// This is the one controller in the bucket where the scoped field (BusOperatorId) lives
// directly on the entity rather than behind a StaffProfileId join, and it's also the actual
// security boundary, not just a visibility filter: without checking it on Create, an operator's
// own staff could plant a profile under a DIFFERENT operator (or under no operator at all —
// i.e. claim platform-staff scope) purely by choosing what to put in the request body. So
// BusOperatorId is verified/overridden on Create rather than trusted as-is, and dropped from
// Update entirely (see PeopleDtos.cs) — never reassignable after creation via this endpoint.
//
// Note this is a secondary path: the normal way a brand-new Staff/Operator account comes into
// being is AdminController.CreateStaff (Piece 1), which creates the login AND this profile
// together. This controller covers everything else — attaching a profile to an existing login,
// and every read/update/delete afterwards.

using TicketPortal.Api.Data;
using TicketPortal.Api.DTO;
using TicketPortal.Api.Extensions;
using TicketPortal.Api.Models.People;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace TicketPortal.Api.Controllers
{
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class StaffProfilesController(AppDbContext db) : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff") && !User.IsInRole("Operator"))
            {
                return Ok(Array.Empty<StaffProfileResponseDto>());
            }

            var query = db.StaffProfiles.AsQueryable();

            if (!User.IsInRole("Admin"))
            {
                var scopeOperatorId = await User.GetBusOperatorIdAsync(db);
                if (scopeOperatorId != null)
                {
                    query = query.Where(sp => sp.BusOperatorId == scopeOperatorId);
                }
            }

            var items = await query.ToListAsync();
            return Ok(items.Select(ToResponseDto));
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(Guid id)
        {
            var item = await db.StaffProfiles.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null) return NotFound();
            if (!await CanAccessAsync(item)) return Forbid();
            return Ok(ToResponseDto(item));
        }

        [HttpPost]
        public async Task<IActionResult> Create(StaffProfileCreateDto dto)
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff") && !User.IsInRole("Operator")) return Forbid();

            var busOperatorId = dto.BusOperatorId;
            if (!User.IsInRole("Admin"))
            {
                var scopeOperatorId = await User.GetBusOperatorIdAsync(db);
                if (scopeOperatorId != null)
                {
                    if (dto.BusOperatorId != scopeOperatorId)
                    {
                        return BadRequest(new { message = "You can only create staff profiles for your own operator." });
                    }
                    busOperatorId = scopeOperatorId;
                }
                // else: platform Staff — allowed to set any BusOperatorId, including null.
            }

            var item = new StaffProfile
            {
                UserId = dto.UserId,
                BusOperatorId = busOperatorId,
                EmployeeCode = dto.EmployeeCode,
                Role = dto.Role,
                NationalIdNumber = dto.NationalIdNumber,
                JoiningDate = dto.JoiningDate,
                Address = dto.Address,
                TotalTripsCompleted = dto.TotalTripsCompleted,
                IsActive = dto.IsActive,
            };

            db.StaffProfiles.Add(item);

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                var error = ex.InnerException?.InnerException?.Message ?? ex.InnerException?.Message ?? ex.Message;
                return Conflict(new { message = "Could not save StaffProfile.", details = error });
            }

            return CreatedAtAction(nameof(GetById), new { id = item.Id }, ToResponseDto(item));
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(Guid id, StaffProfileUpdateDto dto)
        {
            var item = await db.StaffProfiles.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null) return NotFound(new { message = "StaffProfile not found." });
            if (!await CanAccessAsync(item)) return Forbid();

            if (dto.RowVersion == null || dto.RowVersion.Length == 0)
                return BadRequest(new { message = "RowVersion is required." });

            if (!item.RowVersion.SequenceEqual(dto.RowVersion))
            {
                return Conflict(new
                {
                    message = "This StaffProfile was changed by another request. Please GET the latest data and try again."
                });
            }

            db.Entry(item).Property(x => x.RowVersion).OriginalValue = dto.RowVersion;

            // UserId/BusOperatorId deliberately never touched here — see file header.
            item.EmployeeCode = dto.EmployeeCode;
            item.Role = dto.Role;
            item.NationalIdNumber = dto.NationalIdNumber;
            item.JoiningDate = dto.JoiningDate;
            item.Address = dto.Address;
            item.TotalTripsCompleted = dto.TotalTripsCompleted;
            item.IsActive = dto.IsActive;
            item.UpdatedAtUtc = DateTime.UtcNow;

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Conflict(new { message = "This StaffProfile was already modified or deleted by another request." });
            }
            catch (DbUpdateException ex)
            {
                var error = ex.InnerException?.InnerException?.Message ?? ex.InnerException?.Message ?? ex.Message;
                return Conflict(new { message = "Could not save StaffProfile.", details = error });
            }

            return Ok(ToResponseDto(item));
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(Guid id)
        {
            var item = await db.StaffProfiles.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null) return NotFound();
            if (!await CanAccessAsync(item)) return Forbid();

            // Soft delete — real business data is never hard-deleted (see AuditableEntity.MarkDeleted).
            item.MarkDeleted();

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Conflict(new { message = "This StaffProfile was already modified or deleted by another request." });
            }
            catch (DbUpdateException)
            {
                return Conflict(new { message = "Cannot delete this StaffProfile — it is still referenced by other records." });
            }

            return NoContent();
        }

        // Same operator-scoping pattern as the rest of this bucket, applied directly to the
        // entity's own BusOperatorId rather than through a join — see DriverLicensesController
        // for the joined-entity version of this same helper shape.
        private async Task<bool> CanAccessAsync(StaffProfile item)
        {
            if (User.IsInRole("Admin")) return true;
            if (!User.IsInRole("Staff") && !User.IsInRole("Operator")) return false;

            var scopeOperatorId = await User.GetBusOperatorIdAsync(db);
            return scopeOperatorId == null || item.BusOperatorId == scopeOperatorId;
        }

        private static StaffProfileResponseDto ToResponseDto(StaffProfile x) => new()
        {
            Id = x.Id,
            UserId = x.UserId,
            BusOperatorId = x.BusOperatorId,
            EmployeeCode = x.EmployeeCode,
            Role = x.Role,
            NationalIdNumber = x.NationalIdNumber,
            JoiningDate = x.JoiningDate,
            Address = x.Address,
            TotalTripsCompleted = x.TotalTripsCompleted,
            IsActive = x.IsActive,
            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc,
            RowVersion = x.RowVersion,
        };
    }
}
