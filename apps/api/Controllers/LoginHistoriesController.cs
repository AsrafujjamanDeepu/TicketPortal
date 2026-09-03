using TicketPortal.Api.Data;
using TicketPortal.Api.DTO;
using TicketPortal.Api.Models.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace TicketPortal.Api.Controllers
{
    // Read-only. A login/security trail that clients could also edit isn't a trail — the old
    // generic CRUD let any authenticated user rewrite anyone's login history, including
    // flipping a failed attempt to Success after the fact. A user can see their own login
    // history (a normal "recent activity" security page); Admin/Staff see everyone's.
    //
    // Written by AccountController.Login on every attempt, success or failure — see there.
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class LoginHistoriesController(AppDbContext db) : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var query = db.LoginHistories.AsQueryable();

            if (!User.IsInRole("Admin") && !User.IsInRole("Staff"))
            {
                var userId = GetCurrentUserId();
                query = query.Where(x => x.UserId == userId);
            }

            var items = await query.OrderByDescending(x => x.LoginAtUtc).ToListAsync();
            return Ok(items.Select(ToResponseDto));
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(Guid id)
        {
            var item = await db.LoginHistories.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null) return NotFound();

            if (!User.IsInRole("Admin") && !User.IsInRole("Staff") && item.UserId != GetCurrentUserId())
            {
                return Forbid();
            }

            return Ok(ToResponseDto(item));
        }

        // No POST/PUT/DELETE — see the class comment above.

        private Guid? GetCurrentUserId()
        {
            var claim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(claim, out var id) ? id : null;
        }

        private static LoginHistoryResponseDto ToResponseDto(LoginHistory x) => new()
        {
            Id = x.Id,
            UserId = x.UserId,
            LoginAtUtc = x.LoginAtUtc,
            IpAddress = x.IpAddress,
            UserAgent = x.UserAgent,
            Success = x.Success,
        };
    }
}
