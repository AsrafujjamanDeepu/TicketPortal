using Microsoft.Extensions.Configuration;

namespace TicketPortal.Api.Services
{
    // Chunk 6 / C6-2 — anti-hoarding limits for seat holds.
    //
    // A hold takes seats out of sale for a few minutes. Without a cap, one account (or one script
    // behind the per-IP "holds" rate limit) could keep many seats — or a whole bus — Held and
    // starve other customers. Two simple, configurable limits close that:
    //
    //   SeatHold:MaxSeatsPerHold        how many seats ONE hold may contain (default 6)
    //   SeatHold:MaxActiveHoldsPerUser  how many unexpired, Active holds ONE user may have at the
    //                                   same time, across all trips (default 3)
    //
    // The values are read from IConfiguration on every call (like SeatHold:Minutes already is) so
    // a test can override them with WebApplicationFactory.WithWebHostBuilder, and they are
    // validated once at startup (see Program.cs) so a bad value stops the API from starting
    // instead of silently disabling the protection.
    //
    // DemoDataSeeder deliberately does NOT pass these limits to SeatHoldService — it backfills
    // history for demo customers and is not a customer request.
    public sealed record SeatHoldLimits(int MaxSeatsPerHold, int MaxActiveHoldsPerUser)
    {
        public const string MaxSeatsPerHoldKey = "SeatHold:MaxSeatsPerHold";
        public const string MaxActiveHoldsPerUserKey = "SeatHold:MaxActiveHoldsPerUser";

        public const int DefaultMaxSeatsPerHold = 6;
        public const int DefaultMaxActiveHoldsPerUser = 3;

        // Sanity bounds on the settings themselves (a bus has at most a few dozen seats). The
        // active-holds ceiling is generous on purpose: the shared test host raises the cap high
        // enough that unrelated tests never trip it.
        public const int HighestAllowedMaxSeatsPerHold = 60;
        public const int HighestAllowedMaxActiveHoldsPerUser = 1000;

        public static SeatHoldLimits FromConfiguration(IConfiguration configuration)
        {
            var maxSeats = ReadInt(configuration, MaxSeatsPerHoldKey, DefaultMaxSeatsPerHold, HighestAllowedMaxSeatsPerHold);
            var maxHolds = ReadInt(configuration, MaxActiveHoldsPerUserKey, DefaultMaxActiveHoldsPerUser, HighestAllowedMaxActiveHoldsPerUser);
            return new SeatHoldLimits(maxSeats, maxHolds);
        }

        // The two rules that need no database: no repeated seat id, and no more seats than one hold
        // may contain. Returns null when the request is fine. Used by the controller (before it
        // spends an ERP call on the request) and again by SeatHoldService (the authoritative
        // check). `limits` null means "no seat-count cap" (DemoDataSeeder only).
        public static SeatHoldLimitException? CheckRequest(IReadOnlyCollection<Guid> tripSeatIds, SeatHoldLimits? limits)
        {
            if (tripSeatIds.Distinct().Count() != tripSeatIds.Count)
            {
                return new SeatHoldLimitException(
                    SeatHoldLimitKind.DuplicateSeats,
                    "The same seat was selected more than once. Please select each seat only once.");
            }

            if (limits is not null && tripSeatIds.Count > limits.MaxSeatsPerHold)
            {
                return new SeatHoldLimitException(
                    SeatHoldLimitKind.TooManySeats,
                    $"You can hold at most {limits.MaxSeatsPerHold} seat(s) in one booking. " +
                    $"Please select {limits.MaxSeatsPerHold} or fewer.");
            }

            return null;
        }

        private static int ReadInt(IConfiguration configuration, string key, int defaultValue, int highestAllowed)
        {
            var raw = configuration[key];
            if (string.IsNullOrWhiteSpace(raw))
            {
                return defaultValue;
            }

            if (!int.TryParse(raw, out var value) || value < 1 || value > highestAllowed)
            {
                throw new InvalidOperationException(
                    $"{key} must be a whole number between 1 and {highestAllowed} (found '{raw}').");
            }

            return value;
        }
    }

    public enum SeatHoldLimitKind
    {
        DuplicateSeats,
        TooManySeats,
        TooManyActiveHolds,
    }

    // A hold request refused by a hoarding/sanity rule. Thrown BEFORE any seat is touched, so a
    // refusal never leaves a partial hold behind. Separate from SeatsUnavailableException (a
    // race on a specific seat) because the right caller-facing status code differs: the first
    // two kinds are a bad request (400), the active-holds kind is a state conflict (409).
    public class SeatHoldLimitException : Exception
    {
        public SeatHoldLimitKind Kind { get; }

        public SeatHoldLimitException(SeatHoldLimitKind kind, string message) : base(message)
        {
            Kind = kind;
        }
    }
}
