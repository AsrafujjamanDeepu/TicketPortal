using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TicketPortal.Api.Data;
using TicketPortal.Api.Models.Bookings;
using TicketPortal.Api.Models.Enums;
using TicketPortal.Api.Models.Integrations;
using TicketPortal.Api.Models.Payments;
using TicketPortal.Api.Models.Scheduling;

namespace TicketPortal.Api.Services
{
    // Piece 7 / Chunk 8 (concept doc §3.2): the actual "call the operator's API to sync and
    // check booking status" half of the API-connected integration model. See
    // docs/docs/03-Remaining-Fix-Plan.md for the full request/response contract this
    // engine speaks, and apps/mock-erp for a small local server that implements it — point a
    // demo/dev OperatorIntegration.BaseUrl at that (see Data/DemoDataSeeder.cs's Hanif rows) and
    // this engine has something real to talk to for the first time.
    //
    // Four call types now exist (Chunk 8), each keyed off OperatorIntegrationEndpoint.Purpose
    // (case-insensitive), same convention as the original ConfirmBooking-only version:
    //   - ConfirmBooking        — SyncOneAsync, driven by the sweep (ExternalBookingSyncSweepService)
    //   - GetSeatAvailability   — CheckSeatAvailabilityAsync, called synchronously from
    //                             SeatHoldsController before every hold on an ExternalApiManaged trip
    //   - CancelBooking         — TryCancelExternalBookingAsync, best-effort propagation from
    //                             CancellationProcessingService
    //   - GetBookingStatus      — documented in the contract and seeded for completeness, but no
    //                             caller in this project yet; a natural place to add a manual
    //                             "check status now" admin action later.
    //
    // Rejection/timeout policy (Chunk 8 task 4): a booking must never sit "Confirmed" forever on
    // the strength of an operator reply we never actually got. Three ways a booking gets
    // rejected here, all converging on RejectConfirmedBookingAsync:
    //   1. The operator API returns 409 Conflict — treated as an immediate, explicit rejection.
    //   2. A 2xx reply's parsed Status is one of RejectionStatusValues (e.g. "Rejected",
    //      "Failed") — also immediate.
    //   3. Any other failure (5xx, malformed reply, network error, timeout) increments a
    //      per-booking failure count; once that count reaches Integrations:MaxSyncAttempts
    //      (default 5 — see ApplyTimeoutPolicyAsync), it's escalated to a rejection too, so a
    //      permanently unreachable ERP can't leave a booking "confirmed" forever either.
    // RejectConfirmedBookingAsync reuses the same shape as PaymentConfirmationService's
    // paid-but-seats-lost path and CancellationProcessingService.ApproveAsync: cancel the
    // tickets, release the seats through SeatHoldService (still the only class allowed to touch
    // TripSeat.Status), mark the booking Failed, and create an automatic Refund at Requested —
    // RefundProcessingService is still the only thing that ever advances a Refund from there.
    //
    // Registered via builder.Services.AddHttpClient<ExternalBookingSyncService>() in Program.cs —
    // the standard ASP.NET "typed client" pattern, so HttpClient arrives already pooled/managed
    // by IHttpClientFactory instead of this class newing one up itself. That registration also
    // makes this service injectable directly wherever it's needed (SeatHoldsController,
    // OperatorIntegrationsController, CancellationProcessingService), not just from the sweep.
    public class ExternalBookingSyncService
    {
        private const string ConfirmBookingPurpose = "ConfirmBooking";
        private const string GetSeatAvailabilityPurpose = "GetSeatAvailability";
        private const string CancelBookingPurpose = "CancelBooking";
        private const string TestConnectionOperation = "TestConnection";

        // Raw operator-reply status strings that mean "definitely no" — deliberately NOT limited
        // to exact TicketPortal.Api.Models.Enums.BookingStatus member names (an operator's ERP
        // has no reason to use our internal enum's exact spelling), so this is checked as plain
        // string comparison, separate from the BookingStatus.TryParse done in ApplyResultAsync
        // for the informational LastKnownExternalStatus mapping field.
        private static readonly string[] RejectionStatusValues =
        {
            "Failed", "Rejected", "Declined", "Cancelled", "Canceled", "SeatUnavailable"
        };

        private const int DefaultMaxConfirmAttempts = 5;

        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

