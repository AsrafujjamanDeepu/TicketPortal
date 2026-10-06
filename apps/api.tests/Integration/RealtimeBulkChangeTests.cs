using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using TicketPortal.Api.Data;
using TicketPortal.Api.Models.Enums;
using TicketPortal.Api.Realtime;
using TicketPortal.Api.Services;
using TicketPortal.Api.Tests.Infrastructure;
using Xunit;

namespace TicketPortal.Api.Tests.Integration
{
    // docs/02-Project-Concept-and-Solution.md (Real-time updates), Chunk 3 — the bulk-SQL blind spots. Seat releases, the hold-expiry
    // sweep, the trip-cancel cascade and every wallet balance change use ExecuteUpdateAsync, which
    // EF's change tracker (and so Chunk 2's SaveChanges capture) never sees. The services report
    // those changes themselves; these tests use REAL hub connections and the REAL services/endpoints
    // (same long-polling setup as RealtimeChangeCaptureTests) and check three things:
    //   * the right people hear about each change,
    //   * nobody hears about it BEFORE its transaction commits (or at all, if it rolls back),
    //   * nobody who must not hear about it does.
    //
    // Timing facts, same as Chunk 2: delivery runs in a background task after the commit, so
    // positive assertions poll with a timeout; a negative assertion first waits for something that
    // MUST arrive (or a quiet period) before it asserts. Tests that have to arrange unusual data put
    // it back in a finally block, because every test class shares one database.
    [Collection(SharedApiCollection.Name)]
    public class RealtimeBulkChangeTests
    {
        private static readonly TimeSpan ArriveWithin = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(1200);

        private readonly TicketPortalWebApplicationFactory _factory;

        public RealtimeBulkChangeTests(TicketPortalWebApplicationFactory factory)
        {
            _factory = factory;
        }

        // ---- connection helpers ----------------------------------------------------------

        private sealed class Listener : IAsyncDisposable
        {
            public HubConnection Connection { get; }
            public ConcurrentQueue<RealtimeChange> Received { get; } = new();

            public Listener(HubConnection connection)
            {
                Connection = connection;
                connection.On<RealtimeChange[]>(RealtimeEvents.ChangesMethod, batch =>
                {
                    foreach (var change in batch)
                        Received.Enqueue(change);
                });
            }

            public bool HasSeatSignal(Guid tripId) => SeatSignals(tripId) > 0;

            public int SeatSignals(Guid tripId) =>
                Received.Count(m => m.Entity == RealtimeEvents.SeatAvailability && m.TripId == tripId);

            // The bulk seat paths announce "TripSeats changed on this trip" with no row id. (A
            // tracked save of one seat, from Chunk 2's capture, always carries the seat's id.)
            public int BulkSeatChanges(Guid tripId) =>
                Received.Count(m => m.Entity == "TripSeats" && m.TripId == tripId && m.Id == null);

            public bool HasUpdated(string entity, Guid id) =>
                Received.Any(m => m.Entity == entity && m.Id == id && m.Action == RealtimeActions.Updated);

            public ValueTask DisposeAsync() => Connection.DisposeAsync();
        }

        private async Task<Listener> ConnectAsync(string? userName = null, string? password = null)
        {
            string? token = null;
            if (userName is not null)
                token = await _factory.CreateClient().LoginAsync(userName, password!);

            var connection = new HubConnectionBuilder()
                .WithUrl(new Uri(_factory.Server.BaseAddress, RealtimeGroups.HubPath), options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                    if (token is not null)
                        options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                })
                .Build();

            var listener = new Listener(connection);
            await connection.StartAsync();

            // StartAsync returns before the hub's OnConnectedAsync has finished adding the
            // connection to its groups; a hub invocation is only processed after it has, so this
            // cheap round-trip guarantees the groups are in place before the test changes anything.
            await connection.InvokeAsync("GetMyGroups");
            return listener;
        }

        private async Task<Listener> ConnectAdminAsync() =>
            await ConnectAsync(DemoAccounts.BootstrapAdminUserName, DemoAccounts.BootstrapAdminPassword);

