using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TicketPortal.Api.Tests.Infrastructure
{
    // Chunk 6 — a stand-in for an operator's own ERP that tests can script and inspect. It is a
    // REAL HTTP server (Kestrel, bound to a free port on 127.0.0.1), so the API under test makes a
    // genuine outbound call through the same HttpClient/handler it uses in production — the
    // destination policy, redirect refusal, timeouts and header handling are all exercised for
    // real, not mocked.
    //
    // Because it listens on loopback, an API host that should be allowed to call it must be built
    // with Integrations:AllowLocalDestinations=true; a host built WITHOUT that setting must refuse
    // to call it, which several tests rely on.
    public sealed class FakeErpServer : IAsyncDisposable
    {
        public sealed record RecordedRequest(
            string Method,
            string Path,
            IReadOnlyDictionary<string, string> Headers,
            string Body);

        public sealed record FakeResponse(
            int Status = 200,
            string Body = "{}",
            string? Location = null,
            TimeSpan? Delay = null);

        private readonly WebApplication _app;
        private readonly List<RecordedRequest> _requests = new();
        private readonly object _gate = new();

        // Replace to script a scenario. The default behaves like a healthy ERP: nothing sold,
        // bookings "Confirmed".
        public Func<RecordedRequest, FakeResponse> Responder { get; set; } = DefaultResponder;

        public string Origin { get; }

        // What an OperatorIntegration.BaseUrl should be set to (same shape as the mock ERP's).
        public string BaseUrl => Origin + "/api/v1";

        public IReadOnlyList<RecordedRequest> Requests
        {
            get
            {
                lock (_gate)
                {
                    return _requests.ToList();
                }
            }
        }

        public int CountRequests(string method, string pathContains)
        {
            return Requests.Count(r =>
                string.Equals(r.Method, method, StringComparison.OrdinalIgnoreCase)
                && r.Path.Contains(pathContains, StringComparison.OrdinalIgnoreCase));
        }

        private FakeErpServer(WebApplication app, string origin)
        {
            _app = app;
            Origin = origin;
        }

        public static async Task<FakeErpServer> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            var app = builder.Build();

            FakeErpServer? server = null;

            app.Run(async context =>
            {
                string body;
                using (var reader = new StreamReader(context.Request.Body))
                {
                    body = await reader.ReadToEndAsync();
                }

                var headers = context.Request.Headers.ToDictionary(
                    h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);

                var recorded = new RecordedRequest(
                    context.Request.Method,
                    context.Request.Path.Value ?? string.Empty,
                    headers,
                    body);

                server!.Record(recorded);

                var response = server.Responder(recorded);

                if (response.Delay is { } delay)
                {
                    try
                    {
                        await Task.Delay(delay, context.RequestAborted);
                    }
                    catch (OperationCanceledException)
                    {
                        return; // The caller gave up (its own timeout) — nothing to answer.
                    }
                }

                context.Response.StatusCode = response.Status;
                if (response.Location is not null)
                {
                    context.Response.Headers.Location = response.Location;
                }

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(response.Body);
            });

            await app.StartAsync();

            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();

            server = new FakeErpServer(app, address.TrimEnd('/'));
            return server;
        }

        private void Record(RecordedRequest request)
        {
            lock (_gate)
            {
                _requests.Add(request);
            }
        }

        public static FakeResponse DefaultResponder(RecordedRequest request)
        {
            if (request.Path.EndsWith("/seats", StringComparison.OrdinalIgnoreCase))
            {
                return new FakeResponse(200, "{\"soldSeatNumbers\":[]}");
            }

            if (request.Path.EndsWith("/bookings/confirm", StringComparison.OrdinalIgnoreCase))
            {
                return new FakeResponse(200, "{\"status\":\"Confirmed\",\"externalBookingKey\":\"FAKE-1\",\"externalPnr\":\"FAKE-PNR\"}");
            }

            return new FakeResponse(200, "{\"status\":\"ok\"}");
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
