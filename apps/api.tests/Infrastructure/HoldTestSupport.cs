using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketPortal.Api.Data;
using TicketPortal.Api.Models.Enums;
using Xunit;

namespace TicketPortal.Api.Tests.Infrastructure
{
    // Chunk 6 — small helpers shared by SeatHoldLimitTests, TripEditAfterSalesTests and
    // ErpAvailabilityAndSecurityTests. All of them accept any WebApplicationFactory<Program>, so
    // they work with the shared factory AND with a host derived from it via WithWebHostBuilder
    // (same database, different settings).
    public static class HoldTestSupport
    {
        public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

        public sealed record SeatRef(Guid TripId, Guid TripSeatId, string SeatNumber);

        public sealed record HoldCreated(Guid Id, string HoldToken, Guid? HeldByUserId);

        // Registers a brand-new customer through the real endpoint and returns an authenticated
        // client for it. A new user starts with zero holds, which is what makes the per-user
        // "active holds" cap testable without disturbing the shared demo customers.
        public static async Task<HttpClient> RegisterCustomerClientAsync(WebApplicationFactory<Program> factory)
        {
            var userName = $"holdlimit-{Guid.NewGuid():N}";
            const string password = "Hold!Limit123";

            var anonymous = factory.CreateClient();
            var register = await anonymous.PostAsJsonAsync("/api/account/register", new
            {
                FullName = "Hold Limit Test",
                UserName = userName,
                Email = $"{userName}@example.test",
                Password = password,
            });
            Assert.Equal(HttpStatusCode.Created, register.StatusCode);

            var token = await anonymous.LoginAsync(userName, password);
            var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }

        public static async Task<HttpClient> LoginClientAsync(WebApplicationFactory<Program> factory, string userName, string password)
        {
            var anonymous = factory.CreateClient();
            var token = await anonymous.LoginAsync(userName, password);
            var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }

        // Up to `count` Available seats on ONE future, Scheduled trip of the given inventory
        // mode. Seats are taken from the END of the seat list (highest id) so they rarely collide
        // with the first-available seats other test classes pick. `skipTrips` lets one test ask
        // for several different trips.
        public static async Task<List<SeatRef>> FindAvailableSeatsAsync(
            WebApplicationFactory<Program> factory,
            int count,
            OperatorInventoryMode mode = OperatorInventoryMode.PlatformManaged,
            IReadOnlyCollection<Guid>? skipTrips = null)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            skipTrips ??= Array.Empty<Guid>();

            var tripIds = await db.Trips
                .Where(t => t.Status == TripStatus.Scheduled
                    && t.DepartureTimeUtc > DateTime.UtcNow.AddHours(2)
                    && t.InventoryMode == mode
                    && !skipTrips.Contains(t.Id))
                .Where(t => t.TripSeats.Count(s => s.Status == TripSeatStatus.Available) >= count)
                .OrderByDescending(t => t.DepartureTimeUtc)
                .Select(t => t.Id)
                .Take(1)
                .ToListAsync();

            Assert.True(tripIds.Count == 1,
                $"No future Scheduled {mode} trip with at least {count} Available seats exists in the seeded demo data.");

            var seats = await db.TripSeats
                .Where(s => s.TripId == tripIds[0] && s.Status == TripSeatStatus.Available)
                .OrderByDescending(s => s.Id)
                .Take(count)
                .Select(s => new SeatRef(s.TripId, s.Id, s.SeatNumber))
                .ToListAsync();

            Assert.Equal(count, seats.Count);
            return seats;
        }

        public static Task<HttpResponseMessage> HoldAsync(HttpClient client, Guid tripId, params Guid[] tripSeatIds) =>
            client.PostAsJsonAsync("/api/seatholds", new { TripId = tripId, TripSeatIds = tripSeatIds });

        public static async Task<HoldCreated> ReadHoldAsync(HttpResponseMessage response)
        {
            var hold = await response.Content.ReadFromJsonAsync<HoldCreated>(Json);
            Assert.NotNull(hold);
            return hold!;
        }

        // Test hygiene: put seats back so later tests (and re-runs against the same database)
        // find them Available. Never fails the test — a hold may already be released/expired.
        public static async Task ReleaseQuietlyAsync(HttpClient client, params Guid[] holdIds)
        {
            foreach (var holdId in holdIds)
            {
                try
                {
                    await client.PostAsync($"/api/seatholds/{holdId}/release", content: null);
                }
                catch
                {
                    // Best-effort cleanup only.
                }
            }
        }

        public static async Task<string> ReadMessageAsync(HttpResponseMessage response)
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("message", out var message) ? message.GetString() ?? string.Empty : string.Empty;
        }

        public static async Task<string> ReadCodeAsync(HttpResponseMessage response)
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() ?? string.Empty : string.Empty;
        }
    }
}
