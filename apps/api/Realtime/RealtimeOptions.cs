namespace TicketPortal.Api.Realtime
{
    // Bound from the "Realtime" section of appsettings.json. Every value has a safe default so
    // the section can be missing entirely (older appsettings files, test overrides) and the
    // feature still behaves sensibly.
    public sealed class RealtimeOptions
    {
        public const string SectionName = "Realtime";

        // Kill switch (docs/02-Project-Concept-and-Solution.md (Real-time updates), principle 5). false = the hub is not mapped at
        // all, so /hubs/realtime answers 404 and every screen simply behaves as it did before
        // SignalR existed. No redeploy needed: flip the setting (or the Realtime__Enabled
        // environment variable) and restart.
        public bool Enabled { get; set; } = true;

        // How many different trips ONE connection may be subscribed to at the same time. The
        // seat map is public (anonymous visitors may watch it), so without a cap a single
        // client could join every trip in the system.
        public int MaxTripsPerConnection { get; set; } = 20;

        // Largest message a client may send to the hub (SignalR closes the connection when a
        // bigger one arrives). The client only ever sends JoinTrip/LeaveTrip with one GUID,
        // well under 200 bytes, so Chunk 7 tightens the framework default (32 KB) to 4 KB.
        public int MaxReceiveMessageSizeBytes { get; set; } = 4 * 1024;

        // Chunk 7 abuse limit: how many hub calls ONE connection may make per second (sliding
        // window, enforced by RealtimeRateLimitFilter). A seat map makes one call per trip it
        // opens and a reconnect re-joins at most MaxTripsPerConnection trips, so this leaves
        // plenty of headroom for real use and none for a flood.
        public int MaxInvocationsPerSecond { get; set; } = 60;
    }
}
