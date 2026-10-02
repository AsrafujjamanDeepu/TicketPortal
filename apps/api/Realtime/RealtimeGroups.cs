namespace TicketPortal.Api.Realtime
{
    // The ONE place SignalR group names and the hub URL are defined. The hub (who joins what)
    // and the notifier added in Chunk 2 (who gets what) must agree on these strings exactly,
    // so neither is allowed to build a group name by hand.
    public static class RealtimeGroups
    {
        // Hub endpoint. The Angular and React clients derive their URL from the API origin +
        // this path (the API base URL ends in /api, the hub does not live under it).
        public const string HubPath = "/hubs/realtime";

        // Only paths under this prefix may carry the JWT in the query string — see
        // Program.cs (OnMessageReceived) and RealtimeExtensions.UseRealtimeAuthGate.
        public const string HubPathPrefix = "/hubs";

        // Admins and platform staff (BusOperatorId == null): every change, every operator.
        public const string Platform = "platform";

        // Operator-scoped staff: shared reference data (terminals, routes, currencies, ...).
        public const string Staff = "staff";

        public static string Operator(Guid busOperatorId) => $"operator-{busOperatorId:D}";

        public static string Customer(Guid customerProfileId) => $"customer-{customerProfileId:D}";

        public static string Trip(Guid tripId) => $"trip-{tripId:D}";
    }
}
