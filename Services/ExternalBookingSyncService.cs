using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TicketPortal.Api.Data;
using TicketPortal.Api.Models.Bookings;
using TicketPortal.Api.Models.Enums;
using TicketPortal.Api.Models.Integrations;

namespace TicketPortal.Api.Services
{
    // Piece 7 (concept doc §3.2): the actual "call the operator's API to sync and check booking
    // status" half of the API-connected integration model. Everything this needed already
    // existed as data — OperatorIntegration/OperatorIntegrationEndpoint/ExternalBookingMapping/
    // IntegrationSyncLog — but nothing ever read it. This service is the missing engine.
    //
    // Unlike IPaymentGatewayVerifier (deliberately left as an unimplemented interface, because a
    // payment gateway's signature scheme is provider-specific and can't be built generically), an
    // operator's confirm-booking call CAN be built generically: OperatorIntegration already
    // stores everything needed to make it (BaseUrl, AuthType, SecretReference) and
    // OperatorIntegrationEndpoint already stores which path to hit. There's no real operator ERP
    // to test against in this project, so calls to a placeholder/unseeded BaseUrl will fail —
    // that's expected, and exactly what IntegrationSyncLog.Status = Failed is for; the sweep
    // (see ExternalBookingSyncSweepService) just retries next tick, same as any other flaky-
    // network case.
    //
    // Convention this engine relies on: an OperatorIntegration is expected to have one active
    // OperatorIntegrationEndpoint row with Purpose == "ConfirmBooking" (case-insensitive). That's
    // the one call type this engine currently knows how to make — add more Purpose-keyed
    // branches here the same way if a second call type (e.g. "CancelBooking") is ever needed.
    //
    // Registered via builder.Services.AddHttpClient<ExternalBookingSyncService>() in Program.cs —
    // the standard ASP.NET "typed client" pattern, so HttpClient arrives already pooled/managed
    // by IHttpClientFactory instead of this class newing one up itself.
    public class ExternalBookingSyncService
    {
        private const string ConfirmBookingPurpose = "ConfirmBooking";

        private readonly AppDbContext _db;
        private readonly HttpClient _httpClient;
        private readonly ILogger<ExternalBookingSyncService> _logger;

        public ExternalBookingSyncService(AppDbContext db, HttpClient httpClient, ILogger<ExternalBookingSyncService> logger)
        {
            _db = db;
            _httpClient = httpClient;
            _logger = logger;
        }

        // Finds every Booking still waiting on an operator's confirmation and attempts one sync
        // call each. Returns how many it attempted (not how many succeeded — check
        // IntegrationSyncLog.Status for that), so the sweep can log a useful count either way.
        public async Task<int> SyncPendingBookingsAsync(CancellationToken ct = default)
        {
            var pendingBookings = await _db.Bookings
                .Include(b => b.TripSeats)
                .Where(b => b.RequiresExternalConfirmation
                    && b.ExternalConfirmedAtUtc == null
                    && b.Status != BookingStatus.Cancelled
                    && b.Status != BookingStatus.Expired
                    && b.Status != BookingStatus.Failed)
                .ToListAsync(ct);

            foreach (var booking in pendingBookings)
            {
                await SyncOneAsync(booking, ct);
            }

            return pendingBookings.Count;
        }

