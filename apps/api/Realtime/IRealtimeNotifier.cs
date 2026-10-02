namespace TicketPortal.Api.Realtime
{
    // REALTIME_SIGNALR_PLAN.md, Chunk 2 — how the rest of the API announces changes.
    //
    // Both members are best-effort by contract: they never throw, never block the caller on the
    // network, and do nothing when Realtime:Enabled is false or the host has not finished
    // starting. A failure to announce must never change the outcome of a booking or a payment.
    //
    // Chunk 3 adds EntityChangedAsync for bulk-SQL (ExecuteUpdateAsync) paths that EF's change
    // tracker cannot see.
    public interface IRealtimeNotifier
    {
        // Called by the EF interceptors with rows that are now COMMITTED. Returns immediately;
        // scope resolution and delivery run in the background.
        void Publish(IReadOnlyList<CapturedChange> changes);

        // "The seat map of these trips changed." Sends the anonymous-safe SeatAvailability
        // pseudo-entity to each trip's group. Used by seat paths that bypass EF's change
        // tracker (Chunk 3).
        Task SeatsChangedAsync(IEnumerable<Guid> tripIds);
    }
}
