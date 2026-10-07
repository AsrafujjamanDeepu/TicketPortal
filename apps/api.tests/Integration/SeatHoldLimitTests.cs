using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
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
    // Chunk 6 / C6-2 — seat-hold hoarding limits, exercised over real HTTP against SQL Server.
    //
    // The shared test host lifts SeatHold:MaxActiveHoldsPerUser to 1000 (see
    // TicketPortalWebApplicationFactory) because unrelated tests reuse the demo customers and
    // never release their holds. Every test here builds a host derived from it with the REAL
    // limits and acts as a brand-new registered customer, so the counts are exact and nothing
    // else in the shared database can interfere.
    [Collection(SharedApiCollection.Name)]
    public class SeatHoldLimitTests
    {
        private readonly TicketPortalWebApplicationFactory _factory;

        public SeatHoldLimitTests(TicketPortalWebApplicationFactory factory)
        {
            _factory = factory;
        }

        private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> WithLimits(int maxSeatsPerHold, int maxActiveHolds) =>
            _factory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("SeatHold:MaxSeatsPerHold", maxSeatsPerHold.ToString());
                builder.UseSetting("SeatHold:MaxActiveHoldsPerUser", maxActiveHolds.ToString());
            });

        private async Task<Dictionary<Guid, (TripSeatStatus Status, Guid? HoldId)>> SeatStatesAsync(IEnumerable<Guid> tripSeatIds)
        {
            using var scope = _factory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ids = tripSeatIds.ToList();
            var rows = await db.TripSeats.AsNoTracking()
                .Where(s => ids.Contains(s.Id))
                .Select(s => new { s.Id, s.Status, s.CurrentSeatHoldId })
                .ToListAsync();
            return rows.ToDictionary(r => r.Id, r => (r.Status, r.CurrentSeatHoldId));
        }

        // The user's unexpired Active holds, counted straight from the database.
        private async Task<int> ActiveHoldCountAsync(Guid userId)
        {
            using var scope = _factory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.SeatHolds.CountAsync(h =>
                h.HeldByUserId == userId
                && h.Status == SeatHoldStatus.Active
                && h.HoldExpiresAtUtc > DateTime.UtcNow);
        }

        [Fact]
        public async Task MoreSeatsThanTheLimit_IsRejectedWith400_AndNoSeatIsTouched()
        {
            await using var host = WithLimits(maxSeatsPerHold: 2, maxActiveHolds: 3);
            var customer = await RegisterCustomerClientAsync(host);
            var seats = await FindAvailableSeatsAsync(host, 3);

            var response = await HoldAsync(customer, seats[0].TripId, seats.Select(s => s.TripSeatId).ToArray());

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(nameof(SeatHoldLimitKind.TooManySeats), await ReadCodeAsync(response));
            Assert.Contains("at most 2", await ReadMessageAsync(response));

            // Not one of the three seats was held — a rejected request leaves no partial hold.
            var states = await SeatStatesAsync(seats.Select(s => s.TripSeatId));
            Assert.All(states.Values, state =>
            {
                Assert.Equal(TripSeatStatus.Available, state.Status);
                Assert.Null(state.HoldId);
            });
        }

        [Fact]
        public async Task ExactlyTheLimit_IsAccepted()
        {
            await using var host = WithLimits(maxSeatsPerHold: 2, maxActiveHolds: 3);
            var customer = await RegisterCustomerClientAsync(host);
            var seats = await FindAvailableSeatsAsync(host, 2);

            var response = await HoldAsync(customer, seats[0].TripId, seats.Select(s => s.TripSeatId).ToArray());

            try
            {
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            }
            finally
            {
                if (response.StatusCode == HttpStatusCode.Created)
                {
                    await ReleaseQuietlyAsync(customer, (await ReadHoldAsync(response)).Id);
                }
            }
        }

        [Fact]
        public async Task TheSameSeatTwiceInOneRequest_IsRejectedWith400_NotReportedAsTakenByAnotherCustomer()
        {
            await using var host = WithLimits(maxSeatsPerHold: 6, maxActiveHolds: 3);
            var customer = await RegisterCustomerClientAsync(host);
            var seat = (await FindAvailableSeatsAsync(host, 1))[0];

            var response = await HoldAsync(customer, seat.TripId, seat.TripSeatId, seat.TripSeatId);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(nameof(SeatHoldLimitKind.DuplicateSeats), await ReadCodeAsync(response));
            Assert.DoesNotContain("another customer", await ReadMessageAsync(response), StringComparison.OrdinalIgnoreCase);

            var state = (await SeatStatesAsync(new[] { seat.TripSeatId }))[seat.TripSeatId];
            Assert.Equal(TripSeatStatus.Available, state.Status);
        }

        [Fact]
        public async Task OneMoreActiveHoldThanAllowed_IsRejectedWith409_AndReleasingOneFreesTheSlot()
        {
            await using var host = WithLimits(maxSeatsPerHold: 6, maxActiveHolds: 2);
            var customer = await RegisterCustomerClientAsync(host);
            var seats = await FindAvailableSeatsAsync(host, 3);
            var holdIds = new List<Guid>();

            try
            {
                for (var i = 0; i < 2; i++)
                {
                    var ok = await HoldAsync(customer, seats[i].TripId, seats[i].TripSeatId);
                    Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
                    holdIds.Add((await ReadHoldAsync(ok)).Id);
                }

                var refused = await HoldAsync(customer, seats[2].TripId, seats[2].TripSeatId);
                Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
                Assert.Equal(nameof(SeatHoldLimitKind.TooManyActiveHolds), await ReadCodeAsync(refused));

                // The refused request held nothing.
                var refusedSeat = (await SeatStatesAsync(new[] { seats[2].TripSeatId }))[seats[2].TripSeatId];
                Assert.Equal(TripSeatStatus.Available, refusedSeat.Status);

                // Releasing one hold frees a slot immediately.
                var release = await customer.PostAsync($"/api/seatholds/{holdIds[0]}/release", content: null);
                Assert.Equal(HttpStatusCode.NoContent, release.StatusCode);

                var retried = await HoldAsync(customer, seats[2].TripId, seats[2].TripSeatId);
                Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
                holdIds.Add((await ReadHoldAsync(retried)).Id);
            }
            finally
            {
                await ReleaseQuietlyAsync(customer, holdIds.ToArray());
            }
        }

        [Fact]
        public async Task AnExpiredHold_NoLongerCountsAgainstTheLimit_EvenBeforeTheSweepRuns()
        {
            await using var host = WithLimits(maxSeatsPerHold: 6, maxActiveHolds: 1);
            var customer = await RegisterCustomerClientAsync(host);
            var seats = await FindAvailableSeatsAsync(host, 2);
            var holdIds = new List<Guid>();

            try
            {
                var first = await HoldAsync(customer, seats[0].TripId, seats[0].TripSeatId);
                Assert.Equal(HttpStatusCode.Created, first.StatusCode);
                var firstHold = await ReadHoldAsync(first);
                holdIds.Add(firstHold.Id);

                // At the limit: a second hold is refused.
                var blocked = await HoldAsync(customer, seats[1].TripId, seats[1].TripSeatId);
                Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

                // Backdate the first hold's timer. Its status is still Active — the background
                // sweep has not run — but it must already stop counting.
                using (var scope = _factory.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    await db.SeatHolds
                        .Where(h => h.Id == firstHold.Id)
                        .ExecuteUpdateAsync(s => s.SetProperty(h => h.HoldExpiresAtUtc, DateTime.UtcNow.AddMinutes(-1)));
                }

                var allowed = await HoldAsync(customer, seats[1].TripId, seats[1].TripSeatId);
                Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
                holdIds.Add((await ReadHoldAsync(allowed)).Id);
            }
            finally
            {
                await ReleaseQuietlyAsync(customer, holdIds.ToArray());
            }
        }

        [Fact]
        public async Task ParallelHoldRequests_FromOneUser_NeverExceedTheActiveHoldLimit()
        {
            const int limit = 3;
            const int parallelRequests = 8;

            await using var host = WithLimits(maxSeatsPerHold: 6, maxActiveHolds: limit);
            var customer = await RegisterCustomerClientAsync(host);
            var seats = await FindAvailableSeatsAsync(host, parallelRequests);
            var created = new List<Guid>();
            Guid? userId = null;

            try
            {
                // Eight requests at once, each for a DIFFERENT seat (so seat contention cannot be
                // what limits them) — only the per-user limit can refuse any of them.
                var responses = await Task.WhenAll(seats.Select(seat =>
                    HoldAsync(customer, seat.TripId, seat.TripSeatId)));

                var createdCount = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
                var conflictCount = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);

                foreach (var response in responses.Where(r => r.StatusCode == HttpStatusCode.Created))
                {
                    var hold = await ReadHoldAsync(response);
                    created.Add(hold.Id);
                    userId = hold.HeldByUserId;
                }

                Assert.Equal(limit, createdCount);
                Assert.Equal(parallelRequests - limit, conflictCount);

                // The database agrees: exactly `limit` Active holds exist for this user.
                Assert.NotNull(userId);
                Assert.Equal(limit, await ActiveHoldCountAsync(userId!.Value));

                // And every refused request left its seat untouched.
                var states = await SeatStatesAsync(seats.Select(s => s.TripSeatId));
                Assert.Equal(limit, states.Values.Count(s => s.Status == TripSeatStatus.Held));
                Assert.Equal(parallelRequests - limit, states.Values.Count(s => s.Status == TripSeatStatus.Available));
            }
            finally
            {
                await ReleaseQuietlyAsync(customer, created.ToArray());
            }
        }

        [Fact]
        public async Task ReleasingAHold_CancelsItsDraftBooking_AndFreesTheSeat()
        {
            // Release must keep cleaning up everything a hold carries: seat, booking, and so on.
            await using var host = WithLimits(maxSeatsPerHold: 6, maxActiveHolds: 3);
            var customer = await RegisterCustomerClientAsync(host);
            var seat = (await FindAvailableSeatsAsync(host, 1))[0];

            Guid boardingTerminalId;
            Guid droppingTerminalId;
            using (var scope = _factory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var trip = await db.Trips.AsNoTracking().SingleAsync(t => t.Id == seat.TripId);
                boardingTerminalId = trip.DepartureTerminalId;
                droppingTerminalId = trip.ArrivalTerminalId;
            }

            var holdResponse = await HoldAsync(customer, seat.TripId, seat.TripSeatId);
            Assert.Equal(HttpStatusCode.Created, holdResponse.StatusCode);
            var hold = await ReadHoldAsync(holdResponse);

            var bookingResponse = await customer.PostAsJsonAsync("/api/bookings", new
            {
                TripId = seat.TripId,
                HoldToken = hold.HoldToken,
                BoardingTerminalId = boardingTerminalId,
                DroppingTerminalId = droppingTerminalId,
                ContactName = "Release Test",
                ContactPhone = "01700000000",
                Passengers = new[] { new { TripSeatId = seat.TripSeatId, FullName = "Release Passenger" } },
            });
            Assert.True(
                bookingResponse.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
                $"Booking creation returned {(int)bookingResponse.StatusCode}.");

            var release = await customer.PostAsync($"/api/seatholds/{hold.Id}/release", content: null);
            Assert.Equal(HttpStatusCode.NoContent, release.StatusCode);

            using var verify = _factory.CreateScope();
            var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();

            var seatAfter = await verifyDb.TripSeats.AsNoTracking().SingleAsync(s => s.Id == seat.TripSeatId);
            Assert.Equal(TripSeatStatus.Available, seatAfter.Status);
            Assert.Null(seatAfter.CurrentSeatHoldId);

            var booking = await verifyDb.Bookings.AsNoTracking().SingleAsync(b => b.SeatHoldId == hold.Id);
            Assert.Equal(BookingStatus.Cancelled, booking.Status);

            var holdAfter = await verifyDb.SeatHolds.AsNoTracking().SingleAsync(h => h.Id == hold.Id);
            Assert.Equal(SeatHoldStatus.Released, holdAfter.Status);
        }
    }
}
