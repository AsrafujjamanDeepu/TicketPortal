using System.ComponentModel.DataAnnotations;

namespace TicketPortal.Api.DTO
{
    // POST /api/account/register — creates a row in AspNetUsers via UserManager. No token is
    // issued here; register and login are deliberately separate steps.
    public class RegisterDto
    {
        [Required, MaxLength(120)]
        public string FullName { get; set; } = string.Empty;

        [Required, MaxLength(120)]
        public string UserName { get; set; } = string.Empty;

        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        public string Password { get; set; } = string.Empty;
    }

    // POST /api/account/login — checked against Identity's password hash; on success,
    // AccountController hands back a signed JWT built from AuthResponseDto below.
    public class LoginDto
    {
        [Required]
        public string UserName { get; set; } = string.Empty;

        [Required]
        public string Password { get; set; } = string.Empty;
    }

    // POST /api/account/change-password — [Authorize]'d, so the target user is always "whoever
    // the bearer token belongs to", never a body-supplied id (that would let any logged-in user
    // change anyone else's password just by knowing their id).
    public class ChangePasswordDto
    {
        [Required]
        public string CurrentPassword { get; set; } = string.Empty;

        [Required, MinLength(6)]
        public string NewPassword { get; set; } = string.Empty;
    }

    public class AuthResponseDto
    {
        // Paste this into Postman's Authorization tab as a Bearer token for every other endpoint.
        public string Token { get; set; } = string.Empty;
        public DateTime ExpiresAtUtc { get; set; }
        public Guid UserId { get; set; }
        public string UserName { get; set; } = string.Empty;

        // Embedded as claims in the token too, so [Authorize(Roles = "...")] can check them
        // without a database round-trip on every request.
        public IReadOnlyCollection<string> Roles { get; set; } = Array.Empty<string>();
    }
}
