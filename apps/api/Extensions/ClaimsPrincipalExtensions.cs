using TicketPortal.Api.Data;
using TicketPortal.Api.Authorization;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace TicketPortal.Api.Extensions
{
    // Shared permission and operator-scope helpers. Controllers should prefer these over
    // independently interpreting role claims or StaffProfile state.
    public static class ClaimsPrincipalExtensions
    {
        public static async Task<bool> HasPermissionAsync(this ClaimsPrincipal user, AppDbContext db, string permission)
        {
            var actor = await new CurrentActorService(db).ResolveAsync(user);
            return actor.HasPermission(permission);
        }

        public enum OperatorScopeKind { Denied, Platform, Operator }

        public sealed record OperatorScopeResult(OperatorScopeKind Kind, Guid? BusOperatorId = null)
        {
            public static OperatorScopeResult Denied { get; } = new(OperatorScopeKind.Denied);
            public static OperatorScopeResult Platform { get; } = new(OperatorScopeKind.Platform);
            public static OperatorScopeResult ForOperator(Guid id) => new(OperatorScopeKind.Operator, id);
        }

        // A null operator id is a valid platform scope only when backed by an active profile.
        // Profileless or inactive Staff must never collapse into the platform-wide case.
        public static async Task<OperatorScopeResult> GetOperatorScopeAsync(this ClaimsPrincipal user, AppDbContext db)
        {
            if (user.IsInRole("Admin")) return OperatorScopeResult.Platform;
            if (!user.IsInRole("Staff") && !user.IsInRole("Operator")) return OperatorScopeResult.Denied;

            var claim = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(claim, out var userId)) return OperatorScopeResult.Denied;

            var profile = await db.StaffProfiles
                .Where(sp => sp.UserId == userId && sp.IsActive)
                .Select(sp => new { sp.BusOperatorId })
                .FirstOrDefaultAsync();
            if (profile is null) return OperatorScopeResult.Denied;
            return profile.BusOperatorId is Guid operatorId
                ? OperatorScopeResult.ForOperator(operatorId)
                : OperatorScopeResult.Platform;
        }

        // Compatibility projection for existing query filters. Guid.Empty is a denied scope,
        // and therefore cannot be confused with the null platform-wide scope.
        public static async Task<Guid?> GetBusOperatorIdAsync(this ClaimsPrincipal user, AppDbContext db)
        {
            var scope = await user.GetOperatorScopeAsync(db);
            return scope.Kind switch
            {
                OperatorScopeKind.Platform => null,
                OperatorScopeKind.Operator => scope.BusOperatorId,
                _ => Guid.Empty,
            };
        }

        // The other half of the pattern: not just WHO the caller's operator is, but whether
        // they're allowed to manage/see a specific operator's data at all. Consolidated here
        // (Piece 2) so BusOperatorsController/TripsController/BusesController's near-identical
        // private copies, and Piece 1's newly-scoped finance controllers, all go through one
        // mechanism instead of three-plus slightly different reimplementations.
        //
        // Admin: always true. Active platform Staff (StaffProfile.BusOperatorId is null):
        // platform scope. Operator-scoped Staff can manage only their own operator. Anyone
        // without an active StaffProfile is denied.
        //
        // Callers still gate on IsInRole("Admin"/"Staff"/"Operator") themselves first when the
        // decision also affects an unfiltered list response (e.g. "return empty array vs. run
        // a scoped query") — this method only answers the "which operator(s)" half.
        public static async Task<bool> CanManageOperatorAsync(this ClaimsPrincipal user, AppDbContext db, Guid busOperatorId)
        {
            var scope = await user.GetOperatorScopeAsync(db);
            return scope.Kind == OperatorScopeKind.Platform
                || scope.Kind == OperatorScopeKind.Operator && scope.BusOperatorId == busOperatorId;
        }

        // For data that's platform-internal regardless of which operator it's about (ERP
        // sync/integration bookkeeping — see ExternalBookingMappingsController and friends):
        // Admin always qualifies; a Staff/Operator account only qualifies when it's platform
        // staff (StaffProfile.BusOperatorId == null). An operator's own scoped Staff/Operator
        // account never qualifies here, unlike CanManageOperatorAsync above — there is no
        // operator id to scope down to, because this data was never meant to open up to
        // operator-scoped staff at all.
        public static async Task<bool> IsPlatformStaffOrAdminAsync(this ClaimsPrincipal user, AppDbContext db)
        {
            var scope = await user.GetOperatorScopeAsync(db);
            return scope.Kind == OperatorScopeKind.Platform;
        }
    }
}
