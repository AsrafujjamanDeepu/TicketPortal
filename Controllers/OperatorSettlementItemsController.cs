using TicketPortal.Api.Data;
using TicketPortal.Api.DTO;
using TicketPortal.Api.Models.Finance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace TicketPortal.Api.Controllers
{
    // Read-only. Every row here is written by SettlementGenerationService as part of generating
    // its parent OperatorSettlement — see Services/SettlementGenerationService.cs. The old
    // generic CRUD let any authenticated user fabricate or edit a settlement line item
    // (TicketFare, PlatformCharge, etc.) with no connection to a real ledger entry at all.
    // Admin/Staff only, same reasoning as OperatorSettlementsController.
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class OperatorSettlementItemsController(AppDbContext db) : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> GetAll([FromQuery] Guid? operatorSettlementId)
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff"))
            {
                return Ok(Array.Empty<OperatorSettlementItemResponseDto>());
            }

            var query = db.OperatorSettlementItems.AsQueryable();
            if (operatorSettlementId.HasValue)
            {
                query = query.Where(i => i.OperatorSettlementId == operatorSettlementId.Value);
            }

            var items = await query.ToListAsync();
            return Ok(items.Select(ToResponseDto));
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(Guid id)
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff")) return Forbid();

            var item = await db.OperatorSettlementItems.FirstOrDefaultAsync(x => x.Id == id);
            return item == null ? NotFound() : Ok(ToResponseDto(item));
        }

        // No POST/PUT/DELETE — see the class comment above.

        private static OperatorSettlementItemResponseDto ToResponseDto(OperatorSettlementItem x) => new()
        {
            Id = x.Id,
            OperatorSettlementId = x.OperatorSettlementId,
            BookingId = x.BookingId,
            TicketId = x.TicketId,
            PlatformLedgerId = x.PlatformLedgerId,
            ItemType = x.ItemType,
            SaleChannel = x.SaleChannel,
            TicketFare = x.TicketFare,
            PlatformCharge = x.PlatformCharge,
            GatewayCharge = x.GatewayCharge,
            RefundAmount = x.RefundAmount,
            NetAmount = x.NetAmount,
            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc,
            RowVersion = x.RowVersion,
        };
    }
}