        private static async Task WaitForAsync(Func<bool> condition, string because)
        {
            var deadline = DateTime.UtcNow + ArriveWithin;
            while (DateTime.UtcNow < deadline && !condition())
                await Task.Delay(50);

            Assert.True(condition(), $"Timed out waiting for: {because}");
        }

        // ---- arrange helpers -------------------------------------------------------------

        // Same selection rules as SeatHoldConcurrencyTests / SeatHoldExpiryTests (PlatformManaged
        // operator only: an ExternalApiManaged trip makes the hold endpoint call out to an ERP that
        // is not there in tests). `skip` keeps these tests off the seats those classes use.
        private async Task<(Guid TripId, Guid TripSeatId)> FindAvailableSeatAsync(int skip)
        {
            using var scope = _factory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var candidate = await db.TripSeats
                .AsNoTracking()
                .Where(ts => ts.Status == TripSeatStatus.Available)
                .Where(ts => ts.Trip.Status == TripStatus.Scheduled && ts.Trip.DepartureTimeUtc > DateTime.UtcNow)
                .Where(ts => ts.Trip.BusOperator.InventoryMode == OperatorInventoryMode.PlatformManaged)
                .OrderBy(ts => ts.Id)
                .Select(ts => new { ts.Id, ts.TripId })
                .Skip(skip)
                .FirstOrDefaultAsync();

            Assert.True(candidate is not null,
                "No Available TripSeat on a future Scheduled trip of a PlatformManaged operator was found in the demo data.");

            return (candidate!.TripId, candidate.Id);
        }

        private sealed record HoldResponse(Guid Id);

        private static async Task<Guid> HoldAsync(HttpClient customer, Guid tripId, Guid tripSeatId)
        {
            var response = await customer.PostAsJsonAsync(
                "/api/seatholds", new { TripId = tripId, TripSeatIds = new[] { tripSeatId } });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            var hold = await response.Content.ReadFromJsonAsync<HoldResponse>();
            return hold!.Id;
        }

        private async Task<Guid> CustomerProfileIdAsync(string userName)
        {
            using var scope = _factory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.CustomerProfiles.AsNoTracking()
                .Where(c => c.User.UserName == userName)
                .Select(c => c.Id)
                .SingleAsync();
        }

        private async Task<Guid> OperatorOfAsync(string staffUserName)
        {
            using var scope = _factory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var operatorId = await db.StaffProfiles.AsNoTracking()
                .Where(s => s.User.UserName == staffUserName)
                .Select(s => s.BusOperatorId)
                .SingleAsync();
            return operatorId ?? throw new InvalidOperationException($"{staffUserName} is platform staff, not operator staff.");
        }

        // A seat that is really Booked (so ReleaseCancelledSeatsAsync has something to release), on
        // a trip with no Active hold — so the hold-expiry sweep, which runs on its own timer, has
        // nothing to announce for this trip while the test is watching it.
        private async Task<(Guid TripId, Guid TripSeatId, Guid BookingId)> FindBookedSeatAsync()
        {
            using var scope = _factory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var candidate = await db.TripSeats
                .AsNoTracking()
                .Where(ts => ts.Status == TripSeatStatus.Booked && ts.BookingId != null)
                .Where(ts => !db.SeatHolds.Any(h => h.TripId == ts.TripId && h.Status == SeatHoldStatus.Active))
                .OrderBy(ts => ts.Id)
                .Select(ts => new { ts.Id, ts.TripId, ts.BookingId })
                .FirstOrDefaultAsync();

            Assert.True(candidate is not null, "No Booked TripSeat was found in the demo data.");

            return (candidate!.TripId, candidate.Id, candidate.BookingId!.Value);
        }

        private async Task RestoreBookedSeatAsync(Guid tripSeatId, Guid bookingId)
        {
            using var scope = _factory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.TripSeats
                .Where(ts => ts.Id == tripSeatId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(ts => ts.Status, TripSeatStatus.Booked)
                    .SetProperty(ts => ts.BookingId, (Guid?)bookingId));
        }

