using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketPortal.Api.Data;
using TicketPortal.Api.Models.Enums;
using TicketPortal.Api.Models.Integrations;
using TicketPortal.Api.Services;
using TicketPortal.Api.Tests.Infrastructure;
using Xunit;
using static TicketPortal.Api.Tests.Infrastructure.HoldTestSupport;

namespace TicketPortal.Api.Tests.Integration
{
    // Chunk 6 / C6-3 + C6-4 + C6-5 — the API's outbound calls to an operator's own system, tested
    // against a REAL HTTP server (Infrastructure/FakeErpServer) so the production HttpClient,
    // destination policy, redirect refusal, timeouts, headers and logging are all exercised.
    //
    // Each test points the seeded ExternalApiManaged operator's integration at its own fake server
    // for the duration of the test and puts everything back afterwards. The collection runs tests
    // one at a time, so this temporary change cannot be seen by any other test.
    [Collection(SharedApiCollection.Name)]
    public class ErpAvailabilityAndSecurityTests
    {
        private const string TestSecretValue = "test-erp-secret-value-9f3a"; // see TicketPortalWebApplicationFactory
        private const string AvailabilityOperation = "GetSeatAvailability";

        private readonly TicketPortalWebApplicationFactory _factory;

        public ErpAvailabilityAndSecurityTests(TicketPortalWebApplicationFactory factory)
        {
            _factory = factory;
        }

        // =============================================================================
        // Arrangement
        // =============================================================================

        private sealed class Arrangement : IAsyncDisposable
        {
            public required FakeErpServer Erp { get; init; }
            public required WebApplicationFactory<Program> Host { get; init; }
            public required List<SeatRef> Seats { get; init; }
            public required Guid OperatorId { get; init; }
            public required Guid IntegrationId { get; init; }
            public HttpClient? Customer { get; init; }
            public required Func<Task> Restore { get; init; }

            public async ValueTask DisposeAsync()
            {
                await Restore();
                await Erp.DisposeAsync();
                await Host.DisposeAsync();
            }
        }

        private async Task<Arrangement> ArrangeAsync(
            int seatCount = 2,
            bool needSeats = true,
            Action<IWebHostBuilder>? configureHost = null,
            string? secretReference = "env:TEST_ERP_KEY",
            int timeoutSeconds = 30,
            bool integrationActive = true)
        {
            var erp = await FakeErpServer.StartAsync();

            var host = _factory.WithWebHostBuilder(builder =>
            {
                // The fake ERP listens on loopback, so the host under test must be one that is
                // allowed to call loopback. (Tests that need the opposite say so via configureHost.)
                builder.UseSetting("Integrations:AllowLocalDestinations", "true");
                configureHost?.Invoke(builder);
            });

            var seats = needSeats
                ? await FindAvailableSeatsAsync(host, seatCount, OperatorInventoryMode.ExternalApiManaged)
                : new List<SeatRef>();

            Guid operatorId;
            Guid integrationId;
            OperatorIntegration original;

            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                operatorId = needSeats
                    ? await db.Trips.Where(t => t.Id == seats[0].TripId).Select(t => t.BusOperatorId).SingleAsync()
                    : await db.OperatorIntegrations
                        .Where(i => i.BusOperator.InventoryMode == OperatorInventoryMode.ExternalApiManaged
                            && i.Endpoints.Any(e => e.IsActive && e.Purpose == AvailabilityOperation))
                        .OrderBy(i => i.Name).Select(i => i.BusOperatorId).FirstAsync();

                original = await db.OperatorIntegrations.AsNoTracking()
                    .Include(i => i.Endpoints)
                    .Where(i => i.BusOperatorId == operatorId)
                    .OrderByDescending(i => i.IsActive)
                    .FirstAsync();
                integrationId = original.Id;

                Assert.Contains(original.Endpoints, e => e.IsActive && e.Purpose == AvailabilityOperation);

                await db.OperatorIntegrations.Where(i => i.Id == integrationId).ExecuteUpdateAsync(u => u
                    .SetProperty(i => i.BaseUrl, erp.BaseUrl)
                    .SetProperty(i => i.AuthType, IntegrationAuthType.ApiKey)
                    .SetProperty(i => i.ApiKeyHeaderName, "X-API-Key")
                    .SetProperty(i => i.SecretReference, secretReference)
                    .SetProperty(i => i.TimeoutSeconds, timeoutSeconds)
                    .SetProperty(i => i.IsActive, integrationActive));
            }

