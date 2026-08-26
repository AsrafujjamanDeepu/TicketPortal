using TicketPortal.Api.Data;
using TicketPortal.Api.DTO;
using TicketPortal.Api.Models.Identity;
using TicketPortal.Api.Models.People;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace TicketPortal.Api.Controllers
{
    // Completion Plan Piece 1 — Identity, Access Control & Platform Configuration.
    // Admin-only account/role provisioning. Kept separate from AccountController on purpose:
    // AccountController.Register is the public, UNauthenticated customer self-signup path — it
    // must never be able to hand out Staff/Operator/Admin permissions to a caller who hasn't
    // already been let in the door by an existing Admin.
    //
    // Two distinct jobs live here:
    //   1. AssignRole  — change an EXISTING user's permission tier. For one-off fixes
    //      (promoting someone, correcting a mistake) on an account that already exists,
    //      regardless of how it was created.
    //   2. CreateStaff — the normal way a NEW Staff/Operator account comes into being. Creates
    //      the login and its StaffProfile together, in one step, so BusOperatorId is set
    //      correctly from the very first moment the account exists — never a two-step
    //      "create the login, then hope someone remembers to attach a StaffProfile later"
    //      process. This is the answer to the plan's "decide and document how a new
    //      Staff/Operator account gets created with the right StaffProfile.BusOperatorId set at
    //      registration time" — see DbSeeder.SeedRolesAsync for the full role-semantics writeup.
    [Authorize]
    [Route("api/admin")]
    [ApiController]
    public class AdminController(
        AppDbContext db,
        UserManager<ApplicationUser> userManager,
        RoleManager<ApplicationRole> roleManager) : ControllerBase
    {
        [HttpPost("users/{userId}/roles")]
        public async Task<IActionResult> AssignRole(Guid userId, AssignRoleDto dto)
        {
            if (!User.IsInRole("Admin")) return Forbid();

            if (!await roleManager.RoleExistsAsync(dto.Role))
            {
                var validRoles = await roleManager.Roles.Select(r => r.Name).ToListAsync();
                return BadRequest(new { message = $"Unknown role '{dto.Role}'.", validRoles });
            }

            var user = await userManager.FindByIdAsync(userId.ToString());
            if (user == null) return NotFound(new { message = "User not found." });

            // A user has exactly one permission tier at a time — replace, don't accumulate.
            var currentRoles = await userManager.GetRolesAsync(user);
            if (currentRoles.Count > 0)
            {
                var removeResult = await userManager.RemoveFromRolesAsync(user, currentRoles);
                if (!removeResult.Succeeded)
                {
                    return BadRequest(removeResult.Errors.Select(e => e.Description));
                }
            }

            var addResult = await userManager.AddToRoleAsync(user, dto.Role);
            if (!addResult.Succeeded)
            {
                return BadRequest(addResult.Errors.Select(e => e.Description));
            }

            return Ok(new AssignRoleResponseDto
            {
                UserId = user.Id,
                UserName = user.UserName!,
                // GetRolesAsync returns IList<string>, which does NOT implicitly convert to
                // IReadOnlyCollection<string> (see AccountController.Login for the same fix)
                // — .ToList() here is required to compile, not optional.
                Roles = (await userManager.GetRolesAsync(user)).ToList(),
            });
        }

        [HttpPost("staff")]
        public async Task<IActionResult> CreateStaff(CreateStaffAccountDto dto)
        {
            if (!User.IsInRole("Admin")) return Forbid();

            if (dto.Role is not ("Staff" or "Operator" or "Admin"))
            {
                return BadRequest(new
                {
                    message = "Role must be one of: Staff, Operator, Admin. " +
                               "Use POST /api/account/register for Customer accounts."
                });
            }

            if (dto.BusOperatorId.HasValue &&
                !await db.BusOperators.AnyAsync(o => o.Id == dto.BusOperatorId.Value))
            {
                return BadRequest(new { message = "BusOperatorId does not match a real BusOperator." });
            }

            var user = new ApplicationUser
            {
                UserName = dto.UserName,
                Email = dto.Email,
                FullName = dto.FullName,
            };

            var createResult = await userManager.CreateAsync(user, dto.Password);
            if (!createResult.Succeeded)
            {
                return BadRequest(createResult.Errors.Select(e => e.Description));
            }

            await userManager.AddToRoleAsync(user, dto.Role);

            var profile = new StaffProfile
            {
                UserId = user.Id,
                BusOperatorId = dto.BusOperatorId,
                EmployeeCode = dto.EmployeeCode,
                Role = dto.JobRole,
            };
            db.StaffProfiles.Add(profile);

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                // The login account already exists at this point even if the StaffProfile save
                // fails (e.g. a duplicate EmployeeCode) — that's an acceptable trade-off here:
                // an Admin can retry CreateStaff-equivalent cleanup via AssignRole/StaffProfilesController
                // rather than this leaving an unusable half-created mess with no login at all.
                var error = ex.InnerException?.InnerException?.Message ?? ex.InnerException?.Message ?? ex.Message;
                return Conflict(new { message = "User was created, but the StaffProfile could not be saved.", details = error });
            }

            return StatusCode(201, new CreateStaffAccountResponseDto
            {
                UserId = user.Id,
                StaffProfileId = profile.Id,
                UserName = user.UserName!,
                Role = dto.Role,
                BusOperatorId = profile.BusOperatorId,
            });
        }
    }
}
