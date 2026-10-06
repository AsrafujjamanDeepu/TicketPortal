using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketPortal.Api.Data;
using TicketPortal.Api.Models.Identity;
using TicketPortal.Api.Realtime;
using TicketPortal.Api.Tests.Infrastructure;
using Xunit;

namespace TicketPortal.Api.Tests.Integration
{
    // docs/02-Project-Concept-and-Solution.md (Real-time updates), Chunk 1 — hub foundation + authentication. No change events flow
    // yet (that is Chunk 2), so these tests prove the two things Chunk 1 owns:
    //   1. who may connect (anonymous yes, valid token yes, a token that fails validation = 401,
    //      never a silent downgrade to anonymous), and
    //   2. which groups the SERVER puts each kind of connection in (the group matrix in the plan,
    //      section 2) — read back through the hub's own GetMyGroups diagnostics method.
    //
    // Logins go through the real POST /api/account/login (AuthHelper), same as every other test
    // here. The SignalR client talks to the in-memory TestServer over LONG POLLING: the test
    // host has no real socket, and WebSocket support in TestServer is a different code path
    // from the one production uses; the hub logic under test is transport-independent.
    [Collection(SharedApiCollection.Name)]
    public class RealtimeHubTests
    {
        private const string NegotiateUrl = "/hubs/realtime/negotiate?negotiateVersion=1";

        private readonly TicketPortalWebApplicationFactory _factory;

        public RealtimeHubTests(TicketPortalWebApplicationFactory factory)
        {
            _factory = factory;
        }

        // What GetMyGroups returns. Property names match the hub's RealtimeSubscriptionInfo
        // record (the SignalR JSON protocol camel-cases on the wire and reads case-insensitively).
        private sealed class SubscriptionInfo
        {
            public string Actor { get; set; } = string.Empty;
            public string[] Groups { get; set; } = [];
        }

        private HubConnection BuildConnection(string? token)
        {
            return new HubConnectionBuilder()
                .WithUrl(new Uri(_factory.Server.BaseAddress, RealtimeGroups.HubPath), options =>
                {
                    options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                    options.Transports = HttpTransportType.LongPolling;
                    if (token is not null)
                        options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                })
                .Build();
        }

        private async Task<string> LoginAsync(string userName, string password)
            => await _factory.CreateClient().LoginAsync(userName, password);

        private async Task<SubscriptionInfo> ConnectAndGetGroupsAsync(string? token)
        {
            await using var connection = BuildConnection(token);
            await connection.StartAsync();
            return await connection.InvokeAsync<SubscriptionInfo>("GetMyGroups");
        }

        private static string[] Sorted(IEnumerable<string> groups) => groups.OrderBy(g => g, StringComparer.Ordinal).ToArray();

        // ---------------------------------------------------------------- negotiate / auth gate

