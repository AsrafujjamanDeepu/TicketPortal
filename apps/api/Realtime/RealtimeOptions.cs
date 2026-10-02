namespace TicketPortal.Api.Realtime
{
    // Bound from the "Realtime" section of appsettings.json. Every value has a safe default so
    // the section can be missing entirely (older appsettings files, test overrides) and the
    // feature still behaves sensibly.
    public sealed class RealtimeOptions
    {
        public const string SectionName = "Realtime";

        // Kill switch (REALTIME_SIGNALR_PLAN, principle 5). false = the hub is not mapped at
        // all, so /hubs/realtime answers 404 and every screen simply behaves as it did before
        // SignalR existed. No redeploy needed: flip the setting (or the Realtime__Enabled
        // environment variable) and restart.
        public bool Enabled { get; set; } = true;

        // How many different trips ONE connection may be subscribed to at the same time. The
        // seat map is public (anonymous visitors may watch it), so without a cap a single
        // client could join every trip in the system.
        public int MaxTripsPerConnection { get; set; } = 20;

        // Largest message a client may send to the hub. The client only ever sends tiny
        // JoinTrip/LeaveTrip calls, so the framework default (32 KB) is already generous;
        // it is spelled out here so Chunk 7 has one obvious place to tighten it.
        public int MaxReceiveMessageSizeBytes { get; set; } = 32 * 1024;
    }
}
