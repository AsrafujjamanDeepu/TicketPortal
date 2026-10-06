using Microsoft.EntityFrameworkCore;

namespace TicketPortal.Api.Realtime
{
    // docs/02-Project-Concept-and-Solution.md (Real-time updates), Chunk 2 — how the rest of the API announces changes.
    //
    // Every method is best-effort by contract: it never throws, never blocks the caller on the
    // network, and does nothing when Realtime:Enabled is false or the host has not finished
    // starting. A failure to announce must never change the outcome of a booking or a payment.
    public interface IRealtimeNotifier
    {
        // True when a signal sent right now could reach somebody (feature on, host started, at
        // least one client connected). Services use it to skip the small extra queries they need
        // to describe a bulk change when nobody is listening (principle 7, "no listeners, no
        // work"). Never required for correctness: every method below re-checks it itself.
        bool IsLive { get; }

        // Called by the EF interceptors with rows that are now COMMITTED. Returns immediately;
        // scope resolution and delivery run in the background.
        void Publish(IReadOnlyList<CapturedChange> changes);

        // "The seat map of these trips changed." Sends the anonymous-safe SeatAvailability
        // pseudo-entity to each trip's group RIGHT NOW. Not commit-aware — for a seat change made
        // by bulk SQL inside a transaction use EntityChangedAsync with RealtimeBulkChanges.Seats
        // instead, which waits for the commit.
        Task SeatsChangedAsync(IEnumerable<Guid> tripIds);

        // Chunk 3 — announces changes made by bulk SQL (ExecuteUpdateAsync), which EF's change
        // tracker never sees and which therefore never reach the SaveChanges interceptor.
        //
        // Commit-aware, exactly like a normal save (principle 3): when `owner` has an open
        // transaction the changes are parked and announced when that transaction commits, or
        // dropped if it rolls back; with no open transaction (the bulk statement already
        // committed on its own) they are announced immediately. So a caller simply reports what it
        // changed right after the ExecuteUpdateAsync call — it does not need to know who owns the
        // transaction, or wait for the commit itself.
        //
        // The changes carry the scope they belong to (OperatorId, TripId, CustomerProfileId), so
        // routing needs no extra lookup; see RealtimeBulkChanges for the standard shapes.
        Task EntityChangedAsync(DbContext owner, IEnumerable<CapturedChange> changes);
    }

    // The notifier a service falls back to when it is built by hand with no DI — DemoDataSeeder and
    // the service-level tests write `new FinanceLedgerService(db)`. Announces nothing, which is
    // exactly right there: seeding and arranging test data must not be broadcast.
    public sealed class NullRealtimeNotifier : IRealtimeNotifier
    {
        public static readonly NullRealtimeNotifier Instance = new();

        private NullRealtimeNotifier() { }

        public bool IsLive => false;

        public void Publish(IReadOnlyList<CapturedChange> changes) { }

        public Task SeatsChangedAsync(IEnumerable<Guid> tripIds) => Task.CompletedTask;

        public Task EntityChangedAsync(DbContext owner, IEnumerable<CapturedChange> changes) => Task.CompletedTask;
    }
}
