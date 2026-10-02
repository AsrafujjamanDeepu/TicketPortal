using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using TicketPortal.Api.Data;
using TicketPortal.Api.Hubs;

namespace TicketPortal.Api.Realtime
{
    // REALTIME_SIGNALR_PLAN.md, Chunk 2 — resolves who each committed change belongs to and sends
    // it to the matching SignalR groups.
    //
    // Registered as a singleton. It owns no DbContext: scope lookups use a short-lived DI scope of
    // their own, so they can never interfere with the request (or transaction) that caused the
    // change. Everything runs on the thread pool and is wrapped in try/catch — a failure here is
    // logged and swallowed, never surfaced to the caller (principle 4: never break a write).
    public sealed class RealtimeNotifier(
        IHubContext<RealtimeHub> hub,
        IServiceScopeFactory scopeFactory,
        IHostApplicationLifetime lifetime,
        IOptions<RealtimeOptions> options,
        RealtimeConnectionTracker connections,
        ILogger<RealtimeNotifier> logger) : IRealtimeNotifier
    {
        // Off when the kill switch is set, off until the host has fully started, and off while
        // nobody is connected. The second rule matters at startup: the migrator and both seeders
        // write thousands of rows before any client can possibly be connected. The third is
        // principle 7 ("no listeners, no work"): without a connected client there is nobody to
        // tell, so not even the scope lookups run.
        private bool IsLive =>
            options.Value.Enabled
            && connections.HasListeners
            && lifetime.ApplicationStarted.IsCancellationRequested
            && !lifetime.ApplicationStopping.IsCancellationRequested;

        public void Publish(IReadOnlyList<CapturedChange> changes)
        {
            try
            {
                if (changes.Count == 0 || !IsLive)
                    return;

                _ = Task.Run(() => DispatchAsync(changes));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Realtime: could not queue {Count} change(s); continuing.", changes.Count);
            }
        }

        public Task SeatsChangedAsync(IEnumerable<Guid> tripIds)
        {
            try
            {
                if (!IsLive)
                    return Task.CompletedTask;

                var ids = tripIds
                    .Where(id => id != Guid.Empty)
                    .Distinct()
                    .Take(RealtimeRouter.MaxSeatTripsPerBatch)
                    .ToList();

                if (ids.Count == 0)
                    return Task.CompletedTask;

                var at = DateTime.UtcNow;
                _ = Task.Run(async () =>
                {
                    foreach (var tripId in ids)
                    {
                        var message = new RealtimeChange(
                            RealtimeEvents.SeatAvailability, RealtimeActions.Updated, null, tripId, null, at);
                        await SendAsync(RealtimeGroups.Trip(tripId), new List<RealtimeChange> { message });
                    }
                });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Realtime: could not queue seat-availability signal; continuing.");
            }

            return Task.CompletedTask;
        }

        private async Task DispatchAsync(IReadOnlyList<CapturedChange> changes)
        {
            try
            {
                await ResolveScopeAsync(changes);

                foreach (var (group, messages) in RealtimeRouter.Route(changes))
                    await SendAsync(group, messages);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Realtime: dispatch of {Count} change(s) failed; continuing.", changes.Count);
            }
        }

        // If the lookups fail (database briefly unreachable, ...) the batch is still routed with
        // whatever scope is known. Anything whose owner could not be worked out has no operator
        // or customer, so the router sends it to the platform group only (principle 8, fail
        // closed): admin screens still update, and operator/customer screens catch up on their
        // next refresh or reconnect resync.
        private async Task ResolveScopeAsync(IReadOnlyList<CapturedChange> changes)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await RealtimeScopeResolver.ResolveAsync(db, changes);
            }
            catch (Exception ex)
            {
                // Whatever the resolver managed to fill in before failing is still correct, but
                // for everything else "no operator found" means "could not find out", not "owned
                // by nobody" — so no change in this batch may reach the shared staff group.
                foreach (var change in changes)
                    change.ScopeUncertain = true;

                logger.LogWarning(ex, "Realtime: scope lookup failed; routing with partial scope.");
            }
        }

        private async Task SendAsync(string group, List<RealtimeChange> messages)
        {
            try
            {
                // SendCoreAsync with an explicit args array, so the whole batch reaches the client
                // as ONE argument (an array) rather than being spread across parameters.
                await hub.Clients.Group(group).SendCoreAsync(
                    RealtimeEvents.ChangesMethod,
                    new object?[] { messages.ToArray() });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Realtime: send to group {Group} failed; continuing.", group);
            }
        }
    }
}
