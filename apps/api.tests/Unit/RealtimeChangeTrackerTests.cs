using TicketPortal.Api.Realtime;
using Xunit;

namespace TicketPortal.Api.Tests.Unit
{
    // REALTIME_SIGNALR_PLAN.md, Chunk 2, principle 3 ("commit-aware") as pure rules. The tracker
    // only needs an owner object (stands in for the DbContext) and a transaction Guid, so none of
    // this needs a database. The real transaction/SaveChanges wiring is covered end to end in
    // Integration/RealtimeChangeCaptureTests.
    public class RealtimeChangeTrackerTests
    {
        private static List<CapturedChange> Changes(int count = 1) =>
            Enumerable.Range(0, count)
                .Select(_ => new CapturedChange { Entity = "Bookings", Id = Guid.NewGuid() })
                .ToList();

        [Fact]
        public void SaveWithoutATransaction_IsReleasedImmediately()
        {
            var tracker = new RealtimeChangeTracker();
            var context = new object();
            var changes = Changes(2);

            tracker.Capture(context, null, changes);
            var released = tracker.Saved(context, null);

            Assert.Equal(2, released.Count);
        }

        [Fact]
        public void SaveInsideATransaction_IsHeldUntilCommit()
        {
            var tracker = new RealtimeChangeTracker();
            var context = new object();
            var transactionId = Guid.NewGuid();

            tracker.Capture(context, transactionId, Changes(3));
            Assert.Empty(tracker.Saved(context, transactionId));

            var released = tracker.Committed(context, transactionId);

            Assert.Equal(3, released.Count);
        }

        [Fact]
        public void SeveralSavesInOneTransaction_AreAllReleasedTogetherOnCommit()
        {
            var tracker = new RealtimeChangeTracker();
            var context = new object();
            var transactionId = Guid.NewGuid();

            tracker.Capture(context, transactionId, Changes(2));
            tracker.Saved(context, transactionId);
            tracker.Capture(context, transactionId, Changes(4));
            tracker.Saved(context, transactionId);

            Assert.Equal(6, tracker.Committed(context, transactionId).Count);
        }

        [Fact]
        public void RolledBackTransaction_ReleasesNothing()
        {
            var tracker = new RealtimeChangeTracker();
            var context = new object();
            var transactionId = Guid.NewGuid();

            tracker.Capture(context, transactionId, Changes(3));
            tracker.Saved(context, transactionId);
            tracker.RolledBack(context, transactionId);

            Assert.Empty(tracker.Committed(context, transactionId));
        }

        [Fact]
        public void CommittingTwice_ReleasesOnlyOnce()
        {
            var tracker = new RealtimeChangeTracker();
            var context = new object();
            var transactionId = Guid.NewGuid();

            tracker.Capture(context, transactionId, Changes());
            tracker.Saved(context, transactionId);

            Assert.Single(tracker.Committed(context, transactionId));
            Assert.Empty(tracker.Committed(context, transactionId));
        }

        [Fact]
        public void FailedSave_ReleasesNothing()
        {
            var tracker = new RealtimeChangeTracker();
            var context = new object();

            tracker.Capture(context, null, Changes(2));
            tracker.SaveFailed(context);

            Assert.Empty(tracker.Saved(context, null));
        }

        [Fact]
        public void FailedSaveInsideATransaction_DoesNotDiscardEarlierSavesThatLaterCommit()
        {
            var tracker = new RealtimeChangeTracker();
            var context = new object();
            var transactionId = Guid.NewGuid();

            tracker.Capture(context, transactionId, Changes(2));
            tracker.Saved(context, transactionId);

            tracker.Capture(context, transactionId, Changes(5));
            tracker.SaveFailed(context);

            Assert.Equal(2, tracker.Committed(context, transactionId).Count);
        }

        [Fact]
        public void TransactionThatEndedWithoutACommitEvent_IsDroppedByTheNextSave()
        {
            // A transaction disposed without Commit/Rollback raises no commit event. Its parked
            // rows must not leak into a later, unrelated save on the same context.
            var tracker = new RealtimeChangeTracker();
            var context = new object();
            var abandoned = Guid.NewGuid();

            tracker.Capture(context, abandoned, Changes(3));
            tracker.Saved(context, abandoned);

            tracker.Capture(context, null, Changes(1));
            var released = tracker.Saved(context, null);

            Assert.Single(released);
            Assert.Empty(tracker.Committed(context, abandoned));
        }

        [Fact]
        public void EfImplicitTransactionCommit_ReleasesNothingAndDoesNotLoseThePendingSave()
        {
            // EF wraps a plain SaveChanges in its own transaction and raises a commit event for it
            // BEFORE SavedChanges. Nothing is parked for that id, so the commit is a no-op and the
            // save is released by SavedChanges as normal.
            var tracker = new RealtimeChangeTracker();
            var context = new object();

            tracker.Capture(context, null, Changes(2));
            Assert.Empty(tracker.Committed(context, Guid.NewGuid()));

            Assert.Equal(2, tracker.Saved(context, null).Count);
        }

