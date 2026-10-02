namespace TicketPortal.Api.Realtime
{
    // REALTIME_SIGNALR_PLAN.md, Chunk 2 — the server -> client event contract.
    //
    // Signal, not data: a message only says "row X of table Y changed". Clients re-fetch
    // through the normal REST API, so each endpoint's own authorization stays the only gate
    // for who can read what.
    public static class RealtimeEvents
    {
        // The single SignalR client method every message is sent through. The argument is an
        // array of RealtimeChange.
        public const string ChangesMethod = "changes";

        // Pseudo-entity sent to trip-{id} groups (the public seat map) instead of any real
        // table name, so anonymous viewers never learn anything about bookings or customers.
        public const string SeatAvailability = "SeatAvailability";
    }

    public static class RealtimeActions
    {
        public const string Created = "created";
        public const string Updated = "updated";

        // Hard delete, or a soft delete (IsDeleted flipped to true).
        public const string Deleted = "deleted";

        // A very large save (seeders, trip generation, imports) collapsed to one message per
        // table and scope. Carries no row id; clients treat it as "re-fetch this table".
        public const string Bulk = "bulk";
    }

    // What goes over the wire (camelCase JSON via SignalR's default protocol):
    // { "entity": "Bookings", "action": "updated", "id": "...", "tripId": "...",
    //   "operatorId": "...", "atUtc": "2026-09-28T10:15:00Z" }
    // Deliberately NO customer id — customers are routed by group, never by payload.
    public sealed record RealtimeChange(
        string Entity,
        string Action,
        Guid? Id,
        Guid? TripId,
        Guid? OperatorId,
        DateTime AtUtc);

    // Parent tables a changed row can point at, used to work out who a row belongs to when it
    // does not carry BusOperatorId / TripId / CustomerProfileId itself (e.g. a Payment belongs
    // to whoever its Booking belongs to).
    public enum RealtimeRef
    {
        Booking,
        Payment,
        Trip,
        Bus,
        OperatorRoute,
        StaffProfile,
        OperatorInvoice,
        OperatorSettlement,
        SalesCounter,
        CancellationPolicy,
        SeatHold,
        TripSeat
    }

    // One row a save touched, captured BEFORE the commit and resolved to a scope AFTER it.
    // Mutable on purpose: RealtimeScopeResolver fills in whatever the row did not carry.
    public sealed class CapturedChange
    {
        // Table name (Bookings, Tickets, OperatorPayouts, ...).
        public string Entity { get; init; } = string.Empty;
        public string Action { get; set; } = RealtimeActions.Updated;

        // Null for composite-key rows (e.g. AspNetUserRoles) and collapsed bulk entries.
        public Guid? Id { get; init; }

        public Guid? OperatorId { get; set; }
        public Guid? TripId { get; set; }

        // Internal routing only — never serialized (see RealtimeChange).
        public Guid? CustomerProfileId { get; set; }

        // Foreign keys to parent tables, used only to fill in the three scope values above.
        public Dictionary<RealtimeRef, Guid>? Refs { get; set; }

        // True when the owner could not be worked out (the lookup failed, or the parent row no
        // longer exists). Such a change is never sent to the shared "staff" group — "no operator
        // known" must not be mistaken for "belongs to no operator" (principle 8, fail closed).
        public bool ScopeUncertain { get; set; }

        public DateTime AtUtc { get; init; } = DateTime.UtcNow;
    }
}