        private async Task SyncOneAsync(Booking booking, CancellationToken ct)
        {
            var integration = await _db.OperatorIntegrations
                .Include(i => i.Endpoints)
                .Where(i => i.BusOperatorId == booking.BusOperatorId && i.IsActive)
                .FirstOrDefaultAsync(ct);

            // No OperatorIntegration configured for this operator at all — IntegrationSyncLog's
            // OperatorIntegrationId is a required FK (see IntegrationSyncLog/IntegrationWebhookLog:
            // both carry a non-nullable OperatorIntegration navigation), so there's no valid row
            // to attach a log to yet. Nothing to call and nothing safe to log — just skip quietly;
            // this stops being silent the moment someone adds an OperatorIntegration row for this
            // BusOperator, which is the actual fix for "nothing happens."
            if (integration == null)
            {
                return;
            }

            var endpoint = integration.Endpoints
                .FirstOrDefault(e => e.IsActive
                    && string.Equals(e.Purpose, ConfirmBookingPurpose, StringComparison.OrdinalIgnoreCase));

            // An integration row exists but has no ConfirmBooking endpoint configured yet — this
            // DOES have a valid OperatorIntegrationId to log against, so record it as Skipped
            // instead of silently doing nothing every sweep with no trace anywhere.
            if (endpoint == null)
            {
                _db.IntegrationSyncLogs.Add(new IntegrationSyncLog
                {
                    OperatorIntegrationId = integration.Id,
                    EntityName = "Booking",
                    EntityKey = booking.Id.ToString(),
                    Operation = ConfirmBookingPurpose,
                    Status = IntegrationSyncStatus.Skipped,
                    StartedAtUtc = DateTime.UtcNow,
                    CompletedAtUtc = DateTime.UtcNow,
                    ErrorMessage = "OperatorIntegration has no active endpoint with Purpose 'ConfirmBooking'."
                });

                await _db.SaveChangesAsync(ct);
                return;
            }

            var log = new IntegrationSyncLog
            {
                OperatorIntegrationId = integration.Id,
                EntityName = "Booking",
                EntityKey = booking.Id.ToString(),
                Operation = ConfirmBookingPurpose,
                Status = IntegrationSyncStatus.Pending,
                StartedAtUtc = DateTime.UtcNow
            };

            try
            {
                var requestBody = new
                {
                    bookingId = booking.Id,
                    pnr = booking.Pnr,
                    tripId = booking.TripId,
                    seatCount = booking.TripSeats.Count,
                    grandTotal = booking.GrandTotal,
                    currency = booking.Currency
                };

                var requestJson = JsonSerializer.Serialize(requestBody);
                log.RequestJson = requestJson;

                // BaseUrl is expected to be just the operator's host/origin (e.g.
                // "https://operator.example.com"), with PathTemplate supplying the rest,
                // always starting with "/" — plain string concatenation instead of the
                // built-in Uri(baseUri, relativeUri) combine, which silently drops BaseUrl's
                // own path segment whenever the relative part starts with "/".
                var path = endpoint.PathTemplate.Replace("{bookingId}", booking.Id.ToString());
                var requestUri = new Uri(integration.BaseUrl.TrimEnd('/') + (path.StartsWith('/') ? path : "/" + path));

                using var request = new HttpRequestMessage(new HttpMethod(endpoint.HttpMethod), requestUri)
                {
                    Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
                };

                ApplyAuth(request, integration);

                _httpClient.Timeout = TimeSpan.FromSeconds(integration.TimeoutSeconds);

                using var response = await _httpClient.SendAsync(request, ct);
                var responseJson = await response.Content.ReadAsStringAsync(ct);
                log.ResponseJson = responseJson;

                if (!response.IsSuccessStatusCode)
                {
                    log.Status = IntegrationSyncStatus.Failed;
                    log.ErrorMessage = $"Operator API returned {(int)response.StatusCode} {response.StatusCode}.";
                }
                else
                {
                    var parsed = JsonSerializer.Deserialize<ConfirmBookingResponse>(
                        responseJson,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    await ApplyResultAsync(booking, integration, parsed, ct);

                    log.Status = IntegrationSyncStatus.Succeeded;
                }
            }
            catch (Exception ex)
            {
                // Expected in this project today — there's no real operator ERP listening on
                // BaseUrl, so this is the normal path, not a bug. Catches everything (bad
                // BaseUrl, timeout, malformed JSON reply, etc.) so one misconfigured or
                // unreachable operator integration can never take down the whole sweep — see
                // ExternalBookingSyncSweepService, which sweeps every pending booking in one run.
                log.Status = IntegrationSyncStatus.Failed;
                log.ErrorMessage = ex.Message;

                _logger.LogWarning(ex,
                    "External booking sync failed for Booking {BookingId} via OperatorIntegration {IntegrationId}.",
                    booking.Id, integration.Id);
            }
            finally
            {
                log.CompletedAtUtc = DateTime.UtcNow;
                _db.IntegrationSyncLogs.Add(log);
                await _db.SaveChangesAsync(ct);
            }
        }

        private async Task ApplyResultAsync(Booking booking, OperatorIntegration integration, ConfirmBookingResponse? parsed, CancellationToken ct)
        {
            if (parsed == null || string.IsNullOrWhiteSpace(parsed.ExternalBookingKey))
            {
                return; // Reply didn't contain anything usable — treat as a no-op, same as any other malformed/incomplete reply.
            }

            var mapping = await _db.ExternalBookingMappings
                .FirstOrDefaultAsync(m => m.OperatorIntegrationId == integration.Id && m.BookingId == booking.Id, ct);

            if (mapping == null)
            {
                mapping = new ExternalBookingMapping
                {
                    OperatorIntegrationId = integration.Id,
                    BookingId = booking.Id
                };
                _db.ExternalBookingMappings.Add(mapping);
            }

            mapping.ExternalBookingKey = parsed.ExternalBookingKey;
            mapping.ExternalPnr = parsed.ExternalPnr;
            mapping.LastSyncedAtUtc = DateTime.UtcNow;

            var recognizedStatus = Enum.TryParse<BookingStatus>(parsed.Status, ignoreCase: true, out var status)
                ? status
                : (BookingStatus?)null;

            mapping.LastKnownExternalStatus = recognizedStatus;

            booking.ExternalBookingKey = parsed.ExternalBookingKey;
            booking.ExternalPnr = parsed.ExternalPnr;

            // Only an explicit Confirmed from the operator's side clears the flag — Pending,
            // Failed, an unrecognized status string, or no status at all all mean we still don't
            // know for sure their system has really secured this seat, so the sweep keeps
            // retrying next tick instead of assuming success.
            if (recognizedStatus == BookingStatus.Confirmed)
            {
                booking.RequiresExternalConfirmation = false;
                booking.ExternalConfirmedAtUtc = DateTime.UtcNow;
            }
        }

        private static void ApplyAuth(HttpRequestMessage request, OperatorIntegration integration)
        {
            var secret = integration.SecretReference ?? string.Empty;

            switch (integration.AuthType)
            {
                case IntegrationAuthType.ApiKey when !string.IsNullOrWhiteSpace(integration.ApiKeyHeaderName):
                    request.Headers.TryAddWithoutValidation(integration.ApiKeyHeaderName, secret);
                    break;

                case IntegrationAuthType.BearerToken:
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                    break;

                case IntegrationAuthType.Basic:
                    var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(secret));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
                    break;

                // None, a misconfigured ApiKey (no header name set), and OAuth2 (needs a
                // token-fetch flow this project doesn't have yet) all send no auth header — same
                // "not implemented yet" reasoning as IPaymentGatewayVerifier for anything
                // genuinely gateway/provider-specific.
                default:
                    break;
            }
        }

        private class ConfirmBookingResponse
        {
            public string? ExternalBookingKey { get; set; }
            public string? ExternalPnr { get; set; }
            public string? Status { get; set; }
        }
    }
}
