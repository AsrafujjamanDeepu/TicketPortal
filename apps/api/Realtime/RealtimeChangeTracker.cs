using System.Runtime.CompilerServices;

namespace TicketPortal.Api.Realtime
{
    // REALTIME_SIGNALR_PLAN.md, Chunk 2 — remembers, per DbContext, the rows a save touched, and
    // decides WHEN they may be announced (principle 3: commit-aware).
    //
    //   no explicit transaction   -> released as soon as SaveChanges succeeds (that save already
    //                                committed its own implicit transaction)
    //   inside a transaction      -> parked until that transaction commits; dropped if it rolls
    //                                back or fails
    //
    // A client that re-fetches on a "changed" message must never read pre-commit data, and must
    // never be told about a change that was rolled back.
    //
    // Deliberately free of any EF dependency: the owner is just an object (the DbContext) and the
    // transaction is just a Guid, which keeps the rules unit-testable without a database.
    public sealed class RealtimeChangeTracker
    {
        private static readonly IReadOnlyList<CapturedChange> None = Array.Empty<CapturedChange>();

        // Keyed weakly, so a DbContext that is simply dropped (request ended, transaction never
        // committed) takes its buffer with it — nothing can leak into a later, unrelated save.
        private readonly ConditionalWeakTable<object, ContextBuffer> _buffers = new();

        private sealed class ContextBuffer
        {
            public readonly object Gate = new();

            // Captured in SavingChanges, resolved in SavedChanges / SaveChangesFailed.
            public List<CapturedChange>? Pending;

            // Saved inside a transaction, waiting for that transaction's fate.
            public Dictionary<Guid, List<CapturedChange>>? AwaitingCommit;
        }

        // SavingChanges: remember what this save is about to write.
        public void Capture(object owner, Guid? currentTransactionId, IReadOnlyList<CapturedChange> changes)
        {
            var buffer = _buffers.GetValue(owner, _ => new ContextBuffer());
            lock (buffer.Gate)
            {
                // Anything still parked for a transaction other than the one running now belongs to
                // a transaction that ended without a commit event (disposed without Commit). Those
                // rows never landed — drop them.
                if (buffer.AwaitingCommit is { Count: > 0 })
                {
                    foreach (var stale in buffer.AwaitingCommit.Keys.Where(key => key != currentTransactionId).ToList())
                        buffer.AwaitingCommit.Remove(stale);
                }

                buffer.Pending = changes.Count == 0 ? null : changes.ToList();
            }
        }

        // SavedChanges: returns the rows that may be announced right now (empty when they were
        // parked for a still-open transaction).
        public IReadOnlyList<CapturedChange> Saved(object owner, Guid? currentTransactionId)
        {
            if (!_buffers.TryGetValue(owner, out var buffer))
                return None;

            lock (buffer.Gate)
            {
                var pending = buffer.Pending;
                buffer.Pending = null;

                if (pending is null || pending.Count == 0)
                    return None;

                if (currentTransactionId is not { } transactionId)
                    return pending;

                buffer.AwaitingCommit ??= new Dictionary<Guid, List<CapturedChange>>();
                if (!buffer.AwaitingCommit.TryGetValue(transactionId, out var parked))
                {
                    parked = new List<CapturedChange>();
                    buffer.AwaitingCommit[transactionId] = parked;
                }

                parked.AddRange(pending);
                return None;
            }
        }

        // SaveChangesFailed / SaveChangesCanceled: nothing was written.
        public void SaveFailed(object owner)
        {
            if (!_buffers.TryGetValue(owner, out var buffer))
                return;

            lock (buffer.Gate)
                buffer.Pending = null;
        }

        // TransactionCommitted: returns everything that was parked for that transaction. Empty for
        // EF's own implicit per-save transactions (nothing is ever parked for those).
        public IReadOnlyList<CapturedChange> Committed(object owner, Guid transactionId)
        {
            if (!_buffers.TryGetValue(owner, out var buffer))
                return None;

            lock (buffer.Gate)
            {
                if (buffer.AwaitingCommit is not null
                    && buffer.AwaitingCommit.Remove(transactionId, out var parked))
                {
                    return parked;
                }

                return None;
            }
        }

        // TransactionRolledBack / TransactionFailed: the parked rows never happened.
        public void RolledBack(object owner, Guid transactionId)
        {
            if (!_buffers.TryGetValue(owner, out var buffer))
                return;

            lock (buffer.Gate)
                buffer.AwaitingCommit?.Remove(transactionId);
        }
    }
}
