using TicketPortal.Api.Data;
using TicketPortal.Api.DTO;
using TicketPortal.Api.Models.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace TicketPortal.Api.Controllers
{
    // Read-only. AuditLog is a compliance-style before/after record — the old generic CRUD let
    // any authenticated user insert a fake change record, edit an existing one's
    // OldValuesJson/NewValuesJson after the fact, or hard-delete one outright, all of which
    // defeats the entire point of an audit trail. Admin/Staff-only: this is platform-internal
    // compliance data.
    //
    // Same situation as ActivityLog: nothing in the codebase writes here yet. A real audit
    // trail across every entity's changes is normally wired up as a single cross-cutting
    // SaveChanges interceptor rather than instrumented by hand per controller — that's a
    // deliberate design decision bigger than this lockdown pass, flagged as a follow-up rather
    // than guessed at here.
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class AuditLogsController(AppDbContext db) : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff"))
            {
                return Ok(Array.Empty<AuditLogResponseDto>());
            }

            var items = await db.AuditLogs.OrderByDescending(x => x.CreatedAtUtc).ToListAsync();
            return Ok(items.Select(ToResponseDto));
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(Guid id)
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff")) return Forbid();

            var item = await db.AuditLogs.FirstOrDefaultAsync(x => x.Id == id);
            return item == null ? NotFound() : Ok(ToResponseDto(item));
        }

        // No POST/PUT/DELETE — see the class comment above.

        private static AuditLogResponseDto ToResponseDto(AuditLog x) => new()
        {
            Id = x.Id,
            UserId = x.UserId,
            EntityName = x.EntityName,
            EntityId = x.EntityId,
            Action = x.Action,
            OldValuesJson = x.OldValuesJson,
            NewValuesJson = x.NewValuesJson,
            IpAddress = x.IpAddress,
            UserAgent = x.UserAgent,
            CreatedAtUtc = x.CreatedAtUtc,
        };
    }
}
