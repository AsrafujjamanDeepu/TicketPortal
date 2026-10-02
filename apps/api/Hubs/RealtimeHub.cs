using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TicketPortal.Api.Authorization;
using TicketPortal.Api.Data;
using TicketPortal.Api.Realtime;

namespace TicketPortal.Api.Hubs
{
    // Chunk 1 of REALTIME_SIGNALR_PLAN: an authenticated-when-possible SignalR endpoint.
    // NOTHING is pushed through it yet — Chunk 2 adds the change capture that sends the
    // "changes" messages; this chunk only decides, for every connection, WHICH groups it may
    // ever receive from.
    //
    // Deliberately no [Authorize]: the public seat map must work for anonymous visitors.
    // A request that sends a token which does NOT validate never reaches this class — the
    // auth gate in RealtimeExtensions answers 401 first — so "has a token" and "is
    // authenticated" can never silently disagree here.
    //
    // Which groups a connection joins is decided from ICurrentActorService (the same
    // DB-resolved actor every controller uses), never from claims in the token and never from
    // anything the client sends. A role change or a deactivated staff profile therefore takes
    // effect on the client's next (re)connect.
    public sealed class RealtimeHub(
        ICurrentActorService actors,
        AppDbContext db,
        IOptions<RealtimeOptions> options,
        RealtimeConnectionTracker connections,
        ILogger<RealtimeHub> logger) : Hub
    {
        private const string StateKey = "realtime.connection-state";

        // Hub instances are created per invocation, so anything that must survive between
        // calls on one connection lives in Context.Items (a per-connection dictionary).
        private sealed class ConnectionState
        {
            public string Actor { get; set; } = nameof(ActorType.Anonymous);
            public List<string> FixedGroups { get; } = [];
            public HashSet<Guid> Trips { get; } = [];
        }

        private ConnectionState State
        {
            get
            {
                if (Context.Items.TryGetValue(StateKey, out var existing) && existing is ConnectionState state)
                    return state;

                var created = new ConnectionState();
                Context.Items[StateKey] = created;
                return created;
            }
        }

        public override async Task OnConnectedAsync()
        {
            // Chunk 2: count this connection BEFORE anything else, so the change-capture code
            // knows somebody may be listening. Removed again in OnDisconnectedAsync — and here
            // if connecting itself fails, because SignalR does not promise a disconnect callback
            // for a connection whose OnConnectedAsync threw.
            connections.Add(Context.ConnectionId);

            try
            {
                await ConnectCoreAsync();
            }
            catch
            {
                connections.Remove(Context.ConnectionId);
                throw;
            }
        }

        private async Task ConnectCoreAsync()
        {
            var state = State;

            try
            {
                var actor = await actors.ResolveAsync(Context.User ?? new ClaimsPrincipal());
                state.Actor = actor.Type.ToString();

                foreach (var group in await ResolveFixedGroupsAsync(actor))
                {
                    await Groups.AddToGroupAsync(Context.ConnectionId, group);
                    state.FixedGroups.Add(group);
                }
            }
            catch (Exception ex)
            {
                // Best-effort (plan principle 4): a failed lookup must not take the whole
                // connection down. The connection simply keeps whatever groups it already
                // got — at worst the anonymous level — and the client's normal reconnect
                // retries the lookup.
                logger.LogWarning(ex,
                    "Realtime: could not resolve groups for connection {ConnectionId}; continuing with the groups already joined.",
                    Context.ConnectionId);
            }

            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            try
            {
                connections.Remove(Context.ConnectionId);
            }
            finally
            {
                await base.OnDisconnectedAsync(exception);
            }
        }

        // Anonymous visitors get no fixed groups at all; they can only JoinTrip.
        // Unprovisioned staff (a Staff login with no active StaffProfile) are denied everywhere
        // else in the API, so they get nothing here either.
        private async Task<IReadOnlyList<string>> ResolveFixedGroupsAsync(CurrentActor actor)
        {
            switch (actor.Type)
            {
                case ActorType.Admin:
                    return [RealtimeGroups.Platform];

                case ActorType.Staff when actor.BusOperatorId is null:
                    return [RealtimeGroups.Platform];

                case ActorType.Staff:
                    return [RealtimeGroups.Operator(actor.BusOperatorId!.Value), RealtimeGroups.Staff];

                case ActorType.Customer:
                    var customerProfileId = await db.CustomerProfiles
                        .AsNoTracking()
                        .Where(c => c.UserId == actor.UserId)
                        .Select(c => (Guid?)c.Id)
                        .FirstOrDefaultAsync();

                    // A customer login without a profile row yet simply has nothing of its own
                    // to be told about.
                    return customerProfileId is { } id ? [RealtimeGroups.Customer(id)] : [];

                default:
                    return [];
            }
        }

        // Subscribe this connection to one trip's seat-availability signal. Open to everyone,
        // including anonymous visitors — the seat map is public — which is exactly why the
        // per-connection cap below exists. A trip id is not checked against the database: the
        // only thing a trip group ever carries is "seats changed on this trip", and a made-up
        // id simply never receives anything.
        public async Task JoinTrip(Guid tripId)
        {
            if (tripId == Guid.Empty)
                throw new HubException("A trip id is required.");

            var state = State;
            var max = Math.Max(1, options.Value.MaxTripsPerConnection);

            lock (state.Trips)
            {
                if (!state.Trips.Contains(tripId) && state.Trips.Count >= max)
                    throw new HubException($"A connection can follow at most {max} trips at once.");

                state.Trips.Add(tripId);
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.Trip(tripId));
        }

        public async Task LeaveTrip(Guid tripId)
        {
            var state = State;

            bool removed;
            lock (state.Trips)
            {
                removed = state.Trips.Remove(tripId);
            }

            if (removed)
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, RealtimeGroups.Trip(tripId));
        }

        // Diagnostics: tells a connection what the SERVER decided it may listen to. Used by the
        // Chunk 1 tests and handy in the DevTools snippet ("am I really in the platform
        // group?"). It only ever reports the caller's own subscriptions.
        public Task<RealtimeSubscriptionInfo> GetMyGroups()
        {
            var state = State;

            string[] trips;
            lock (state.Trips)
            {
                trips = state.Trips.Select(RealtimeGroups.Trip).ToArray();
            }

            return Task.FromResult(new RealtimeSubscriptionInfo(
                state.Actor,
                state.FixedGroups.Concat(trips).ToArray()));
        }
    }

    public sealed record RealtimeSubscriptionInfo(string Actor, string[] Groups);
}
