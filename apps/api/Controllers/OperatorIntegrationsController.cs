// Piece 6 (People/HR & ERP Integrations) — Admin-only gate. 🟡 tier per the completion plan:
// structurally fine as generic CRUD, this only ever needed locking down, no new service. Was
// reachable read/write by any authenticated user — including SecretReference, which holds a
// reference to the credential used to call out to an operator's own ERP API. Now Admin-only end
// to end, same pattern as CommissionRulesController in Piece 1: GetAll returns an empty list for
// non-Admins, every other action returns 403. Deliberately stricter than plain "Admin/Staff" —
// this is data about the platform's own connection to an operator's systems, not something even
// most Staff (platform or operator-side) have a reason to see.
//
// Chunk 8 additions (RBAC Amendment v3 §8 — Integrations.Manage stays Admin-only, but an
// operator manager MAY get a redacted status/read view of their own integration):
//   - GetById/Create/Update/Delete/GetAll (above) are UNCHANGED — still Admin-only via plain
//     User.IsInRole("Admin"), same as before this chunk. Deliberately not migrated to the
//     Permissions/CurrentActor system here, to keep this patch's footprint on Chunk 2's own
//     files minimal; see PermissionMatrix.cs for the one small addition this chunk DOES make
//     (Permissions.IntegrationsRead on OperatorManagerPermissions, purely to power GetStatus
//     below).
//   - ToResponseDto now masks the secret (HasSecret + SecretReferenceMasked, never the raw
//     SecretReference) — task 3.
//   - GetStatus is new: Permissions.IntegrationsRead + CanManageOperator(busOperatorId), so an
//     operator manager can see a redacted status view for THEIR OWN operator without needing
//     Admin. Never returns BaseUrl, auth details, or anything secret-shaped.
//   - TestConnection is new: Permissions.IntegrationsManage (Admin-only, via HasPermission's own
//     IsAdmin bypass — see CurrentActor.HasPermission) — backs the "Test connection" button on
//     the admin integration screen.
using TicketPortal.Api.Authorization;
using TicketPortal.Api.Data;
using TicketPortal.Api.DTO;
using TicketPortal.Api.Models.Enums;
using TicketPortal.Api.Models.Integrations;
using TicketPortal.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace TicketPortal.Api.Controllers
{
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class OperatorIntegrationsController(
        AppDbContext db,
        ICurrentActorService currentActor,
        ExternalBookingSyncService externalSync,
        IConfiguration configuration,
        IntegrationSecurityOptions security,
        ErpDestinationPolicy.HostResolver hostResolver) : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            if (!User.IsInRole("Admin"))
            {
                return Ok(Array.Empty<OperatorIntegrationResponseDto>());
            }

            var items = await db.OperatorIntegrations.ToListAsync();
            return Ok(items.Select(ToResponseDto));
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(Guid id)
        {
            if (!User.IsInRole("Admin")) return Forbid();

            var item = await db.OperatorIntegrations.FirstOrDefaultAsync(x => x.Id == id);
            return item == null ? NotFound() : Ok(ToResponseDto(item));
        }

        // Chunk 8 / RBAC Amendment v3 §8: redacted read view — an operator manager can see this
        // for their OWN operator (CanManageOperator) without needing Admin. Deliberately a
        // narrow, separate DTO (OperatorIntegrationStatusDto) rather than reusing
        // OperatorIntegrationResponseDto with fields blanked out, so there's no risk of a future
        // field added to the full DTO silently leaking into this one.
        [HttpGet("status/{busOperatorId:guid}")]
        public async Task<IActionResult> GetStatus(Guid busOperatorId)
        {
            var actor = await currentActor.ResolveAsync(User);
            if (!actor.HasPermission(Permissions.IntegrationsRead) || !actor.CanManageOperator(busOperatorId))
            {
                return Forbid();
            }

            var busOperator = await db.BusOperators.FirstOrDefaultAsync(o => o.Id == busOperatorId);
            if (busOperator == null) return NotFound();

            var integration = await db.OperatorIntegrations
                .Include(i => i.Endpoints)
                .Where(i => i.BusOperatorId == busOperatorId)
                .OrderByDescending(i => i.IsActive)
                .ThenByDescending(i => i.CreatedAtUtc)
                .FirstOrDefaultAsync();

            var hasAvailabilityEndpoint = integration != null && integration.Endpoints.Any(e => e.IsActive
                && string.Equals(e.Purpose, "GetSeatAvailability", StringComparison.OrdinalIgnoreCase));

            var dto = new OperatorIntegrationStatusDto
            {
                BusOperatorId = busOperator.Id,
                BusOperatorName = busOperator.Name,
                InventoryMode = busOperator.InventoryMode,
                HasIntegrationConfigured = integration != null,
                IntegrationName = integration?.Name,
                IsActive = integration?.IsActive ?? false,
                LastSuccessfulSyncAtUtc = integration?.LastSuccessfulSyncAtUtc,
                AvailabilityFailureMode = ExternalAvailabilityPolicy.GetFailureMode(configuration).ToString(),
                HasAvailabilityEndpoint = hasAvailabilityEndpoint,
            };

            if (integration != null)
            {
                var since = DateTime.UtcNow.AddHours(-24);
                dto.RecentFailureCount = await db.IntegrationSyncLogs.CountAsync(l =>
                    l.OperatorIntegrationId == integration.Id
                    && l.Status == IntegrationSyncStatus.Failed
                    && l.StartedAtUtc >= since);

                var lastLog = await db.IntegrationSyncLogs
                    .Where(l => l.OperatorIntegrationId == integration.Id)
                    .OrderByDescending(l => l.StartedAtUtc)
                    .FirstOrDefaultAsync();

                dto.LastSyncStatus = lastLog?.Status.ToString();
                dto.LastSyncAtUtc = lastLog?.StartedAtUtc;
            }

            // Chunk 6 / C6-4: an operator whose own system is the source of truth cannot sell
            // safely (Closed) or verifiably (Open) unless this is all in place — say what is not.
            if (busOperator.InventoryMode == OperatorInventoryMode.ExternalApiManaged)
            {
                dto.AttentionReason =
                    integration == null ? "No integration is configured, so seat availability and booking confirmation cannot be checked."
                    : !integration.IsActive ? "The integration is switched off."
                    : !hasAvailabilityEndpoint ? "The integration has no active GetSeatAvailability endpoint."
                    : (IntegrationSecretReference.Validate(integration.SecretReference) is not null) ? "The integration's secret reference is not valid."
                    : dto.LastSyncStatus == nameof(IntegrationSyncStatus.Failed) ? "The most recent call to the operator's system failed."
                    : null;

                dto.NeedsAttention = dto.AttentionReason != null;
            }

            return Ok(dto);
        }

        // Chunk 6 / C6-4: the platform-wide policy in force (what customers see when an
        // operator's system can't be reached). Same permission as the operator's own status view.
        [HttpGet("policy")]
        public async Task<IActionResult> GetPolicy()
        {
            var actor = await currentActor.ResolveAsync(User);
            if (!actor.HasPermission(Permissions.IntegrationsRead)) return Forbid();

            return Ok(new IntegrationPolicyDto
            {
                AvailabilityFailureMode = ExternalAvailabilityPolicy.GetFailureMode(configuration).ToString(),
                AvailabilityCacheSeconds = (int)ExternalAvailabilityPolicy.GetCacheTtl(configuration).TotalSeconds,
                AllowLocalDestinations = security.AllowLocalDestinations,
            });
        }

        // Chunk 8 task 9: backs the admin integration screen's "Test connection" button.
        // Admin-only (Permissions.IntegrationsManage) — never exposed to an operator manager's
        // redacted view, per RBAC Amendment v3 §8 ("never... test-connection controls...
        // unless an explicit future requirement approves it").
        [HttpPost("{id:guid}/test-connection")]
        public async Task<IActionResult> TestConnection(Guid id)
        {
            var actor = await currentActor.ResolveAsync(User);
            if (!actor.HasPermission(Permissions.IntegrationsManage)) return Forbid();

            var integration = await db.OperatorIntegrations.FirstOrDefaultAsync(x => x.Id == id);
            if (integration == null) return NotFound();

            var result = await externalSync.TestConnectionAsync(integration);
            return Ok(result);
        }

        [HttpPost]
        public async Task<IActionResult> Create(OperatorIntegrationCreateDto dto)
        {
            if (!User.IsInRole("Admin")) return Forbid();

            var invalid = await ValidateInputAsync(dto, HttpContext.RequestAborted);
            if (invalid != null) return invalid;

            var item = new OperatorIntegration
            {
                BusOperatorId = dto.BusOperatorId,
                Name = dto.Name,
                BaseUrl = dto.BaseUrl,
                AuthType = dto.AuthType,
                ApiKeyHeaderName = dto.ApiKeyHeaderName,
                SecretReference = dto.SecretReference,
                TimeoutSeconds = dto.TimeoutSeconds,
                IsActive = dto.IsActive,
                LastSuccessfulSyncAtUtc = dto.LastSuccessfulSyncAtUtc,
            };

            db.OperatorIntegrations.Add(item);
            await db.SaveChangesAsync();

            return CreatedAtAction(nameof(GetById), new { id = item.Id }, ToResponseDto(item));
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(Guid id, OperatorIntegrationUpdateDto dto)
        {
            if (!User.IsInRole("Admin")) return Forbid();

            var item = await db.OperatorIntegrations.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null) return NotFound(new { message = "OperatorIntegration not found." });

            if (dto.RowVersion == null || dto.RowVersion.Length == 0)
                return BadRequest(new { message = "RowVersion is required." });

            var invalid = await ValidateInputAsync(dto, HttpContext.RequestAborted);
            if (invalid != null) return invalid;

            if (!item.RowVersion.SequenceEqual(dto.RowVersion))
            {
                return Conflict(new
                {
                    message = "This OperatorIntegration was changed by another request. Please GET the latest data and try again."
                });
            }

            db.Entry(item).Property(x => x.RowVersion).OriginalValue = dto.RowVersion;

            item.BusOperatorId = dto.BusOperatorId;
            item.Name = dto.Name;
            item.BaseUrl = dto.BaseUrl;
            item.AuthType = dto.AuthType;
            item.ApiKeyHeaderName = dto.ApiKeyHeaderName;
            item.SecretReference = dto.SecretReference;
            item.TimeoutSeconds = dto.TimeoutSeconds;
            item.IsActive = dto.IsActive;
            item.LastSuccessfulSyncAtUtc = dto.LastSuccessfulSyncAtUtc;
            item.UpdatedAtUtc = DateTime.UtcNow;

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Conflict(new { message = "This OperatorIntegration was already modified or deleted by another request." });
            }
            catch (DbUpdateException ex)
            {
                var error = ex.InnerException?.InnerException?.Message ?? ex.InnerException?.Message ?? ex.Message;
                return Conflict(new { message = "Could not save OperatorIntegration.", details = error });
            }

            return Ok(ToResponseDto(item));
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(Guid id)
        {
            if (!User.IsInRole("Admin")) return Forbid();

            var item = await db.OperatorIntegrations.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null) return NotFound();

            // Soft delete — real business data is never hard-deleted (see AuditableEntity.MarkDeleted).
            item.MarkDeleted();

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Conflict(new { message = "This OperatorIntegration was already modified or deleted by another request." });
            }
            catch (DbUpdateException)
            {
                return Conflict(new { message = "Cannot delete this OperatorIntegration — it is still referenced by other records." });
            }

            return NoContent();
        }

        // Chunk 6 / C6-3: what an administrator may type into an integration, checked on save so
        // they get a clear 400 here instead of a failed call later. The same destination check is
        // repeated before every outbound call (DNS can change after save); nothing here is trusted
        // later on the strength of having passed once. Error text never echoes the secret field.
        private async Task<IActionResult?> ValidateInputAsync(OperatorIntegrationCreateDto dto, CancellationToken ct)
        {
            var secretProblem = IntegrationSecretReference.Validate(dto.SecretReference);
            if (secretProblem != null)
            {
                return BadRequest(new { message = secretProblem, field = "secretReference" });
            }

            var headerProblem = IntegrationInputRules.ValidateApiKeyHeaderName(dto.ApiKeyHeaderName);
            if (headerProblem != null)
            {
                return BadRequest(new { message = headerProblem, field = "apiKeyHeaderName" });
            }

            var destination = await ErpDestinationPolicy.ValidateBaseUrlAsync(
                dto.BaseUrl, security.AllowLocalDestinations, hostResolver, ct);

            if (!destination.IsAllowed)
            {
                return BadRequest(new { message = destination.Reason, field = "baseUrl" });
            }

            return null;
        }

        private OperatorIntegrationResponseDto ToResponseDto(OperatorIntegration x) => new()
        {
            Id = x.Id,
            BusOperatorId = x.BusOperatorId,
            Name = x.Name,
            BaseUrl = x.BaseUrl,
            AuthType = x.AuthType,
            ApiKeyHeaderName = x.ApiKeyHeaderName,
            HasSecret = !string.IsNullOrEmpty(x.SecretReference),
            SecretReferenceMasked = MaskSecret(x.SecretReference),
            SecretReferenceProblem = IntegrationSecretReference.Validate(x.SecretReference),
            SecretConfigured = IntegrationSecretReference.Resolve(configuration, x.SecretReference) != null,
            TimeoutSeconds = x.TimeoutSeconds,
            IsActive = x.IsActive,
            LastSuccessfulSyncAtUtc = x.LastSuccessfulSyncAtUtc,
            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc,
            RowVersion = x.RowVersion,
        };

        // A valid reference ("env:HANIF_ERP_API_KEY") is only the NAME of a configuration entry —
        // not itself secret — so it is shown in full, which is what an administrator needs to
        // know which entry to set. Anything else is a legacy row that may hold a literal secret
        // (the old code accepted one), so none of it is echoed back: it is fully masked.
        private static string? MaskSecret(string? reference)
        {
            if (string.IsNullOrEmpty(reference)) return null;
            return IntegrationSecretReference.TryGetName(reference, out _)
                ? reference
                : new string('•', Math.Min(reference.Length, 12));
        }
    }
}
