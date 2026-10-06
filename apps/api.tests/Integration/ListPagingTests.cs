using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TicketPortal.Api.Data;
using TicketPortal.Api.Tests.Infrastructure;
using Xunit;

namespace TicketPortal.Api.Tests.Integration
{
    // C7-3: Bookings and Tickets lists. Page 1 + page 2 + ... must equal the full ordered list (no
    // duplicates, no gaps), scoping must apply before counting and paging, and oversize requests are
    // capped. No paging parameters must still return the old plain array.
    [Collection(SharedApiCollection.Name)]
    public class ListPagingTests
    {
        private readonly TicketPortalWebApplicationFactory _factory;

        public ListPagingTests(TicketPortalWebApplicationFactory factory) => _factory = factory;

        private Task<HttpClient> AdminAsync() =>
            _factory.CreateAuthenticatedClientAsync(DemoAccounts.BootstrapAdminUserName, DemoAccounts.BootstrapAdminPassword);

        private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        }

        private static List<string> Ids(JsonElement array) =>
            array.EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToList();

        // Walks every page of `path` at `size` and returns the concatenated ids.
        private static async Task<(List<string> Ids, int Total)> WalkAsync(HttpClient client, string path, int size)
        {
            var all = new List<string>();
            var total = int.MaxValue;
            for (var page = 1; all.Count < total && page < 500; page++)
            {
                var envelope = await JsonAsync(await client.GetAsync($"{path}?page={page}&pageSize={size}"));
                total = envelope.GetProperty("totalCount").GetInt32();
                all.AddRange(Ids(envelope.GetProperty("items")));
                if (envelope.GetProperty("items").GetArrayLength() == 0) break;
            }
            return (all, total);
        }

        [Theory]
        [InlineData("/api/Bookings")]
        [InlineData("/api/Tickets")]
        public async Task AllPagesTogether_EqualTheFullList_WithNoDuplicatesOrGaps(string path)
        {
            var client = await AdminAsync();

            var legacy = await client.GetAsync(path);
            var full = Ids(await JsonAsync(legacy));
            Assert.True(legacy.Headers.Contains("X-Total-Count"));
            var headerTotal = int.Parse(legacy.Headers.GetValues("X-Total-Count").Single());
            Assert.True(headerTotal <= 200, "Seeded demo data is expected to fit inside the legacy cap; adjust this test if it grows.");
            Assert.Equal(headerTotal, full.Count);

            var (paged, total) = await WalkAsync(client, path, size: 3);

            Assert.Equal(headerTotal, total);
            Assert.Equal(full.Count, paged.Count);
            Assert.Equal(paged.Count, paged.Distinct().Count());   // no duplicates
            Assert.Equal(full, paged);                             // same rows, same stable order
        }

        [Theory]
        [InlineData("/api/Bookings")]
        [InlineData("/api/Tickets")]
        public async Task OversizedAndInvalidPageSizes_AreCappedOrNormalised(string path)
        {
            var client = await AdminAsync();

            var huge = await JsonAsync(await client.GetAsync($"{path}?page=1&pageSize=100000"));
            Assert.Equal(100, huge.GetProperty("pageSize").GetInt32());
            Assert.True(huge.GetProperty("items").GetArrayLength() <= 100);

            var zero = await JsonAsync(await client.GetAsync($"{path}?page=0&pageSize=-4"));
            Assert.Equal(1, zero.GetProperty("page").GetInt32());
            Assert.Equal(25, zero.GetProperty("pageSize").GetInt32());

            var beyond = await JsonAsync(await client.GetAsync($"{path}?page=100000&pageSize=10"));
            Assert.Equal(0, beyond.GetProperty("items").GetArrayLength());
            Assert.False(beyond.GetProperty("hasNextPage").GetBoolean());
        }

        [Theory]
        [InlineData("/api/Bookings")]
        [InlineData("/api/Tickets")]
        public async Task AnUnknownStatusFilter_IsABadRequest_NotAnEmptyList(string path)
        {
            var client = await AdminAsync();
            var response = await client.GetAsync($"{path}?status=NotARealStatus");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task BookingSearch_RunsOnTheServer_AcrossAllPages()
        {
            string pnr;
            using (var scope = _factory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                pnr = await db.Bookings.AsNoTracking().OrderBy(b => b.Pnr).Select(b => b.Pnr).FirstAsync();
            }

            var client = await AdminAsync();
            var envelope = await JsonAsync(await client.GetAsync($"/api/Bookings?page=1&pageSize=5&search={Uri.EscapeDataString(pnr)}"));

            Assert.True(envelope.GetProperty("totalCount").GetInt32() >= 1);
            Assert.All(envelope.GetProperty("items").EnumerateArray(),
                item => Assert.Contains(pnr, item.GetProperty("pnr").GetString(), StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task OperatorStaff_AreScopedBeforeCountingAndPaging()
        {
            Guid operatorId;
            HashSet<string> ownIds;
            using (var scope = _factory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                operatorId = await db.StaffProfiles.Where(sp => sp.User.UserName == DemoAccounts.GreenLineManager)
                    .Select(sp => sp.BusOperatorId!.Value).FirstAsync();
                ownIds = (await db.Bookings.AsNoTracking().Where(b => b.BusOperatorId == operatorId)
                    .Select(b => b.Id.ToString()).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            var client = await _factory.CreateAuthenticatedClientAsync(DemoAccounts.GreenLineManager, DemoAccounts.Password);
            var (paged, total) = await WalkAsync(client, "/api/Bookings", size: 2);

            Assert.Equal(ownIds.Count, total);                       // the total counts only their operator
            Assert.All(paged, id => Assert.Contains(id, ownIds));    // no other operator's row on any page
            Assert.Equal(ownIds.Count, paged.Distinct().Count());
        }

        [Fact]
        public async Task Customers_OnlyEverSeeTheirOwnBookings_OnEveryPage()
        {
            HashSet<string> ownIds;
            using (var scope = _factory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                ownIds = (await db.Bookings.AsNoTracking()
                    .Where(b => b.CustomerProfile != null && b.CustomerProfile.User.UserName == DemoAccounts.Customer)
                    .Select(b => b.Id.ToString()).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            var client = await _factory.CreateAuthenticatedClientAsync(DemoAccounts.Customer, DemoAccounts.Password);
            var (paged, total) = await WalkAsync(client, "/api/Bookings", size: 2);

            Assert.Equal(ownIds.Count, total);
            Assert.All(paged, id => Assert.Contains(id, ownIds));
        }

        [Fact]
        public async Task WithoutPagingParameters_TheAnswerIsStillAPlainArray()
        {
            var client = await AdminAsync();
            var body = await JsonAsync(await client.GetAsync("/api/Bookings"));
            Assert.Equal(JsonValueKind.Array, body.ValueKind);
        }
    }
}