        // GetSeatAvailability is called on every seat-hold attempt for an ExternalApiManaged
        // trip (see SeatHoldsController), so a short in-memory cache keeps a busy search/checkout
        // page from hammering the operator's ERP on every click. Deliberately static (this
        // service itself is scoped-per-request/per-typed-client-call, so an instance field would
        // reset every time) and keyed by TripId. Same tradeoff as any other in-process cache: a
        // multi-instance deployment would see up to AvailabilityCacheTtl of staleness per
        // instance, which is acceptable here since the hold itself is still re-validated against
        // TripSeat.Status at the database level regardless (see SeatHoldService).
        //
        // Chunk 6 / C6-4: the TTL is now Integrations:AvailabilityCacheSeconds (default 30, 0 turns
        // the cache off — tests do that). A SUCCESSFUL answer is reused for the full TTL; a FAILED
        // check is only remembered for a few seconds, so a flapping ERP is not hammered on every
        // click but a recovered one is noticed almost immediately. Entries older than their TTL
        // are never served — a stale "all clear" must not outlive the TTL because the ERP has since
        // become unreachable.
        private static readonly ConcurrentDictionary<Guid, CachedAvailability> AvailabilityCache = new();
        private static readonly TimeSpan FailedAvailabilityCacheTtl = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan AvailabilityCheckTimeout = TimeSpan.FromSeconds(5);

        // Chunk 6 / C6-5: sent on every ConfirmBooking / CancelBooking call, so an operator ERP that
        // supports idempotency can recognise a RETRY of the same request (a timeout whose reply we
        // never saw) instead of creating a second booking. The key is derived from the booking id,
        // so every retry of the same booking carries the same key; the booking id is also still in
        // the JSON body, which the original mock-erp contract keyed on.
        private const string IdempotencyKeyHeader = "Idempotency-Key";

        private readonly AppDbContext _db;
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly SeatHoldService _seatHoldService;
        private readonly ILogger<ExternalBookingSyncService> _logger;
        private readonly IntegrationSecurityOptions _security;
        private readonly ErpDestinationPolicy.HostResolver _resolver;

