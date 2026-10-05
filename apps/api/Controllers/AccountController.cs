using TicketPortal.Api.Authorization;
using TicketPortal.Api.Data;
using TicketPortal.Api.DTO;
using TicketPortal.Api.Models.Diagnostics;
using TicketPortal.Api.Models.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Cryptography;
using TicketPortal.Api.Services;

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
        AppDbContext db,
        IPasswordResetMessageSender passwordResetMessageSender,
        ICurrentActorService currentActor,
        IPasswordHasher<ApplicationUser> passwordHasher,
        ILogger<AccountController> logger) : ControllerBase
    {
        private static readonly ApplicationUser DummyLoginUser = new();
        private static readonly string DummyPasswordHash = new PasswordHasher<ApplicationUser>()
            .HashPassword(DummyLoginUser, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));

        [AllowAnonymous]
        [HttpPost("register")]
        [EnableRateLimiting("register")]
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

        [AllowAnonymous]
        [HttpPost("login")]
        [EnableRateLimiting("login")]
        public async Task<IActionResult> Login(LoginDto dto)
        {
            var user = await userManager.FindByNameAsync(dto.UserName);

            var passwordOk = user is not null
                ? await userManager.CheckPasswordAsync(user, dto.Password)
                : passwordHasher.VerifyHashedPassword(DummyLoginUser, DummyPasswordHash, dto.Password)
                    != PasswordVerificationResult.Failed;
            var accountLocked = user is not null && await userManager.IsLockedOutAsync(user);
            var canLogin = user is { IsActive: true } && passwordOk && !accountLocked;

            if (user is not null)
            {
                db.LoginHistories.Add(new LoginHistory
                {
                    UserId = user.Id,
                    LoginAtUtc = DateTime.UtcNow,
                    IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                    UserAgent = Request.Headers.UserAgent.ToString(),
                    Success = canLogin,
                });
                await db.SaveChangesAsync();
            }

            if (!canLogin)
            {
                var failureReason = user is null ? "unknown-user"
                    : !user.IsActive ? "disabled-account"
                    : accountLocked ? "legacy-locked-account" : "invalid-credentials";
                logger.LogWarning("Authentication failed ({FailureReason}) for account {UserId} from {RemoteIp}.",
                    failureReason, user?.Id, HttpContext.Connection.RemoteIpAddress?.ToString());
            }

            if (!canLogin)
            {
                return Unauthorized("Invalid username or password");
            }

            var authenticatedUser = user!;
            var roles = await userManager.GetRolesAsync(authenticatedUser);
            var accountClaims = await userManager.GetClaimsAsync(authenticatedUser);

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, authenticatedUser.Id.ToString()),
                new(ClaimTypes.Name, authenticatedUser.UserName!),
                new("security_stamp", await userManager.GetSecurityStampAsync(authenticatedUser))
            };
            claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
            claims.AddRange(accountClaims
                .Where(claim => claim.Type == DbSeeder.MustChangeBootstrapPasswordClaim));

            var key = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(configuration["JWT:SigningKey"]!));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var expiresAtUtc = DateTime.UtcNow.AddMinutes(
                configuration.GetValue<int?>("Auth:AccessTokenMinutes") ?? 15);

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
                MustChangePassword = accountClaims.Any(claim =>
                    claim.Type == DbSeeder.MustChangeBootstrapPasswordClaim && claim.Value == "true"),
                // GetRolesAsync returns IList<string>, which does NOT implicitly convert to
                // IReadOnlyCollection<string> — .ToList() here is required to compile, not optional.
                Roles = roles.ToList()
            });
        }

        // [Authorize] on the action, not the class — this stays the one controller that's
        // reachable without a token (see the class-level comment above); this is just the one
        // action on it that needs an existing, valid token to know WHOSE password to change.
        [HttpPost("change-password")]
        [Authorize]
        public async Task<IActionResult> ChangePassword(ChangePasswordDto dto)
        {
            // Same NameIdentifier-claim pattern BookingsController.ResolveOrCreateCustomerProfileIdAsync
            // uses — the target user always comes from the bearer token, never the request body.
            var claim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(claim, out var userId))
            {
                return Unauthorized();
            }

            var user = await userManager.FindByIdAsync(userId.ToString());
            if (user == null)
            {
                return Unauthorized();
            }

            // ChangePasswordAsync itself verifies dto.CurrentPassword against the stored hash
            // before applying dto.NewPassword — this one call does both the "prove you're really
            // you" check and the update, so there's no separate CheckPasswordAsync step needed.
            var result = await userManager.ChangePasswordAsync(user, dto.CurrentPassword, dto.NewPassword);
            if (!result.Succeeded)
            {
                return BadRequest(result.Errors.Select(e => e.Description));
            }

            await userManager.UpdateSecurityStampAsync(user);
            var bootstrapClaims = (await userManager.GetClaimsAsync(user))
                .Where(claim => claim.Type == DbSeeder.MustChangeBootstrapPasswordClaim)
                .ToArray();
            if (bootstrapClaims.Length > 0)
            {
                var claimRemoval = await userManager.RemoveClaimsAsync(user, bootstrapClaims);
                if (!claimRemoval.Succeeded)
                {
                    logger.LogError("Could not clear the required-password-change claim for user {UserId}.", user.Id);
                    return Problem(statusCode: StatusCodes.Status500InternalServerError,
                        detail: "The password was changed, but the account needs administrator assistance before it can be used.");
                }
            }

            return NoContent();
        }

        // RBAC Amendment v3 task 7 — see SessionInfoDto's doc comment. Resolved fresh on every
        // call via CurrentActorService, so a role change or counter-assignment revocation is
        // reflected the very next time the client asks, without waiting for the JWT to expire.
        [HttpGet("me")]
        [Authorize]
        public async Task<IActionResult> Me()
        {
            var actor = await currentActor.ResolveAsync(User);
            if (actor.Type == Authorization.ActorType.Anonymous) return Unauthorized();

            var user = await userManager.FindByIdAsync(actor.UserId.ToString());
            if (user == null) return Unauthorized();

            string? busOperatorName = null;
            if (actor.BusOperatorId != null)
            {
                busOperatorName = await db.BusOperators
                    .Where(o => o.Id == actor.BusOperatorId)
                    .Select(o => o.Name)
                    .FirstOrDefaultAsync();
            }

            var assignedCounters = actor.AssignedCounterIds.Count == 0
                ? Array.Empty<SessionCounterDto>()
                : await db.SalesCounters
                    .Where(c => actor.AssignedCounterIds.Contains(c.Id))
                    .Select(c => new SessionCounterDto { Id = c.Id, CounterName = c.CounterName })
                    .ToArrayAsync();

            return Ok(new SessionInfoDto
            {
                UserId = actor.UserId,
                UserName = user.UserName ?? string.Empty,
                FullName = user.FullName,
                ActorType = actor.Type.ToString(),
                JobRole = actor.JobRole?.ToString(),
                BusOperatorId = actor.BusOperatorId,
                BusOperatorName = busOperatorName,
                AssignedCounters = assignedCounters,
                Permissions = actor.Permissions,
            });
        }
        // Forgot password: It uses ASP.NET Identity's
        // one-time, time-limited reset token rather than storing a recoverable password or
        // inventing a second token scheme. Keep the success response generic to prevent email
        // address enumeration.
        [AllowAnonymous]
        [HttpPost("forgot-password")]
        [EnableRateLimiting("password-reset")]
        public async Task<IActionResult> ForgotPassword(ForgotPasswordDto dto)
        {
            var user = await userManager.FindByEmailAsync(dto.Email.Trim());
            if (user is not null && !string.IsNullOrWhiteSpace(user.Email))
            {
                var identityToken = await userManager.GeneratePasswordResetTokenAsync(user);
                var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(identityToken));
                var clientBaseUrl = configuration["PasswordReset:PublicAppUrl"]?.TrimEnd('/')
                    ?? "http://localhost:4200";
                var resetUrl = $"{clientBaseUrl}/auth/reset-password?email=" +
                    $"{Uri.EscapeDataString(user.Email)}&token={Uri.EscapeDataString(encodedToken)}";

                try
                {
                    await passwordResetMessageSender.SendAsync(user, resetUrl);
                }
                catch (Exception ex)
                {
                    // Do not turn a mail-delivery issue into an account-existence oracle. The
                    // operational error is retained in the API log for the team to action.
                    logger.LogError(ex, "Could not deliver password-reset instructions for user {UserId}", user.Id);
                }
            }

            return Ok(new
            {
                message = "If an account uses that email address, password-reset instructions have been sent."
            });
        }

        [AllowAnonymous]
        [HttpPost("reset-password")]
        [EnableRateLimiting("password-reset")]
        public async Task<IActionResult> ResetPassword(ResetPasswordDto dto)
        {
            var user = await userManager.FindByEmailAsync(dto.Email.Trim());
            if (user is null)
            {
                return BadRequest(new { message = "This reset link is invalid or has expired." });
            }

            string identityToken;
            try
            {
                identityToken = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(dto.Token));
            }
            catch (FormatException)
            {
                return BadRequest(new { message = "This reset link is invalid or has expired." });
            }

            var result = await userManager.ResetPasswordAsync(user, identityToken, dto.NewPassword);
            if (!result.Succeeded)
            {
                return BadRequest(new
                {
                    message = "This reset link is invalid or has expired.",
                    errors = result.Errors.Select(error => error.Description)
                });
            }

            // Reset any legacy failed-attempt counters left by older deployments.
            await userManager.ResetAccessFailedCountAsync(user);
            await userManager.UpdateSecurityStampAsync(user);
            return NoContent();
        }
    }
}
