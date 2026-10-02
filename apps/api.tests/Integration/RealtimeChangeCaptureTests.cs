using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using TicketPortal.Api.Data;
using TicketPortal.Api.Realtime;
using TicketPortal.Api.Tests.Infrastructure;
using Xunit;

namespace TicketPortal.Api.Tests.Integration
{
    // REALTIME_SIGNALR_PLAN.md, Chunk 2 — "every committed save anywhere in the app produces a
    // routed SignalR message". These tests use REAL hub connections (same long-polling setup as
    // RealtimeHubTests) and make REAL EF saves through the same DI container the API uses, so the
    // interceptors, tracker, scope resolver, router and notifier all run exactly as in production.
    //
    // Two timing facts shape the helpers below:
    //   * Delivery is deliberately asynchronous (a background task after the commit), so positive
    //     assertions poll with a timeout instead of asserting immediately.
    //   * A negative assertion ("operator B never sees it") can only be made after giving the
    //     message time to arrive, so those tests first wait for a message that MUST arrive on
    //     another connection and then allow a short grace period.
    [Collection(SharedApiCollection.Name)]
    public class RealtimeChangeCaptureTests
    {
        private static readonly TimeSpan ArriveWithin = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(1200);

        private readonly TicketPortalWebApplicationFactory _factory;

        public RealtimeChangeCaptureTests(TicketPortalWebApplicationFactory factory)
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

            public bool Has(string entity, Guid id) =>
                Received.Any(m => m.Entity == entity && m.Id == id);

            public bool HasSeatSignal(Guid tripId) =>
                Received.Any(m => m.Entity == RealtimeEvents.SeatAvailability && m.TripId == tripId);

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

            // StartAsync returns once the handshake is done, which is BEFORE the hub's
            // OnConnectedAsync has finished adding the connection to its groups. Hub invocations
            // are only processed after OnConnectedAsync completes, so a cheap round-trip here
            // (the Chunk 1 diagnostics call) guarantees the groups are in place before the test
            // makes its change.
            await connection.InvokeAsync("GetMyGroups");
            return listener;
        }

        private static async Task WaitForAsync(Func<bool> condition, string because)
        {
            var deadline = DateTime.UtcNow + ArriveWithin;
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                    return;
                await Task.Delay(50);
            }

