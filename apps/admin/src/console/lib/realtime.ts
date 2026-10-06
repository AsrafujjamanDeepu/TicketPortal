// docs/02-Project-Concept-and-Solution.md (Real-time updates), Chunk 6 — the admin console's single SignalR connection.
//
// One shared connection for the whole console (token from getStoredSession()), reference
// counted: it opens when the first thing needs it and closes when the last one lets go.
// The server pushes a *signal* ("Bookings row X changed"), never the data — callers react by
// re-fetching through the normal REST API, so endpoint authorization stays the only gate for
// who can read what.
//
// Everything here is best-effort. If the hub is down, disabled (Realtime:Enabled=false ->
// 404) or unreachable, nothing throws into the UI: the status flips to "offline" /
// "reconnecting" and startLiveRefresh() falls back to the screen's original polling cadence,
// so every screen behaves exactly as it did before real-time existed.

import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { clearSession, getStoredSession } from '../../lib/apiClient';

export type RealtimeStatus = 'live' | 'reconnecting' | 'offline';

/** One entry of the server's `changes` message (see "Event contract" in the plan). */
export interface RealtimeChange {
  /** EF table name ("Bookings", "Tickets", ...), the pseudo-entity "SeatAvailability", or "*" for a resync. */
  entity: string;
  action: string;
  id?: string;
  tripId?: string;
  operatorId?: string;
  atUtc?: string;
}

/** Subscribe to these tables, or '*' for everything. */
export type EntityFilter = readonly string[] | '*';

/** The slice of SignalR's HubConnection this module uses — small so tests can fake it. */
export interface RealtimeConnection {
  start(): Promise<void>;
  stop(): Promise<void>;
  on(methodName: string, handler: (...args: unknown[]) => void): void;
  onreconnecting(handler: (error?: Error) => void): void;
  onreconnected(handler: (connectionId?: string) => void): void;
  onclose(handler: (error?: Error) => void): void;
}

export interface RealtimeClientOptions {
  hubUrl: string;
  /** Current JWT, or null when there is no valid session. Read fresh on every (re)connect. */
  getToken: () => string | null;
  createConnection: (hubUrl: string, accessTokenFactory: () => string) => RealtimeConnection;
  /** Called when the session is gone (expired/revoked) so the app can return to login. */
  onSessionLost?: () => void;
  /** Delays between our own restart attempts after the connection has fully closed. */
  retryDelaysMs?: readonly number[];
}

export interface SubscribeOptions {
  /** Coalesce a burst of changes into one callback. Default 300 ms. */
  debounceMs?: number;
}

/** With push working, polling is only a safety net at this cadence. */
export const SAFETY_NET_MS = 60_000;

/**
 * Tables whose changes the server does NOT announce in every case. A screen that shows one of
 * these keeps polling at its original cadence even while push works, otherwise it could go stale
 * for up to a minute.
 *
 * Empty since Chunk 3: every bulk-SQL path (ExecuteUpdateAsync) that EF's change tracker cannot
 * see - seat holds and seats, booking expiry, wallets - now reports its change through
 * IRealtimeNotifier.EntityChangedAsync. If you add a NEW ExecuteUpdateAsync path and do not
 * announce it, list its table here so the screens that show it keep polling.
 */
export const BULK_SQL_BLIND_SPOTS: ReadonlySet<string> = new Set<string>();

/**
 * The HTTP status behind a failed connection attempt, or undefined. The real SignalR client
 * does NOT surface it as `error.statusCode` for a failed negotiate - it wraps the failure in
 * FailedToNegotiateWithServerError whose message contains "Status code '404'" - so look at
 * both. (A thrown HttpError from elsewhere in the client does carry `statusCode`.)
 */
export function httpStatusOf(error: unknown): number | undefined {
  const direct = (error as { statusCode?: unknown } | null | undefined)?.statusCode;
  if (typeof direct === 'number') return direct;

  const message = error instanceof Error ? error.message : typeof error === 'string' ? error : '';
  const match = /Status code '(\d{3})'/.exec(message);
  return match ? Number(match[1]) : undefined;
}

const DEFAULT_DEBOUNCE_MS = 300;
const DEFAULT_RETRY_DELAYS_MS = [2_000, 5_000, 10_000, 30_000] as const;
// SignalR's own automatic reconnect covers short blips; when it gives up we take over above.
const AUTOMATIC_RECONNECT_DELAYS_MS = [0, 2_000, 5_000, 10_000, 30_000];

