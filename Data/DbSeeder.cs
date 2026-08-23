using TicketPortal.Api.Models.CompanyNetwork;
using Microsoft.EntityFrameworkCore;

namespace TicketPortal.Api.Data
{
    // Seeds platform-wide reference data (Terminals, BusRoutes) that no module's controller
    // is responsible for creating, but that Trip and Booking require via non-nullable FK.
    // Safe to call on every startup — it only inserts when the tables are empty.
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
    }
}
