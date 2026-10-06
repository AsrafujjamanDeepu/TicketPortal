import { Injectable, effect, inject, signal, untracked } from '@angular/core';
import { HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { Observable, Subject, Subscription } from 'rxjs';
import { environment } from '../../../environments/environment';
import { AuthService } from '../services/auth.service';

/** Connection health, for a status badge: `live` = pushes are flowing, `reconnecting` = trying, `offline` = not connected. */
export type RealtimeState = 'live' | 'reconnecting' | 'offline';

/** One committed change, exactly as the server announces it (see the event contract in docs/02-Project-Concept-and-Solution.md (Real-time updates) §2). */
export interface RealtimeChange {
  /** Table name (`Bookings`, `Tickets`, `OperatorPayouts`, …) or the pseudo-entity `SeatAvailability`. */
  entity: string;
  /** `bulk` = a very large save collapsed to one message per table (no row id): re-fetch the whole table. */
  action: 'created' | 'updated' | 'deleted' | 'bulk' | (string & {});
  id?: string;
  tripId?: string;
  operatorId?: string;
  atUtc?: string;
}

export interface RealtimeWatchOptions {
  /**
   * Coalescing window in ms (default 300). The first matching change opens a window; everything that
   * arrives inside it is delivered as ONE batch, so a burst of 20 row changes triggers one re-fetch.
   */
  debounceMs?: number;
}

/** Path of the SignalR endpoint, relative to the API origin (Chunk 1: `MapHub<RealtimeHub>("/hubs/realtime")`). */
const HUB_PATH = '/hubs/realtime';

/** Reconnect / retry back-off. The last value repeats forever, so a long API outage heals by itself. */
const RETRY_DELAYS_MS = [0, 2_000, 5_000, 10_000, 30_000];

const DEFAULT_DEBOUNCE_MS = 300;
const SEAT_AVAILABILITY = 'SeatAvailability';

/**
 * The HTTP status behind a failed connection attempt, or undefined.
 *
 * The SignalR client does NOT put the status on `error.statusCode` when /negotiate fails: it wraps the
 * failure in a FailedToNegotiateWithServerError whose message contains "Status code '404'". Read both, so
 * the 404 (kill switch) and 401 (rejected token) cases below really do stop the retry loop.
 */
export function httpStatusOf(error: unknown): number | undefined {
  const direct = (error as { statusCode?: unknown } | null | undefined)?.statusCode;
  if (typeof direct === 'number') return direct;

  const message = error instanceof Error ? error.message : typeof error === 'string' ? error : '';
  const match = /Status code '(\d{3})'/.exec(message);
  return match ? Number(match[1]) : undefined;
}

/**
 * The one SignalR connection for the whole Angular app.
 *
 * Design (docs/02-Project-Concept-and-Solution.md (Real-time updates) §0): the server pushes a *signal* ("Bookings row X changed"), never
 * the data. Screens react by re-fetching through the normal REST API, so the existing per-endpoint
 * authorization stays the only gate on who can read what — nothing sensitive travels over the socket.
 *
 * Usage from a screen (normally through `liveRefresh()` in ./live-refresh.ts):
 *
 *   realtime.watch(['Bookings', 'Payments']).pipe(takeUntilDestroyed(ref)).subscribe(() => this.load());
 *
 * Behaviours worth knowing:
 *  - Lazy: the socket opens on the first `watch()` / `watchSeats()` / `joinTrip()`, so pages that never
 *    go live never open one.
 *  - Login / logout (a different user id) restarts the connection so the server re-evaluates which
 *    groups it belongs to (it decides that from the database-resolved actor, never from the client).
 *  - After any outage — or an identity change — every active watcher receives one synthetic "resync"
 *    batch (an EMPTY array), so nothing that happened while disconnected stays stale.
 *  - Never throws into a screen: if the hub is unreachable everything behaves as it did before real-time
 *    existed. HTTP 404 (the server's `Realtime:Enabled=false` kill switch) and 401 (rejected token) stop
 *    the retry loop; anything else (API down, network) retries with back-off until it succeeds.
 */
@Injectable({ providedIn: 'root' })
export class RealtimeService {
  private readonly auth = inject(AuthService);

  private readonly _state = signal<RealtimeState>('offline');
  /** Connection health as a signal — bind a badge to it. */
  readonly state = this._state.asReadonly();

  private readonly _active = signal(false);
  /**
   * True once some screen has asked for live updates (the socket is opened lazily). A status badge should
   * only show while this is true — before that, "offline" would just mean "nobody needed the socket yet".
   */
  readonly active = this._active.asReadonly();

  private readonly changeSubject = new Subject<RealtimeChange>();
  private readonly resyncSubject = new Subject<void>();
  /** Every change the server sends this connection, unbatched. Prefer `watch()` in screens. */
  readonly changes$: Observable<RealtimeChange> = this.changeSubject.asObservable();
  /** Fires after a (re)connect that followed an outage or a login/logout. `watch()` folds this into its batches. */
  readonly resync$: Observable<void> = this.resyncSubject.asObservable();

  private connection: HubConnection | null = null;
  private started = false;
  private generation = 0;
  private identity: string | null = null;
  private needsResync = false;
  private retryAttempt = 0;
  private retryTimer: ReturnType<typeof setTimeout> | undefined;

  /** tripId -> number of active consumers, so two components can share one server-side group. */
  private readonly joinedTrips = new Map<string, number>();

  constructor() {
    // Re-evaluate group membership when the signed-in user changes. Before the socket has been asked for
    // this only records the identity; the connection itself is opened lazily by ensureStarted().
    effect(() => {
      const userId = this.auth.currentUser()?.userId ?? null;
      untracked(() => {
        if (!this.started) {
          this.identity = userId;
          return;
        }
        if (userId === this.identity) return;
        this.identity = userId;
        this.needsResync = true;
        void this.connect();
      });
    });
  }

  /**
   * Batches of changes for the given tables. An EMPTY array means "resync — reload everything you show".
   * Subscribing opens the connection if it isn't open yet.
   */
  watch(entities: readonly string[], options: RealtimeWatchOptions = {}): Observable<RealtimeChange[]> {
    const wanted = new Set(entities);
    return this.batched((change) => wanted.has(change.entity), options.debounceMs ?? DEFAULT_DEBOUNCE_MS);
  }

  /**
   * Seat-availability signals for one trip. Subscribing joins the trip's group on the server; unsubscribing
   * leaves it (reference-counted, so two seat maps on the same trip share one join). An EMPTY array means
   * "resync — re-fetch the seat map". Anonymous visitors may use this (the seat map is public).
   */
  watchSeats(tripId: string, options: RealtimeWatchOptions = {}): Observable<RealtimeChange[]> {
    const wantedTrip = tripId.toLowerCase();
    const inner = this.batched(
      (change) => change.entity === SEAT_AVAILABILITY && change.tripId?.toLowerCase() === wantedTrip,
      options.debounceMs ?? DEFAULT_DEBOUNCE_MS,
    );
    return new Observable<RealtimeChange[]>((subscriber) => {
      this.joinTrip(tripId);
      const subscription = inner.subscribe(subscriber);
      return () => {
        subscription.unsubscribe();
        this.leaveTrip(tripId);
      };
    });
  }

  /** Joins a trip's seat-availability group (the server caps how many one connection may hold). Reference-counted. */
  joinTrip(tripId: string): void {
    this.ensureStarted();
    const key = tripId.toLowerCase();
    const count = (this.joinedTrips.get(key) ?? 0) + 1;
    this.joinedTrips.set(key, count);
    if (count === 1) this.invokeQuietly('JoinTrip', tripId);
  }

  leaveTrip(tripId: string): void {
    const key = tripId.toLowerCase();
    const count = this.joinedTrips.get(key);
    if (!count) return;
    if (count > 1) {
      this.joinedTrips.set(key, count - 1);
      return;
    }
    this.joinedTrips.delete(key);
    this.invokeQuietly('LeaveTrip', tripId);
  }

  // ---- batching ---------------------------------------------------------------------------------------

  private batched(matches: (change: RealtimeChange) => boolean, debounceMs: number): Observable<RealtimeChange[]> {
    return new Observable<RealtimeChange[]>((subscriber) => {
      this.ensureStarted();

      let pending: RealtimeChange[] = [];
      let timer: ReturnType<typeof setTimeout> | undefined;

      const flush = (): void => {
        timer = undefined;
        const batch = pending;
        pending = [];
        subscriber.next(batch);
      };
      const schedule = (): void => {
        if (timer === undefined) timer = setTimeout(flush, debounceMs);
      };

      const subscription = new Subscription();
      subscription.add(
        this.changeSubject.subscribe((change) => {
          if (!matches(change)) return;
          pending.push(change);
          schedule();
        }),
      );
      subscription.add(this.resyncSubject.subscribe(() => schedule()));

      return () => {
        subscription.unsubscribe();
        if (timer !== undefined) clearTimeout(timer);
      };
    });
  }

  // ---- connection lifecycle ---------------------------------------------------------------------------

  private ensureStarted(): void {
    if (this.started) return;
    this.started = true;
    this._active.set(true);
    this.identity = this.auth.currentUser()?.userId ?? null;
    void this.connect();
  }

  private hubUrl(): string {
    // apiBaseUrl ends in "/api"; the hub lives at the origin.
    return environment.apiBaseUrl.replace(/\/api\/?$/, '') + HUB_PATH;
  }

  private buildConnection(generation: number): HubConnection {
    const connection = new HubConnectionBuilder()
      .withUrl(this.hubUrl(), {
        // Auth is a JWT, not a cookie, so no credentials are sent (the API's CORS policy has no AllowCredentials).
        withCredentials: false,
        // Empty string = connect anonymously (the public seat map). An expired token is never presented, because
        // the server answers "presented but invalid" with a 401 rather than treating it as anonymous.
        accessTokenFactory: () => (this.auth.isAuthenticated() ? (this.auth.getToken() ?? '') : ''),
      })
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (context) =>
          RETRY_DELAYS_MS[Math.min(context.previousRetryCount, RETRY_DELAYS_MS.length - 1)],
      })
      .configureLogging(environment.production ? LogLevel.None : LogLevel.Warning)
      .build();

    connection.on('changes', (payload: unknown) => {
      if (generation === this.generation) this.receive(payload);
    });

    connection.onreconnecting(() => {
      if (generation !== this.generation) return;
      this._state.set('reconnecting');
      this.needsResync = true;
    });

    connection.onreconnected(() => {
      if (generation !== this.generation) return;
      this._state.set('live');
      void this.afterConnected(connection);
    });

    // Fires when automatic reconnect gave up or the server closed the socket (e.g. the token expired).
    connection.onclose(() => {
      if (generation !== this.generation) return;
      this._state.set('offline');
      this.needsResync = true;
      this.scheduleRetry();
    });

    return connection;
  }

  private async connect(): Promise<void> {
    const generation = ++this.generation;
    this.clearRetry();

    const previous = this.connection;
    this.connection = null;
    if (previous) {
      try {
        await previous.stop();
      } catch {
        // Nothing useful to do — the old socket is being discarded anyway.
      }
      if (generation !== this.generation) return;
    }

    const connection = this.buildConnection(generation);
    this.connection = connection;
    this._state.set('reconnecting');

    try {
      await connection.start();
    } catch (error) {
      if (generation !== this.generation) return;
      this.onStartFailed(error);
      return;
    }

    if (generation !== this.generation) {
      void connection.stop();
      return;
    }
    this.retryAttempt = 0;
    this._state.set('live');
    await this.afterConnected(connection);
  }

  private onStartFailed(error: unknown): void {
    this._state.set('offline');
    this.needsResync = true;

    const status = httpStatusOf(error);
    // 404: the server's Realtime:Enabled=false kill switch (the hub isn't mapped). 401: the token was rejected.
    // Retrying either with identical inputs would only spam the API; a login/logout starts a fresh attempt.
    if (status === 404 || status === 401) return;

    this.scheduleRetry();
  }

  private scheduleRetry(): void {
    this.clearRetry();
    const delay = RETRY_DELAYS_MS[Math.min(this.retryAttempt, RETRY_DELAYS_MS.length - 1)];
    this.retryAttempt++;
    this.retryTimer = setTimeout(() => void this.connect(), delay);
  }

  private clearRetry(): void {
    if (this.retryTimer !== undefined) {
      clearTimeout(this.retryTimer);
      this.retryTimer = undefined;
    }
  }

  /** Runs after every successful (re)connect: re-join trip groups (server groups die with the socket), then resync if needed. */
  private async afterConnected(connection: HubConnection): Promise<void> {
    await Promise.all(
      [...this.joinedTrips.keys()].map((tripId) =>
        connection.invoke('JoinTrip', tripId).catch(() => undefined),
      ),
    );
    if (connection !== this.connection) return;

    if (this.needsResync) {
      this.needsResync = false;
      this.resyncSubject.next();
    }
  }

  private invokeQuietly(method: string, ...args: unknown[]): void {
    const connection = this.connection;
    if (!connection || this._state() !== 'live') return; // afterConnected() re-joins everything once live
    connection.invoke(method, ...args).catch(() => undefined);
  }

  // ---- incoming ---------------------------------------------------------------------------------------

  private receive(payload: unknown): void {
    const items = Array.isArray(payload) ? payload : [payload];
    for (const item of items) {
      const change = this.normalise(item);
      if (change) this.changeSubject.next(change);
    }
  }

  private normalise(item: unknown): RealtimeChange | null {
    if (!item || typeof item !== 'object') return null;
    const raw = item as Record<string, unknown>;
    if (typeof raw['entity'] !== 'string') return null;

    return {
      entity: raw['entity'],
      action: typeof raw['action'] === 'string' ? raw['action'].toLowerCase() : 'updated',
      id: typeof raw['id'] === 'string' ? raw['id'] : undefined,
      tripId: typeof raw['tripId'] === 'string' ? raw['tripId'] : undefined,
      operatorId: typeof raw['operatorId'] === 'string' ? raw['operatorId'] : undefined,
      atUtc: typeof raw['atUtc'] === 'string' ? raw['atUtc'] : undefined,
    };
  }
}
