using TicketPortal.Api.Data;
using TicketPortal.Api.DTO;
using TicketPortal.Api.Models.Finance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace TicketPortal.Api.Controllers
{
    // Read-only — see OperatorStatementsController and Services/SettlementGenerationService.cs.
    // One row per ledger entry swept into the parent statement.
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class OperatorStatementItemsController(AppDbContext db) : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> GetAll([FromQuery] Guid? operatorStatementId)
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff"))
            {
                return Ok(Array.Empty<OperatorStatementItemResponseDto>());
            }

            var query = db.OperatorStatementItems.AsQueryable();
            if (operatorStatementId.HasValue)
            {
                query = query.Where(i => i.OperatorStatementId == operatorStatementId.Value);
            }

            var items = await query.ToListAsync();
            return Ok(items.Select(ToResponseDto));
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(Guid id)
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff")) return Forbid();

            var item = await db.OperatorStatementItems.FirstOrDefaultAsync(x => x.Id == id);
            return item == null ? NotFound() : Ok(ToResponseDto(item));
        }

        // No POST/PUT/DELETE — see OperatorStatementsController's class comment.

        private static OperatorStatementItemResponseDto ToResponseDto(OperatorStatementItem x) => new()
        {
            Id = x.Id,
            OperatorStatementId = x.OperatorStatementId,
            BookingId = x.BookingId,
            TicketId = x.TicketId,
            PaymentId = x.PaymentId,
            RefundId = x.RefundId,
            PlatformLedgerId = x.PlatformLedgerId,
            ItemType = x.ItemType,
            SaleChannel = x.SaleChannel,
            DebitAmount = x.DebitAmount,
            CreditAmount = x.CreditAmount,
            Currency = x.Currency,
            Description = x.Description,
            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc,
            RowVersion = x.RowVersion,
        };
    }
}
