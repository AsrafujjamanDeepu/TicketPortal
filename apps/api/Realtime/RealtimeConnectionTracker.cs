using System.Collections.Concurrent;

namespace TicketPortal.Api.Realtime
{
    // REALTIME_SIGNALR_PLAN.md, principle 7 ("no listeners, no work"). The hub registers every
    // connection here on connect and removes it on disconnect; the change-capture code asks
    // HasListeners before doing anything at all. Migrations, the demo seeder, the sweepers and
    // the test suite save through AppDbContext constantly, and with nobody connected none of
    // them pay anything for the realtime feature.
    //
    // Keyed by connection id (not a bare counter) so that a connect/disconnect pair that is
    // reported twice, or a disconnect that never arrives for a connection whose connect failed,
    // can never push the count negative or leave it stuck above zero.
    public sealed class RealtimeConnectionTracker
    {
        private readonly ConcurrentDictionary<string, byte> _connections = new(StringComparer.Ordinal);

        public bool HasListeners => !_connections.IsEmpty;

        public int Count => _connections.Count;

        public bool Contains(string connectionId) => _connections.ContainsKey(connectionId);

        public void Add(string connectionId) => _connections[connectionId] = 0;

        public void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);
    }
}