        [Fact]
        public async Task Negotiate_Anonymous_Returns200()
        {
            var response = await _factory.CreateClient().PostAsync(NegotiateUrl, content: null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task Negotiate_WithValidToken_Returns200()
        {
            var token = await LoginAsync(DemoAccounts.BootstrapAdminUserName, DemoAccounts.BootstrapAdminPassword);

            var response = await _factory.CreateClient()
                .PostAsync($"{NegotiateUrl}&access_token={Uri.EscapeDataString(token)}", content: null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task Negotiate_WithABadToken_Returns401_NotAnAnonymousConnection()
        {
            var response = await _factory.CreateClient()
                .PostAsync($"{NegotiateUrl}&access_token=this-is-not-a-jwt", content: null);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task Connect_WithABadToken_IsRejected()
        {
            await using var connection = BuildConnection("this-is-not-a-jwt");

            await Assert.ThrowsAnyAsync<HttpRequestException>(() => connection.StartAsync());
        }

        [Fact]
        public async Task QueryStringToken_IsHonoredOnlyForTheHub_NotForTheRestApi()
        {
            var token = await LoginAsync(DemoAccounts.BootstrapAdminUserName, DemoAccounts.BootstrapAdminPassword);

            // A perfectly valid admin token, but in the URL instead of the Authorization header:
            // a normal API endpoint must still say 401 (auth runs before model binding, so the
            // empty body never matters).
            var response = await _factory.CreateClient().PostAsync(
                $"/api/buses?access_token={Uri.EscapeDataString(token)}",
                new StringContent("{}", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // ---------------------------------------------------------------- group matrix

        [Fact]
        public async Task Anonymous_GetsNoFixedGroups()
        {
            var info = await ConnectAndGetGroupsAsync(token: null);

            Assert.Equal("Anonymous", info.Actor);
            Assert.Empty(info.Groups);
        }

        [Fact]
        public async Task Admin_JoinsOnlyThePlatformGroup()
        {
            var token = await LoginAsync(DemoAccounts.BootstrapAdminUserName, DemoAccounts.BootstrapAdminPassword);

            var info = await ConnectAndGetGroupsAsync(token);

            Assert.Equal("Admin", info.Actor);
            Assert.Equal(new[] { RealtimeGroups.Platform }, info.Groups);
        }

        [Fact]
        public async Task PlatformStaff_JoinTheirPlatformGroup()
        {
            var token = await LoginAsync(DemoAccounts.PlatformFinance, DemoAccounts.Password);

            var info = await ConnectAndGetGroupsAsync(token);

            Assert.Equal("Staff", info.Actor);
            Assert.Equal(new[] { RealtimeGroups.Platform }, info.Groups);
        }

        [Fact]
        public async Task OperatorStaff_JoinTheirOwnOperatorGroupAndStaff_NeverPlatform()
        {
            var operatorId = await FindOperatorIdOfStaffAsync(DemoAccounts.GreenLineManager);
            var token = await LoginAsync(DemoAccounts.GreenLineManager, DemoAccounts.Password);

            var info = await ConnectAndGetGroupsAsync(token);

            Assert.Equal("Staff", info.Actor);
            Assert.Equal(
                Sorted([RealtimeGroups.Operator(operatorId), RealtimeGroups.Staff]),
                Sorted(info.Groups));
            Assert.DoesNotContain(RealtimeGroups.Platform, info.Groups);
        }

        [Fact]
        public async Task TwoOperators_NeverShareAnOperatorGroup()
        {
            var greenLine = await ConnectAndGetGroupsAsync(
                await LoginAsync(DemoAccounts.GreenLineManager, DemoAccounts.Password));
            var shohagh = await ConnectAndGetGroupsAsync(
                await LoginAsync(DemoAccounts.ShohaghSupervisor, DemoAccounts.Password));

            var greenLineOperatorGroup = Assert.Single(greenLine.Groups, g => g.StartsWith("operator-", StringComparison.Ordinal));
            var shohaghOperatorGroup = Assert.Single(shohagh.Groups, g => g.StartsWith("operator-", StringComparison.Ordinal));

            Assert.NotEqual(greenLineOperatorGroup, shohaghOperatorGroup);
        }

        [Fact]
        public async Task Customer_JoinsOnlyTheirOwnCustomerGroup()
        {
            var customerProfileId = await FindCustomerProfileIdAsync(DemoAccounts.Customer);
            var token = await LoginAsync(DemoAccounts.Customer, DemoAccounts.Password);

            var info = await ConnectAndGetGroupsAsync(token);

            Assert.Equal("Customer", info.Actor);
            Assert.Equal(new[] { RealtimeGroups.Customer(customerProfileId) }, info.Groups);
        }

        // ---------------------------------------------------------------- JoinTrip / LeaveTrip

        [Fact]
        public async Task JoinTrip_ThenLeaveTrip_AddsAndRemovesTheTripGroup()
        {
            var tripId = Guid.NewGuid();
            await using var connection = BuildConnection(token: null);
            await connection.StartAsync();

            await connection.InvokeAsync("JoinTrip", tripId);
            await connection.InvokeAsync("JoinTrip", tripId); // joining twice must not double-count
            var joined = await connection.InvokeAsync<SubscriptionInfo>("GetMyGroups");
            Assert.Equal(new[] { RealtimeGroups.Trip(tripId) }, joined.Groups);

            await connection.InvokeAsync("LeaveTrip", tripId);
            var left = await connection.InvokeAsync<SubscriptionInfo>("GetMyGroups");
            Assert.Empty(left.Groups);
        }

        [Fact]
        public async Task JoinTrip_IsCappedPerConnection()
        {
            await using var connection = BuildConnection(token: null);
            await connection.StartAsync();

            // Realtime:MaxTripsPerConnection defaults to 20 (appsettings.json).
            for (var i = 0; i < 20; i++)
                await connection.InvokeAsync("JoinTrip", Guid.NewGuid());

            var overTheLimit = await Assert.ThrowsAsync<HubException>(
                () => connection.InvokeAsync("JoinTrip", Guid.NewGuid()));
            Assert.Contains("at most 20 trips", overTheLimit.Message);

            var info = await connection.InvokeAsync<SubscriptionInfo>("GetMyGroups");
            Assert.Equal(20, info.Groups.Length);
        }

        [Fact]
        public async Task JoinTrip_RejectsAnEmptyGuid()
        {
            await using var connection = BuildConnection(token: null);
            await connection.StartAsync();

            var ex = await Assert.ThrowsAsync<HubException>(
                () => connection.InvokeAsync("JoinTrip", Guid.Empty));
            Assert.Contains("trip id is required", ex.Message);
        }

        // ---------------------------------------------------------------- kill switch

        [Fact]
        public async Task KillSwitch_WhenDisabled_TheHubIsNotMapped()
        {
            // A second host from the same Program.cs, with only the flag changed. It reuses the
            // shared test database (migrations are a no-op, the demo seed is guarded), so this
            // costs a host start, not a new database.
            await using var disabled = _factory.WithWebHostBuilder(builder =>
                builder.UseSetting("Realtime:Enabled", "false"));
            var client = disabled.CreateClient();

            var anonymous = await client.PostAsync(NegotiateUrl, content: null);
            var badToken = await client.PostAsync($"{NegotiateUrl}&access_token=this-is-not-a-jwt", content: null);

            Assert.Equal(HttpStatusCode.NotFound, anonymous.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, badToken.StatusCode);
        }

        // ---------------------------------------------------------------- arrange helpers

        private async Task<Guid> FindOperatorIdOfStaffAsync(string userName)
        {
            using var scope = _factory.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var user = await users.FindByNameAsync(userName);
            Assert.NotNull(user);

            var operatorId = await db.StaffProfiles
                .AsNoTracking()
                .Where(sp => sp.UserId == user!.Id)
                .Select(sp => sp.BusOperatorId)
                .SingleAsync();

            Assert.True(operatorId.HasValue, $"Demo account '{userName}' is expected to be operator-scoped staff.");
            return operatorId!.Value;
        }

        private async Task<Guid> FindCustomerProfileIdAsync(string userName)
        {
            using var scope = _factory.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var user = await users.FindByNameAsync(userName);
            Assert.NotNull(user);

            return await db.CustomerProfiles
                .AsNoTracking()
                .Where(c => c.UserId == user!.Id)
                .Select(c => c.Id)
                .SingleAsync();
        }
    }
}
