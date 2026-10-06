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
    // docs/02-Project-Concept-and-Solution.md (Real-time updates), Chunk 7 - authorization tests for the "who receives what" matrix.
    //
    // RealtimeChangeCaptureTests (Chunk 2) already proves the headline guarantees: admin and
    // platform staff hear everything, operator A never hears operator B, a customer never hears
    // another customer's booking, anonymous viewers only ever get seat signals. This class covers
    // the rest of the matrix with the same method - REAL hub connections and REAL EF saves, so the
    // interceptors, resolver, router and notifier all run as in production:
    //
    //   audience            table used          who must hear it                    who must NOT
    //   PlatformOnly        LoginHistories      admin, platform staff               operator staff, customer, anonymous
    //   Shared              Terminals           admin, platform, ALL operator staff customer, anonymous
    //   Operator (only)     Trips               admin, platform, that operator      other operator, customer, anonymous
    //   Customer (only)     CustomerProfiles    admin, platform, that customer      operator staff, other customer's view
    //
    // Negative assertions follow the Chunk 2 pattern: first wait for a message that MUST arrive
    // on another connection (so the change has demonstrably been delivered), then allow a short
    // grace period before asserting that nothing arrived where it must not.
    [Collection(SharedApiCollection.Name)]
    public class RealtimeAudienceTests
    {
        private static readonly TimeSpan ArriveWithin = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(1200);

        private readonly TicketPortalWebApplicationFactory _factory;

        public RealtimeAudienceTests(TicketPortalWebApplicationFactory factory)
        {
            _factory = factory;
        }

        // ---- the matrix -----------------------------------------------------------------

        [Fact]
        public async Task PlatformOnlyTable_ReachesAdminAndPlatformStaff_NobodyElse()
        {
            await using var admin = await ConnectAsync(DemoAccounts.BootstrapAdminUserName, DemoAccounts.BootstrapAdminPassword);
            await using var platform = await ConnectAsync(DemoAccounts.PlatformManager, DemoAccounts.Password);
            await using var greenLine = await ConnectAsync(DemoAccounts.GreenLineManager, DemoAccounts.Password);
            await using var shohagh = await ConnectAsync(DemoAccounts.ShohaghSupervisor, DemoAccounts.Password);
            await using var customer = await ConnectAsync(DemoAccounts.Customer, DemoAccounts.Password);
            await using var anonymous = await ConnectAsync();

            var id = await TouchFirstAsync(db => db.LoginHistories);

            await WaitForAsync(() => admin.Has("LoginHistories", id) && platform.Has("LoginHistories", id),
                "admin and platform staff to be told about the platform-only change");
            await Task.Delay(QuietPeriod);

            foreach (var other in new[] { greenLine, shohagh, customer, anonymous })
                Assert.False(other.Has("LoginHistories", id), "A platform-only change must never reach operator staff, customers or anonymous visitors.");
        }

        [Fact]
        public async Task SharedReferenceData_ReachesEveryOperatorsStaff_ButNeverCustomersOrAnonymous()
        {
            await using var admin = await ConnectAsync(DemoAccounts.BootstrapAdminUserName, DemoAccounts.BootstrapAdminPassword);
            await using var greenLine = await ConnectAsync(DemoAccounts.GreenLineManager, DemoAccounts.Password);
            await using var shohagh = await ConnectAsync(DemoAccounts.ShohaghSupervisor, DemoAccounts.Password);
            await using var customer = await ConnectAsync(DemoAccounts.Customer, DemoAccounts.Password);
            await using var anonymous = await ConnectAsync();

            var id = await TouchFirstAsync(db => db.Terminals);

            await WaitForAsync(
                () => admin.Has("Terminals", id) && greenLine.Has("Terminals", id) && shohagh.Has("Terminals", id),
                "the admin and both operators' staff to be told a shared terminal changed");
            await Task.Delay(QuietPeriod);

            Assert.False(customer.Has("Terminals", id), "Shared reference data is for staff, not customers.");
            Assert.False(anonymous.Has("Terminals", id), "Shared reference data is for staff, not anonymous visitors.");
        }

        [Fact]
        public async Task OperatorOnlyTable_NeverReachesCustomers_EvenForTheirOwnBookingsOperator()
        {
            var greenLine = await OperatorOfAsync(DemoAccounts.GreenLineManager);

            await using var admin = await ConnectAsync(DemoAccounts.BootstrapAdminUserName, DemoAccounts.BootstrapAdminPassword);
            await using var greenLineStaff = await ConnectAsync(DemoAccounts.GreenLineManager, DemoAccounts.Password);
            await using var shohaghStaff = await ConnectAsync(DemoAccounts.ShohaghSupervisor, DemoAccounts.Password);
            await using var customer = await ConnectAsync(DemoAccounts.Customer, DemoAccounts.Password);
            await using var anonymous = await ConnectAsync();

            var tripId = await TouchFirstAsync(db => db.Trips.Where(t => t.BusOperatorId == greenLine));

            await WaitForAsync(() => admin.Has("Trips", tripId) && greenLineStaff.Has("Trips", tripId),
                "the admin and the owning operator's staff to be told the trip changed");
            await Task.Delay(QuietPeriod);

            Assert.False(shohaghStaff.Has("Trips", tripId), "Another operator's staff must never hear about this operator's trip.");
            Assert.False(customer.Has("Trips", tripId), "Trips are operator data: customers get seat signals, not trip rows.");
            Assert.False(anonymous.Has("Trips", tripId), "Anonymous visitors must never receive trip rows.");
        }

        [Fact]
        public async Task CustomerOnlyTable_ReachesTheOwnerAndThePlatform_NeverOperatorStaff()
        {
            var rahim = await CustomerProfileIdAsync(DemoAccounts.Customer);

            await using var admin = await ConnectAsync(DemoAccounts.BootstrapAdminUserName, DemoAccounts.BootstrapAdminPassword);
            await using var owner = await ConnectAsync(DemoAccounts.Customer, DemoAccounts.Password);
            await using var greenLine = await ConnectAsync(DemoAccounts.GreenLineManager, DemoAccounts.Password);
            await using var shohagh = await ConnectAsync(DemoAccounts.ShohaghSupervisor, DemoAccounts.Password);
            await using var anonymous = await ConnectAsync();

            var id = await TouchFirstAsync(db => db.CustomerProfiles.Where(c => c.Id == rahim));

            await WaitForAsync(() => admin.Has("CustomerProfiles", id) && owner.Has("CustomerProfiles", id),
                "the admin and the customer themself to be told the profile changed");
            await Task.Delay(QuietPeriod);

            Assert.False(greenLine.Has("CustomerProfiles", id), "A customer's profile is private to them and the platform, not operator staff.");
            Assert.False(shohagh.Has("CustomerProfiles", id), "A customer's profile is private to them and the platform, not operator staff.");
            Assert.False(anonymous.Has("CustomerProfiles", id), "Anonymous visitors must never receive customer data.");
        }

        [Fact]
        public async Task EveryMessage_CarriesNoCustomerIdentity()
        {
            // The wire contract has no customer field at all (customers are routed by group, not by
            // payload). Check it on a real message so a future field cannot leak one unnoticed.
            await using var admin = await ConnectAsync(DemoAccounts.BootstrapAdminUserName, DemoAccounts.BootstrapAdminPassword);

            var id = await TouchFirstAsync(db => db.Bookings);
            await WaitForAsync(() => admin.Has("Bookings", id), "the admin to be told a booking changed");

            var names = typeof(RealtimeChange).GetProperties().Select(p => p.Name).OrderBy(n => n).ToArray();
            Assert.Equal(new[] { "Action", "AtUtc", "Entity", "Id", "OperatorId", "TripId" }, names);
        }

        // ---- connection helpers ---------------------------------------------------------

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

            public bool Has(string entity, Guid id) => Received.Any(m => m.Entity == entity && m.Id == id);

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

            // StartAsync returns before OnConnectedAsync has finished joining groups; hub calls
            // are only processed after it completes, so one cheap round-trip guarantees the
            // groups are in place before the test makes its change (same trick as Chunk 2's tests).
            await connection.InvokeAsync("GetMyGroups");
            return listener;
        }

        private static async Task WaitForAsync(Func<bool> condition, string because)
        {
            var deadline = DateTime.UtcNow + ArriveWithin;
            while (DateTime.UtcNow < deadline && !condition())
                await Task.Delay(50);

            Assert.True(condition(), $"Timed out waiting for: {because}");
        }

        // ---- arrange helpers ------------------------------------------------------------

        // "Touching" a row = marking it Modified without changing a value: EF issues a real
        // UPDATE of a real seeded row (so the interceptors see a genuine committed change) while
        // nothing other tests depend on is altered. Works for every table, whatever columns it has.
        private async Task<Guid> TouchFirstAsync<T>(Func<AppDbContext, IQueryable<T>> source) where T : class
        {
            using var scope = _factory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var entity = await source(db).FirstAsync();
            var entry = db.Entry(entity);
            entry.State = EntityState.Modified;
            await db.SaveChangesAsync();

            return (Guid)entry.Property("Id").CurrentValue!;
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

        private async Task<Guid> CustomerProfileIdAsync(string userName)
        {
            using var scope = _factory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.CustomerProfiles.AsNoTracking()
                .Where(c => c.User.UserName == userName)
                .Select(c => c.Id)
                .SingleAsync();
        }
    }
}
