using TicketPortal.Api.Data;
using TicketPortal.Api.DTO;
using TicketPortal.Api.Models.Finance;
using TicketPortal.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace TicketPortal.Api.Controllers
{
    // The formal bill for the counter-sale commission side — most invoices are raised
    // automatically by SettlementGenerationService when an operator owes the platform net for a
    // settlement period, but staff can also raise one by hand. Staff-generated/staff-confirmed
    // only: the old generic CRUD let any authenticated user create or edit an invoice — including
    // setting Status straight to Paid with nothing behind it — for any operator. Status now only
    // ever moves via Issue/Cancel here, or via InvoicePaymentService.RecordReceiptAsync when a
    // real receipt is recorded (see OperatorPaymentReceiptsController). Admin/Staff only.
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class OperatorInvoicesController(AppDbContext db, InvoicePaymentService invoicePaymentService) : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> GetAll([FromQuery] Guid? busOperatorId)
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff"))
            {
                return Ok(Array.Empty<OperatorInvoiceResponseDto>());
            }

            var query = db.OperatorInvoices.AsQueryable();
            if (busOperatorId.HasValue)
            {
                query = query.Where(i => i.BusOperatorId == busOperatorId.Value);
            }

            var items = await query.OrderByDescending(i => i.CreatedAtUtc).ToListAsync();
            return Ok(items.Select(ToResponseDto));
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(Guid id)
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff")) return Forbid();

            var item = await db.OperatorInvoices.FirstOrDefaultAsync(x => x.Id == id);
            return item == null ? NotFound() : Ok(ToResponseDto(item));
        }

        [HttpPost]
        public async Task<IActionResult> Create(OperatorInvoiceCreateDto dto)
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff")) return Forbid();

            try
            {
                var invoice = await invoicePaymentService.CreateAsync(
                    dto.BusOperatorId, dto.OperatorStatementId, dto.InvoiceDate, dto.DueDate, dto.Direction, dto.Amount, dto.Currency);
                return CreatedAtAction(nameof(GetById), new { id = invoice.Id }, ToResponseDto(invoice));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // Draft -> Issued.
        [HttpPost("{id}/issue")]
        public async Task<IActionResult> Issue(Guid id)
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff")) return Forbid();

            try
            {
                await invoicePaymentService.IssueAsync(id);
                return NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // Draft/Issued (no receipts recorded yet) -> Cancelled.
        [HttpPost("{id}/cancel")]
        public async Task<IActionResult> Cancel(Guid id, OperatorInvoiceActionDto dto)
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff")) return Forbid();

            try
            {
                await invoicePaymentService.CancelAsync(id, dto.Reason);
                return NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // No generic PUT/DELETE on purpose — see the class comment above.

        private static OperatorInvoiceResponseDto ToResponseDto(OperatorInvoice x) => new()
        {
            Id = x.Id,
            BusOperatorId = x.BusOperatorId,
            OperatorStatementId = x.OperatorStatementId,
            InvoiceNo = x.InvoiceNo,
            InvoiceDate = x.InvoiceDate,
            DueDate = x.DueDate,
            Direction = x.Direction,
            Amount = x.Amount,
            Currency = x.Currency,
            Status = x.Status,
            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc,
            RowVersion = x.RowVersion,
        };
    }
}