            async Task RestoreAsync()
            {
                using var scope = host.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.OperatorIntegrations.Where(i => i.Id == integrationId).ExecuteUpdateAsync(u => u
                    .SetProperty(i => i.BaseUrl, original.BaseUrl)
                    .SetProperty(i => i.AuthType, original.AuthType)
                    .SetProperty(i => i.ApiKeyHeaderName, original.ApiKeyHeaderName)
                    .SetProperty(i => i.SecretReference, original.SecretReference)
                    .SetProperty(i => i.TimeoutSeconds, original.TimeoutSeconds)
                    .SetProperty(i => i.IsActive, original.IsActive));
            }

            return new Arrangement
            {
                Erp = erp,
                Host = host,
                Seats = seats,
                OperatorId = operatorId,
                IntegrationId = integrationId,
                Customer = needSeats ? await RegisterCustomerClientAsync(host) : null,
                Restore = RestoreAsync,
            };
        }

        private static Task<HttpResponseMessage> Hold(Arrangement a, int seatIndex = 0) =>
            HoldAsync(a.Customer!, a.Seats[seatIndex].TripId, a.Seats[seatIndex].TripSeatId);

        private static async Task<IntegrationSyncLog> LatestAvailabilityLogAsync(Arrangement a)
        {
            using var scope = a.Host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.IntegrationSyncLogs.AsNoTracking()
                .Where(l => l.OperatorIntegrationId == a.IntegrationId && l.Operation == AvailabilityOperation)
                .OrderByDescending(l => l.StartedAtUtc)
                .FirstAsync();
        }

        private static async Task<TripSeatStatus> SeatStatusAsync(Arrangement a, int seatIndex = 0)
        {
            using var scope = a.Host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var id = a.Seats[seatIndex].TripSeatId;
            return await db.TripSeats.AsNoTracking().Where(s => s.Id == id).Select(s => s.Status).SingleAsync();
        }

        private static async Task ReleaseAsync(Arrangement a, params HttpResponseMessage[] holdResponses)
        {
            foreach (var response in holdResponses.Where(r => r.StatusCode == HttpStatusCode.Created))
            {
                await ReleaseQuietlyAsync(a.Customer!, (await ReadHoldAsync(response)).Id);
            }
        }

        private static void UseOpenMode(IWebHostBuilder builder) =>
            builder.UseSetting("Integrations:AvailabilityFailureMode", "Open");

        // =============================================================================
        // C6-4 — what a failed availability check means (decision D7)
        // =============================================================================

        [Fact]
        public async Task Closed_ErpAnswers500_HoldIsRefusedWith503_NoSeatIsHeld_AndTheFailureIsLogged()
        {
            await using var a = await ArrangeAsync();
            a.Erp.Responder = _ => new FakeErpServer.FakeResponse(500, "{\"error\":\"boom\"}");

            var response = await Hold(a);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("OperatorAvailabilityUnavailable", await ReadCodeAsync(response));
            Assert.Contains("try again", await ReadMessageAsync(response));
            Assert.Equal(TripSeatStatus.Available, await SeatStatusAsync(a));

            var log = await LatestAvailabilityLogAsync(a);
            Assert.Equal(IntegrationSyncStatus.Failed, log.Status);
            Assert.Contains("500", log.ErrorMessage);
        }

