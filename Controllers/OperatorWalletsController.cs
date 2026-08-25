using TicketPortal.Api.Data;
using TicketPortal.Api.DTO;
using TicketPortal.Api.Models.Finance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace TicketPortal.Api.Controllers
{
    // Read-only. OperatorWallet is a cached running balance — the model's own comment says
    // "FinanceLedgerService is the only code allowed to change these numbers". The old generic
    // PUT let any authenticated user set any operator's balance fields directly, which is as
    // close to "give yourself free money" as this codebase gets. Staff/Admin only, same
    // reasoning as PlatformLedgersController.
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class OperatorWalletsController(AppDbContext db) : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff"))
            {
                return Ok(Array.Empty<OperatorWalletResponseDto>());
            }

            var items = await db.OperatorWallets.ToListAsync();
            return Ok(items.Select(ToResponseDto));
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(Guid id)
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff")) return Forbid();

            var item = await db.OperatorWallets.FirstOrDefaultAsync(x => x.Id == id);
            return item == null ? NotFound() : Ok(ToResponseDto(item));
        }

        // Most callers know the operator, not the wallet's own surrogate id — this is the
        // lookup they actually want.
        [HttpGet("by-operator/{busOperatorId}")]
        public async Task<IActionResult> GetByOperator(Guid busOperatorId)
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff")) return Forbid();

            var item = await db.OperatorWallets.FirstOrDefaultAsync(x => x.BusOperatorId == busOperatorId);
            return item == null ? NotFound() : Ok(ToResponseDto(item));
        }

        // No POST/PUT/DELETE — see the class comment above. Every number here only ever moves
        // via FinanceLedgerService, in the same transaction as the ledger entry that justifies it.

        private static OperatorWalletResponseDto ToResponseDto(OperatorWallet x) => new()
        {
            Id = x.Id,
            BusOperatorId = x.BusOperatorId,
            TotalOnlineSalesAmount = x.TotalOnlineSalesAmount,
            TotalCounterSalesAmount = x.TotalCounterSalesAmount,
            PendingSettlementBalance = x.PendingSettlementBalance,
            AvailablePayoutBalance = x.AvailablePayoutBalance,
            WithdrawnAmount = x.WithdrawnAmount,
            TotalPlatformCommission = x.TotalPlatformCommission,
            TotalGatewayCharge = x.TotalGatewayCharge,
            OperatorReceivableFromPlatform = x.OperatorReceivableFromPlatform,
            PlatformReceivableFromOperator = x.PlatformReceivableFromOperator,
            LastStatementDateUtc = x.LastStatementDateUtc,
            LastSettlementDateUtc = x.LastSettlementDateUtc,
            IsActive = x.IsActive,
            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc,
            RowVersion = x.RowVersion,
        };
    }
}
