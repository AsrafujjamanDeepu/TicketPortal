using TicketPortal.Api.Data;
using TicketPortal.Api.DTO;
using TicketPortal.Api.Extensions;
using TicketPortal.Api.Models.Finance;
using TicketPortal.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace TicketPortal.Api.Controllers
{
    // A payout only moves to Paid once there's a real bank reference confirming money actually
    // left the account — the old generic PUT let any authenticated user set Status = Paid with a
    // made-up BankTransactionReference and no check against what was actually available to pay
    // out. Every write here now goes through PayoutProcessingService (Create reserves the
    // amount atomically, Complete requires a bank reference, Fail/Cancel release the
    // reservation) — see that file for the full lifecycle. Admin/platform-Staff manage every
    // operator's payouts; an operator's own Staff/Operator account is scoped to its own
    // operator's payouts only (Piece 1).
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class OperatorPayoutsController(AppDbContext db, PayoutProcessingService payoutProcessingService) : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> GetAll([FromQuery] Guid? busOperatorId)
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff") && !User.IsInRole("Operator"))
            {
                return Ok(Array.Empty<OperatorPayoutResponseDto>());
            }

            var query = db.OperatorPayouts.AsQueryable();

            var callerOperatorId = await User.GetBusOperatorIdAsync(db);
            if (callerOperatorId != null)
            {
                query = query.Where(p => p.BusOperatorId == callerOperatorId.Value);
            }
            else if (busOperatorId.HasValue)
            {
                query = query.Where(p => p.BusOperatorId == busOperatorId.Value);
            }

            var items = await query.OrderByDescending(p => p.CreatedAtUtc).ToListAsync();
            return Ok(items.Select(ToResponseDto));
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(Guid id)
        {
            var item = await db.OperatorPayouts.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null) return NotFound();
            if (!await User.CanManageOperatorAsync(db, item.BusOperatorId)) return Forbid();
            return Ok(ToResponseDto(item));
        }

        // Reserves the amount from AvailablePayoutBalance immediately — see
        // PayoutProcessingService.CreateAsync.
        [HttpPost]
        public async Task<IActionResult> Create(OperatorPayoutCreateDto dto)
        {
            if (!await User.CanManageOperatorAsync(db, dto.BusOperatorId)) return Forbid();

            try
            {
                var payout = await payoutProcessingService.CreateAsync(
                    dto.BusOperatorId, dto.Amount, dto.Currency, dto.OperatorSettlementId, dto.Notes);
                return CreatedAtAction(nameof(GetById), new { id = payout.Id }, ToResponseDto(payout));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // Pending -> Processing: staff confirms they've started the actual bank transfer.
        [HttpPost("{id}/process")]
        public async Task<IActionResult> Process(Guid id)
        {
            var access = await CheckAccessAsync(id);
            if (access == AccessResult.NotFound) return NotFound();
            if (access == AccessResult.Forbidden) return Forbid();

            try
            {
                await payoutProcessingService.MarkProcessingAsync(id);
                return NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // -> Paid: the step that actually confirms money left the account.
        [HttpPost("{id}/complete")]
        public async Task<IActionResult> Complete(Guid id, OperatorPayoutCompleteDto dto)
        {
            var access = await CheckAccessAsync(id);
            if (access == AccessResult.NotFound) return NotFound();
            if (access == AccessResult.Forbidden) return Forbid();

            try
            {
                await payoutProcessingService.CompleteAsync(id, dto.BankTransactionReference);
                var item = await db.OperatorPayouts.FirstOrDefaultAsync(x => x.Id == id);
                return Ok(ToResponseDto(item!));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("{id}/fail")]
        public async Task<IActionResult> Fail(Guid id, OperatorPayoutActionDto dto)
        {
            var access = await CheckAccessAsync(id);
            if (access == AccessResult.NotFound) return NotFound();
            if (access == AccessResult.Forbidden) return Forbid();

            try
            {
                await payoutProcessingService.FailAsync(id, dto.Reason);
                return NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("{id}/cancel")]
        public async Task<IActionResult> Cancel(Guid id, OperatorPayoutActionDto dto)
        {
            var access = await CheckAccessAsync(id);
            if (access == AccessResult.NotFound) return NotFound();
            if (access == AccessResult.Forbidden) return Forbid();

            try
            {
                await payoutProcessingService.CancelAsync(id, dto.Reason);
                return NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        private enum AccessResult { Ok, NotFound, Forbidden }

        // Single-record gate for the four action endpoints above — loads the payout so
        // CanManageOperatorAsync can check its real BusOperatorId. Distinguishes "no such
        // payout" (404) from "exists, but not this caller's operator" (403).
        private async Task<AccessResult> CheckAccessAsync(Guid payoutId)
        {
            var operatorId = await db.OperatorPayouts
                .Where(p => p.Id == payoutId)
                .Select(p => (Guid?)p.BusOperatorId)
                .FirstOrDefaultAsync();

            if (operatorId == null) return AccessResult.NotFound;

            return await User.CanManageOperatorAsync(db, operatorId.Value)
                ? AccessResult.Ok
                : AccessResult.Forbidden;
        }

        // No generic PUT/DELETE on purpose — see the class comment above.

        private static OperatorPayoutResponseDto ToResponseDto(OperatorPayout x) => new()
        {
            Id = x.Id,
            BusOperatorId = x.BusOperatorId,
            OperatorSettlementId = x.OperatorSettlementId,
            PayoutNo = x.PayoutNo,
            Amount = x.Amount,
            Currency = x.Currency,
            Status = x.Status,
            PaidAtUtc = x.PaidAtUtc,
            BankTransactionReference = x.BankTransactionReference,
            Notes = x.Notes,
            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc,
            RowVersion = x.RowVersion,
        };
    }
}
