// Piece 6 (People/HR & ERP Integrations) — operator scoping. 🟡 tier, same shape as
// DriverLicensesController/StaffAttendancesController — but this is the one the plan calls out
// "especially": compensation data was reachable by literally any logged-in customer before this.
// Same two-part fix: customers blocked entirely; among Staff, an operator's own staff only
// see/write salary records for their OWN operator's employees; platform Staff/Admin see
// everything. StaffProfileId on Create is verified against that scope rather than trusted as-is.
//
// Amount/IsPaid/PaidAtUtc/PaymentReference stay directly editable here — this stays the same
// 🟡 "scope the existing CRUD" shape as the rest of the piece, not a new service. A real payroll
// workflow (e.g. require a real bank reference before flipping IsPaid, the way
// OperatorPayoutsController's approve→process shape works in Piece 3) is real feature work and
// out of scope here — flag as a follow-up if payroll disputes ever come up in practice.

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
    public class StaffSalariesController(AppDbContext db) : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff"))
            {
                return Ok(Array.Empty<StaffSalaryResponseDto>());
            }

            var query = db.StaffSalaries.AsQueryable();

            if (!User.IsInRole("Admin"))
            {
                var scopeOperatorId = await User.GetBusOperatorIdAsync(db);
                if (scopeOperatorId != null)
                {
                    query = query.Where(s => db.StaffProfiles.Any(sp =>
                        sp.Id == s.StaffProfileId && sp.BusOperatorId == scopeOperatorId));
                }
            }

            var items = await query.ToListAsync();
            return Ok(items.Select(ToResponseDto));
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(Guid id)
        {
            var item = await db.StaffSalaries.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null) return NotFound();
            if (!await CanAccessAsync(item.StaffProfileId)) return Forbid();
            return Ok(ToResponseDto(item));
        }

        [HttpPost]
        public async Task<IActionResult> Create(StaffSalaryCreateDto dto)
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff")) return Forbid();
            if (!await CanAccessAsync(dto.StaffProfileId))
            {
                return BadRequest(new { message = "That staff member doesn't belong to your operator." });
            }

            var item = new StaffSalary
            {
                StaffProfileId = dto.StaffProfileId,
                PayPeriodStart = dto.PayPeriodStart,
                PayPeriodEnd = dto.PayPeriodEnd,
                Amount = dto.Amount,
                IsPaid = dto.IsPaid,
                PaidAtUtc = dto.PaidAtUtc,
                PaymentReference = dto.PaymentReference,
            };

            db.StaffSalaries.Add(item);
            await db.SaveChangesAsync();

            return CreatedAtAction(nameof(GetById), new { id = item.Id }, ToResponseDto(item));
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(Guid id, StaffSalaryUpdateDto dto)
        {
            var item = await db.StaffSalaries.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null) return NotFound(new { message = "StaffSalary not found." });
            if (!await CanAccessAsync(item.StaffProfileId)) return Forbid();

            if (dto.RowVersion == null || dto.RowVersion.Length == 0)
                return BadRequest(new { message = "RowVersion is required." });

            if (!item.RowVersion.SequenceEqual(dto.RowVersion))
            {
                return Conflict(new
                {
                    message = "This StaffSalary was changed by another request. Please GET the latest data and try again."
                });
            }

            db.Entry(item).Property(x => x.RowVersion).OriginalValue = dto.RowVersion;

            // StaffProfileId deliberately never touched here — same reasoning as DriverLicense.
            item.PayPeriodStart = dto.PayPeriodStart;
            item.PayPeriodEnd = dto.PayPeriodEnd;
            item.Amount = dto.Amount;
            item.IsPaid = dto.IsPaid;
            item.PaidAtUtc = dto.PaidAtUtc;
            item.PaymentReference = dto.PaymentReference;
            item.UpdatedAtUtc = DateTime.UtcNow;

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Conflict(new { message = "This StaffSalary was already modified or deleted by another request." });
            }
            catch (DbUpdateException ex)
            {
                var error = ex.InnerException?.InnerException?.Message ?? ex.InnerException?.Message ?? ex.Message;
                return Conflict(new { message = "Could not save StaffSalary.", details = error });
            }

            return Ok(ToResponseDto(item));
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(Guid id)
        {
            var item = await db.StaffSalaries.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null) return NotFound();
            if (!await CanAccessAsync(item.StaffProfileId)) return Forbid();

            // Soft delete — real business data is never hard-deleted (see AuditableEntity.MarkDeleted).
            item.MarkDeleted();

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Conflict(new { message = "This StaffSalary was already modified or deleted by another request." });
            }
            catch (DbUpdateException)
            {
                return Conflict(new { message = "Cannot delete this StaffSalary — it is still referenced by other records." });
            }

            return NoContent();
        }

        // Same operator-scoping helper shape as DriverLicensesController — see its comment.
        private async Task<bool> CanAccessAsync(Guid staffProfileId)
        {
            if (User.IsInRole("Admin")) return true;
            if (!User.IsInRole("Staff")) return false;

            var scopeOperatorId = await User.GetBusOperatorIdAsync(db);
            if (scopeOperatorId == null) return true;

            return await db.StaffProfiles.AnyAsync(sp =>
                sp.Id == staffProfileId && sp.BusOperatorId == scopeOperatorId);
        }

        private static StaffSalaryResponseDto ToResponseDto(StaffSalary x) => new()
        {
            Id = x.Id,
            StaffProfileId = x.StaffProfileId,
            PayPeriodStart = x.PayPeriodStart,
            PayPeriodEnd = x.PayPeriodEnd,
            Amount = x.Amount,
            IsPaid = x.IsPaid,
            PaidAtUtc = x.PaidAtUtc,
            PaymentReference = x.PaymentReference,
            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc,
            RowVersion = x.RowVersion,
        };
    }
}
