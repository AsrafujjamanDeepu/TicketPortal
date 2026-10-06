using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using TicketPortal.Api.Tests.Infrastructure;
using Xunit;

namespace TicketPortal.Api.Tests.Integration
{
    // docs/02-Project-Concept-and-Solution.md (Real-time updates), Chunk 7 — abuse limits, token safety and the kill switch.
    // (That the kill switch makes the hub answer 404 is already covered by
    // RealtimeHubTests.KillSwitch_WhenDisabled_TheHubIsNotMapped; here we check the other half -
    // that switching real-time off leaves the rest of the API untouched.)
    //
    // Not covered here on purpose: "CloseOnAuthenticationExpiration closes the socket when the
    // JWT expires". Login hard-codes a 3-hour lifetime and this suite's own rule is never to
    // mint a JWT by hand, so an automated test would need one of the two. That check is a short
    // manual procedure instead - see docs/docs/01-Run-and-Manual-Test-Guide.md, "Verifying token expiry" - and the
    // client's reaction to it (return to login) is unit-tested in the React hook tests.
    [Collection(SharedApiCollection.Name)]
    public class RealtimeHardeningTests
    {
        private const string HubPath = "/hubs/realtime";

        private readonly TicketPortalWebApplicationFactory _factory;

        public RealtimeHardeningTests(TicketPortalWebApplicationFactory factory)
        {
            _factory = factory;
        }

        private static HubConnection BuildConnection(WebApplicationFactory<Program> factory, string? token = null) =>
            new HubConnectionBuilder()
                .WithUrl(new Uri(factory.Server.BaseAddress, HubPath), options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                    if (token is not null)
                        options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                })
                .Build();

        // ---- abuse limits -------------------------------------------------------------------

        [Fact]
        public async Task RateLimit_ACallFlood_IsRefused_WithoutDroppingTheConnection()
        {
            await using var limited = _factory.WithWebHostBuilder(builder =>
                builder.UseSetting("Realtime:MaxInvocationsPerSecond", "5"));

            await using var connection = BuildConnection(limited);
            await connection.StartAsync();

            HubException? refused = null;
            for (var i = 0; i < 40 && refused is null; i++)
            {
                try
                {
                    // LeaveTrip is harmless and fast, so only the rate limit can refuse it.
                    await connection.InvokeAsync("LeaveTrip", Guid.NewGuid());
                }
                catch (HubException ex)
                {
                    refused = ex;
                }
            }

            Assert.NotNull(refused);
            Assert.Contains("Too many requests", refused!.Message);
            Assert.Equal(HubConnectionState.Connected, connection.State);

            await connection.StopAsync();
        }

        [Fact]
        public async Task RateLimit_NormalUse_IsNeverRefused()
        {
            // The app's own worst case: a reconnect re-joining a full set of trips at once.
            await using var connection = BuildConnection(_factory);
            await connection.StartAsync();

            for (var i = 0; i < 20; i++)
                await connection.InvokeAsync("JoinTrip", Guid.NewGuid());

            await connection.StopAsync();
        }

        [Fact]
        public async Task OversizedMessage_IsRefused_AndTheConnectionEnds()
        {
            await using var connection = BuildConnection(_factory);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.Closed += _ =>
            {
                closed.TrySetResult();
                return Task.CompletedTask;
            };
            await connection.StartAsync();

            // 64 KB in place of a GUID: far over Realtime:MaxReceiveMessageSizeBytes (4096 by default).
            await Assert.ThrowsAnyAsync<Exception>(() =>
                connection.InvokeAsync("JoinTrip", new string('x', 64 * 1024)));

            var finished = await Task.WhenAny(closed.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            Assert.Same(closed.Task, finished);
            Assert.NotEqual(HubConnectionState.Connected, connection.State);
        }

        // ---- token safety -------------------------------------------------------------------

        [Fact]
        public async Task AccessToken_NeverAppearsInLogs()
        {
            // The SignalR client sends the JWT in the URL. ASP.NET Core's request logging would
            // write that URL (token included) at Information level unless the framework
            // categories stay at Warning - see the "Logging" comment in appsettings.json. This
            // fails loudly if someone lowers that level or adds HTTP logging.
            var logs = new CapturingLoggerProvider();
            await using var observed = _factory.WithWebHostBuilder(builder =>
                builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(logs)));

            var token = await observed.CreateClient().LoginAsync(DemoAccounts.BootstrapAdminUserName, DemoAccounts.BootstrapAdminPassword);

            // A real connection, a negotiate with the token in the query string, and a bad token.
            await using (var connection = BuildConnection(observed, token))
            {
                await connection.StartAsync();
                await connection.InvokeAsync("JoinTrip", Guid.NewGuid());
                await connection.StopAsync();
            }
            await observed.CreateClient().PostAsync(
                $"{HubPath}/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(token)}",
                new StringContent(string.Empty));
            await observed.CreateClient().PostAsync(
                $"{HubPath}/negotiate?negotiateVersion=1&access_token=not.a.real-token-value",
                new StringContent(string.Empty));

            // The capture must actually be working, or "nothing found" proves nothing.
            Assert.NotEmpty(logs.Messages);

            var parts = token.Split('.');
            foreach (var message in logs.Messages)
            {
                Assert.DoesNotContain(token, message);
                Assert.DoesNotContain(parts[1], message); // payload (user id, roles)
                Assert.DoesNotContain(parts[2], message); // signature
                Assert.DoesNotContain("not.a.real-token-value", message);
            }
        }

        // ---- kill switch --------------------------------------------------------------------

        [Fact]
        public async Task KillSwitch_RealtimeDisabled_TheRestOfTheApiStillWorks()
        {
            await using var disabled = _factory.WithWebHostBuilder(builder =>
                builder.UseSetting("Realtime:Enabled", "false"));

            var client = disabled.CreateClient();
            var token = await client.LoginAsync(DemoAccounts.BootstrapAdminUserName, DemoAccounts.BootstrapAdminPassword);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await client.GetAsync("/api/customerprofiles");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // Collects every formatted log line from every category, at whatever level the host's
        // own configuration lets through.
        private sealed class CapturingLoggerProvider : ILoggerProvider
        {
            public ConcurrentQueue<string> Messages { get; } = new();

            public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

            public void Dispose() { }

            private sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
            {
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

                public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

                public void Log<TState>(
                    LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                    Func<TState, Exception?, string> formatter)
                {
                    owner.Messages.Enqueue($"{category}: {formatter(state, exception)} {exception}");
                }
            }
        }
    }
}
