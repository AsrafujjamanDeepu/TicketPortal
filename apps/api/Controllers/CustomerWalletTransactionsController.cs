using TicketPortal.Api.Data;
using TicketPortal.Api.Authorization;
using TicketPortal.Api.Extensions;
using TicketPortal.Api.DTO;
using TicketPortal.Api.Models.Enums;
using TicketPortal.Api.Models.People;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace TicketPortal.Api.Controllers
{
    // Read-only. This is the paper trail behind CustomerProfile.WalletBalance — the model's
    // own comment says "nothing else in the app should touch the balance directly", and
    // CustomerWalletService is the only code that writes here (always alongside the balance
    // change it explains). The old generic CRUD let a client insert a transaction with any
    // BalanceAfter it liked, completely disconnected from the customer's real balance.
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class CustomerWalletTransactionsController(AppDbContext db, ICurrentActorService currentActor) : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var query = db.CustomerWalletTransactions.AsQueryable();

            var actor = await currentActor.ResolveAsync(User);
            if (actor.IsAdmin)
            {
                // Admin can inspect all customer wallet history.
            }
            else if (actor.Type == ActorType.Staff)
            {
                if (actor.HasPermission(Permissions.FinanceReadPlatform))
                {
                    // Platform finance scope.
                }
                else if (actor.HasPermission(Permissions.FinanceReadOwnOperator) && actor.BusOperatorId.HasValue)
                {
                    query = query.Where(t => t.BookingId.HasValue && db.Bookings.Any(b =>
                        b.Id == t.BookingId.Value && b.BusOperatorId == actor.BusOperatorId.Value));
                }
                else return Ok(Array.Empty<CustomerWalletTransactionResponseDto>());
            }
            else
            {
                var userId = GetCurrentUserId();
                query = query.Where(t => db.CustomerProfiles.Any(cp =>
                    cp.Id == t.CustomerProfileId && cp.UserId == userId));
            }

            var items = await query.OrderByDescending(t => t.CreatedAtUtc).ToListAsync();
            return Ok(items.Select(ToResponseDto));
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(Guid id)
        {
            var item = await db.CustomerWalletTransactions.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null) return NotFound();

            var actor = await currentActor.ResolveAsync(User);
            if (actor.IsAdmin)
            {
                // Admin can inspect all customer wallet history.
            }
            else if (actor.Type == ActorType.Staff)
            {
                if (actor.HasPermission(Permissions.FinanceReadPlatform))
                {
                    // Platform finance scope.
                }
                else if (actor.HasPermission(Permissions.FinanceReadOwnOperator) && actor.BusOperatorId.HasValue)
                {
                    var belongsToOperator = item.BookingId.HasValue && await db.Bookings.AnyAsync(b =>
                        b.Id == item.BookingId.Value && b.BusOperatorId == actor.BusOperatorId.Value);
                    if (!belongsToOperator) return Forbid();
                }
                else return Forbid();
            }
            else
            {
                var userId = GetCurrentUserId();
                var owns = await db.CustomerProfiles.AnyAsync(cp =>
                    cp.Id == item.CustomerProfileId && cp.UserId == userId);
                if (!owns) return Forbid();
            }

            return Ok(ToResponseDto(item));
        }

        // No POST/PUT/DELETE — see the class comment above. Use
        // CustomerWalletService.CreditAsync / DebitAsync to actually move wallet money.

        private Guid? GetCurrentUserId()
        {
            var claim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(claim, out var id) ? id : null;
        }

        private static CustomerWalletTransactionResponseDto ToResponseDto(CustomerWalletTransaction x) => new()
        {
            Id = x.Id,
            CustomerProfileId = x.CustomerProfileId,
            BookingId = x.BookingId,
            RefundId = x.RefundId,
            TransactionType = x.TransactionType,
            Amount = x.Amount,
            BalanceAfter = x.BalanceAfter,
            Currency = x.Currency,
            Description = x.Description,
            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc,
            RowVersion = x.RowVersion,
        };
    }
}
