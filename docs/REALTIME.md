# Real-time updates (SignalR)

How TicketPortal pushes changes to open screens, how to operate it, and how to demo and defend
it. The full plan and rationale is `REALTIME_SIGNALR_PLAN.md`; this file is the operator's and
examiner's reference.

## Implementation status

All seven chunks of `REALTIME_SIGNALR_PLAN.md` are delivered.

| Chunk | What |
|---|---|
| 1 | Hub at `/hubs/realtime`, JWT auth, groups, `JoinTrip` cap, kill switch |
| 2 | Automatic change capture (EF `SaveChanges` + transaction interceptors, scope map, router) |
| 3 | Bulk-SQL changes announced (`SeatHoldService`, wallets) - `ExecuteUpdateAsync` is invisible to EF's change tracker |
| 4 | Angular realtime core (`RealtimeService`, `liveRefresh()`, badge) and the live customer experience |
| 5 | Angular staff, operator and finance panels |
| 6 | React admin console: push instead of polling, connection badge |
| 7 | Hardening: abuse limits, tests, this document |

## How it works

```
 any committed write ──► server signals "Bookings row X changed" ──► SignalR groups ──► browsers
                                                                                         │
                                            browser re-fetches through the normal REST API ◄┘
```

- **Signal, not data.** The socket carries only `{ entity, action, id, tripId?, operatorId? }`.
  Screens re-fetch through the ordinary REST endpoints, so the existing authorization on each
  endpoint remains the *only* thing deciding who can read what. Nothing sensitive is on the wire.
- **Groups decide who hears what**, and they are assigned on the server from the database
  (`ICurrentActorService`), never from anything the client sends:

| Connection | Groups | Receives |
|---|---|---|
| Anonymous | none (may `JoinTrip`) | `SeatAvailability` for trips joined |
| Customer | `customer-{profileId}` | changes to their own records, for tables the catalog marks Customer |
| Operator staff | `operator-{id}`, `staff` | their operator's changes (tables marked Operator) + shared reference data (marked Shared) |
| Platform staff, Admin | `platform` | everything |
| Unprovisioned staff | none | nothing |

Which table is announced to whom is decided by `RealtimeEntityCatalog` (platform-only is the
default for anything unlisted). The routing itself is `RealtimeRouter`.

## Configuration (`Realtime` section, `appsettings.json`)

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Kill switch. `false` = the hub is not mapped (`/hubs/realtime` -> 404). Needs a host restart. |
| `MaxTripsPerConnection` | `20` | Seat maps one connection may follow at once. |
| `MaxReceiveMessageSizeBytes` | `4096` | Largest message a client may send (SignalR `MaximumReceiveMessageSize`). Clients only send a GUID, so anything bigger closes the connection. |
| `MaxInvocationsPerSecond` | `60` | Hub calls per second per connection; excess calls get a `HubException`, the connection stays up. |

Environment override example: `Realtime__Enabled=false`.

## Client behaviour

### Angular apps (`apps/frontend`, Chunks 4-5)

`core/realtime/realtime.service.ts` owns one lazy connection (opened by the first screen that asks
for live data); screens use `liveRefresh()` / `watch()` / `watchSeats()`. It restarts on
login/logout so the server re-evaluates the groups, re-joins seat-map groups after a reconnect,
and emits one resync after any outage. A 404 (kill switch) or 401 (rejected token) stops the
retry loop; anything else retries with back-off. `tp-realtime-badge` shows the state.

### React admin console (Chunk 6)

Code: `apps/admin/src/console/lib/realtime.ts`, `hooks/useRealtime.ts`,
`components/layout/ConnectionBadge.tsx`.

- **One shared connection**, reference-counted, opened with the JWT from the stored admin session
  (`tp_admin_auth`) and `withCredentials: false`.
- **Badge** in the top bar: *Live* / *Reconnecting* / *Offline*.
- **Debounce** (300 ms; 1 s on the Dashboard) so a burst of writes causes one re-fetch. A very
  large save arrives as one `bulk` message per table, which the client treats like any change.
- **Resync after every reconnect**, so anything missed during an outage is re-fetched.
- `GenericCrudPage` subscribes to its resource key (which equals the EF table name), so all
  generic CRUD and read-only log screens are live from one edit. It does not reload under an
  open form/dialog; it refreshes when the dialog closes.
- The `BroadcastChannel` cross-tab code is untouched and still works.

### Fallback behaviour

Every converted screen keeps its original polling interval, but the timer is only a **60 s safety
net** while push is *confirmed working* (connected **and** at least one message received in this
page session). In every other case - hub disabled, unreachable, reconnecting, or connected but
never heard from - it polls at the original cadence. Pages therefore never get slower than they
were before real-time existed.

`BULK_SQL_BLIND_SPOTS` in `lib/realtime.ts` is the escape hatch for a table the server does *not*
announce: screens that show a listed table keep their original cadence. It is empty because
Chunk 3 announces every `ExecuteUpdateAsync` path. If you add a new bulk-SQL path, either announce
it (`IRealtimeNotifier.EntityChangedAsync` + `RealtimeBulkChanges`) or list its table there.

## Security notes

- **Token in the URL.** Browsers cannot set an `Authorization` header on a WebSocket, so the JWT
  travels as `?access_token=` - accepted **only** for `/hubs` paths (a token in the URL is
  ignored on every normal API route; there is a test for that).