        // The last two parameters are optional so DemoDataSeeder (which builds this service by
        // hand and never calls an ERP) keeps compiling; dependency injection supplies both. With
        // neither, the strict defaults apply: no local/private destinations, system DNS.
        public ExternalBookingSyncService(
            AppDbContext db,
            HttpClient httpClient,
            IConfiguration configuration,
            SeatHoldService seatHoldService,
            ILogger<ExternalBookingSyncService> logger,
            IntegrationSecurityOptions? security = null,
            ErpDestinationPolicy.HostResolver? resolver = null)
        {
            _db = db;
            _httpClient = httpClient;
            _configuration = configuration;
            _seatHoldService = seatHoldService;
            _logger = logger;
            _security = security ?? new IntegrationSecurityOptions(AllowLocalDestinations: false);
            _resolver = resolver ?? ErpDestinationPolicy.SystemResolver;
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
                // Chunk 6 / C6-4: still no row to attach an IntegrationSyncLog to, but this is no
                // longer SILENT — a booking that needs the operator's confirmation and has no
                // integration to get it from will never confirm, which someone has to notice.
                _logger.LogWarning(
                    "Booking {BookingId} needs external confirmation but operator {OperatorId} has no active OperatorIntegration.",
                    booking.Id, booking.BusOperatorId);
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

            // Kept outside the try so the catch can scrub it from any text it records.
            string? secret = null;

            try
            {
                var requestBody = new
                {
                    bookingId = booking.Id,
                    pnr = booking.Pnr,
                    tripId = booking.TripId,
                    seatCount = booking.TripSeats.Count,
                    seatNumbers = booking.TripSeats.Select(s => s.SeatNumber).ToArray(),
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

                // Chunk 6 / C6-3: refuses an unsafe destination (non-HTTPS, private/metadata
                // address, ...) and an unusable secret BEFORE any request is built.
                var call = await PrepareCallAsync(integration, path, ct);
                secret = call.Secret;

                using var request = new HttpRequestMessage(new HttpMethod(endpoint.HttpMethod), call.RequestUri)
                {
                    Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
                };

                ApplyAuth(request, integration, secret);
                request.Headers.TryAddWithoutValidation(IdempotencyKeyHeader, $"confirm-{booking.Id}");

                // Per-call timeout via a linked CancellationTokenSource, NOT _httpClient.Timeout —
                // this same _httpClient instance is reused across every booking in one sweep tick
                // (see SyncPendingBookingsAsync's loop), and HttpClient.Timeout can only be set
                // BEFORE the first request is ever sent on an instance; setting it again on the
                // second booking's call throws InvalidOperationException. TimeoutSeconds also
                // varies per operator, so a single fixed client-level timeout wouldn't fit anyway.
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(integration.TimeoutSeconds));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                using var response = await _httpClient.SendAsync(request, linkedCts.Token);
                var responseJson = await response.Content.ReadAsStringAsync(ct);
                log.ResponseJson = Safe(responseJson, secret, 8000);

                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    // 409 is this contract's way of saying "no, definitively" (e.g. the seat was
                    // sold through the operator's own counter in the meantime) — an immediate
                    // rejection, not a transient failure worth retrying.
                    log.Status = IntegrationSyncStatus.Failed;
                    log.ErrorMessage = "Operator API rejected the booking (409 Conflict) — seat no longer available on their side.";
                    await RejectConfirmedBookingAsync(booking, log.ErrorMessage, ct);
                }
                else if (!response.IsSuccessStatusCode)
                {
                    log.Status = IntegrationSyncStatus.Failed;
                    log.ErrorMessage = DescribeFailureStatus(response);
                    await ApplyTimeoutPolicyAsync(booking, integration, ct);
                }
                else
                {
                    var parsed = JsonSerializer.Deserialize<ConfirmBookingResponse>(responseJson, JsonOpts);

                    // Always record whatever the operator sent back (ExternalBookingKey/Pnr,
                    // LastKnownExternalStatus) before deciding whether it amounts to a rejection
                    // — even a rejected attempt is useful audit trail on the mapping row.
                    await ApplyResultAsync(booking, integration, parsed, ct);

                    var isExplicitRejection = parsed != null
                        && parsed.Status != null
                        && RejectionStatusValues.Contains(parsed.Status, StringComparer.OrdinalIgnoreCase);

                    if (isExplicitRejection)
                    {
                        log.Status = IntegrationSyncStatus.Failed;
                        log.ErrorMessage = $"Operator ERP explicitly returned status '{parsed!.Status}'.";
                        await RejectConfirmedBookingAsync(booking, log.ErrorMessage, ct);
                    }
                    else
                    {
                        log.Status = IntegrationSyncStatus.Succeeded;
                        integration.LastSuccessfulSyncAtUtc = DateTime.UtcNow;
                    }
                }
            }
            catch (Exception ex)
            {
                // Network error, DNS failure, timeout, malformed JSON reply, etc. — all funnel
                // into the same "this attempt failed" path, which ApplyTimeoutPolicyAsync counts
                // toward Integrations:MaxSyncAttempts. One misconfigured or unreachable operator
                // integration can never take down the whole sweep either way — see
                // ExternalBookingSyncSweepService, which sweeps every pending booking in one run.
                log.Status = IntegrationSyncStatus.Failed;
                log.ErrorMessage = Safe(DescribeException(ex, integration.TimeoutSeconds), secret);

                _logger.LogWarning(
                    "External booking sync failed for Booking {BookingId} via OperatorIntegration {IntegrationId}: {Reason}",
                    booking.Id, integration.Id, log.ErrorMessage);

                await ApplyTimeoutPolicyAsync(booking, integration, ct);
            }
            finally
            {
                log.CompletedAtUtc = DateTime.UtcNow;
                _db.IntegrationSyncLogs.Add(log);
                await _db.SaveChangesAsync(ct);
            }
        }

        // Chunk 8 task 4's timeout half: once a booking has racked up MaxSyncAttempts failed
        // ConfirmBooking attempts (this one included — its own log row hasn't been saved yet at
        // the point this runs, hence "+1" below), a permanently unreachable or misbehaving ERP
        // stops getting an infinite number of retries and is treated the same as an explicit
        // rejection. Configurable via Integrations:MaxSyncAttempts for tests/tuning.
        private async Task ApplyTimeoutPolicyAsync(Booking booking, OperatorIntegration integration, CancellationToken ct)
        {
            var maxAttempts = _configuration.GetValue<int?>("Integrations:MaxSyncAttempts") ?? DefaultMaxConfirmAttempts;

            var priorFailedAttempts = await _db.IntegrationSyncLogs.CountAsync(l =>
                l.OperatorIntegrationId == integration.Id
                && l.EntityName == "Booking"
                && l.EntityKey == booking.Id.ToString()
                && l.Operation == ConfirmBookingPurpose
                && l.Status == IntegrationSyncStatus.Failed, ct);

            if (priorFailedAttempts + 1 >= maxAttempts)
            {
                await RejectConfirmedBookingAsync(booking,
                    $"Operator ERP did not confirm the booking after {priorFailedAttempts + 1} attempts (timed out or unreachable).",
                    ct);
            }
        }

