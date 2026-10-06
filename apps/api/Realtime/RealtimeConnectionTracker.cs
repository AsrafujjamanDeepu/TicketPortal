using System.Collections.Concurrent;

namespace TicketPortal.Api.Realtime
{
    // docs/02-Project-Concept-and-Solution.md (Real-time updates), principle 7 ("no listeners, no work"). The hub registers every
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
        private sealed record Connection(string? UserId, string? SecurityStamp, Action? Abort);
        private readonly ConcurrentDictionary<string, Connection> _connections = new(StringComparer.Ordinal);

        public bool HasListeners => !_connections.IsEmpty;

        public int Count => _connections.Count;

        public bool Contains(string connectionId) => _connections.ContainsKey(connectionId);

        public void Add(string connectionId) => _connections[connectionId] = new Connection(null, null, null);

        public void Add(string connectionId, string? userId, string? securityStamp, Action abort) =>
            _connections[connectionId] = new Connection(userId, securityStamp, abort);

        public IReadOnlyList<(string ConnectionId, string UserId, string? SecurityStamp, Action Abort)> AuthenticatedConnections() =>
            _connections
                .Where(pair => pair.Value.UserId is not null && pair.Value.Abort is not null)
                .Select(pair => (
                    pair.Key,
                    pair.Value.UserId!,
                    pair.Value.SecurityStamp,
                    pair.Value.Abort!))
                .ToArray();

        public void Abort(string connectionId)
        {
            if (_connections.TryGetValue(connectionId, out var connection))
            {
                try { connection.Abort?.Invoke(); }
                catch (Exception) { Remove(connectionId); }
            }
        }

        public void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);
    }
}
