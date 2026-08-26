using TicketPortal.Api.Data;
using TicketPortal.Api.DTO;
using TicketPortal.Api.Models.Finance;
using TicketPortal.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace TicketPortal.Api.Controllers
{
    // OperatorSettlement is the internal record of a completed settlement run — every field on
    // it (the gross/charge/net breakdown, which ledger rows it closed out) is computed from
    // real PlatformLedger rows, not something a client should ever be trusted to type in
    // directly. The old generic CRUD let any authenticated user fabricate a settlement (and its
    // "NetAmount") out of thin air, or edit a real one after the fact — this is now generated
    // exclusively by SettlementGenerationService (see that file for the full design rationale).
    //
    // Admin/Staff only, same reasoning as PlatformLedgersController/OperatorWalletsController —
    // this is operator/platform-internal accounting data. An operator-facing "see your own
    // settlements" view is a reasonable follow-up once Piece 1's operator-scoping helper lands
    // (StaffProfile.BusOperatorId), not built here to avoid guessing at infrastructure that
    // isn't confirmed to exist yet.
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class OperatorSettlementsController(AppDbContext db, SettlementGenerationService settlementGenerationService) : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> GetAll([FromQuery] Guid? busOperatorId)
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff"))
            {
                return Ok(Array.Empty<OperatorSettlementResponseDto>());
            }

            var query = db.OperatorSettlements.AsQueryable();
            if (busOperatorId.HasValue)
            {
                query = query.Where(s => s.BusOperatorId == busOperatorId.Value);
            }

            var items = await query.OrderByDescending(s => s.CreatedAtUtc).ToListAsync();
            return Ok(items.Select(ToResponseDto));
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(Guid id)
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff")) return Forbid();

            var item = await db.OperatorSettlements
                .Include(s => s.Items)
                .FirstOrDefaultAsync(x => x.Id == id);
            return item == null ? NotFound() : Ok(ToDetailResponseDto(item));
        }

        // Runs the batch process for one operator + date range — see
        // Services/SettlementGenerationService.cs. Replaces the old generic POST entirely.
        [HttpPost("generate")]
        public async Task<IActionResult> Generate(SettlementGenerateDto dto)
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff")) return Forbid();

            try
            {
                // settlement.Items is already populated in memory by the service — same
                // DbContext instance for this request, no extra round trip needed.
                var settlement = await settlementGenerationService.GenerateSettlementAsync(
                    dto.BusOperatorId, dto.FromDate, dto.ToDate, dto.Remarks);

                return CreatedAtAction(nameof(GetById), new { id = settlement.Id }, ToDetailResponseDto(settlement));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // Staff sign-off — Draft -> Approved. No money moves here (see the service for why).
        [HttpPost("{id}/approve")]
        public async Task<IActionResult> Approve(Guid id, SettlementApproveDto dto)
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff")) return Forbid();

            try
            {
                await settlementGenerationService.ApproveAsync(id, dto.Remarks);
                return NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // No generic POST/PUT/DELETE on purpose — see the class comment above.

        private static OperatorSettlementResponseDto ToResponseDto(OperatorSettlement x) => new()
        {
            Id = x.Id,
            BusOperatorId = x.BusOperatorId,
            OperatorStatementId = x.OperatorStatementId,
            OperatorInvoiceId = x.OperatorInvoiceId,
            SettlementNo = x.SettlementNo,
            FromDate = x.FromDate,
            ToDate = x.ToDate,
            Direction = x.Direction,
            Status = x.Status,
            OnlineGrossAmount = x.OnlineGrossAmount,
            OfflineGrossAmount = x.OfflineGrossAmount,
            PlatformCharge = x.PlatformCharge,
            GatewayCharge = x.GatewayCharge,
            RefundAmount = x.RefundAmount,
            NetAmount = x.NetAmount,
            PaidAtUtc = x.PaidAtUtc,
            Remarks = x.Remarks,
            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc,
            RowVersion = x.RowVersion,
        };

        private static OperatorSettlementDetailResponseDto ToDetailResponseDto(OperatorSettlement x) => new()
        {
            Id = x.Id,
            BusOperatorId = x.BusOperatorId,
            OperatorStatementId = x.OperatorStatementId,
            OperatorInvoiceId = x.OperatorInvoiceId,
            SettlementNo = x.SettlementNo,
            FromDate = x.FromDate,
            ToDate = x.ToDate,
            Direction = x.Direction,
            Status = x.Status,
            OnlineGrossAmount = x.OnlineGrossAmount,
            OfflineGrossAmount = x.OfflineGrossAmount,
            PlatformCharge = x.PlatformCharge,
            GatewayCharge = x.GatewayCharge,
            RefundAmount = x.RefundAmount,
            NetAmount = x.NetAmount,
            PaidAtUtc = x.PaidAtUtc,
            Remarks = x.Remarks,
            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc,
            RowVersion = x.RowVersion,
            Items = x.Items.Select(i => new OperatorSettlementItemResponseDto
            {
                Id = i.Id,
                OperatorSettlementId = i.OperatorSettlementId,
                BookingId = i.BookingId,
                TicketId = i.TicketId,
                PlatformLedgerId = i.PlatformLedgerId,
                ItemType = i.ItemType,
                SaleChannel = i.SaleChannel,
                TicketFare = i.TicketFare,
                PlatformCharge = i.PlatformCharge,
                GatewayCharge = i.GatewayCharge,
                RefundAmount = i.RefundAmount,
                NetAmount = i.NetAmount,
                CreatedAtUtc = i.CreatedAtUtc,
                UpdatedAtUtc = i.UpdatedAtUtc,
                RowVersion = i.RowVersion,
            }).ToList(),
        };
    }
}
