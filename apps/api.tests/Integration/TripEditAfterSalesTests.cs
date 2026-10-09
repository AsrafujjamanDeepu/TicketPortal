using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketPortal.Api.Data;
using TicketPortal.Api.Models.Enums;
using TicketPortal.Api.Services;
using TicketPortal.Api.Tests.Infrastructure;
using Xunit;
using static TicketPortal.Api.Tests.Infrastructure.HoldTestSupport;

namespace TicketPortal.Api.Tests.Integration
{
    // Chunk 6 / C6-1 (decision D6) — editing a trip once customers have a stake in it, over real
    // HTTP as the platform administrator (the only demo account that can edit any operator's trips).
    //
    // "Has sales" here is a HELD seat — a customer mid-checkout — which the API treats exactly like
    // a Booked one (see TripEditGuard). Paying for a seat needs the whole payment flow, which adds
    // nothing to what is being tested; the Booked case is covered in Unit/TripEditGuardTests.
    [Collection(SharedApiCollection.Name)]
    public class TripEditAfterSalesTests
    {
        private readonly TicketPortalWebApplicationFactory _factory;

        public TripEditAfterSalesTests(TicketPortalWebApplicationFactory factory)
        {
            _factory = factory;
        }

        private Task<HttpClient> AdminAsync() =>
            LoginClientAsync(_factory, DemoAccounts.BootstrapAdminUserName, DemoAccounts.BootstrapAdminPassword);

        private static async Task<JsonObject> GetTripAsync(HttpClient client, Guid tripId)
        {
            var trip = await client.GetFromJsonAsync<JsonObject>($"/api/trips/{tripId}");
            Assert.NotNull(trip);
            return trip!;
        }

        // The update payload is built from what GET returned, exactly like the Angular and React
        // edit screens do — so these tests break if the contract between them ever drifts.
        private static JsonObject BuildUpdate(JsonObject trip, Action<JsonObject>? mutate = null)
        {
            var seats = new JsonArray();
            foreach (var node in trip["tripSeats"]!.AsArray())
            {
                var seat = node!.AsObject();
                seats.Add(new JsonObject
                {
                    ["seatId"] = seat["seatId"]!.DeepClone(),
                    ["seatNumber"] = seat["seatNumber"]!.DeepClone(),
                    ["seatType"] = seat["seatType"]!.DeepClone(),
                    ["fare"] = seat["fare"]!.DeepClone(),
                });
            }

            var body = new JsonObject
            {
                ["busOperatorId"] = trip["busOperatorId"]!.DeepClone(),
                ["busRouteId"] = trip["busRouteId"]!.DeepClone(),
                ["busId"] = trip["busId"]!.DeepClone(),
                ["departureTerminalId"] = trip["departureTerminalId"]!.DeepClone(),
                ["arrivalTerminalId"] = trip["arrivalTerminalId"]!.DeepClone(),
                ["tripCode"] = trip["tripCode"]!.DeepClone(),
                ["departureTimeUtc"] = trip["departureTimeUtc"]!.DeepClone(),
                ["arrivalTimeUtc"] = trip["arrivalTimeUtc"]!.DeepClone(),
                ["baseFare"] = trip["baseFare"]!.DeepClone(),
                ["currency"] = trip["currency"]!.DeepClone(),
                ["isWheelchairAccessible"] = trip["isWheelchairAccessible"]!.DeepClone(),
                ["status"] = trip["status"]!.DeepClone(),
                ["delayReason"] = trip["delayReason"]?.DeepClone(),
                ["rowVersion"] = trip["rowVersion"]!.DeepClone(),
                ["tripSeats"] = seats,
            };

            mutate?.Invoke(body);
            return body;
        }

        private static JsonNode Shifted(JsonNode? original, TimeSpan by)
        {
            var value = DateTime.SpecifyKind(original!.GetValue<DateTime>(), DateTimeKind.Utc).Add(by);
            return JsonValue.Create(value)!;
        }

        private async Task<(Guid TripId, List<Guid> TripSeatIds)> FindTripWithoutSalesAsync()
        {
            using var scope = _factory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // The demo seed deliberately leaves ONE long (180-minute) mid-checkout hold - scenario 8 - on the
            // only trip that has no ticket, so it stays visibly "active" in demos. These tests need a trip with
            // no held seat, so free any Active hold on a ticket-free trip exactly as the real expiry job would
            // (make it overdue, then run the same sweep method). Nothing else in the suite uses that hold.
            var ticketFreeTripIds = await db.Trips
                .Where(t => t.Status == TripStatus.Scheduled
                    && t.DepartureTimeUtc > DateTime.UtcNow.AddHours(3)
                    && t.InventoryMode == OperatorInventoryMode.PlatformManaged
                    && !db.Tickets.Any(k => k.TripId == t.Id))
                .Select(t => t.Id)
                .ToListAsync();
            await db.SeatHolds
                .Where(h => h.Status == SeatHoldStatus.Active && ticketFreeTripIds.Contains(h.TripId))
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.HoldExpiresAtUtc, DateTime.UtcNow.AddMinutes(-1)));
            await scope.ServiceProvider.GetRequiredService<SeatHoldService>().ExpireOverdueHoldsAsync();