        // ---- seat paths: held / released / expired ---------------------------------------

        [Fact]
        public async Task HoldingASeat_TellsTheTripsSeatMap()
        {
            // The headline case: the live seat map depends on it.
            var (tripId, tripSeatId) = await FindAvailableSeatAsync(skip: 20);

            await using var viewer = await ConnectAsync();
            await viewer.Connection.InvokeAsync("JoinTrip", tripId);

            var customer = await _factory.CreateAuthenticatedClientAsync(DemoAccounts.Customer, DemoAccounts.Password);
            var holdId = await HoldAsync(customer, tripId, tripSeatId);
            try
            {
                await WaitForAsync(() => viewer.HasSeatSignal(tripId), "the seat-map viewer to be told a seat was held");

                // The hold's tracked rows (Chunk 2) and its bulk seat update (Chunk 3) commit
                // together and are released as ONE batch, so the viewer gets exactly one signal.
                await Task.Delay(QuietPeriod);
                Assert.Equal(1, viewer.SeatSignals(tripId));
            }
            finally
            {
                // Don't leave the seat held for the rest of the test run.
                await customer.PostAsync($"/api/seatholds/{holdId}/release", content: null);
            }
        }

        [Fact]
        public async Task ReleasingAHold_TellsTheTripsSeatMap()
        {
            var (tripId, tripSeatId) = await FindAvailableSeatAsync(skip: 21);

            await using var viewer = await ConnectAsync();
            await viewer.Connection.InvokeAsync("JoinTrip", tripId);

            var customer = await _factory.CreateAuthenticatedClientAsync(DemoAccounts.Customer, DemoAccounts.Password);
            var holdId = await HoldAsync(customer, tripId, tripSeatId);
            await WaitForAsync(() => viewer.HasSeatSignal(tripId), "the hold's own signal");

            viewer.Received.Clear(); // from here on only the release can produce a signal

            var release = await customer.PostAsync($"/api/seatholds/{holdId}/release", content: null);
            Assert.Equal(HttpStatusCode.NoContent, release.StatusCode);

            await WaitForAsync(() => viewer.HasSeatSignal(tripId), "the seat-map viewer to be told the seat was released");
        }