- **It must never reach logs.** `Logging:LogLevel:Microsoft.AspNetCore` must stay at `Warning`
  or higher (request logging would print the URL at `Information`), and HTTP logging middleware
  must not be enabled without redacting the query string. If you put a reverse proxy in front,
  configure it not to log query strings for `/hubs/*` either.
  Test: `RealtimeHardeningTests.AccessToken_NeverAppearsInLogs`.
- **Invalid credentials get 401**, not a silent downgrade to anonymous.
- **Sockets cannot outlive the token** (`CloseOnAuthenticationExpiration = true`). The client
  then finds its stored session expired and returns to the login page.

### Verifying token expiry (manual, ~5 min)

Login issues 3-hour tokens, so this is not an automated test. To see it end to end:

1. Temporarily change `var expiresAtUtc = DateTime.UtcNow.AddHours(3);` in `AccountController`
   to `AddMinutes(2)` and restart the API.
2. Log in to the admin console; the badge shows *Live*.
3. Wait about two minutes. The socket closes when the token expires and the browser is sent to
   `/login`.
4. **Revert the change.**

## Scale-out (more than one API instance)

SignalR groups live in memory of one server. With two or more API instances behind a load
balancer, a change saved on instance A would not reach a browser connected to instance B. When
that day comes, add the Redis backplane:

```csharp
// dotnet add apps/api package Microsoft.AspNetCore.SignalR.StackExchangeRedis
builder.Services.AddSignalR(...).AddStackExchangeRedis(builder.Configuration.GetConnectionString("Redis")!);
```

plus sticky sessions or WebSocket-only transport so a negotiate and its connection hit the same
instance. **Not built** - the project runs a single instance, so it would be untested complexity.

## Tests

| Where | What |
|---|---|
| `api.tests/Integration/RealtimeHubTests.cs` | Connect/negotiate, bad token, trip cap, kill switch (Chunk 1) |
| `api.tests/Integration/RealtimeChangeCaptureTests.cs`, `api.tests/Unit/Realtime*Tests.cs` | Change capture, commit/rollback timing, operator and customer isolation, router matrix, catalog drift guard (Chunk 2) |
| `api.tests/Integration/RealtimeBulkChangeTests.cs` | Bulk-SQL changes (seat holds, expiry, wallets) are announced exactly once, on commit only (Chunk 3) |
| `api.tests/Integration/RealtimeAudienceTests.cs` | The rest of the "who receives what" matrix with real connections and real saves: platform-only, shared reference data, operator-only and customer-only tables |
| `api.tests/Integration/RealtimeHardeningTests.cs` | Call-rate limit, oversized message, token never in logs, kill switch leaves the API working |
| `admin/src/console/lib/realtime.test.ts`, `hooks/useRealtime.test.tsx` | React client with a mocked connection: debounce, ref-counting, reconnect + resync, 404/401 (including the real client's wrapped error text), session expiry, fallback polling |

The Angular app has no unit-test runner configured, so `RealtimeService` is covered by the
two-browser scenarios below rather than by specs.

```bash
npx nx run api.tests:test                 # backend
npx vitest run --root apps/admin          # admin console client tests (or: npx nx run admin:test)
```

## Two-browser test scenarios

Use two windows (or one normal + one incognito).

1. **Live admin list.** Browser A: admin console -> Bookings. Browser B: customer books a seat.
   A's list gains the booking within ~1 s and the badge is *Live*; the Network tab stops showing
   the old 4-15 s polling bursts.
2. **Live dashboard.** Browser A: admin Dashboard. Browser B: complete a payment. A's totals move
   without pressing refresh.
3. **Live seat map (Angular).** Two windows on the same trip's seat map. Hold a seat in one; it
   shows as held in the other within ~1 s. Let the hold expire (or cancel it): the seat frees up
   in both without a reload. An anonymous window sees the same.
4. **Live customer pages (Angular).** Customer window on *My bookings*; a staff window cancels or
   refunds one of that customer's bookings. The customer's page updates by itself, and another
   customer's window does not react at all.
5. **Live staff panels (Angular).** Counter dashboard and finance *Payouts* open in one window;
   sell a ticket / process a payout elsewhere and watch both update.
6. **Resilience.** Stop the API: the badge goes *Reconnecting*, then *Offline*; screens keep
   refreshing on their old timers. Start it again: the badge returns to *Live* and every screen
   resyncs by itself.
7. **Kill switch.** Set `Realtime__Enabled=false`, restart. Everything still works; the badges
   show *Offline* and the Network tab shows a single failed `/hubs/realtime` request per page, not
   a stream of them.
8. **Form safety.** Open a create/edit dialog on any generic admin page; change the same table
   from another browser. The dialog is not disturbed; the list refreshes after it closes.

## Volume check (watch during the demo)

Paste in the browser DevTools console while on any admin page to count pushes per table over the
next minute (needs the `@microsoft/signalr` browser bundle, see the plan for the snippet):

```js
const counts = {};
c.on('changes', (m) => m.forEach((x) => (counts[x.entity] = (counts[x.entity] ?? 0) + 1)));
setTimeout(() => console.table(counts), 60_000);
```

A table that dominates the count (log tables are the usual suspects) is a candidate for a longer
debounce on its screens or for collapsing on the server. The Dashboard already debounces at 1 s.
