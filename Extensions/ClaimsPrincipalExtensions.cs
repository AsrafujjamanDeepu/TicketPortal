using TicketPortal.Api.Data;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace TicketPortal.Api.Extensions
{
    // The operator-scoping pattern from the Completion Plan (Section 2), lifted into one shared
    // helper so Pieces 5 and 6 don't each reimplement it slightly differently. Every controller
    // that needs to tell "sees everything" apart from "only this one operator's own rows" calls
    // this after already gating on User.IsInRole("Admin"/"Staff") — see usage note below.
    public static class ClaimsPrincipalExtensions
    {
        // Returns:
        //   null      — either no StaffProfile exists for this user at all (e.g. an Admin
        //               account, which doesn't need one), or one exists with
        //               BusOperatorId == null (our own platform staff). Both cases mean the
        //               same thing to a caller: "don't filter, see everything."
        //   non-null  — this account belongs to exactly one BusOperator's own staff; callers
        //               should filter their query down to that operator's rows only.
        //
        // It's safe that "no profile" and "platform staff" collapse to the same null result —
        // this method only decides HOW MUCH access to grant, never WHETHER to grant any. Every
        // caller is expected to have already rejected plain Customer accounts with an
        // IsInRole("Admin"/"Staff") check before ever calling this.
        public static async Task<Guid?> GetBusOperatorIdAsync(this ClaimsPrincipal user, AppDbContext db)
        {
            var claim = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(claim, out var userId)) return null;

            return await db.StaffProfiles
                .Where(sp => sp.UserId == userId)
                .Select(sp => sp.BusOperatorId)
                .FirstOrDefaultAsync();
        }
    }
}
