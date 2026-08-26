using TicketPortal.Api.Models.CompanyNetwork;
using TicketPortal.Api.Models.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace TicketPortal.Api.Data
{
    // Seeds platform-wide reference data (Terminals, BusRoutes) that no module's controller
    // is responsible for creating, but that Trip and Booking require via non-nullable FK.
    // Safe to call on every startup — it only inserts when the tables are empty.
    //
    // Also seeds the real login-permission roles and a bootstrap Admin account (Completion
    // Plan Piece 1 — "seed real ApplicationRole rows... right now the only way to grant a role
    // is a raw SQL insert, which isn't a real solution"). See AdminController for how every
    // other Staff/Operator/Admin account gets created after this one exists.
    public static class DbSeeder
    {
        // Fixed GUIDs (not Guid.NewGuid()) so the same IDs come back every time you drop
        // and recreate the database — hardcode them straight into Postman requests instead
        // of re-querying the database after every reseed.
        public static readonly Guid DhakaGabtoliId = Guid.Parse("11111111-1111-1111-1111-111111111101");
        public static readonly Guid DhakaKalyanpurId = Guid.Parse("11111111-1111-1111-1111-111111111102");
        public static readonly Guid ChittagongCentralId = Guid.Parse("11111111-1111-1111-1111-111111111103");
        public static readonly Guid SylhetKadamtaliId = Guid.Parse("11111111-1111-1111-1111-111111111104");
        public static readonly Guid CoxsBazarId = Guid.Parse("11111111-1111-1111-1111-111111111105");

        public static readonly Guid DhakaToChittagongRouteId = Guid.Parse("22222222-2222-2222-2222-222222222201");
        public static readonly Guid ChittagongToDhakaRouteId = Guid.Parse("22222222-2222-2222-2222-222222222202");
        public static readonly Guid DhakaToSylhetRouteId = Guid.Parse("22222222-2222-2222-2222-222222222203");
        public static readonly Guid DhakaToCoxsBazarRouteId = Guid.Parse("22222222-2222-2222-2222-222222222204");

        public static async Task SeedReferenceDataAsync(AppDbContext db)
        {
            if (!await db.Terminals.AnyAsync())
            {
                db.Terminals.AddRange(
                    new Terminal { Id = DhakaGabtoliId, Name = "Gabtoli Bus Terminal", Code = "DHK-GBT", City = "Dhaka", District = "Dhaka", Division = "Dhaka" },
                    new Terminal { Id = DhakaKalyanpurId, Name = "Kalyanpur Bus Stand", Code = "DHK-KLP", City = "Dhaka", District = "Dhaka", Division = "Dhaka" },
                    new Terminal { Id = ChittagongCentralId, Name = "Chittagong Central Terminal", Code = "CTG-CEN", City = "Chittagong", District = "Chittagong", Division = "Chittagong" },
                    new Terminal { Id = SylhetKadamtaliId, Name = "Sylhet Kadamtali Terminal", Code = "SYL-KDT", City = "Sylhet", District = "Sylhet", Division = "Sylhet" },
                    new Terminal { Id = CoxsBazarId, Name = "Cox's Bazar Bus Terminal", Code = "CXB-CEN", City = "Cox's Bazar", District = "Cox's Bazar", Division = "Chittagong" }
                );
                await db.SaveChangesAsync();
            }

            if (!await db.BusRoutes.AnyAsync())
            {
                db.BusRoutes.AddRange(
                    new BusRoute { Id = DhakaToChittagongRouteId, OriginTerminalId = DhakaGabtoliId, DestinationTerminalId = ChittagongCentralId, RouteCode = "DHK-CTG", Name = "Dhaka - Chittagong", DistanceKm = 264, EstimatedDurationMinutes = 360 },
                    new BusRoute { Id = ChittagongToDhakaRouteId, OriginTerminalId = ChittagongCentralId, DestinationTerminalId = DhakaGabtoliId, RouteCode = "CTG-DHK", Name = "Chittagong - Dhaka", DistanceKm = 264, EstimatedDurationMinutes = 360, ReverseRouteId = DhakaToChittagongRouteId },
                    new BusRoute { Id = DhakaToSylhetRouteId, OriginTerminalId = DhakaKalyanpurId, DestinationTerminalId = SylhetKadamtaliId, RouteCode = "DHK-SYL", Name = "Dhaka - Sylhet", DistanceKm = 247, EstimatedDurationMinutes = 330 },
                    new BusRoute { Id = DhakaToCoxsBazarRouteId, OriginTerminalId = DhakaGabtoliId, DestinationTerminalId = CoxsBazarId, RouteCode = "DHK-CXB", Name = "Dhaka - Cox's Bazar", DistanceKm = 414, EstimatedDurationMinutes = 540 }
                );
                await db.SaveChangesAsync();
            }
        }

        // The four login-permission tiers used by every [Authorize]/IsInRole check across the
        // whole backend (see the Completion Plan's "Shared conventions" section). Idempotent —
        // safe to run on every startup.
        //
        // Role semantics decided here (nothing else pins this down):
        //   Customer — assigned automatically by AccountController.Register (public self-signup).
        //   Staff    — every account with a StaffProfile, BOTH our own platform staff
        //              (StaffProfile.BusOperatorId == null) AND an operator's own staff
        //              (BusOperatorId == that operator). Every `IsInRole("Staff")` check
        //              elsewhere in the codebase is written against this one role; BusOperatorId
        //              is what narrows an operator's staff down to their own rows on top of that
        //              (see Extensions/ClaimsPrincipalExtensions.GetBusOperatorIdAsync).
        //   Operator — seeded because the plan calls for it explicitly, and assignable through
        //              AdminController, but nothing in the codebase currently gates on it: every
        //              scoping check that tells "our staff" from "an operator's staff" apart
        //              uses BusOperatorId, not the role name. Reserved for a future
        //              operator-company-portal login that isn't tied to an individual
        //              StaffProfile — flag for removal if that need never materializes.
        //   Admin    — full access everywhere; only ever granted through AdminController.
        public static async Task SeedRolesAsync(RoleManager<ApplicationRole> roleManager)
        {
            string[] roles = ["Admin", "Staff", "Operator", "Customer"];

            foreach (var role in roles)
            {
                if (!await roleManager.RoleExistsAsync(role))
                {
                    await roleManager.CreateAsync(new ApplicationRole { Name = role });
                }
            }
        }

        // Fixed Guid, same reasoning as the Terminal/BusRoute ids above — a fresh clone of this
        // repo needs ONE working Admin account to call AdminController with, or nobody can ever
        // grant anyone else a role without a raw SQL insert (the exact problem Piece 1 exists to
        // remove). Dev-only credentials — rotate or remove before any real deployment.
        public static readonly Guid BootstrapAdminId = Guid.Parse("33333333-3333-3333-3333-333333333301");
        public const string BootstrapAdminUserName = "admin";
        public const string BootstrapAdminPassword = "Admin@12345";

        public static async Task SeedAdminUserAsync(UserManager<ApplicationUser> userManager)
        {
            if (await userManager.FindByNameAsync(BootstrapAdminUserName) != null) return;

            var admin = new ApplicationUser
            {
                Id = BootstrapAdminId,
                UserName = BootstrapAdminUserName,
                Email = "admin@ticketportal.local",
                FullName = "Platform Admin",
                EmailConfirmed = true,
            };

            var result = await userManager.CreateAsync(admin, BootstrapAdminPassword);
            if (result.Succeeded)
            {
                await userManager.AddToRoleAsync(admin, "Admin");
            }
        }
    }
}