interface Subscriber {
  filter: EntityFilter;
  debounceMs: number;
  callback: (changes: RealtimeChange[]) => void;
  pending: RealtimeChange[];
  timer: ReturnType<typeof setTimeout> | null;
}

export class RealtimeClient {
  private readonly hubUrl: string;
  private readonly getToken: () => string | null;
  private readonly createConnection: RealtimeClientOptions['createConnection'];
  private readonly onSessionLost: () => void;
  private readonly retryDelays: readonly number[];

  private readonly subscribers = new Set<Subscriber>();
  private readonly statusListeners = new Set<() => void>();

  private status: RealtimeStatus = 'offline';
  private refCount = 0;
  private connection: RealtimeConnection | null = null;
  private retryTimer: ReturnType<typeof setTimeout> | null = null;
  private retryAttempt = 0;
  private hasConnectedOnce = false;
  /**
   * True once a real `changes` message has arrived in this page session. A connected socket
   * alone proves nothing - if the server never emits (change capture not deployed, or a broken
   * pipeline) a "live" badge would silently starve every screen of refreshes. Until the first
   * message is seen, startLiveRefresh keeps polling at each screen's original cadence.
   */
  private pushConfirmed = false;
  /** Set when the hub answered 404 (kill switch): stop trying until the page is reloaded. */
  private disabled = false;
  private generation = 0;

  constructor(options: RealtimeClientOptions) {
    this.hubUrl = options.hubUrl;
    this.getToken = options.getToken;
    this.createConnection = options.createConnection;
    this.onSessionLost = options.onSessionLost ?? (() => undefined);
    this.retryDelays = options.retryDelaysMs ?? DEFAULT_RETRY_DELAYS_MS;
  }

  // ---- status -------------------------------------------------------------------------

  getStatus = (): RealtimeStatus => this.status;

  isLive(): boolean {
    return this.status === 'live';
  }

  /** Connected AND known to deliver: the only state in which polling may be relaxed. */
  isPushHealthy(): boolean {
    return this.status === 'live' && this.pushConfirmed;
  }

  /** useSyncExternalStore-compatible: returns the unsubscribe function. */
  onStatusChange = (listener: () => void): (() => void) => {
    this.statusListeners.add(listener);
    return () => {
      this.statusListeners.delete(listener);
    };
  };

  private setStatus(next: RealtimeStatus): void {
    if (this.status === next) return;
    this.status = next;
    for (const listener of [...this.statusListeners]) {
      try {
        listener();
      } catch {
        /* a broken listener must not stop the others */
      }
    }
  }

  // ---- connection lifetime (reference counted) ------------------------------------------

  /** Keep the connection open until the returned function is called. */
  retain(): () => void {
    this.refCount += 1;
    if (this.refCount === 1) void this.connect();

    let released = false;
    return () => {
      if (released) return;
      released = true;
      this.refCount = Math.max(0, this.refCount - 1);
      if (this.refCount === 0) void this.disconnect();
    };
  }

  /**
   * Call `callback` (debounced) whenever one of `entities` changes, and once with a synthetic
   * `{ entity: '*', action: 'resync' }` after every reconnect so nothing missed while offline
   * stays stale. Holds the connection open while subscribed.
   */
  subscribe(
    entities: EntityFilter,
    callback: (changes: RealtimeChange[]) => void,
    options: SubscribeOptions = {},
  ): () => void {
    const subscriber: Subscriber = {
      filter: entities,
      debounceMs: Math.max(0, options.debounceMs ?? DEFAULT_DEBOUNCE_MS),
      callback,
      pending: [],
      timer: null,
    };
    this.subscribers.add(subscriber);
    const release = this.retain();

    return () => {
      if (subscriber.timer) clearTimeout(subscriber.timer);
      subscriber.timer = null;
      subscriber.pending = [];
      this.subscribers.delete(subscriber);
      release();
    };
  }

  // ---- delivery -----------------------------------------------------------------------

  private handleMessage(payload: unknown): void {
    if (!Array.isArray(payload)) return;
    const changes: RealtimeChange[] = [];
    for (const item of payload) {
      if (item && typeof item === 'object' && typeof (item as RealtimeChange).entity === 'string') {
        changes.push(item as RealtimeChange);
      }
    }
    if (changes.length === 0) return;
    this.pushConfirmed = true;

    for (const subscriber of this.subscribers) {
      const matched =
        subscriber.filter === '*'
          ? changes
          : changes.filter((c) => (subscriber.filter as readonly string[]).includes(c.entity));
      if (matched.length > 0) this.enqueue(subscriber, matched);
    }
  }