        [Fact]
        public void ContextsDoNotShareBuffers()
        {
            var tracker = new RealtimeChangeTracker();
            var first = new object();
            var second = new object();
            var transactionId = Guid.NewGuid();

            tracker.Capture(first, transactionId, Changes(2));
            tracker.Saved(first, transactionId);

            Assert.Empty(tracker.Committed(second, transactionId));
            Assert.Equal(2, tracker.Committed(first, transactionId).Count);
        }

        [Fact]
        public void EmptySave_ReleasesNothing()
        {
            var tracker = new RealtimeChangeTracker();
            var context = new object();

            tracker.Capture(context, null, new List<CapturedChange>());

            Assert.Empty(tracker.Saved(context, null));
        }

        // ---- Chunk 3: bulk-SQL changes handed in with Enqueue follow the same commit rule ----

        [Fact]
        public void BulkChangeWithoutATransaction_IsReleasedImmediately()
        {
            // The ExecuteUpdateAsync statement already committed on its own.
            var tracker = new RealtimeChangeTracker();
            var context = new object();

            var released = tracker.Enqueue(context, null, Changes(2));

            Assert.Equal(2, released.Count);
        }

        [Fact]
        public void BulkChangeInsideATransaction_IsHeldUntilCommit()
        {
            var tracker = new RealtimeChangeTracker();
            var context = new object();
            var transactionId = Guid.NewGuid();

            Assert.Empty(tracker.Enqueue(context, transactionId, Changes(3)));

            Assert.Equal(3, tracker.Committed(context, transactionId).Count);
        }

        [Fact]
        public void BulkChangeInsideATransaction_ThatRollsBack_ReleasesNothing()
        {
            var tracker = new RealtimeChangeTracker();
            var context = new object();
            var transactionId = Guid.NewGuid();

            tracker.Enqueue(context, transactionId, Changes(3));
            tracker.RolledBack(context, transactionId);

            Assert.Empty(tracker.Committed(context, transactionId));
        }

        [Fact]
        public void BulkChangeAndSavesInOneTransaction_AreReleasedTogetherOnCommit()
        {
            // The normal shape of a service method: a tracked save, then a bulk update, then commit.
            var tracker = new RealtimeChangeTracker();
            var context = new object();
            var transactionId = Guid.NewGuid();

            tracker.Capture(context, transactionId, Changes(2));
            tracker.Saved(context, transactionId);
            tracker.Enqueue(context, transactionId, Changes(3));
            tracker.Capture(context, transactionId, Changes(1));
            tracker.Saved(context, transactionId);

            Assert.Equal(6, tracker.Committed(context, transactionId).Count);
        }

        [Fact]
        public void BulkChangeFollowedByAFailedSave_IsStillReleasedOnCommit()
        {
            // A save that fails inside the transaction discards only ITS OWN pending rows; the bulk
            // change reported earlier is untouched (whether the transaction then commits is the
            // caller's decision, and the commit event is what releases it).
            var tracker = new RealtimeChangeTracker();
            var context = new object();
            var transactionId = Guid.NewGuid();

            tracker.Enqueue(context, transactionId, Changes(2));
            tracker.Capture(context, transactionId, Changes(5));
            tracker.SaveFailed(context);

            Assert.Equal(2, tracker.Committed(context, transactionId).Count);
        }

        [Fact]
        public void EnqueueingNothing_ReleasesNothing()
        {
            var tracker = new RealtimeChangeTracker();
            var context = new object();
            var transactionId = Guid.NewGuid();

            Assert.Empty(tracker.Enqueue(context, null, new List<CapturedChange>()));
            Assert.Empty(tracker.Enqueue(context, transactionId, new List<CapturedChange>()));
            Assert.Empty(tracker.Committed(context, transactionId));
        }

        [Fact]
        public void BulkChangeInATransactionThatEndedWithoutACommitEvent_IsDroppedByTheNextOne()
        {
            var tracker = new RealtimeChangeTracker();
            var context = new object();
            var abandoned = Guid.NewGuid();
            var current = Guid.NewGuid();

            tracker.Enqueue(context, abandoned, Changes(3));
            tracker.Enqueue(context, current, Changes(1));

            Assert.Empty(tracker.Committed(context, abandoned));
            Assert.Single(tracker.Committed(context, current));
        }

        [Fact]
        public void BulkChangesDoNotShareBuffersBetweenContexts()
        {
            var tracker = new RealtimeChangeTracker();
            var first = new object();
            var second = new object();
            var transactionId = Guid.NewGuid();

            tracker.Enqueue(first, transactionId, Changes(2));

            Assert.Empty(tracker.Committed(second, transactionId));
            Assert.Equal(2, tracker.Committed(first, transactionId).Count);
        }
    }
}
