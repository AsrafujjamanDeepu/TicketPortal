using TicketPortal.Api.Data;
using TicketPortal.Api.Authorization;
using TicketPortal.Api.DTO;
using TicketPortal.Api.Extensions;
using TicketPortal.Api.Models.Finance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace TicketPortal.Api.Controllers
{
    // Read-only. PlatformLedger is the append-only master money diary — the model's own
    // comment says rows are "NEVER edited or deleted once written", and FinanceLedgerService's
    // comment says it's "the ONLY place in the codebase allowed to write" here. The old
    // generic CRUD let any authenticated user fabricate or edit revenue/commission entries for
    // any operator; there is no legitimate client-facing write path for this table at all.
    //
    // This is operator/platform-internal accounting data, not anything a customer has a
    // reason to see (even their own booking's ledger rows describe the platform-operator
    // relationship, not something that belongs to the customer) — so unlike the customer-facing
    // controllers, this one has no "see your own" fallback for a plain Customer. Admin/
    // platform-Staff see every operator's ledger rows; an operator's own Staff/Operator
    // account (Piece 1) is scoped to its own operator's rows only.
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class PlatformLedgersController(AppDbContext db, ICurrentActorService currentActor) : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> GetAll([FromQuery] Guid? busOperatorId)
        {
            var actor = await currentActor.ResolveAsync(User);
            var canReadAll = actor.IsAdmin || actor.HasPermission(Permissions.FinanceReadPlatform);
            if (!canReadAll && !(actor.HasPermission(Permissions.FinanceReadOwnOperator) && actor.BusOperatorId.HasValue))
                return Ok(Array.Empty<PlatformLedgerResponseDto>());

            var query = db.PlatformLedgers.AsQueryable();
            if (!canReadAll)
            {
                query = query.Where(l => l.BusOperatorId == actor.BusOperatorId!.Value);
            }
            else if (busOperatorId.HasValue)
            {
                query = query.Where(l => l.BusOperatorId == busOperatorId.Value);
            }

            var items = await query.OrderByDescending(l => l.CreatedAtUtc).ToListAsync();
            return Ok(items.Select(ToResponseDto));
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(Guid id)
        {
            var item = await db.PlatformLedgers.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null) return NotFound();
            var actor = await currentActor.ResolveAsync(User);
            var canReadAll = actor.IsAdmin || actor.HasPermission(Permissions.FinanceReadPlatform);
            if (!canReadAll && !(actor.HasPermission(Permissions.FinanceReadOwnOperator) && actor.BusOperatorId.HasValue))
                return Forbid();

            // BusOperatorId is nullable here (some ledger rows are pure platform entries, not
            // tied to any operator) — CanManageOperatorAsync needs a real operator id, so a null
            // row falls back to "platform Admin/Staff only", same as the old unscoped gate.
            if (item.BusOperatorId == null)
            {
                if (!canReadAll) return Forbid();
                return Ok(ToResponseDto(item));
            }

            if (!canReadAll && actor.BusOperatorId != item.BusOperatorId) return Forbid();
            return Ok(ToResponseDto(item));
        }

        // No POST/PUT/DELETE — see the class comment above. All writes go through
        // FinanceLedgerService (PostOnlineSaleAsync / PostCounterSaleCommissionAsync /
        // PostRefundAsync / PostCounterSaleRefundAsync), called from PaymentConfirmationService
        // and RefundProcessingService.

        private static PlatformLedgerResponseDto ToResponseDto(PlatformLedger x) => new()
        {
            Id = x.Id,
            BookingId = x.BookingId,
            PaymentId = x.PaymentId,
            RefundId = x.RefundId,
            BusOperatorId = x.BusOperatorId,
            OperatorSettlementId = x.OperatorSettlementId,
            LedgerNo = x.LedgerNo,
            ItemType = x.ItemType,
            SaleChannel = x.SaleChannel,
            DebitAmount = x.DebitAmount,
            CreditAmount = x.CreditAmount,
            Currency = x.Currency,
            ReferenceNo = x.ReferenceNo,
            Description = x.Description,
            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc,
            RowVersion = x.RowVersion,
        };
    }
}