  private resyncAll(): void {
    const resync: RealtimeChange = { entity: '*', action: 'resync', atUtc: new Date().toISOString() };
    for (const subscriber of this.subscribers) this.enqueue(subscriber, [resync]);
  }

  private enqueue(subscriber: Subscriber, changes: RealtimeChange[]): void {
    subscriber.pending.push(...changes);
    if (subscriber.timer) return; // trailing debounce: one callback per burst

    subscriber.timer = setTimeout(() => {
      subscriber.timer = null;
      const batch = subscriber.pending;
      subscriber.pending = [];
      if (!this.subscribers.has(subscriber) || batch.length === 0) return;
      try {
        subscriber.callback(batch);
      } catch {
        /* a subscriber's failure must never break delivery to the others */
      }
    }, subscriber.debounceMs);
  }

  // ---- connect / reconnect ------------------------------------------------------------

  private async connect(): Promise<void> {
    if (this.disabled || this.refCount === 0 || this.connection) return;

    if (!this.getToken()) {
      // The console needs a login; never connect anonymously (an anonymous socket would look
      // "live" while receiving nothing).
      this.setStatus('offline');
      this.onSessionLost();
      return;
    }

    const generation = ++this.generation;
    this.setStatus('reconnecting');

    const connection = this.createConnection(this.hubUrl, () => {
      const token = this.getToken();
      if (!token) throw new Error('The session has expired.');
      return token;
    });
    this.connection = connection;

    connection.on('changes', (payload: unknown) => {
      if (generation === this.generation) this.handleMessage(payload);
    });
    connection.onreconnecting(() => {
      if (generation !== this.generation) return;
      if (!this.getToken()) {
        // The server closed the socket because the token expired (CloseOnAuthenticationExpiration)
        // and SignalR is now trying to reconnect with a token that no longer exists. Do not wait
        // for its whole retry schedule: go back to login now.
        this.endExpiredSession(connection);
        return;
      }
      this.setStatus('reconnecting');
    });
    connection.onreconnected(() => {
      if (generation !== this.generation) return;
      this.retryAttempt = 0;
      this.setStatus('live');
      this.resyncAll();
    });
    connection.onclose(() => {
      if (generation !== this.generation) return;
      this.connection = null;
      this.handleClosed();
    });

    try {
      await connection.start();
    } catch (error) {
      if (generation !== this.generation) return;
      this.connection = null;
      this.handleStartFailure(error);
      return;
    }

    if (generation !== this.generation || this.refCount === 0) {
      // Everyone let go while we were connecting.
      void connection.stop().catch(() => undefined);
      return;
    }

    this.retryAttempt = 0;
    this.setStatus('live');
    if (this.hasConnectedOnce) this.resyncAll(); // we were offline for a while: catch up
    this.hasConnectedOnce = true;
  }

  private endExpiredSession(connection: RealtimeConnection): void {
    this.generation += 1; // silence the handlers of the connection being dropped
    if (this.connection === connection) this.connection = null;
    this.setStatus('offline');
    void connection.stop().catch(() => undefined);
    this.onSessionLost();
  }

  private handleStartFailure(error: unknown): void {
    const statusCode = httpStatusOf(error);

    if (statusCode === 404) {
      // Realtime:Enabled=false — the hub is not mapped. Screens keep working on their
      // original polling; no point retrying until the page is reloaded.
      this.disabled = true;
      this.setStatus('offline');
      return;
    }

    if (statusCode === 401 || !this.getToken()) {
      this.setStatus('offline');
      this.onSessionLost();
      return;
    }

    this.scheduleRetry();
  }

  private handleClosed(): void {
    if (this.refCount === 0) {
      this.setStatus('offline');
      return;
    }
    // Closed by the server: token expired (CloseOnAuthenticationExpiration) or automatic
    // reconnect gave up. Expired token => back to login; otherwise keep trying.
    if (!this.getToken()) {
      this.setStatus('offline');
      this.onSessionLost();
      return;
    }
    this.scheduleRetry();
  }

  private scheduleRetry(): void {
    this.setStatus('offline');
    if (this.retryTimer || this.refCount === 0 || this.disabled) return;

    const delays = this.retryDelays.length > 0 ? this.retryDelays : DEFAULT_RETRY_DELAYS_MS;
    const delay = delays[Math.min(this.retryAttempt, delays.length - 1)];
    this.retryAttempt += 1;

    this.retryTimer = setTimeout(() => {
      this.retryTimer = null;
      void this.connect();
    }, delay);
  }