        [Fact]
        public async Task Open_ErpAnswers500_HoldProceedsOnTheOwnSeatMap_ButTheFailureIsStillLogged()
        {
            await using var a = await ArrangeAsync(configureHost: UseOpenMode);
            a.Erp.Responder = _ => new FakeErpServer.FakeResponse(500, "{\"error\":\"boom\"}");

            var response = await Hold(a);
            try
            {
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                Assert.Equal(TripSeatStatus.Held, await SeatStatusAsync(a));

                var log = await LatestAvailabilityLogAsync(a);
                Assert.Equal(IntegrationSyncStatus.Failed, log.Status);
            }
            finally
            {
                await ReleaseAsync(a, response);
            }
        }

        [Fact]
        public async Task Closed_ErpDoesNotAnswerInTime_HoldIsRefusedWith503_AfterTheConfiguredTimeout()
        {
            await using var a = await ArrangeAsync(timeoutSeconds: 1);
            a.Erp.Responder = _ => new FakeErpServer.FakeResponse(200, "{\"soldSeatNumbers\":[]}", Delay: TimeSpan.FromSeconds(15));

            var clock = Stopwatch.StartNew();
            var response = await Hold(a);
            clock.Stop();

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), $"The check should give up after ~1s, took {clock.Elapsed}.");
            Assert.Equal(TripSeatStatus.Available, await SeatStatusAsync(a));

            var log = await LatestAvailabilityLogAsync(a);
            Assert.Equal(IntegrationSyncStatus.Failed, log.Status);
            Assert.Contains("did not respond within 1 second", log.ErrorMessage);
        }

        [Fact]
        public async Task ASeatTheErpReportsSold_IsRefusedWith409_NamingTheSeat_InEitherMode()
        {
            foreach (var openMode in new[] { false, true })
            {
                await using var a = await ArrangeAsync(configureHost: openMode ? UseOpenMode : null);
                var soldSeat = a.Seats[0].SeatNumber;
                a.Erp.Responder = _ => new FakeErpServer.FakeResponse(200, $"{{\"soldSeatNumbers\":[\"{soldSeat}\"]}}");

                var response = await Hold(a);

                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                Assert.Contains(soldSeat, await ReadMessageAsync(response));
                Assert.Equal(TripSeatStatus.Available, await SeatStatusAsync(a));
            }
        }

        [Fact]
        public async Task WhenTheErpAnswersNothingSold_TheHoldSucceeds_AndTheConfiguredSecretIsSent()
        {
            await using var a = await ArrangeAsync();

            var response = await Hold(a);
            try
            {
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);

                var request = Assert.Single(a.Erp.Requests, r => r.Path.EndsWith("/seats"));
                Assert.Equal("GET", request.Method);
                Assert.Contains(a.Seats[0].TripId.ToString(), request.Path);
                Assert.Equal(TestSecretValue, request.Headers["X-API-Key"]);

                var log = await LatestAvailabilityLogAsync(a);
                Assert.Equal(IntegrationSyncStatus.Succeeded, log.Status);

                using var scope = a.Host.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var integration = await db.OperatorIntegrations.AsNoTracking().SingleAsync(i => i.Id == a.IntegrationId);
                Assert.NotNull(integration.LastSuccessfulSyncAtUtc);
            }
            finally
            {
                await ReleaseAsync(a, response);
            }
        }

        [Fact]
        public async Task AnOperatorWithNoActiveIntegration_CannotBeHeld_WhenClosed_AndNoRequestIsMade()
        {
            await using var a = await ArrangeAsync(integrationActive: false);

            var response = await Hold(a);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Empty(a.Erp.Requests);
            Assert.Equal(TripSeatStatus.Available, await SeatStatusAsync(a));
        }

        [Fact]
        public async Task ASuccessfulAnswer_IsReusedWithinTheCacheLifetime_ButNeverServedOnceStale()
        {
            await using var a = await ArrangeAsync(
                seatCount: 3,
                configureHost: b => b.UseSetting("Integrations:AvailabilityCacheSeconds", "1"));

            var first = await Hold(a, 0);
            HttpResponseMessage? second = null;
            HttpResponseMessage? third = null;
            try
            {
                Assert.Equal(HttpStatusCode.Created, first.StatusCode);

                // The ERP is now down — but the answer from a moment ago is still fresh.
                a.Erp.Responder = _ => new FakeErpServer.FakeResponse(500, "{}");
                second = await Hold(a, 1);
                Assert.Equal(HttpStatusCode.Created, second.StatusCode);
                Assert.Equal(1, a.Erp.CountRequests("GET", "/seats"));

                // Once the cached answer is older than its lifetime it must not be trusted.
                await Task.Delay(TimeSpan.FromMilliseconds(1600));
                third = await Hold(a, 2);

                Assert.Equal(HttpStatusCode.ServiceUnavailable, third.StatusCode);
                Assert.Equal(2, a.Erp.CountRequests("GET", "/seats"));
                Assert.Equal(TripSeatStatus.Available, await SeatStatusAsync(a, 2));
            }
            finally
            {
                await ReleaseAsync(a, first);
                if (second is not null) await ReleaseAsync(a, second);
                if (third is not null) await ReleaseAsync(a, third);
            }
        }

        // =============================================================================
        // C6-3 — what the API is willing to send, and where
        // =============================================================================

        [Fact]
        public async Task ARedirectFromTheErp_IsNotFollowed_AndTheTargetIsNeverCalled()
        {
            await using var target = await FakeErpServer.StartAsync();
            await using var a = await ArrangeAsync();
            a.Erp.Responder = _ => new FakeErpServer.FakeResponse(302, "{}", Location: target.Origin + "/api/v1/stolen");

            var response = await Hold(a);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Empty(target.Requests);                       // the API key was never sent there
            Assert.Single(a.Erp.Requests);                       // and the original call was not repeated

            var log = await LatestAvailabilityLogAsync(a);
            Assert.Contains("redirect", log.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("env:TP_NOT_CONFIGURED_ANYWHERE", "not configured")]    // a valid reference with no value
        [InlineData("a-literal-api-key-in-the-database", "SecretReference")] // legacy literal: never used
        [InlineData("env:JWT:SigningKey", "SecretReference")]                // any other config key: not reachable
        public async Task AnUnusableSecret_StopsTheCallBeforeAnythingIsSent_AndIsNeverLogged(string secretReference, string expectedFragment)
        {
            await using var a = await ArrangeAsync(secretReference: secretReference);

            var response = await Hold(a);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Empty(a.Erp.Requests);

            var log = await LatestAvailabilityLogAsync(a);
            Assert.Contains(expectedFragment, log.ErrorMessage);
            Assert.DoesNotContain("a-literal-api-key-in-the-database", log.ErrorMessage ?? string.Empty);
            Assert.DoesNotContain(TestSecretValue, log.ErrorMessage ?? string.Empty);
        }

        [Fact]
        public async Task ALocalDestination_IsRefused_WhenLocalDestinationsAreNotAllowed()
        {
            await using var a = await ArrangeAsync(
                configureHost: b => b.UseSetting("Integrations:AllowLocalDestinations", "false"));

            var response = await Hold(a);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Empty(a.Erp.Requests);

            var log = await LatestAvailabilityLogAsync(a);
            Assert.Contains("Destination refused", log.ErrorMessage);
        }

        [Fact]
        public async Task ASecretEchoedBackByTheErp_IsRedactedFromTheStoredLog()
        {
            await using var a = await ArrangeAsync();
            a.Erp.Responder = _ => new FakeErpServer.FakeResponse(500, $"{{\"error\":\"invalid key {TestSecretValue}\"}}");

            var response = await Hold(a);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

            var log = await LatestAvailabilityLogAsync(a);
            Assert.NotNull(log.ResponseJson);
            Assert.DoesNotContain(TestSecretValue, log.ResponseJson);
            Assert.Contains("[redacted]", log.ResponseJson);
            Assert.DoesNotContain(TestSecretValue, log.ErrorMessage ?? string.Empty);
        }

        [Fact]
        public async Task TestConnection_ReportsARefusedDestination_WithoutCallingIt()
        {
            await using var a = await ArrangeAsync(
                needSeats: false,
                configureHost: b => b.UseSetting("Integrations:AllowLocalDestinations", "false"));
            var admin = await LoginClientAsync(a.Host, DemoAccounts.BootstrapAdminUserName, DemoAccounts.BootstrapAdminPassword);

            var response = await admin.PostAsync($"/api/operatorintegrations/{a.IntegrationId}/test-connection", content: null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.Contains("Destination refused", doc.RootElement.GetProperty("message").GetString());
            Assert.Empty(a.Erp.Requests);
        }

        // =============================================================================
        // C6-5 — the booking confirmation call carries an idempotency key
        // =============================================================================

        [Fact]
        public async Task ConfirmBooking_CarriesAStableIdempotencyKey_AndTheConfiguredSecret_OnEveryRetry()
        {
            // 202 Pending leaves the seeded awaiting-confirmation booking exactly as it was, so a
            // second sweep is a genuine RETRY of the same request.
            await using var a = await ArrangeAsync(
                needSeats: false,
                // 20 is the highest value the startup validator accepts (1-20); two sweeps can never reach it.
                configureHost: b => b.UseSetting("Integrations:MaxSyncAttempts", "20"));
            a.Erp.Responder = request => request.Path.EndsWith("/bookings/confirm")
                ? new FakeErpServer.FakeResponse(202, "{\"status\":\"Pending\"}")
                : FakeErpServer.DefaultResponder(request);

            async Task<List<(string BookingId, string? Key, string? ApiKey)>> SweepAsync()
            {
                var before = a.Erp.Requests.Count;
                using var scope = a.Host.Services.CreateScope();
                var sync = scope.ServiceProvider.GetRequiredService<ExternalBookingSyncService>();
                var attempted = await sync.SyncPendingBookingsAsync();
                Assert.True(attempted >= 1, "The seeded demo data should contain a paid booking awaiting the operator's confirmation.");

                return a.Erp.Requests.Skip(before)
                    .Where(r => r.Method == "POST" && r.Path.EndsWith("/bookings/confirm"))
                    .Select(r =>
                    {
                        using var body = JsonDocument.Parse(r.Body);
                        r.Headers.TryGetValue("Idempotency-Key", out var key);
                        r.Headers.TryGetValue("X-API-Key", out var apiKey);
                        return (body.RootElement.GetProperty("bookingId").GetString()!, key, apiKey);
                    })
                    .ToList();
            }

            var firstSweep = await SweepAsync();
            var secondSweep = await SweepAsync();

            Assert.NotEmpty(firstSweep);
            Assert.All(firstSweep.Concat(secondSweep), call =>
            {
                Assert.Equal($"confirm-{call.BookingId}", call.Key);
                Assert.Equal(TestSecretValue, call.ApiKey);
            });

            // A retry of a booking carries the SAME key as the first attempt — that is the point.
            Assert.Equal(
                firstSweep.Select(c => c.Key).OrderBy(k => k),
                secondSweep.Select(c => c.Key).OrderBy(k => k));
        }

        // =============================================================================
        // Saving an integration (administrator input) — C6-3 validation
        // =============================================================================

        private async Task<(HttpClient Admin, Guid OperatorId)> AdminAndOperatorAsync(WebApplicationFactory<Program>? host = null)
        {
            host ??= _factory;
            var admin = await LoginClientAsync(host, DemoAccounts.BootstrapAdminUserName, DemoAccounts.BootstrapAdminPassword);

            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return (admin, await db.BusOperators.Select(o => o.Id).FirstAsync());
        }

        private static object IntegrationBody(Guid operatorId, string baseUrl, string? secretReference, string? headerName = "X-API-Key", int timeoutSeconds = 30) => new
        {
            BusOperatorId = operatorId,
            Name = $"Chunk6 test {Guid.NewGuid():N}",
            BaseUrl = baseUrl,
            AuthType = "ApiKey",
            ApiKeyHeaderName = headerName,
            SecretReference = secretReference,
            TimeoutSeconds = timeoutSeconds,
            IsActive = false,   // never competes with the operator's real integration
        };

        private static async Task<string?> FieldAsync(HttpResponseMessage response)
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("field", out var field) ? field.GetString() : null;
        }

        [Theory]
        [InlineData("a-literal-api-key")]
        [InlineData("sk_live_0123456789abcdef")]
        [InlineData("env:JWT:SigningKey")]
        [InlineData("env:ConnectionStrings:DefaultConnection")]
        [InlineData("env:with space")]
        public async Task SavingAnIntegration_WithALiteralOrUnreachableSecretReference_IsRefused(string secretReference)
        {
            var (admin, operatorId) = await AdminAndOperatorAsync();

            var response = await admin.PostAsJsonAsync("/api/operatorintegrations",
                IntegrationBody(operatorId, "https://8.8.8.8/api/v1", secretReference));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("secretReference", await FieldAsync(response));
            Assert.DoesNotContain(secretReference, await response.Content.ReadAsStringAsync());
        }

        [Theory]
        [InlineData("http://8.8.8.8/api")]                     // plain HTTP outside Development
        [InlineData("https://127.0.0.1/api")]                  // loopback
        [InlineData("https://10.0.0.5/api")]                   // private network
        [InlineData("https://169.254.169.254/latest")]         // cloud metadata
        [InlineData("https://user:password@8.8.8.8/api")]      // credentials in the URL
        [InlineData("https://8.8.8.8/api?token=abc")]          // query string
        public async Task SavingAnIntegration_WithAnUnsafeBaseUrl_IsRefused(string baseUrl)
        {
            var (admin, operatorId) = await AdminAndOperatorAsync();

            var response = await admin.PostAsJsonAsync("/api/operatorintegrations",
                IntegrationBody(operatorId, baseUrl, "env:TEST_ERP_KEY"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("baseUrl", await FieldAsync(response));
        }

        [Fact]
        public async Task SavingAnIntegration_WhoseHostNameResolvesToAPrivateAddress_IsRefused()
        {
            await using var host = _factory.WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services =>
                    services.AddSingleton<ErpDestinationPolicy.HostResolver>(
                        (name, ct) => Task.FromResult(new[] { IPAddress.Parse("10.1.2.3") }))));
            var (admin, operatorId) = await AdminAndOperatorAsync(host);

            var response = await admin.PostAsJsonAsync("/api/operatorintegrations",
                IntegrationBody(operatorId, "https://harmless-looking.example/api", "env:TEST_ERP_KEY"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("baseUrl", await FieldAsync(response));
            Assert.Contains("non-public", await ReadMessageAsync(response));
        }

        [Theory]
        [InlineData("Host")]
        [InlineData("Content-Length")]
        [InlineData("X-Bad Header")]
        public async Task SavingAnIntegration_WithADangerousApiKeyHeaderName_IsRefused(string headerName)
        {
            var (admin, operatorId) = await AdminAndOperatorAsync();

            var response = await admin.PostAsJsonAsync("/api/operatorintegrations",
                IntegrationBody(operatorId, "https://8.8.8.8/api/v1", "env:TEST_ERP_KEY", headerName));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("apiKeyHeaderName", await FieldAsync(response));
        }

        [Fact]
        public async Task SavingAnIntegration_WithAnExcessiveTimeout_IsRefused()
        {
            var (admin, operatorId) = await AdminAndOperatorAsync();

            var response = await admin.PostAsJsonAsync("/api/operatorintegrations",
                IntegrationBody(operatorId, "https://8.8.8.8/api/v1", "env:TEST_ERP_KEY", timeoutSeconds: 3600));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task AValidIntegration_IsSaved_ShowsItsReferenceAndWhetherTheSecretIsSet_AndCanBeEditedSafely()
        {
            var (admin, operatorId) = await AdminAndOperatorAsync();

            var created = await admin.PostAsJsonAsync("/api/operatorintegrations",
                IntegrationBody(operatorId, "https://8.8.8.8/api/v1", "env:TEST_ERP_KEY"));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            using var createdDoc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var root = createdDoc.RootElement;
            var id = root.GetProperty("id").GetGuid();

            try
            {
                // The reference NAME is shown (it is not secret); the VALUE never is.
                Assert.Equal("env:TEST_ERP_KEY", root.GetProperty("secretReferenceMasked").GetString());
                Assert.True(root.GetProperty("secretConfigured").GetBoolean());
                Assert.True(
                    !root.TryGetProperty("secretReferenceProblem", out var problem) || problem.ValueKind == JsonValueKind.Null,
                    "A valid reference should report no problem.");
                Assert.DoesNotContain(TestSecretValue, root.GetRawText());

                // Editing it into something unsafe is refused just like creating it.
                var rowVersion = root.GetProperty("rowVersion").GetString();
                var unsafeEdit = await admin.PutAsJsonAsync($"/api/operatorintegrations/{id}", new
                {
                    BusOperatorId = operatorId,
                    Name = root.GetProperty("name").GetString(),
                    BaseUrl = "https://10.0.0.5/api/v1",
                    AuthType = "ApiKey",
                    ApiKeyHeaderName = "X-API-Key",
                    SecretReference = "env:TEST_ERP_KEY",
                    TimeoutSeconds = 30,
                    IsActive = false,
                    RowVersion = rowVersion,
                });
                Assert.Equal(HttpStatusCode.BadRequest, unsafeEdit.StatusCode);
                Assert.Equal("baseUrl", await FieldAsync(unsafeEdit));
            }
            finally
            {
                await admin.DeleteAsync($"/api/operatorintegrations/{id}");
            }
        }

        [Theory]
        [InlineData("TRACE", "/trips/{tripId}/seats")]
        [InlineData("GET", "trips/{tripId}/seats")]
        [InlineData("GET", "//evil.example/seats")]
        [InlineData("GET", "/a/../b")]
        public async Task SavingAnEndpoint_WithAnUnsafeMethodOrPath_IsRefused(string method, string path)
        {
            var (admin, operatorId) = await AdminAndOperatorAsync();

            var created = await admin.PostAsJsonAsync("/api/operatorintegrations",
                IntegrationBody(operatorId, "https://8.8.8.8/api/v1", "env:TEST_ERP_KEY"));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

            try
            {
                var response = await admin.PostAsJsonAsync("/api/operatorintegrationendpoints", new
                {
                    OperatorIntegrationId = id,
                    Purpose = "GetSeatAvailability",
                    HttpMethod = method,
                    PathTemplate = path,
                    IsActive = true,
                });

                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }
            finally
            {
                await admin.DeleteAsync($"/api/operatorintegrations/{id}");
            }
        }

        // =============================================================================
        // C6-4 — the policy and the "needs attention" signal administrators can see
        // =============================================================================

        [Theory]
        [InlineData(null, "Closed")]
        [InlineData("Open", "Open")]
        public async Task ThePolicyEndpoint_ReportsTheModeInForce(string? setting, string expected)
        {
            await using var host = _factory.WithWebHostBuilder(builder =>
            {
                if (setting is not null) builder.UseSetting("Integrations:AvailabilityFailureMode", setting);
            });
            var (admin, _) = await AdminAndOperatorAsync(host);

            var policy = await admin.GetFromJsonAsync<JsonElement>("/api/operatorintegrations/policy");

            Assert.Equal(expected, policy.GetProperty("availabilityFailureMode").GetString());
            Assert.False(policy.GetProperty("allowLocalDestinations").GetBoolean());   // the Testing environment is not Development
        }

        [Fact]
        public async Task AnExternalOperatorWhoseIntegrationIsSwitchedOff_NeedsAttention()
        {
            await using var a = await ArrangeAsync(needSeats: false, integrationActive: false);
            var admin = await LoginClientAsync(a.Host, DemoAccounts.BootstrapAdminUserName, DemoAccounts.BootstrapAdminPassword);

            var status = await admin.GetFromJsonAsync<JsonElement>($"/api/operatorintegrations/status/{a.OperatorId}");

            Assert.True(status.GetProperty("needsAttention").GetBoolean());
            Assert.Contains("switched off", status.GetProperty("attentionReason").GetString());
            Assert.Equal("Closed", status.GetProperty("availabilityFailureMode").GetString());
        }
    }
}
