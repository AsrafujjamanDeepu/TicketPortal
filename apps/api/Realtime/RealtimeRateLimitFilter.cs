using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace TicketPortal.Api.Realtime
{
    // REALTIME_SIGNALR_PLAN.md, Chunk 7 — abuse limit: a sanity cap on hub calls per second for
    // ONE connection (sliding one-second window). Clients only ever call JoinTrip / LeaveTrip,
    // so a connection hammering the hub is a bug or an attack, not a user; it gets a
    // HubException on the excess calls (the connection itself stays up, and the trip cap in
    // RealtimeHub still bounds what it can subscribe to).
    //
    // State lives in HubCallerContext.Items, i.e. per connection. SignalR runs one hub call at
    // a time per connection by default (MaximumParallelInvocationsPerClient = 1), so the queue
    // needs no locking.
    public sealed class RealtimeRateLimitFilter(
        IOptions<RealtimeOptions> options,
        ILogger<RealtimeRateLimitFilter> logger) : IHubFilter
    {
        private const string WindowKey = "realtime.invocationWindow";
        private const long WindowMs = 1000;

        public async ValueTask<object?> InvokeMethodAsync(
            HubInvocationContext invocationContext,
            Func<HubInvocationContext, ValueTask<object?>> next)
        {
            var limit = Math.Max(1, options.Value.MaxInvocationsPerSecond);
            var items = invocationContext.Context.Items;

            Queue<long> window;
            if (items.TryGetValue(WindowKey, out var existing) && existing is Queue<long> known)
            {
                window = known;
            }
            else
            {
                window = new Queue<long>();
                items[WindowKey] = window;
            }

            var now = Environment.TickCount64;
            while (window.Count > 0 && now - window.Peek() >= WindowMs)
                window.Dequeue();

            if (window.Count >= limit)
            {
                // Debug, not Warning: the client controls how often this fires, and it must not
                // be able to flood the log.
                logger.LogDebug(
                    "Connection {ConnectionId} exceeded {Limit} hub calls per second; call {Method} refused.",
                    invocationContext.Context.ConnectionId, limit, invocationContext.HubMethodName);
                throw new HubException("Too many requests. Slow down.");
            }

            window.Enqueue(now);
            return await next(invocationContext);
        }
    }
}
