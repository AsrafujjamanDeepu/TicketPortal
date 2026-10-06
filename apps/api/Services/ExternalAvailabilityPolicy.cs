using Microsoft.Extensions.Configuration;

namespace TicketPortal.Api.Services
{
    // Chunk 6 / C6-4 — decision D7: what should a customer experience when the operator's own
    // system (the source of truth for an ExternalApiManaged trip) cannot be asked whether a seat
    // is still free?
    //
    //   Closed  (DEFAULT)  Refuse the hold with a clear "try again shortly" message (HTTP 503).
    //                      We genuinely do not know whether the seat has been sold, and a hold
    //                      that turns into a payment would later be rejected by the operator and
    //                      refunded — a bad experience, and a double-sale risk if their system
    //                      never rejects. Protects the customer and the operator.
    //   Open               Let the hold through, trusting our own seat map (the old behaviour).
    //                      Keeps selling during an ERP outage, accepting that some of those sales
    //                      may be rejected later. Choose this only for an operator whose ERP is
    //                      known to be flaky and whose rejections are cheap to handle.
    //
    // The setting is GLOBAL (Integrations:AvailabilityFailureMode), not per integration: a
    // per-integration column would need an EF migration, and the choice is really a platform
    // policy. The mode that is in force is shown on the admin Integrations page.
    //
    // "Failed" covers every way the check can fail: no active integration or GetSeatAvailability
    // endpoint, a refused destination, a missing secret, a redirect, a non-2xx reply, a timeout.
    public enum AvailabilityFailureMode
    {
        Closed,
        Open,
    }

    public static class ExternalAvailabilityPolicy
    {
        public const string FailureModeKey = "Integrations:AvailabilityFailureMode";
        public const string CacheSecondsKey = "Integrations:AvailabilityCacheSeconds";

        public const int DefaultCacheSeconds = 30;
        public const int HighestAllowedCacheSeconds = 300;

        // Read on every call (like SeatHold:Minutes) so tests can override it; validated at
        // startup by Startup/StartupSettingsValidator.cs so a typo stops the API from starting instead of silently
        // picking a mode.
        public static AvailabilityFailureMode GetFailureMode(IConfiguration configuration)
        {
            var raw = configuration[FailureModeKey];

            if (string.IsNullOrWhiteSpace(raw))
            {
                return AvailabilityFailureMode.Closed;
            }

            return raw.Trim().ToLowerInvariant() switch
            {
                "closed" => AvailabilityFailureMode.Closed,
                "open" => AvailabilityFailureMode.Open,
                _ => throw new InvalidOperationException(
                    $"{FailureModeKey} must be 'Closed' or 'Open' (found '{raw}')."),
            };
        }

        // How long a SUCCESSFUL availability answer is reused. 0 disables the cache.
        public static TimeSpan GetCacheTtl(IConfiguration configuration)
        {
            var raw = configuration[CacheSecondsKey];

            if (string.IsNullOrWhiteSpace(raw))
            {
                return TimeSpan.FromSeconds(DefaultCacheSeconds);
            }

            if (!int.TryParse(raw, out var seconds) || seconds < 0 || seconds > HighestAllowedCacheSeconds)
            {
                throw new InvalidOperationException(
                    $"{CacheSecondsKey} must be a whole number between 0 and {HighestAllowedCacheSeconds} (found '{raw}').");
            }

            return TimeSpan.FromSeconds(seconds);
        }

        public const string ClosedCustomerMessage =
            "We couldn't confirm seat availability with the operator's booking system right now, " +
            "so seats on this trip can't be held. Please try again in a few minutes.";
    }
}
