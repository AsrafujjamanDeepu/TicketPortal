// Piece 1 (Identity, Access Control & Platform Configuration) — Admin-only gate. 🟡 tier per the
// completion plan: structurally fine as generic CRUD, this only ever needed locking down, no new
// service. Was reachable read/write by any authenticated user; now Admin-only end to end — a
// customer or unscoped staff account being able to zero out (or inflate) an operator's commission
// rate is a direct way to manipulate money owed. Real Staff/Operator role-scoping
// (StaffProfile.BusOperatorId) doesn't apply here since this is platform-wide reference/finance
// data, not any one operator's own rows.

using TicketPortal.Api.Data;
using TicketPortal.Api.DTO;
using TicketPortal.Api.Models.Enums;
using TicketPortal.Api.Models.Finance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace TicketPortal.Api.Controllers
{
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class CommissionRulesController(AppDbContext db) : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            if (!User.IsInRole("Admin"))
            {
                return Ok(Array.Empty<CommissionRuleResponseDto>());
            }

            var items = await db.CommissionRules.ToListAsync();
            return Ok(items.Select(ToResponseDto));
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(Guid id)
        {
            if (!User.IsInRole("Admin")) return Forbid();

            var item = await db.CommissionRules.FirstOrDefaultAsync(x => x.Id == id);
            return item == null ? NotFound() : Ok(ToResponseDto(item));
        }

        [HttpPost]
        public async Task<IActionResult> Create(CommissionRuleCreateDto dto)
        {
            if (!User.IsInRole("Admin")) return Forbid();

            if (!IsCommissionValueValid(dto.CommissionType, dto.CommissionValue, out var validationError))
            {
                return BadRequest(new { message = validationError });
            }

            var item = new CommissionRule
            {
                BusOperatorId = dto.BusOperatorId,
                OperatorContractId = dto.OperatorContractId,
                BusRouteId = dto.BusRouteId,
                SaleChannel = dto.SaleChannel,
                CommissionType = dto.CommissionType,
                CommissionValue = dto.CommissionValue,
                EffectiveFrom = dto.EffectiveFrom,
                EffectiveTo = dto.EffectiveTo,
                IsActive = dto.IsActive,
            };

            db.CommissionRules.Add(item);
            await db.SaveChangesAsync();

            return CreatedAtAction(nameof(GetById), new { id = item.Id }, ToResponseDto(item));
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(Guid id, CommissionRuleUpdateDto dto)
        {
            if (!User.IsInRole("Admin")) return Forbid();

            var item = await db.CommissionRules.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null) return NotFound(new { message = "CommissionRule not found." });

            if (dto.RowVersion == null || dto.RowVersion.Length == 0)
                return BadRequest(new { message = "RowVersion is required." });

            if (!item.RowVersion.SequenceEqual(dto.RowVersion))
            {
                return Conflict(new
                {
                    message = "This CommissionRule was changed by another request. Please GET the latest data and try again."
                });
            }

            if (!IsCommissionValueValid(dto.CommissionType, dto.CommissionValue, out var validationError))
            {
                return BadRequest(new { message = validationError });
            }

            db.Entry(item).Property(x => x.RowVersion).OriginalValue = dto.RowVersion;

            item.BusOperatorId = dto.BusOperatorId;
            item.OperatorContractId = dto.OperatorContractId;
            item.BusRouteId = dto.BusRouteId;
            item.SaleChannel = dto.SaleChannel;
            item.CommissionType = dto.CommissionType;
            item.CommissionValue = dto.CommissionValue;
            item.EffectiveFrom = dto.EffectiveFrom;
            item.EffectiveTo = dto.EffectiveTo;
            item.IsActive = dto.IsActive;
            item.UpdatedAtUtc = DateTime.UtcNow;

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Conflict(new { message = "This CommissionRule was already modified or deleted by another request." });
            }
            catch (DbUpdateException ex)
            {
                var error = ex.InnerException?.InnerException?.Message ?? ex.InnerException?.Message ?? ex.Message;
                return Conflict(new { message = "Could not save CommissionRule.", details = error });
            }

            return Ok(ToResponseDto(item));
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(Guid id)
        {
            if (!User.IsInRole("Admin")) return Forbid();

            var item = await db.CommissionRules.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null) return NotFound();

            // Soft delete — real business data is never hard-deleted (see AuditableEntity.MarkDeleted).
            item.MarkDeleted();

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Conflict(new { message = "This CommissionRule was already modified or deleted by another request." });
            }
            catch (DbUpdateException)
            {
                return Conflict(new { message = "Cannot delete this CommissionRule — it is still referenced by other records." });
            }

            return NoContent();
        }

        // CommissionRuleCreateDto.CommissionValue is only non-negative-checked at the DTO level
        // ([Range(0, double.MaxValue)]) because a flat CommissionValue can legitimately exceed
        // 100. A Percentage-type value can't, though — this is the one bound that genuinely
        // depends on a sibling field, so it lives here rather than as a data annotation.
        private static bool IsCommissionValueValid(CommissionType type, decimal value, out string? error)
        {
            if (type == CommissionType.Percentage && value > 100)
            {
                error = "CommissionValue cannot exceed 100 when CommissionType is Percentage.";
                return false;
            }

            error = null;
            return true;
        }

        private static CommissionRuleResponseDto ToResponseDto(CommissionRule x) => new()
        {
            Id = x.Id,
            BusOperatorId = x.BusOperatorId,
            OperatorContractId = x.OperatorContractId,
            BusRouteId = x.BusRouteId,
            SaleChannel = x.SaleChannel,
            CommissionType = x.CommissionType,
            CommissionValue = x.CommissionValue,
            EffectiveFrom = x.EffectiveFrom,
            EffectiveTo = x.EffectiveTo,
            IsActive = x.IsActive,
            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc,
            RowVersion = x.RowVersion,
        };
    }
}