  private async disconnect(): Promise<void> {
    this.generation += 1; // invalidate handlers of the connection being closed
    if (this.retryTimer) clearTimeout(this.retryTimer);
    this.retryTimer = null;
    this.retryAttempt = 0;

    const connection = this.connection;
    this.connection = null;
    this.setStatus('offline');
    if (connection) {
      try {
        await connection.stop();
      } catch {
        /* already closed */
      }
    }
  }
}

// ---- default wiring ---------------------------------------------------------------------

/** "https://host:54221/api" -> "https://host:54221/hubs/realtime" */
export function hubUrlFromApiBase(apiBaseUrl: string): string {
  const origin = apiBaseUrl.replace(/\/+$/, '').replace(/\/api$/i, '');
  return `${origin}/hubs/realtime`;
}

function createSignalRConnection(hubUrl: string, accessTokenFactory: () => string): RealtimeConnection {
  return new HubConnectionBuilder()
    // withCredentials:false — auth is a JWT, not a cookie, and the API's CORS policy does not
    // allow credentials.
    .withUrl(hubUrl, { accessTokenFactory, withCredentials: false })
    .withAutomaticReconnect(AUTOMATIC_RECONNECT_DELAYS_MS)
    .configureLogging(LogLevel.Warning)
    .build();
}

function sessionLost(): void {
  // Same outcome as apiFetch()/AuthProvider.logout(): drop the stale session, go to login.
  clearSession();
  if (typeof window !== 'undefined') window.location.href = '/login';
}

// Read the env var directly (same source and default as lib/api.ts) rather than importing api.ts,
// which would drag axios and every service module into this otherwise dependency-light file.
const API_BASE = String(import.meta.env.VITE_API_BASE_URL ?? 'https://localhost:54221/api');

export const realtime = new RealtimeClient({
  hubUrl: hubUrlFromApiBase(API_BASE),
  getToken: () => getStoredSession()?.token ?? null,
  createConnection: createSignalRConnection,
  onSessionLost: sessionLost,
});

// ---- helper for effect-style call sites -------------------------------------------------

export interface LiveRefreshOptions {
  client?: RealtimeClient;
  debounceMs?: number;
  safetyNetMs?: number;
  /** Test seam; the app always uses BULK_SQL_BLIND_SPOTS. */
  blindSpots?: ReadonlySet<string>;
}

/**
 * Replacement for `setInterval(load, ms)` inside a useEffect: reload when one of `entities`
 * changes, and keep polling at the screen's original `fallbackMs` unless push is confirmed
 * working (connected and at least one message received), in which case the timer only fires
 * as a 60 s safety net - except for screens showing a BULK_SQL_BLIND_SPOTS table, which keep
 * their original cadence (the set is empty today). Returns the cleanup function.
 *
 *   useEffect(() => { load(); return startLiveRefresh(['Tickets'], load, 15000); }, [load]);
 */
export function startLiveRefresh(
  entities: EntityFilter,
  load: () => unknown,
  fallbackMs: number,
  options: LiveRefreshOptions = {},
): () => void {
  const client = options.client ?? realtime;
  const blindSpots = options.blindSpots ?? BULK_SQL_BLIND_SPOTS;
  const hasBlindSpot = blindSpots.size > 0 && (entities === '*' || entities.some((e) => blindSpots.has(e)));
  const safetyNetMs = hasBlindSpot ? 0 : (options.safetyNetMs ?? SAFETY_NET_MS);
  let lastRunAt = Date.now(); // the caller has just done (or is doing) the first load

  const run = () => {
    lastRunAt = Date.now();
    try {
      const result = load();
      if (result && typeof (result as Promise<unknown>).catch === 'function') {
        (result as Promise<unknown>).catch(() => undefined);
      }
    } catch {
      /* the caller's load() reports its own errors */
    }
  };

  const unsubscribe = client.subscribe(entities, run, { debounceMs: options.debounceMs });

  const timer = setInterval(() => {
    // Push is confirmed working: the timer is only a safety net, so skip unless a full period
    // passed. Otherwise (offline, reconnecting, or push never seen) poll as before.
    if (client.isPushHealthy() && Date.now() - lastRunAt < safetyNetMs) return;
    run();
  }, fallbackMs);

  return () => {
    unsubscribe();
    clearInterval(timer);
  };
}