        // The compensating transaction for a booking the operator's ERP has, one way or another,
        // said no to. Reuses the exact shape of PaymentConfirmationService's paid-but-seats-lost
        // path and CancellationProcessingService.ApproveAsync: cancel the tickets, hand the seats
        // back to SeatHoldService (still the only class allowed to touch TripSeat.Status), mark
        // the booking Failed, and create an automatic Refund at Requested for
        // RefundProcessingService to pick up from there.
        private async Task RejectConfirmedBookingAsync(Booking booking, string reason, CancellationToken ct)
        {
            // Idempotency guard: once the booking is no longer Confirmed there's nothing left to
            // reject (a previous sweep tick, or this same tick reached via a different path,
            // already did it) — just make sure the flag isn't left dangling.
            if (booking.Status != BookingStatus.Confirmed)
            {
                if (booking.RequiresExternalConfirmation)
                {
                    booking.RequiresExternalConfirmation = false;
                    await _db.SaveChangesAsync(ct);
                }
                return;
            }

            var truncatedReason = reason.Length > 250 ? reason[..250] : reason;

            var tickets = await _db.Tickets
                .Where(t => t.BookingId == booking.Id
                    && t.Status != TicketStatus.Cancelled
                    && t.Status != TicketStatus.Refunded)
                .ToListAsync(ct);

            var cancelledTripSeatIds = new List<Guid>();
            foreach (var ticket in tickets)
            {
                ticket.Status = TicketStatus.Cancelled;
                ticket.CancelledAtUtc = DateTime.UtcNow;
                cancelledTripSeatIds.Add(ticket.TripSeatId);
            }

            booking.Status = BookingStatus.Failed;
            booking.CancelledAtUtc = DateTime.UtcNow;
            booking.CancellationReason = truncatedReason;
            booking.RequiresExternalConfirmation = false;

            var payment = await _db.Payments
                .Where(p => p.BookingId == booking.Id && p.Status == PaymentStatus.Succeeded)
                .OrderByDescending(p => p.PaidAtUtc)
                .FirstOrDefaultAsync(ct);

            if (payment != null)
            {
                _db.Refunds.Add(new Refund
                {
                    BookingId = booking.Id,
                    PaymentId = payment.Id,
                    Amount = payment.Amount,
                    Currency = payment.Currency,
                    Status = RefundStatus.Requested,
                    Reason = $"Automatic refund: {truncatedReason}",
                    RequestedAtUtc = DateTime.UtcNow,
                });
            }
            else
            {
                // Shouldn't normally happen — a Confirmed booking implies a successful payment —
                // but this must never throw and block the rest of the rejection from applying,
                // so it's just logged for someone to notice on the integration screen / server logs.
                _logger.LogWarning(
                    "Booking {BookingId} was rejected by the operator ERP but no successful Payment " +
                    "was found to refund automatically.", booking.Id);
            }

            // Same reasoning as CancellationProcessingService.ApproveAsync: everything above is a
            // tracked-entity change, saved together; the seat release is a separate, immediate
            // ExecuteUpdateAsync call on this same AppDbContext (SeatHoldService.
            // ReleaseCancelledSeatsAsync) — an explicit transaction around both keeps this either
            // fully applied or fully rolled back, instead of risking cancelled tickets on record
            // with their seats still stuck as Booked if the release step failed on its own.
            await using var transaction = await _db.Database.BeginTransactionAsync(ct);
            await _db.SaveChangesAsync(ct);
            await _seatHoldService.ReleaseCancelledSeatsAsync(booking.Id, cancelledTripSeatIds);
            await transaction.CommitAsync(ct);
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
            // retrying next tick instead of assuming success. (Failed/Rejected/etc. get handled
            // separately by the explicit-rejection check in SyncOneAsync, above.)
            if (recognizedStatus == BookingStatus.Confirmed)
            {
                booking.RequiresExternalConfirmation = false;
                booking.ExternalConfirmedAtUtc = DateTime.UtcNow;
            }
        }

