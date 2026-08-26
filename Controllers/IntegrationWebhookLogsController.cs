using TicketPortal.Api.Data;
using TicketPortal.Api.DTO;
using TicketPortal.Api.Models.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace TicketPortal.Api.Controllers
{
    // Read-only, Admin/Staff-only. Same reasoning as IntegrationSyncLogsController — raw
    // inbound webhook events from an operator's own ERP are platform-internal integration
    // detail. Nothing writes here yet either, for the same reason: the sync worker that would
    // receive these is future work, not built here.
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class IntegrationWebhookLogsController(AppDbContext db) : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff"))
            {
                return Ok(Array.Empty<IntegrationWebhookLogResponseDto>());
            }

            var items = await db.IntegrationWebhookLogs.OrderByDescending(x => x.ReceivedAtUtc).ToListAsync();
            return Ok(items.Select(ToResponseDto));
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(Guid id)
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff")) return Forbid();

            var item = await db.IntegrationWebhookLogs.FirstOrDefaultAsync(x => x.Id == id);
            return item == null ? NotFound() : Ok(ToResponseDto(item));
        }

        // No POST/PUT/DELETE — see the class comment above.

        private static IntegrationWebhookLogResponseDto ToResponseDto(IntegrationWebhookLog x) => new()
        {
            Id = x.Id,
            OperatorIntegrationId = x.OperatorIntegrationId,
            ExternalEventId = x.ExternalEventId,
            EventType = x.EventType,
            ReceivedAtUtc = x.ReceivedAtUtc,
            IsProcessed = x.IsProcessed,
            ProcessedAtUtc = x.ProcessedAtUtc,
            PayloadJson = x.PayloadJson,
            ErrorMessage = x.ErrorMessage,
            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc,
            RowVersion = x.RowVersion,
        };
    }
}
