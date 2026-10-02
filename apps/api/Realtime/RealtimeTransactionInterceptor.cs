using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace TicketPortal.Api.Realtime
{
    // REALTIME_SIGNALR_PLAN.md, Chunk 2 — the commit-aware half (principle 3).
    //
    // Inside an explicit transaction (db.Database.BeginTransactionAsync — used all over the
    // booking, payment, wallet and settlement services) RealtimeSaveChangesInterceptor parks what
    // each SaveChanges wrote. This interceptor decides its fate:
    //
    //   committed          -> announce everything parked for that transaction
    //   rolled back/failed -> throw it away; clients are never told about a change that did not happen
    //
    // Without this, a client could re-fetch on a message and read pre-commit (stale) data, or be
    // told about a booking that was rolled back a moment later.
    //
    // Every callback is wrapped: an exception here must never fail a commit or rollback.
    public sealed class RealtimeTransactionInterceptor(
        RealtimeChangeTracker tracker,
        IRealtimeNotifier notifier,
        ILogger<RealtimeTransactionInterceptor> logger) : DbTransactionInterceptor
    {
        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
            Committed(eventData.Context, eventData.TransactionId);

        public override Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Committed(eventData.Context, eventData.TransactionId);
            return Task.CompletedTask;
        }

        public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) =>
            Discarded(eventData.Context, eventData.TransactionId);

        public override Task TransactionRolledBackAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Discarded(eventData.Context, eventData.TransactionId);
            return Task.CompletedTask;
        }

        public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) =>
            Discarded(eventData.Context, eventData.TransactionId);

        public override Task TransactionFailedAsync(
            DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            Discarded(eventData.Context, eventData.TransactionId);
            return Task.CompletedTask;
        }

        private void Committed(DbContext? context, Guid transactionId)
        {
            if (context is null)
                return;

            try
            {
                var released = tracker.Committed(context, transactionId);
                if (released.Count > 0)
                    notifier.Publish(released);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Realtime: could not announce a committed transaction.");
            }
        }

        private void Discarded(DbContext? context, Guid transactionId)
        {
            if (context is null)
                return;

            try
            {
                tracker.RolledBack(context, transactionId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Realtime: could not discard a rolled-back transaction's pending changes.");
            }
        }
    }
}