            var tripId = await db.Trips
                .Where(t => t.Status == TripStatus.Scheduled
                    && t.DepartureTimeUtc > DateTime.UtcNow.AddHours(3)
                    && t.InventoryMode == OperatorInventoryMode.PlatformManaged
                    && t.TripSeats.Any()
                    && !t.TripSeats.Any(s => s.Status != TripSeatStatus.Available)
                    && !db.Tickets.Any(k => k.TripId == t.Id))
                .OrderBy(t => t.DepartureTimeUtc)
                .Select(t => t.Id)
                .FirstOrDefaultAsync();

            Assert.True(tripId != Guid.Empty,
                "No future Scheduled PlatformManaged trip without any held/booked seat or ticket exists in the demo data.");

            var seatIds = await db.TripSeats.AsNoTracking()
                .Where(s => s.TripId == tripId).Select(s => s.Id).ToListAsync();
            return (tripId, seatIds);
        }

        [Fact]
        public async Task TripWithAHeldSeat_CannotBeRescheduled_AndNothingChanges()
        {
            var seat = (await FindAvailableSeatsAsync(_factory, 2))[0];
            var customer = await RegisterCustomerClientAsync(_factory);
            var admin = await AdminAsync();

            var holdResponse = await HoldAsync(customer, seat.TripId, seat.TripSeatId);
            Assert.Equal(HttpStatusCode.Created, holdResponse.StatusCode);
            var hold = await ReadHoldAsync(holdResponse);

            try
            {
                var trip = await GetTripAsync(admin, seat.TripId);
                var body = BuildUpdate(trip, b =>
                {
                    b["departureTimeUtc"] = Shifted(trip["departureTimeUtc"], TimeSpan.FromHours(1));
                    b["arrivalTimeUtc"] = Shifted(trip["arrivalTimeUtc"], TimeSpan.FromHours(1));
                });

                var response = await admin.PutAsJsonAsync($"/api/trips/{seat.TripId}", body);

                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                Assert.Equal("TripHasSales", await ReadCodeAsync(response));

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var locked = doc.RootElement.GetProperty("lockedFields").EnumerateArray().Select(e => e.GetString()).ToList();
                Assert.Contains("departureTimeUtc", locked);
                Assert.Contains("arrivalTimeUtc", locked);
                Assert.Contains("cancel the trip", await ReadMessageAsync(response));

                using var scope = _factory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var stored = await db.Trips.AsNoTracking().SingleAsync(t => t.Id == seat.TripId);
                Assert.Equal(trip["departureTimeUtc"]!.GetValue<DateTime>(), stored.DepartureTimeUtc);

                var seatAfter = await db.TripSeats.AsNoTracking().SingleAsync(s => s.Id == seat.TripSeatId);
                Assert.Equal(TripSeatStatus.Held, seatAfter.Status);
                Assert.Equal(hold.Id, seatAfter.CurrentSeatHoldId);
            }
            finally
            {
                await ReleaseQuietlyAsync(customer, hold.Id);
            }
        }

        [Fact]
        public async Task TripWithAHeldSeat_CanStillBeMarkedDelayed_AndTheHeldSeatIsUntouched()
        {
            var seat = (await FindAvailableSeatsAsync(_factory, 2))[0];
            var customer = await RegisterCustomerClientAsync(_factory);
            var admin = await AdminAsync();

            var holdResponse = await HoldAsync(customer, seat.TripId, seat.TripSeatId);
            Assert.Equal(HttpStatusCode.Created, holdResponse.StatusCode);
            var hold = await ReadHoldAsync(holdResponse);

            try
            {
                var trip = await GetTripAsync(admin, seat.TripId);
                var delayed = await admin.PutAsJsonAsync($"/api/trips/{seat.TripId}", BuildUpdate(trip, b =>
                {
                    b["status"] = "Delayed";
                    b["delayReason"] = "Heavy traffic on the highway";
                }));
                Assert.Equal(HttpStatusCode.OK, delayed.StatusCode);

                using (var scope = _factory.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var stored = await db.Trips.AsNoTracking().SingleAsync(t => t.Id == seat.TripId);
                    Assert.Equal(TripStatus.Delayed, stored.Status);
                    Assert.Equal("Heavy traffic on the highway", stored.DelayReason);
                    Assert.True(await db.TripStatusHistories.AnyAsync(h => h.TripId == seat.TripId && h.Status == TripStatus.Delayed));

                    // The same TripSeat row, still Held by the same hold — not deleted and recreated.
                    var seatAfter = await db.TripSeats.AsNoTracking().SingleAsync(s => s.Id == seat.TripSeatId);
                    Assert.Equal(TripSeatStatus.Held, seatAfter.Status);
                    Assert.Equal(hold.Id, seatAfter.CurrentSeatHoldId);
                }

                // The delay can be lifted again (Delayed -> Scheduled is a legal move).
                var current = await GetTripAsync(admin, seat.TripId);
                var restored = await admin.PutAsJsonAsync($"/api/trips/{seat.TripId}", BuildUpdate(current, b =>
                {
                    b["status"] = "Scheduled";
                    b["delayReason"] = null;
                }));
                Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
            }
            finally
            {
                await ReleaseQuietlyAsync(customer, hold.Id);
            }
        }

        [Fact]
        public async Task TripWithAHeldSeat_RefusesAFareChangeOnThatSeat_ButAcceptsOneOnAFreeSeat()
        {
            var seat = (await FindAvailableSeatsAsync(_factory, 2))[0];
            var customer = await RegisterCustomerClientAsync(_factory);
            var admin = await AdminAsync();

            var holdResponse = await HoldAsync(customer, seat.TripId, seat.TripSeatId);
            Assert.Equal(HttpStatusCode.Created, holdResponse.StatusCode);
            var hold = await ReadHoldAsync(holdResponse);

            try
            {
                var trip = await GetTripAsync(admin, seat.TripId);
                var seats = trip["tripSeats"]!.AsArray().Select(n => n!.AsObject()).ToList();
                var heldJson = seats.Single(s => s["id"]!.GetValue<Guid>() == seat.TripSeatId);
                var freeJson = seats.First(s => s["status"]!.GetValue<string>() == "Available");

                decimal FareOf(JsonObject seatJson) => seatJson["fare"]!.GetValue<decimal>();

                void ChangeFare(JsonObject body, JsonObject target, decimal newFare)
                {
                    var targetSeatId = target["seatId"]!.GetValue<Guid>();
                    foreach (var node in body["tripSeats"]!.AsArray())
                    {
                        if (node!["seatId"]!.GetValue<Guid>() == targetSeatId)
                        {
                            node["fare"] = JsonValue.Create(newFare);
                        }
                    }
                }

                // 1. Raising the fare of the HELD seat is refused.
                var refused = await admin.PutAsJsonAsync($"/api/trips/{seat.TripId}",
                    BuildUpdate(trip, b => ChangeFare(b, heldJson, FareOf(heldJson) + 10m)));
                Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
                Assert.Equal("TripHasSales", await ReadCodeAsync(refused));
                using (var doc = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()))
                {
                    var lockedSeats = doc.RootElement.GetProperty("lockedSeats").EnumerateArray().Select(e => e.GetString()).ToList();
                    Assert.Contains(lockedSeats, s => s!.StartsWith(seat.SeatNumber));
                }

                // 2. Raising the fare of a FREE seat is accepted.
                var freeBefore = FareOf(freeJson);
                var accepted = await admin.PutAsJsonAsync($"/api/trips/{seat.TripId}",
                    BuildUpdate(trip, b => ChangeFare(b, freeJson, freeBefore + 5m)));
                Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

                using var scope = _factory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var freeSeatId = freeJson["id"]!.GetValue<Guid>();
                var freeAfter = await db.TripSeats.AsNoTracking().SingleAsync(s => s.Id == freeSeatId);
                Assert.Equal(freeBefore + 5m, freeAfter.Fare);

                var heldAfter = await db.TripSeats.AsNoTracking().SingleAsync(s => s.Id == seat.TripSeatId);
                Assert.Equal(FareOf(heldJson), heldAfter.Fare);
                Assert.Equal(TripSeatStatus.Held, heldAfter.Status);

                // Put the free seat's fare back so later runs see the demo data they expect.
                var latest = await GetTripAsync(admin, seat.TripId);
                var latestFree = latest["tripSeats"]!.AsArray().Select(n => n!.AsObject()).Single(s => s["id"]!.GetValue<Guid>() == freeSeatId);
                await admin.PutAsJsonAsync($"/api/trips/{seat.TripId}",
                    BuildUpdate(latest, b => ChangeFare(b, latestFree, freeBefore)));
            }
            finally
            {
                await ReleaseQuietlyAsync(customer, hold.Id);
            }
        }

        [Fact]
        public async Task TripWithoutSales_CanBeRescheduled_AndItsSeatRowsKeepTheirIdentity()
        {
            var (tripId, seatIdsBefore) = await FindTripWithoutSalesAsync();
            var admin = await AdminAsync();

            var trip = await GetTripAsync(admin, tripId);
            var originalDeparture = trip["departureTimeUtc"]!.GetValue<DateTime>();

            var moved = await admin.PutAsJsonAsync($"/api/trips/{tripId}", BuildUpdate(trip, b =>
            {
                b["departureTimeUtc"] = Shifted(trip["departureTimeUtc"], TimeSpan.FromMinutes(10));
                b["arrivalTimeUtc"] = Shifted(trip["arrivalTimeUtc"], TimeSpan.FromMinutes(10));
            }));
            Assert.Equal(HttpStatusCode.OK, moved.StatusCode);

            try
            {
                using var scope = _factory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var stored = await db.Trips.AsNoTracking().SingleAsync(t => t.Id == tripId);
                Assert.Equal(originalDeparture.AddMinutes(10), stored.DepartureTimeUtc);

                // Seats are reconciled in place: the very same rows, none deleted, none added.
                var seatIdsAfter = await db.TripSeats.AsNoTracking()
                    .Where(s => s.TripId == tripId).Select(s => s.Id).ToListAsync();
                Assert.Equal(seatIdsBefore.OrderBy(i => i), seatIdsAfter.OrderBy(i => i));
            }
            finally
            {
                var latest = await GetTripAsync(admin, tripId);
                await admin.PutAsJsonAsync($"/api/trips/{tripId}", BuildUpdate(latest, b =>
                {
                    b["departureTimeUtc"] = Shifted(latest["departureTimeUtc"], TimeSpan.FromMinutes(-10));
                    b["arrivalTimeUtc"] = Shifted(latest["arrivalTimeUtc"], TimeSpan.FromMinutes(-10));
                }));
            }
        }

        [Fact]
        public async Task AnEdit_DoesNotResetABlockedSeatToAvailable()
        {
            var (tripId, seatIds) = await FindTripWithoutSalesAsync();
            var blockedSeatId = seatIds[0];
            var admin = await AdminAsync();

            using (var scope = _factory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.TripSeats.Where(s => s.Id == blockedSeatId)
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, TripSeatStatus.Blocked));
            }

            try
            {
                var trip = await GetTripAsync(admin, tripId);
                var originalBaseFare = trip["baseFare"]!.GetValue<decimal>();

                var response = await admin.PutAsJsonAsync($"/api/trips/{tripId}",
                    BuildUpdate(trip, b => b["baseFare"] = JsonValue.Create(originalBaseFare + 1m)));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                using (var scope = _factory.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var seatAfter = await db.TripSeats.AsNoTracking().SingleOrDefaultAsync(s => s.Id == blockedSeatId);

                    // The old delete-and-recreate update brought every seat back as Available.
                    Assert.NotNull(seatAfter);
                    Assert.Equal(TripSeatStatus.Blocked, seatAfter!.Status);
                }

                var latest = await GetTripAsync(admin, tripId);
                await admin.PutAsJsonAsync($"/api/trips/{tripId}",
                    BuildUpdate(latest, b => b["baseFare"] = JsonValue.Create(originalBaseFare)));
            }
            finally
            {
                using var scope = _factory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.TripSeats.Where(s => s.Id == blockedSeatId)
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, TripSeatStatus.Available));
            }
        }

        [Fact]
        public async Task ReleasedHoldHistory_NoLongerBlocksAnUnrelatedEdit()
        {
            // Before Chunk 6 an update deleted and recreated every TripSeat, so one hold that had
            // ever existed on a seat (even a released one) made the whole save fail on a foreign key.
            var seat = (await FindAvailableSeatsAsync(_factory, 2))[0];
            var customer = await RegisterCustomerClientAsync(_factory);
            var admin = await AdminAsync();

            var holdResponse = await HoldAsync(customer, seat.TripId, seat.TripSeatId);
            Assert.Equal(HttpStatusCode.Created, holdResponse.StatusCode);
            var release = await customer.PostAsync($"/api/seatholds/{(await ReadHoldAsync(holdResponse)).Id}/release", content: null);
            Assert.Equal(HttpStatusCode.NoContent, release.StatusCode);

            var trip = await GetTripAsync(admin, seat.TripId);
            var originalBaseFare = trip["baseFare"]!.GetValue<decimal>();

            var response = await admin.PutAsJsonAsync($"/api/trips/{seat.TripId}",
                BuildUpdate(trip, b => b["baseFare"] = JsonValue.Create(originalBaseFare + 1m)));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var latest = await GetTripAsync(admin, seat.TripId);
            await admin.PutAsJsonAsync($"/api/trips/{seat.TripId}",
                BuildUpdate(latest, b => b["baseFare"] = JsonValue.Create(originalBaseFare)));
        }
    }
}
