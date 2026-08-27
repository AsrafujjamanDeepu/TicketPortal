using TicketPortal.Api.Data;
using TicketPortal.Api.DTO;
using TicketPortal.Api.Models.Diagnostics;
using TicketPortal.Api.Models.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace TicketPortal.Api.Controllers
{
    // Register creates a user through Identity; Login checks the password and hands back a
    // signed JWT carrying the user's Guid id (NameIdentifier) and their roles, so downstream
    // controllers can use both [Authorize] and [Authorize(Roles = "...")].
    // Deliberately NOT [Authorize] — this is the one controller that has to be reachable
    // without a token, since it's what ISSUES the token in the first place.
    [Route("api/[controller]")]
    [ApiController]
    public class AccountController(
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        AppDbContext db) : ControllerBase
    {
        [HttpPost("register")]
        public async Task<IActionResult> Register(RegisterDto dto)
        {
            var user = new ApplicationUser
            {
                UserName = dto.UserName,
                Email = dto.Email,
                FullName = dto.FullName
            };

            var result = await userManager.CreateAsync(user, dto.Password);
            if (!result.Succeeded)
            {
                return BadRequest(result.Errors.Select(e => e.Description));
            }

            // Matches the role semantics DbSeeder.SeedRolesAsync already documents: every
            // public self-signup account is a Customer. A role-assignment failure here (e.g.
            // the seeded "Customer" role is somehow missing) shouldn't undo an
            // otherwise-successful account creation, but it's surfaced in the response instead
            // of failing silently, since it means the account was created without the role its
            // own creation path is supposed to guarantee.
            var roleResult = await userManager.AddToRoleAsync(user, "Customer");
            if (!roleResult.Succeeded)
            {
                return StatusCode(201,
                    $"User '{user.UserName}' created, but role assignment failed: " +
                    string.Join("; ", roleResult.Errors.Select(e => e.Description)));
            }

            return StatusCode(201, $"User '{user.UserName}' created.");
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login(LoginDto dto)
        {
            var user = await userManager.FindByNameAsync(dto.UserName);
            var passwordOk = user != null && await userManager.CheckPasswordAsync(user, dto.Password);

            // Only a real user (found by username) has anything to attribute a LoginHistory row
            // to — LoginHistory.UserId is a required FK, so a completely unknown username has
            // no row to write, same as it always did. A found user with the wrong password
            // still gets a Success = false row, which is the actual security-relevant case
            // this trail exists for.
            if (user != null)
            {
                db.LoginHistories.Add(new LoginHistory
                {
                    UserId = user.Id,
                    LoginAtUtc = DateTime.UtcNow,
                    IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                    UserAgent = Request.Headers.UserAgent.ToString(),
                    Success = passwordOk,
                });
                await db.SaveChangesAsync();
            }

            if (!passwordOk)
            {
                return Unauthorized("Invalid username or password");
            }

            // passwordOk can only be true when user != null (see its definition above), so this
            // is safe — kept as a separate variable rather than sprinkling `!` everywhere below.
            var authenticatedUser = user!;
            var roles = await userManager.GetRolesAsync(authenticatedUser);

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, authenticatedUser.Id.ToString()),
                new(ClaimTypes.Name, authenticatedUser.UserName!)
            };
            claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

            var key = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(configuration["JWT:SigningKey"]!));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var expiresAtUtc = DateTime.UtcNow.AddHours(3);

            var token = new JwtSecurityToken(
                issuer: configuration["JWT:Issuer"],
                audience: configuration["JWT:Audience"],
                claims: claims,
                expires: expiresAtUtc,
                signingCredentials: creds);

            return Ok(new AuthResponseDto
            {
                Token = new JwtSecurityTokenHandler().WriteToken(token),
                ExpiresAtUtc = expiresAtUtc,
                UserId = authenticatedUser.Id,
                UserName = authenticatedUser.UserName!,
                // GetRolesAsync returns IList<string>, which does NOT implicitly convert to
                // IReadOnlyCollection<string> — .ToList() here is required to compile, not optional.
                Roles = roles.ToList()
            });
        }
    }
}
