// Piece 5 (Operator Back-Office & Fleet Operations) — operator scoping. 🟡 tier. SalesCounter has
// its own BusOperatorId, same shape as OperatorBranchesController — see that controller's header
// comment for the Admin/Staff/Operator role-gate note. One extra check here: OperatorBranchId is
// optional but, when set, must belong to the SAME operator as the counter itself — otherwise a
// counter could point at another operator's branch, which makes no sense and isn't caught by any
// FK constraint (OperatorBranch and SalesCounter don't share a composite key).

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
    public class SalesCountersController(AppDbContext db) : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff") && !User.IsInRole("Operator"))
            {
                return Ok(Array.Empty<SalesCounterResponseDto>());
            }

            var busOperatorId = await User.GetBusOperatorIdAsync(db);
            var query = db.SalesCounters.AsQueryable();
            if (busOperatorId != null)
            {
                query = query.Where(x => x.BusOperatorId == busOperatorId);
            }

            var items = await query.ToListAsync();
            return Ok(items.Select(ToResponseDto));
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(Guid id)
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff") && !User.IsInRole("Operator")) return Forbid();

            var item = await db.SalesCounters.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null) return NotFound();

            var busOperatorId = await User.GetBusOperatorIdAsync(db);
            if (busOperatorId != null && item.BusOperatorId != busOperatorId) return Forbid();

            return Ok(ToResponseDto(item));
        }

        // Validates that dto.OperatorBranchId (if present) is a real branch belonging to
        // targetOperatorId. Returns an error result to short-circuit on, or null if everything's
        // fine (no branch given, or the given branch checks out).
        private async Task<IActionResult?> ValidateBranchAsync(Guid? operatorBranchId, Guid targetOperatorId)
        {
            if (operatorBranchId == null) return null;

            var branchOperatorId = await db.OperatorBranches
                .Where(b => b.Id == operatorBranchId)
                .Select(b => (Guid?)b.BusOperatorId)
                .FirstOrDefaultAsync();

            if (branchOperatorId == null)
            {
                return BadRequest(new { message = "OperatorBranchId does not match a real OperatorBranch." });
            }
            if (branchOperatorId != targetOperatorId)
            {
                return BadRequest(new { message = "That OperatorBranch belongs to a different operator." });
            }
            return null;
        }

        [HttpPost]
        public async Task<IActionResult> Create(SalesCounterCreateDto dto)
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff") && !User.IsInRole("Operator")) return Forbid();

            var busOperatorId = await User.GetBusOperatorIdAsync(db);

            // Scoped (operator's own staff): the counter always belongs to THEIR operator —
            // whatever the client sent in BusOperatorId is ignored outright.
            Guid targetOperatorId;
            if (busOperatorId != null)
            {
                targetOperatorId = busOperatorId.Value;
            }
            else
            {
                if (!await db.BusOperators.AnyAsync(o => o.Id == dto.BusOperatorId))
                {
                    return BadRequest(new { message = "BusOperatorId does not match a real BusOperator." });
                }
                targetOperatorId = dto.BusOperatorId;
            }

            var branchError = await ValidateBranchAsync(dto.OperatorBranchId, targetOperatorId);
            if (branchError != null) return branchError;

            var item = new SalesCounter
            {
                BusOperatorId = targetOperatorId,
                TerminalId = dto.TerminalId,
                OperatorBranchId = dto.OperatorBranchId,
                CounterName = dto.CounterName,
                CounterCode = dto.CounterCode,
                PhoneNumber = dto.PhoneNumber,
                Address = dto.Address,
                IsActive = dto.IsActive,
            };

            db.SalesCounters.Add(item);
            await db.SaveChangesAsync();

            return CreatedAtAction(nameof(GetById), new { id = item.Id }, ToResponseDto(item));
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(Guid id, SalesCounterUpdateDto dto)
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff") && !User.IsInRole("Operator")) return Forbid();

            var item = await db.SalesCounters.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null) return NotFound(new { message = "SalesCounter not found." });

            var busOperatorId = await User.GetBusOperatorIdAsync(db);
            if (busOperatorId != null && item.BusOperatorId != busOperatorId) return Forbid();

            if (dto.RowVersion == null || dto.RowVersion.Length == 0)
                return BadRequest(new { message = "RowVersion is required." });

            if (!item.RowVersion.SequenceEqual(dto.RowVersion))
            {
                return Conflict(new
                {
                    message = "This SalesCounter was changed by another request. Please GET the latest data and try again."
                });
            }

            db.Entry(item).Property(x => x.RowVersion).OriginalValue = dto.RowVersion;

            // Scoped staff can edit their own counter's details but can never move it to another
            // operator. Only unscoped Admin/Staff can reassign BusOperatorId.
            Guid targetOperatorId;
            if (busOperatorId == null)
            {
                if (!await db.BusOperators.AnyAsync(o => o.Id == dto.BusOperatorId))
                {
                    return BadRequest(new { message = "BusOperatorId does not match a real BusOperator." });
                }
                item.BusOperatorId = dto.BusOperatorId;
                targetOperatorId = dto.BusOperatorId;
            }
            else
            {
                targetOperatorId = busOperatorId.Value;
            }

            var branchError = await ValidateBranchAsync(dto.OperatorBranchId, targetOperatorId);
            if (branchError != null) return branchError;

            item.TerminalId = dto.TerminalId;
            item.OperatorBranchId = dto.OperatorBranchId;
            item.CounterName = dto.CounterName;
            item.CounterCode = dto.CounterCode;
            item.PhoneNumber = dto.PhoneNumber;
            item.Address = dto.Address;
            item.IsActive = dto.IsActive;
            item.UpdatedAtUtc = DateTime.UtcNow;

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Conflict(new { message = "This SalesCounter was already modified or deleted by another request." });
            }
            catch (DbUpdateException ex)
            {
                var error = ex.InnerException?.InnerException?.Message ?? ex.InnerException?.Message ?? ex.Message;
                return Conflict(new { message = "Could not save SalesCounter.", details = error });
            }

            return Ok(ToResponseDto(item));
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(Guid id)
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff") && !User.IsInRole("Operator")) return Forbid();

            var item = await db.SalesCounters.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null) return NotFound();

            var busOperatorId = await User.GetBusOperatorIdAsync(db);
            if (busOperatorId != null && item.BusOperatorId != busOperatorId) return Forbid();

            // Soft delete — real business data is never hard-deleted (see AuditableEntity.MarkDeleted).
            item.MarkDeleted();

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Conflict(new { message = "This SalesCounter was already modified or deleted by another request." });
            }
            catch (DbUpdateException)
            {
                return Conflict(new { message = "Cannot delete this SalesCounter — it is still referenced by other records." });
            }

            return NoContent();
        }

        private static SalesCounterResponseDto ToResponseDto(SalesCounter x) => new()
        {
            Id = x.Id,
            BusOperatorId = x.BusOperatorId,
            TerminalId = x.TerminalId,
            OperatorBranchId = x.OperatorBranchId,
            CounterName = x.CounterName,
            CounterCode = x.CounterCode,
            PhoneNumber = x.PhoneNumber,
            Address = x.Address,
            IsActive = x.IsActive,
            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc,
            RowVersion = x.RowVersion,
        };
    }
}
