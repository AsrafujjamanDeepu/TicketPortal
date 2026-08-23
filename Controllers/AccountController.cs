using TicketPortal.Api.DTO;
using TicketPortal.Api.Models.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
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
        IConfiguration configuration) : ControllerBase
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

            return StatusCode(201, $"User '{user.UserName}' created.");
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login(LoginDto dto)
        {
            var user = await userManager.FindByNameAsync(dto.UserName);
            if (user == null || !await userManager.CheckPasswordAsync(user, dto.Password))
            {
                return Unauthorized("Invalid username or password");
            }

            var roles = await userManager.GetRolesAsync(user);

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new(ClaimTypes.Name, user.UserName!)
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
                UserId = user.Id,
                UserName = user.UserName!,
                // GetRolesAsync returns IList<string>, which does NOT implicitly convert to
                // IReadOnlyCollection<string> — .ToList() here is required to compile, not optional.
                Roles = roles.ToList()
            });
        }
    }
}