        // Chunk 8 task 5: called synchronously from SeatHoldsController before every hold on an
        // ExternalApiManaged trip, because for those trips the operator's own ERP — not our
        // TripSeat table — is the source of truth for what has been sold.
        //
        // Chunk 6 / C6-4 (decision D7): this method no longer decides what a FAILED check means.
        // It reports honestly (Success=false, with a reason that is safe to log) and the caller
        // applies Integrations:AvailabilityFailureMode — "Closed" (default: refuse the hold, the
        // seat's real state is unknown) or "Open" (let the hold through on our own seat map, as it
        // used to always do). Previously a missing integration or endpoint was reported as a
        // SUCCESS with nothing sold, i.e. it failed open silently. Every kind of failure —
        // no integration, no endpoint, refused destination, missing secret, redirect, non-2xx,
        // timeout — is a Success=false result here, and all but "no integration" (no log row can
        // exist without one) also leave an IntegrationSyncLog row an administrator can read.
        public async Task<ExternalAvailabilityResult> CheckSeatAvailabilityAsync(Trip trip, CancellationToken ct = default)
        {
            var cacheTtl = ExternalAvailabilityPolicy.GetCacheTtl(_configuration);

            if (cacheTtl > TimeSpan.Zero
                && AvailabilityCache.TryGetValue(trip.Id, out var cached))
            {
                var allowedAge = cached.Result.Success
                    ? cacheTtl
                    : TimeSpan.FromTicks(Math.Min(cacheTtl.Ticks, FailedAvailabilityCacheTtl.Ticks));

                if (DateTime.UtcNow - cached.CachedAtUtc < allowedAge)
                {
                    return cached.Result;
                }
            }

            var integration = await _db.OperatorIntegrations
                .Include(i => i.Endpoints)
                .Where(i => i.BusOperatorId == trip.BusOperatorId && i.IsActive)
                .FirstOrDefaultAsync(ct);

            if (integration == null)
            {
                _logger.LogWarning(
                    "Trip {TripId} is ExternalApiManaged but operator {OperatorId} has no active OperatorIntegration, so seat availability cannot be verified.",
                    trip.Id, trip.BusOperatorId);

                // Not cached: a fix (adding the integration) should take effect on the next click.
                return new ExternalAvailabilityResult
                {
                    Success = false,
                    NotConfigured = true,
                    ErrorMessage = "This operator has no active integration configured."
                };
            }

            var endpoint = integration.Endpoints.FirstOrDefault(e => e.IsActive
                && string.Equals(e.Purpose, GetSeatAvailabilityPurpose, StringComparison.OrdinalIgnoreCase));

            var log = new IntegrationSyncLog
            {
                OperatorIntegrationId = integration.Id,
                EntityName = "Trip",
                EntityKey = trip.Id.ToString(),
                Operation = GetSeatAvailabilityPurpose,
                Status = IntegrationSyncStatus.Pending,
                StartedAtUtc = DateTime.UtcNow,
            };

            ExternalAvailabilityResult result;
            string? secret = null;
            var cacheable = true;

            if (endpoint == null)
            {
                log.Status = IntegrationSyncStatus.Skipped;
                log.ErrorMessage = "OperatorIntegration has no active endpoint with Purpose 'GetSeatAvailability'.";
                result = new ExternalAvailabilityResult { Success = false, NotConfigured = true, ErrorMessage = log.ErrorMessage };
                cacheable = false;
            }
            else
            {
                try
                {
                    var path = endpoint.PathTemplate.Replace("{tripId}", trip.Id.ToString());
                    var call = await PrepareCallAsync(integration, path, ct);
                    secret = call.Secret;

                    using var request = new HttpRequestMessage(new HttpMethod(endpoint.HttpMethod), call.RequestUri);
                    ApplyAuth(request, integration, secret);

                    var timeoutSeconds = Math.Min(AvailabilityCheckTimeout.TotalSeconds, integration.TimeoutSeconds);
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                    using var response = await _httpClient.SendAsync(request, linkedCts.Token);
                    var responseJson = await response.Content.ReadAsStringAsync(linkedCts.Token);
                    log.ResponseJson = Safe(responseJson, secret, 8000);

                    if (!response.IsSuccessStatusCode)
                    {
                        log.Status = IntegrationSyncStatus.Failed;
                        log.ErrorMessage = DescribeFailureStatus(response);
                        result = new ExternalAvailabilityResult { Success = false, ErrorMessage = log.ErrorMessage };
                    }
                    else
                    {
                        var parsed = JsonSerializer.Deserialize<SeatAvailabilityResponse>(responseJson, JsonOpts);
                        var sold = new HashSet<string>(
                            parsed?.SoldSeatNumbers ?? new List<string>(),
                            StringComparer.OrdinalIgnoreCase);

                        log.Status = IntegrationSyncStatus.Succeeded;
                        integration.LastSuccessfulSyncAtUtc = DateTime.UtcNow;
                        result = new ExternalAvailabilityResult { Success = true, SoldSeatNumbers = sold };
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // The CALLER gave up (the customer closed the tab) — that says nothing about
                    // the operator's system, so don't log it as an integration failure.
                    throw;
                }
                catch (Exception ex)
                {
                    log.Status = IntegrationSyncStatus.Failed;
                    log.ErrorMessage = Safe(DescribeException(ex, (int)Math.Min(AvailabilityCheckTimeout.TotalSeconds, integration.TimeoutSeconds)), secret);
                    _logger.LogWarning(
                        "GetSeatAvailability failed for Trip {TripId} via OperatorIntegration {IntegrationId}: {Reason}",
                        trip.Id, integration.Id, log.ErrorMessage);
                    result = new ExternalAvailabilityResult { Success = false, ErrorMessage = log.ErrorMessage };
                }
            }

            log.CompletedAtUtc = DateTime.UtcNow;
            _db.IntegrationSyncLogs.Add(log);
            await _db.SaveChangesAsync(ct);

            if (cacheable && cacheTtl > TimeSpan.Zero)
            {
                AvailabilityCache[trip.Id] = new CachedAvailability { CachedAtUtc = DateTime.UtcNow, Result = result };
            }

            return result;
        }

        // Chunk 8 task 7 (P1): best-effort propagation of a cancellation to the operator's ERP.
        // Deliberately never throws — called from CancellationProcessingService.ApproveAsync
        // AFTER the refund/seat-release transaction has already committed, so a failure here
        // must never undo or block that. A failure just becomes a Failed sync log, same as
        // every other call this engine makes, visible on the integration screen.
        public async Task TryCancelExternalBookingAsync(Guid bookingId, CancellationToken ct = default)
        {
            var booking = await _db.Bookings.FirstOrDefaultAsync(b => b.Id == bookingId, ct);
            if (booking == null || string.IsNullOrWhiteSpace(booking.ExternalBookingKey))
            {
                return; // Never synced with an operator ERP in the first place — nothing to cancel there.
            }

            var integration = await _db.OperatorIntegrations
                .Include(i => i.Endpoints)
                .Where(i => i.BusOperatorId == booking.BusOperatorId && i.IsActive)
                .FirstOrDefaultAsync(ct);

            if (integration == null)
            {
                _logger.LogWarning(
                    "Booking {BookingId} has an external booking key but operator {OperatorId} has no active OperatorIntegration, so the cancellation was not propagated.",
                    booking.Id, booking.BusOperatorId);
                return;
            }

            var endpoint = integration.Endpoints.FirstOrDefault(e => e.IsActive
                && string.Equals(e.Purpose, CancelBookingPurpose, StringComparison.OrdinalIgnoreCase));

            var log = new IntegrationSyncLog
            {
                OperatorIntegrationId = integration.Id,
                EntityName = "Booking",
                EntityKey = booking.Id.ToString(),
                Operation = CancelBookingPurpose,
                Status = IntegrationSyncStatus.Pending,
                StartedAtUtc = DateTime.UtcNow,
            };

            if (endpoint == null)
            {
                log.Status = IntegrationSyncStatus.Skipped;
                log.ErrorMessage = "OperatorIntegration has no active endpoint with Purpose 'CancelBooking'.";
            }
            else
            {
                string? secret = null;

                try
                {
                    var path = endpoint.PathTemplate
                        .Replace("{bookingId}", booking.Id.ToString())
                        .Replace("{externalBookingKey}", Uri.EscapeDataString(booking.ExternalBookingKey));
                    var call = await PrepareCallAsync(integration, path, ct);
                    secret = call.Secret;

                    var requestJson = JsonSerializer.Serialize(new
                    {
                        bookingId = booking.Id,
                        externalBookingKey = booking.ExternalBookingKey
                    });
                    log.RequestJson = requestJson;

                    using var request = new HttpRequestMessage(new HttpMethod(endpoint.HttpMethod), call.RequestUri)
                    {
                        Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
                    };
                    ApplyAuth(request, integration, secret);
                    request.Headers.TryAddWithoutValidation(IdempotencyKeyHeader, $"cancel-{booking.Id}");

                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(integration.TimeoutSeconds));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                    using var response = await _httpClient.SendAsync(request, linkedCts.Token);
                    log.ResponseJson = Safe(await response.Content.ReadAsStringAsync(ct), secret, 8000);

                    log.Status = response.IsSuccessStatusCode ? IntegrationSyncStatus.Succeeded : IntegrationSyncStatus.Failed;
                    if (response.IsSuccessStatusCode)
                    {
                        integration.LastSuccessfulSyncAtUtc = DateTime.UtcNow;
                    }
                    else
                    {
                        log.ErrorMessage = DescribeFailureStatus(response);
                    }
                }
                catch (Exception ex)
                {
                    log.Status = IntegrationSyncStatus.Failed;
                    log.ErrorMessage = Safe(DescribeException(ex, integration.TimeoutSeconds), secret);
                    _logger.LogWarning(
                        "CancelBooking propagation failed for Booking {BookingId} via OperatorIntegration {IntegrationId}: {Reason}",
                        booking.Id, integration.Id, log.ErrorMessage);
                }
            }

            log.CompletedAtUtc = DateTime.UtcNow;
            _db.IntegrationSyncLogs.Add(log);
            await _db.SaveChangesAsync(ct);
        }