        [Fact]
        public async Task TheExpirySweep_TellsTheSeatMap_TheAdminHoldsPage_AndTheBookingsOwnerOnly()
        {
            var (tripId, tripSeatId) = await FindAvailableSeatAsync(skip: 22);
            var rahim = await CustomerProfileIdAsync(DemoAccounts.Customer);

            var customer = await _factory.CreateAuthenticatedClientAsync(DemoAccounts.Customer, DemoAccounts.Password);
            var holdId = await HoldAsync(customer, tripId, tripSeatId);

            // Listeners connect FIRST: the hosted sweeper (every 15 s) may expire the hold the
            // moment it is backdated, and whoever does the sweep, its announcement must find them.
            await using var viewer = await ConnectAsync();
            await viewer.Connection.InvokeAsync("JoinTrip", tripId);
            await using var admin = await ConnectAdminAsync();
            await using var owner = await ConnectAsync(DemoAccounts.Customer, DemoAccounts.Password);
            await using var someoneElse = await ConnectAsync("karim.sheikh", DemoAccounts.Password);

            // Arrange a booking that is stuck waiting on this hold (what an abandoned checkout
            // looks like), using one of rahim's seeded bookings and putting it back afterwards.
            Guid bookingId;
            BookingStatus originalStatus;
            Guid? originalHoldId;
            using (var scope = _factory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var booking = await db.Bookings.AsNoTracking()
                    .Where(b => b.CustomerProfileId == rahim)
                    .OrderBy(b => b.Id)
                    .Select(b => new { b.Id, b.Status, b.SeatHoldId })
                    .FirstAsync();
                bookingId = booking.Id;
                originalStatus = booking.Status;
                originalHoldId = booking.SeatHoldId;

                await db.Bookings
                    .Where(b => b.Id == bookingId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(b => b.Status, BookingStatus.PendingPayment)
                        .SetProperty(b => b.SeatHoldId, (Guid?)holdId));
            }

            try
            {
                // Backdate the hold, exactly like SeatHoldExpiryTests: only the clock is faked.
                using (var scope = _factory.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    await db.SeatHolds
                        .Where(h => h.Id == holdId)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(h => h.HoldExpiresAtUtc, DateTime.UtcNow.AddMinutes(-1)));
                }

                using (var scope = _factory.CreateScope())
                {
                    var seatHolds = scope.ServiceProvider.GetRequiredService<SeatHoldService>();
                    await seatHolds.ExpireOverdueHoldsAsync();
                }

                // The public seat map.
                await WaitForAsync(() => viewer.HasSeatSignal(tripId), "the seat-map viewer to be told the seat was freed");

                // The admin Seat Holds page and Trip Seats: the hold row changed (an UPDATE, not the
                // creation that announced it earlier), and the seats of that trip changed in bulk.
                await WaitForAsync(() => admin.HasUpdated("SeatHolds", holdId), "the admin to be told the hold expired");
                await WaitForAsync(() => admin.BulkSeatChanges(tripId) >= 1, "the admin to be told the trip's seats changed");

                // The booking that was stuck on the hold: its owner and the admin.
                await WaitForAsync(() => admin.HasUpdated("Bookings", bookingId), "the admin to be told the booking expired");
                await WaitForAsync(() => owner.HasUpdated("Bookings", bookingId), "the booking's owner to be told it expired");

                await Task.Delay(QuietPeriod);
                Assert.DoesNotContain(someoneElse.Received, m => m.Entity == "Bookings" && m.Id == bookingId);

                // The anonymous viewer only ever gets the seat signal — nothing about holds or bookings.
                Assert.All(viewer.Received, m => Assert.Equal(RealtimeEvents.SeatAvailability, m.Entity));

                // And the database really did change the way the signals said it did.
                using var verifyScope = _factory.CreateScope();
                var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
                Assert.Equal(BookingStatus.Expired,
                    await verifyDb.Bookings.AsNoTracking().Where(b => b.Id == bookingId).Select(b => b.Status).SingleAsync());
                Assert.Equal(TripSeatStatus.Available,
                    await verifyDb.TripSeats.AsNoTracking().Where(ts => ts.Id == tripSeatId).Select(ts => ts.Status).SingleAsync());
            }
            finally
            {
                using var scope = _factory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Bookings
                    .Where(b => b.Id == bookingId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(b => b.Status, originalStatus)
                        .SetProperty(b => b.SeatHoldId, originalHoldId));
            }
        }

        [Fact]
        public async Task TheTripCancelCascade_TellsTheSeatMap_AndTheAdminHoldsPage()
        {
            var (tripId, tripSeatId) = await FindAvailableSeatAsync(skip: 23);

            var customer = await _factory.CreateAuthenticatedClientAsync(DemoAccounts.Customer, DemoAccounts.Password);
            var holdId = await HoldAsync(customer, tripId, tripSeatId);

            await using var viewer = await ConnectAsync();
            await viewer.Connection.InvokeAsync("JoinTrip", tripId);
            await using var admin = await ConnectAdminAsync();

            int released;
            using (var scope = _factory.CreateScope())
            {
                var seatHolds = scope.ServiceProvider.GetRequiredService<SeatHoldService>();
                released = await seatHolds.ReleaseActiveHoldsForTripAsync(tripId, "Realtime Chunk 3 test");
            }

            Assert.True(released >= 1, "Expected the cascade to close at least the hold this test just made.");

            await WaitForAsync(() => viewer.HasSeatSignal(tripId), "the seat-map viewer to be told the seats were freed");
            await WaitForAsync(() => admin.HasUpdated("SeatHolds", holdId), "the admin to be told the hold was closed");
            await WaitForAsync(() => admin.BulkSeatChanges(tripId) >= 1, "the admin to be told the trip's seats changed");
        }

        // ---- seat release inside a caller's transaction (cancellations) -------------------

        [Fact]
        public async Task ACancelledSeatsRelease_IsAnnouncedOnlyAfterTheCallersTransactionCommits()
        {
            // CancellationProcessingService.ApproveAsync and ExternalBookingSyncService both call
            // SeatHoldService.ReleaseCancelledSeatsAsync INSIDE their own transaction. This does
            // the same, by hand, to pin the commit-awareness: no signal while the transaction is
            // open, exactly one after it commits.
            var (tripId, tripSeatId, bookingId) = await FindBookedSeatAsync();

            await using var viewer = await ConnectAsync();
            await viewer.Connection.InvokeAsync("JoinTrip", tripId);
            await using var admin = await ConnectAdminAsync();

            try
            {
                using var scope = _factory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var seatHolds = scope.ServiceProvider.GetRequiredService<SeatHoldService>();

                await using (var transaction = await db.Database.BeginTransactionAsync())
                {
                    var released = await seatHolds.ReleaseCancelledSeatsAsync(bookingId, new[] { tripSeatId });
                    Assert.Equal(1, released);

                    await Task.Delay(QuietPeriod);
                    Assert.Equal(0, admin.BulkSeatChanges(tripId));
                    Assert.False(viewer.HasSeatSignal(tripId), "A seat release must not be announced before its transaction commits.");

                    await transaction.CommitAsync();
                }

                await WaitForAsync(() => viewer.HasSeatSignal(tripId), "the seat-map viewer to be told the seat was released");
                await WaitForAsync(() => admin.BulkSeatChanges(tripId) == 1, "the admin to be told the trip's seats changed");
            }
            finally
            {
                await RestoreBookedSeatAsync(tripSeatId, bookingId);
            }
        }

        [Fact]
        public async Task ACancelledSeatsRelease_InATransactionThatRollsBack_IsNeverAnnounced()
        {
            var (tripId, tripSeatId, bookingId) = await FindBookedSeatAsync();

            await using var viewer = await ConnectAsync();
            await viewer.Connection.InvokeAsync("JoinTrip", tripId);
            await using var admin = await ConnectAdminAsync();

            try
            {
                using var scope = _factory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var seatHolds = scope.ServiceProvider.GetRequiredService<SeatHoldService>();

                // 1) Released, then rolled back: the seat never really changed, so nobody is told.
                await using (var transaction = await db.Database.BeginTransactionAsync())
                {
                    Assert.Equal(1, await seatHolds.ReleaseCancelledSeatsAsync(bookingId, new[] { tripSeatId }));
                    await transaction.RollbackAsync();
                }

                db.ChangeTracker.Clear();

                // 2) The same release again, committed — the positive control. If the rolled-back
                //    one had been announced there would now be TWO announcements, not one.
                await using (var transaction = await db.Database.BeginTransactionAsync())
                {
                    Assert.Equal(1, await seatHolds.ReleaseCancelledSeatsAsync(bookingId, new[] { tripSeatId }));
                    await transaction.CommitAsync();
                }

                await WaitForAsync(() => admin.BulkSeatChanges(tripId) >= 1, "the committed release to be announced");
                await Task.Delay(QuietPeriod);

                Assert.Equal(1, admin.BulkSeatChanges(tripId));
            }
            finally
            {
                await RestoreBookedSeatAsync(tripSeatId, bookingId);
            }
        }

        // ---- wallet paths ----------------------------------------------------------------

        [Fact]
        public async Task ACustomersWalletChange_ReachesThatCustomerAndTheAdmin_ButNotAnotherCustomer()
        {
            var rahim = await CustomerProfileIdAsync(DemoAccounts.Customer);

            await using var owner = await ConnectAsync(DemoAccounts.Customer, DemoAccounts.Password);
            await using var someoneElse = await ConnectAsync("karim.sheikh", DemoAccounts.Password);
            await using var admin = await ConnectAdminAsync();

            using var scope = _factory.CreateScope();
            var wallet = scope.ServiceProvider.GetRequiredService<CustomerWalletService>();

            await wallet.CreditAsync(rahim, 10m, CustomerWalletTransactionType.TopUp,
                description: "Realtime Chunk 3 test top-up.");
            try
            {
                await WaitForAsync(() => owner.HasUpdated("CustomerProfiles", rahim), "the customer to be told their wallet balance changed");
                await WaitForAsync(() => admin.HasUpdated("CustomerProfiles", rahim), "the admin to be told the wallet balance changed");

                await Task.Delay(QuietPeriod);
                Assert.Empty(someoneElse.Received.Where(m => m.Entity == "CustomerProfiles" || m.Entity == "CustomerWalletTransactions"));
            }
            finally
            {
                // Net zero, so no other test sees a different balance.
                await wallet.DebitAsync(rahim, 10m, CustomerWalletTransactionType.AdminAdjustment,
                    description: "Realtime Chunk 3 test clean-up.");
            }
        }

        [Fact]
        public async Task AnOperatorsWalletChange_ReachesThatOperatorsStaffAndTheAdmin_ButNotAnotherOperator()
        {
            var greenLine = await OperatorOfAsync(DemoAccounts.GreenLineManager);
            var shohagh = await OperatorOfAsync(DemoAccounts.ShohaghSupervisor);
            Assert.NotEqual(greenLine, shohagh);

            await using var admin = await ConnectAdminAsync();
            await using var greenLineStaff = await ConnectAsync(DemoAccounts.GreenLineManager, DemoAccounts.Password);
            await using var shohaghStaff = await ConnectAsync(DemoAccounts.ShohaghSupervisor, DemoAccounts.Password);

            // A payout reserves money out of the wallet's AvailablePayoutBalance with a bulk UPDATE,
            // so give the wallet something to reserve and put the exact original number back after.
            decimal originalAvailable;
            using (var scope = _factory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                originalAvailable = await db.OperatorWallets.AsNoTracking()
                    .Where(w => w.BusOperatorId == greenLine)
                    .Select(w => w.AvailablePayoutBalance)
                    .SingleAsync();

                await db.OperatorWallets
                    .Where(w => w.BusOperatorId == greenLine)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(w => w.AvailablePayoutBalance, originalAvailable + 100m));
            }

            Guid? payoutId = null;
            try
            {
                using var scope = _factory.CreateScope();
                var payouts = scope.ServiceProvider.GetRequiredService<PayoutProcessingService>();

                var payout = await payouts.CreateAsync(greenLine, 50m, "BDT", null, "Realtime Chunk 3 test payout.");
                payoutId = payout.Id;

                await WaitForAsync(
                    () => greenLineStaff.Received.Any(m => m.Entity == "OperatorWallets" && m.OperatorId == greenLine),
                    "Green Line staff to be told their operator's wallet changed");
                await WaitForAsync(
                    () => admin.Received.Any(m => m.Entity == "OperatorWallets" && m.OperatorId == greenLine),
                    "the admin to be told the wallet changed");

                // Cancelling gives the reservation back — also a bulk wallet update, also announced.
                await payouts.CancelAsync(payout.Id, "Realtime Chunk 3 test clean-up.");
                payoutId = null;

                await Task.Delay(QuietPeriod);
                Assert.DoesNotContain(shohaghStaff.Received,
                    m => m.Entity == "OperatorWallets" || m.Entity == "OperatorPayouts");
                Assert.All(shohaghStaff.Received.Where(m => m.OperatorId is not null), m => Assert.Equal(shohagh, m.OperatorId));
            }
            finally
            {
                using var scope = _factory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // If an assertion failed before the cancel above, don't leave a Pending payout behind.
                if (payoutId is { } leftover)
                {
                    await db.OperatorPayouts
                        .Where(p => p.Id == leftover)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.Status, PayoutStatus.Cancelled));
                }

                await db.OperatorWallets
                    .Where(w => w.BusOperatorId == greenLine)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(w => w.AvailablePayoutBalance, originalAvailable));
            }
        }
    }
}
