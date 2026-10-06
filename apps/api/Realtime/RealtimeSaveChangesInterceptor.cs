using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace TicketPortal.Api.Realtime
{
    // docs/02-Project-Concept-and-Solution.md (Real-time updates), Chunk 2 — the single capture point for changes EF Core writes
    // (principle 2). Registered once on AppDbContext, so all ~80 tables, every controller, every
    // service and every background sweeper are covered without a broadcast call in any of them.
    //
    //   SavingChanges  -> read the change tracker, remember what is about to be written
    //   SavedChanges   -> announce it (or park it if a transaction is still open)
    //   SaveChanges failed/cancelled -> forget it
    //
    // Two classes rather than one because a class cannot inherit both SaveChangesInterceptor and
    // DbTransactionInterceptor; the transaction half is RealtimeTransactionInterceptor.
    //
    // Every callback is wrapped: an exception here must never fail (or change the result of) the
    // save. With Realtime:Enabled=false it is not even attached to the DbContext (see
    // RealtimeExtensions.AddRealtimeInterceptors), and while nobody is connected it returns
    // before touching the change tracker (principle 7, "no listeners, no work").
    public sealed class RealtimeSaveChangesInterceptor(
        RealtimeChangeTracker tracker,
        IRealtimeNotifier notifier,
        RealtimeConnectionTracker connections,
        ILogger<RealtimeSaveChangesInterceptor> logger) : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData, InterceptionResult<int> result)
        {
            Capture(eventData.Context);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Capture(eventData.Context);
            return ValueTask.FromResult(result);
        }

        public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
        {
            Release(eventData.Context);
            return result;
        }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            Release(eventData.Context);
            return ValueTask.FromResult(result);
        }

        public override void SaveChangesFailed(DbContextErrorEventData eventData) =>
            Forget(eventData.Context);

        public override Task SaveChangesFailedAsync(
            DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            Forget(eventData.Context);
            return Task.CompletedTask;
        }

        public override void SaveChangesCanceled(DbContextEventData eventData) =>
            Forget(eventData.Context);

        public override Task SaveChangesCanceledAsync(
            DbContextEventData eventData, CancellationToken cancellationToken = default)
        {
            Forget(eventData.Context);
            return Task.CompletedTask;
        }

        private void Capture(DbContext? context)
        {
            if (context is null || !connections.HasListeners)
                return;

            try
            {
                var captured = RealtimeChangeCapture.FromEntries(context.ChangeTracker.Entries().ToList());
                tracker.Capture(context, context.Database.CurrentTransaction?.TransactionId, captured);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Realtime: could not capture pending changes; this save will not be announced.");
            }
        }

        private void Release(DbContext? context)
        {
            if (context is null)
                return;

            try
            {
                var released = tracker.Saved(context, context.Database.CurrentTransaction?.TransactionId);
                if (released.Count > 0)
                    notifier.Publish(released);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Realtime: could not announce a completed save.");
            }
        }

        private void Forget(DbContext? context)
        {
            if (context is null)
                return;

            try
            {
                tracker.SaveFailed(context);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Realtime: could not discard a failed save's pending changes.");
            }
        }
    }
}