        // Backs the admin integration screen's "Test connection" button (Chunk 8 task 9,
        // Integrations.Manage-only — see OperatorIntegrationsController). Deliberately generic —
        // hits BaseUrl + "/health" rather than any one Purpose-specific endpoint, so it works
        // the same way regardless of which endpoints happen to be configured yet, and doesn't
        // require picking a real bookingId/tripId just to check reachability. apps/mock-erp
        // implements this route; a real operator ERP would need to add an equivalent one (noted
        // in the contract doc).
        public async Task<TestConnectionResult> TestConnectionAsync(OperatorIntegration integration, CancellationToken ct = default)
        {
            var log = new IntegrationSyncLog
            {
                OperatorIntegrationId = integration.Id,
                EntityName = "OperatorIntegration",
                EntityKey = integration.Id.ToString(),
                Operation = TestConnectionOperation,
                Status = IntegrationSyncStatus.Pending,
                StartedAtUtc = DateTime.UtcNow,
            };

            var sw = Stopwatch.StartNew();
            TestConnectionResult result;
            string? secret = null;

            try
            {
                var call = await PrepareCallAsync(integration, "/health", ct);
                secret = call.Secret;

                using var request = new HttpRequestMessage(HttpMethod.Get, call.RequestUri);
                ApplyAuth(request, integration, secret);

                var timeoutSeconds = Math.Min(10, integration.TimeoutSeconds);
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                using var response = await _httpClient.SendAsync(request, linkedCts.Token);
                log.ResponseJson = Safe(await response.Content.ReadAsStringAsync(ct), secret, 8000);
                sw.Stop();

                log.Status = response.IsSuccessStatusCode ? IntegrationSyncStatus.Succeeded : IntegrationSyncStatus.Failed;
                result = new TestConnectionResult
                {
                    Success = response.IsSuccessStatusCode,
                    Message = response.IsSuccessStatusCode
                        ? $"Reached {integration.BaseUrl} — {(int)response.StatusCode} {response.StatusCode}."
                        : ((int)response.StatusCode is >= 300 and < 400)
                            ? $"{integration.BaseUrl} answered with a redirect ({(int)response.StatusCode}); redirects are not followed."
                            : $"{integration.BaseUrl} responded {(int)response.StatusCode} {response.StatusCode}.",
                    StatusCode = (int)response.StatusCode,
                    DurationMs = sw.ElapsedMilliseconds,
                };

                if (response.IsSuccessStatusCode)
                {
                    integration.LastSuccessfulSyncAtUtc = DateTime.UtcNow;
                }
                else
                {
                    log.ErrorMessage = result.Message;
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                log.Status = IntegrationSyncStatus.Failed;
                var reason = Safe(DescribeException(ex, Math.Min(10, integration.TimeoutSeconds)), secret) ?? "The call failed.";
                log.ErrorMessage = reason;
                result = new TestConnectionResult { Success = false, Message = reason, DurationMs = sw.ElapsedMilliseconds };
            }

            log.CompletedAtUtc = DateTime.UtcNow;
            _db.IntegrationSyncLogs.Add(log);
            await _db.SaveChangesAsync(ct);

            return result;
        }

        // ---------------------------------------------------------------------------------
        // Call preparation, auth and safe logging (Chunk 6 / C6-3)
        // ---------------------------------------------------------------------------------

        private sealed record PreparedCall(Uri RequestUri, string? Secret);

        // Everything that must be true before this server sends a request on an administrator's
        // behalf, in one place so all four call types get it:
        //   1. BaseUrl passes the destination policy (https, no userinfo, and every address it
        //      resolves to is public — or local in Development). Re-checked on EVERY call, so a
        //      record that was fine when saved but whose DNS has since changed is still refused;
        //      ErpHttpHandlerFactory checks the address actually connected to as well.
        //   2. The endpoint path cannot change scheme/host/port (it is appended to BaseUrl).
        //   3. If the AuthType needs a secret, it resolves from Integrations:Secrets:NAME. A
        //      missing secret is an immediate, clear failure — nothing is sent un-authenticated.
        // BaseUrl is expected to be just the operator's origin plus an optional path prefix, with
        // PathTemplate supplying the rest — plain concatenation rather than Uri(base, relative),
        // which would silently drop BaseUrl's own path whenever the relative part starts with "/".
        private async Task<PreparedCall> PrepareCallAsync(OperatorIntegration integration, string path, CancellationToken ct)
        {
            var check = await ErpDestinationPolicy.ValidateBaseUrlAsync(
                integration.BaseUrl, _security.AllowLocalDestinations, _resolver, ct);

            if (!check.IsAllowed)
            {
                throw new IntegrationConfigurationException($"Destination refused: {check.Reason}");
            }

            var baseUrl = integration.BaseUrl.Trim();
            var baseUri = new Uri(baseUrl);
            var requestUri = new Uri(baseUrl.TrimEnd('/') + (path.StartsWith('/') ? path : "/" + path));

            if (!string.Equals(requestUri.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(requestUri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)
                || requestUri.Port != baseUri.Port)
            {
                throw new IntegrationConfigurationException(
                    "Destination refused: the endpoint path would change the request's host.");
            }

            string? secret = null;

            if (RequiresSecret(integration))
            {
                secret = IntegrationSecretReference.Resolve(_configuration, integration.SecretReference);

                if (secret is null)
                {
                    throw new IntegrationConfigurationException(
                        IntegrationSecretReference.TryGetName(integration.SecretReference, out var name)
                            ? $"The integration's secret is not configured (expected a value at '{IntegrationSecretReference.ConfigurationSection}:{name}')."
                            : "The integration's SecretReference is missing or not in the accepted 'env:NAME' form.");
                }
            }

            return new PreparedCall(requestUri, secret);
        }

        private static bool RequiresSecret(OperatorIntegration integration) => integration.AuthType switch
        {
            IntegrationAuthType.ApiKey => !string.IsNullOrWhiteSpace(integration.ApiKeyHeaderName),
            IntegrationAuthType.BearerToken => true,
            IntegrationAuthType.Basic => true,
            _ => false,
        };

        private static void ApplyAuth(HttpRequestMessage request, OperatorIntegration integration, string? secret)
        {
            if (secret is null)
            {
                return; // AuthType None / OAuth2 (not implemented) / ApiKey with no header name.
            }

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
            }
        }

        // Text that is about to be stored in IntegrationSyncLog (readable by administrators) or
        // logged: the secret scrubbed out of it (an operator can echo a key back in an error
        // body) and the length capped to what the column allows.
        private static string? Safe(string? text, string? secret, int maxLength = 1000)
        {
            var redacted = IntegrationSecretReference.Redact(text, secret);
            return redacted is not null && redacted.Length > maxLength ? redacted[..maxLength] : redacted;
        }

        private static string DescribeFailureStatus(HttpResponseMessage response) =>
            ((int)response.StatusCode is >= 300 and < 400)
                ? $"Operator API returned a redirect ({(int)response.StatusCode}); redirects are not followed."
                : $"Operator API returned {(int)response.StatusCode} {response.StatusCode}.";

        private static string DescribeException(Exception ex, int timeoutSeconds) =>
            ex is OperationCanceledException
                ? $"The operator API did not respond within {timeoutSeconds} second(s)."
                : ex.Message;

        private class ConfirmBookingResponse
        {
            public string? ExternalBookingKey { get; set; }
            public string? ExternalPnr { get; set; }
            public string? Status { get; set; }
        }

        private class SeatAvailabilityResponse
        {
            public string? TripId { get; set; }
            public List<string>? SoldSeatNumbers { get; set; }
        }

        private class CachedAvailability
        {
            public DateTime CachedAtUtc { get; init; }
            public ExternalAvailabilityResult Result { get; init; } = default!;
        }
    }

    // Result of CheckSeatAvailabilityAsync — public because SeatHoldsController consumes it
    // directly. Success=false means the check itself couldn't be completed (not configured,
    // refused destination, network/timeout/non-2xx), NOT that seats are unavailable. What a
    // failed check means for the customer is Integrations:AvailabilityFailureMode (see
    // ExternalAvailabilityPolicy); on a Success=true result the caller refuses exactly the seats
    // that are present in SoldSeatNumbers.
    public class ExternalAvailabilityResult
    {
        public bool Success { get; init; }
        public HashSet<string> SoldSeatNumbers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public string? ErrorMessage { get; init; }

        // Chunk 6: true when the check could not even be attempted because the operator has no
        // active integration / no GetSeatAvailability endpoint (as opposed to the call failing).
        public bool NotConfigured { get; init; }
    }

    // Result of TestConnectionAsync — consumed by OperatorIntegrationsController's
    // test-connection endpoint and rendered directly on the admin integration screen.
    public class TestConnectionResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = string.Empty;
        public int? StatusCode { get; init; }
        public long DurationMs { get; init; }
    }
}