            Assert.True(condition(), $"Timed out waiting for: {because}");
        }

        private static async Task WaitForAsync(Func<bool> condition, string because, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline && !condition())
                await Task.Delay(50);

            Assert.True(condition(), $"Timed out waiting for: {because}");
        }

        // ---- arrange helpers -------------------------------------------------------------

        // "Touching" a row means setting UpdatedAtUtc: a real EF UPDATE of a real row (every
        // business table has that column via AuditableEntity) that changes nothing other tests
        // depend on.
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

        private async Task<Guid> CustomerProfileIdAsync(string userName)
        {
            using var scope = _factory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.CustomerProfiles.AsNoTracking()
                .Where(c => c.User.UserName == userName)
                .Select(c => c.Id)
                .SingleAsync();
        }

        private async Task TouchTripAsync(Guid tripId)
        {
            using var scope = _factory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var trip = await db.Trips.SingleAsync(t => t.Id == tripId);
            trip.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        private async Task TouchBookingAsync(Guid bookingId)
        {
            using var scope = _factory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var booking = await db.Bookings.SingleAsync(b => b.Id == bookingId);
            booking.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        // ---- tests -----------------------------------------------------------------------

        [Fact]
        public async Task ConnectionTracker_CountsLiveConnections_AndReleasesThemOnDisconnect()
        {
            // Principle 7: the change-capture code does nothing while nobody is connected, which
            // only works if the hub reports connects AND disconnects accurately. Keyed by this
            // connection's own id, so other connections coming and going cannot disturb it.
            var tracker = _factory.Services.GetRequiredService<RealtimeConnectionTracker>();

            var listener = await ConnectAsync();
            var connectionId = listener.Connection.ConnectionId;
            Assert.False(string.IsNullOrEmpty(connectionId));
            Assert.True(tracker.Contains(connectionId!));
            Assert.True(tracker.HasListeners);

            await listener.DisposeAsync();

            await WaitForAsync(() => !tracker.Contains(connectionId!), "the disconnected connection to be released", ArriveWithin);
        }

        [Fact]
        public async Task Admin_ReceivesAChange_WhenABookingIsSaved()
        {
            await using var admin = await ConnectAsync(DemoAccounts.BootstrapAdminUserName, DemoAccounts.BootstrapAdminPassword);

            Guid bookingId;
            using (var scope = _factory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                bookingId = await db.Bookings.AsNoTracking().Select(b => b.Id).FirstAsync();
            }

            await TouchBookingAsync(bookingId);

            await WaitForAsync(() => admin.Has("Bookings", bookingId), "the admin to be told the booking changed");
            var message = admin.Received.First(m => m.Entity == "Bookings" && m.Id == bookingId);
            Assert.Equal(RealtimeActions.Updated, message.Action);
            Assert.NotNull(message.OperatorId);
            Assert.NotNull(message.TripId);
        }

        [Fact]
        public async Task PlatformStaff_ReceivesEveryChange_LikeAnAdmin()
        {
            await using var platform = await ConnectAsync(DemoAccounts.PlatformManager, DemoAccounts.Password);

            Guid tripId;
            using (var scope = _factory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                tripId = await db.Trips.AsNoTracking().Select(t => t.Id).FirstAsync();
            }

            await TouchTripAsync(tripId);

            await WaitForAsync(() => platform.Has("Trips", tripId), "platform staff to be told the trip changed");
        }

        [Fact]
        public async Task OperatorStaff_ReceivesOnlyTheirOwnOperatorsChanges()
        {
            var greenLine = await OperatorOfAsync(DemoAccounts.GreenLineManager);
            var shohagh = await OperatorOfAsync(DemoAccounts.ShohaghSupervisor);
            Assert.NotEqual(greenLine, shohagh);

            Guid greenLineTrip;
            Guid shohaghTrip;
            using (var scope = _factory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                greenLineTrip = await db.Trips.AsNoTracking().Where(t => t.BusOperatorId == greenLine).Select(t => t.Id).FirstAsync();
                shohaghTrip = await db.Trips.AsNoTracking().Where(t => t.BusOperatorId == shohagh).Select(t => t.Id).FirstAsync();
            }

            await using var admin = await ConnectAsync(DemoAccounts.BootstrapAdminUserName, DemoAccounts.BootstrapAdminPassword);
            await using var greenLineStaff = await ConnectAsync(DemoAccounts.GreenLineManager, DemoAccounts.Password);
            await using var shohaghStaff = await ConnectAsync(DemoAccounts.ShohaghSupervisor, DemoAccounts.Password);

            await TouchTripAsync(greenLineTrip);
            await TouchTripAsync(shohaghTrip);

            await WaitForAsync(
                () => admin.Has("Trips", greenLineTrip) && admin.Has("Trips", shohaghTrip),
                "the admin to see both operators' trips");
            await WaitForAsync(() => greenLineStaff.Has("Trips", greenLineTrip), "Green Line staff to see their own trip");
            await WaitForAsync(() => shohaghStaff.Has("Trips", shohaghTrip), "Shohagh staff to see their own trip");

            await Task.Delay(QuietPeriod);

            Assert.False(greenLineStaff.Has("Trips", shohaghTrip), "Green Line staff must never see a Shohagh change.");
            Assert.False(shohaghStaff.Has("Trips", greenLineTrip), "Shohagh staff must never see a Green Line change.");
            Assert.All(greenLineStaff.Received.Where(m => m.OperatorId is not null), m => Assert.Equal(greenLine, m.OperatorId));
            Assert.All(shohaghStaff.Received.Where(m => m.OperatorId is not null), m => Assert.Equal(shohagh, m.OperatorId));
        }

        [Fact]
        public async Task Customer_ReceivesOnlyTheirOwnBookings()
        {
            var rahim = await CustomerProfileIdAsync(DemoAccounts.Customer);

            Guid ownBooking;
            Guid someoneElsesBooking;
            using (var scope = _factory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                ownBooking = await db.Bookings.AsNoTracking()
                    .Where(b => b.CustomerProfileId == rahim).Select(b => b.Id).FirstAsync();
                someoneElsesBooking = await db.Bookings.AsNoTracking()
                    .Where(b => b.CustomerProfileId != null && b.CustomerProfileId != rahim).Select(b => b.Id).FirstAsync();
            }

            await using var admin = await ConnectAsync(DemoAccounts.BootstrapAdminUserName, DemoAccounts.BootstrapAdminPassword);
            await using var customer = await ConnectAsync(DemoAccounts.Customer, DemoAccounts.Password);

            await TouchBookingAsync(someoneElsesBooking);
            await TouchBookingAsync(ownBooking);

            await WaitForAsync(
                () => admin.Has("Bookings", ownBooking) && admin.Has("Bookings", someoneElsesBooking),
                "the admin to see both bookings change");
            await WaitForAsync(() => customer.Has("Bookings", ownBooking), "the customer to see their own booking change");

            await Task.Delay(QuietPeriod);

            Assert.False(customer.Has("Bookings", someoneElsesBooking), "A customer must never see another customer's booking.");
        }

        [Fact]
        public async Task ChildRows_AreRoutedThroughTheirBooking_ToTheOwnerAndTheOperator()
        {
            // A Ticket has no customer or operator of its own; the resolver must walk
            // Ticket -> Booking to find both.
            var rahim = await CustomerProfileIdAsync(DemoAccounts.Customer);

            Guid ticketId;
            Guid bookingOperator;
            using (var scope = _factory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var row = await db.Tickets.AsNoTracking()
                    .Where(t => t.Booking.CustomerProfileId == rahim)
                    .Select(t => new { t.Id, t.Booking.BusOperatorId })
                    .FirstAsync();
                ticketId = row.Id;
                bookingOperator = row.BusOperatorId;
            }

            await using var customer = await ConnectAsync(DemoAccounts.Customer, DemoAccounts.Password);
            await using var admin = await ConnectAsync(DemoAccounts.BootstrapAdminUserName, DemoAccounts.BootstrapAdminPassword);

            using (var scope = _factory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var ticket = await db.Tickets.SingleAsync(t => t.Id == ticketId);
                ticket.UpdatedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }

            await WaitForAsync(() => customer.Has("Tickets", ticketId), "the customer to be told their ticket changed");
            await WaitForAsync(() => admin.Has("Tickets", ticketId), "the admin to be told the ticket changed");
            Assert.Equal(bookingOperator, admin.Received.First(m => m.Entity == "Tickets" && m.Id == ticketId).OperatorId);
        }

        [Fact]
        public async Task RolledBackTransaction_SendsNothing_AndACommittedOneSendsOnlyAfterTheCommit()
        {
            Guid rolledBack;
            Guid committed;
            using (var scope = _factory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var ids = await db.Bookings.AsNoTracking().Select(b => b.Id).Take(2).ToListAsync();
                Assert.Equal(2, ids.Count);
                rolledBack = ids[0];
                committed = ids[1];
            }

            await using var admin = await ConnectAsync(DemoAccounts.BootstrapAdminUserName, DemoAccounts.BootstrapAdminPassword);

            using (var scope = _factory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // 1) Saved, then rolled back: the row was never really changed, so nobody is told.
                await using (var transaction = await db.Database.BeginTransactionAsync())
                {
                    var booking = await db.Bookings.SingleAsync(b => b.Id == rolledBack);
                    booking.UpdatedAtUtc = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                    await transaction.RollbackAsync();
                }

                db.ChangeTracker.Clear();

                // 2) Saved inside a transaction: held back until the commit lands.
                await using (var transaction = await db.Database.BeginTransactionAsync())
                {
                    var booking = await db.Bookings.SingleAsync(b => b.Id == committed);
                    booking.UpdatedAtUtc = DateTime.UtcNow;
                    await db.SaveChangesAsync();

                    await Task.Delay(QuietPeriod);
                    Assert.False(admin.Has("Bookings", committed), "A change must not be announced before its transaction commits.");

                    await transaction.CommitAsync();
                }
            }

            await WaitForAsync(() => admin.Has("Bookings", committed), "the committed booking to be announced after the commit");
            Assert.False(admin.Has("Bookings", rolledBack), "A rolled-back change must never be announced.");
        }

        [Fact]
        public async Task FailedSave_SendsNothing()
        {
            Guid bookingId;
            using (var scope = _factory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                bookingId = await db.Bookings.AsNoTracking().Select(b => b.Id).FirstAsync();
            }

            await using var admin = await ConnectAsync(DemoAccounts.BootstrapAdminUserName, DemoAccounts.BootstrapAdminPassword);

            using (var scope = _factory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var booking = await db.Bookings.SingleAsync(b => b.Id == bookingId);
                booking.UpdatedAtUtc = DateTime.UtcNow;

                // A foreign key that points nowhere: SQL Server rejects the whole save.
                booking.TripId = Guid.NewGuid();

                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            }

            await Task.Delay(QuietPeriod);
            Assert.False(admin.Has("Bookings", bookingId), "A save that failed must never be announced.");
        }

        [Fact]
        public async Task Anonymous_OnlyEverReceivesSeatAvailability_ForTripsTheyJoined()
        {
            Guid tripId;
            Guid tripSeatId;
            Guid bookingId;
            using (var scope = _factory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var booking = await db.Bookings.AsNoTracking()
                    .Where(b => b.CustomerProfileId != null)
                    .Select(b => new { b.Id, b.TripId })
                    .FirstAsync();
                bookingId = booking.Id;
                tripId = booking.TripId;
                tripSeatId = await db.TripSeats.AsNoTracking()
                    .Where(ts => ts.TripId == tripId).Select(ts => ts.Id).FirstAsync();
            }

            await using var anonymous = await ConnectAsync();
            await anonymous.Connection.InvokeAsync("JoinTrip", tripId);

            await using var admin = await ConnectAsync(DemoAccounts.BootstrapAdminUserName, DemoAccounts.BootstrapAdminPassword);

            await TouchBookingAsync(bookingId);
            using (var scope = _factory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var seat = await db.TripSeats.SingleAsync(ts => ts.Id == tripSeatId);
                seat.UpdatedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }

            await WaitForAsync(() => anonymous.HasSeatSignal(tripId), "the anonymous seat-map viewer to get a seat signal");
            await WaitForAsync(() => admin.Has("Bookings", bookingId) && admin.Has("TripSeats", tripSeatId), "the admin to see both changes");

            await Task.Delay(QuietPeriod);

            Assert.All(anonymous.Received, m => Assert.Equal(RealtimeEvents.SeatAvailability, m.Entity));
            Assert.All(anonymous.Received, m =>
            {
                Assert.Null(m.Id);
                Assert.Null(m.OperatorId);
            });
        }

        [Fact]
        public async Task Anonymous_DoesNotReceiveSeatSignals_ForTripsTheyDidNotJoin()
        {
            Guid tripId;
            Guid tripSeatId;
            using (var scope = _factory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var seat = await db.TripSeats.AsNoTracking().Select(ts => new { ts.Id, ts.TripId }).FirstAsync();
                tripId = seat.TripId;
                tripSeatId = seat.Id;
            }

            await using var anonymous = await ConnectAsync();
            await using var admin = await ConnectAsync(DemoAccounts.BootstrapAdminUserName, DemoAccounts.BootstrapAdminPassword);

            using (var scope = _factory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var seat = await db.TripSeats.SingleAsync(ts => ts.Id == tripSeatId);
                seat.UpdatedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }

            await WaitForAsync(() => admin.Has("TripSeats", tripSeatId), "the admin to see the seat change");
            await Task.Delay(QuietPeriod);

            Assert.False(anonymous.HasSeatSignal(tripId));
            Assert.Empty(anonymous.Received.Where(m => m.TripId == tripId));
        }
    }
}
