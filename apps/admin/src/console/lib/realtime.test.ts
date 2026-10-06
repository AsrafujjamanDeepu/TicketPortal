import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  BULK_SQL_BLIND_SPOTS,
  httpStatusOf,
  hubUrlFromApiBase,
  SAFETY_NET_MS,
  startLiveRefresh,
  type RealtimeChange,
} from './realtime';
import { createHarness, flush } from './realtime.testkit';

// docs/02-Project-Concept-and-Solution.md (Real-time updates), Chunk 7: client unit tests with a mocked connection.

beforeEach(() => vi.useFakeTimers());
afterEach(() => vi.useRealTimers());

describe('hubUrlFromApiBase', () => {
  it('drops the /api suffix and adds the hub path', () => {
    expect(hubUrlFromApiBase('https://localhost:54221/api')).toBe('https://localhost:54221/hubs/realtime');
    expect(hubUrlFromApiBase('https://api.example.com/api/')).toBe('https://api.example.com/hubs/realtime');
  });
});

describe('httpStatusOf', () => {
  it("reads the status from the real client's wrapped negotiate error message", () => {
    // What @microsoft/signalr throws when /negotiate answers 404: no statusCode property, only text.
    const wrapped = new Error(
      "Failed to complete negotiation with the server: Error: Not Found: Status code '404' Either this is not a SignalR endpoint or there is a proxy blocking the connection.",
    );
    expect(httpStatusOf(wrapped)).toBe(404);
    expect(httpStatusOf(new Error("Failed to complete negotiation with the server: Error: Unauthorized: Status code '401'"))).toBe(401);
  });

  it('reads a statusCode property and ignores unrelated errors', () => {
    expect(httpStatusOf({ statusCode: 404 })).toBe(404);
    expect(httpStatusOf(new Error('network down'))).toBe(undefined);
    expect(httpStatusOf(undefined)).toBe(undefined);
  });
});

describe('RealtimeClient delivery', () => {
  it('delivers only the subscribed entities, debounced into one call', async () => {
    const h = createHarness();
    const seen: RealtimeChange[][] = [];
    h.client.subscribe(['Bookings'], (c) => seen.push(c), { debounceMs: 300 });
    await flush();

    h.current().emitChanges([{ entity: 'Bookings', id: '1' }, { entity: 'Tickets', id: '9' }]);
    h.current().emitChanges([{ entity: 'Bookings', id: '2' }]);
    expect(seen).toHaveLength(0); // still inside the debounce window

    vi.advanceTimersByTime(300);
    expect(seen).toHaveLength(1);
    expect(seen[0].map((c) => c.id)).toEqual(['1', '2']); // Tickets filtered out, burst coalesced
  });

  it("'*' receives every entity", async () => {
    const h = createHarness();
    const seen: RealtimeChange[][] = [];
    h.client.subscribe('*', (c) => seen.push(c), { debounceMs: 0 });
    await flush();

    h.current().emitChanges([{ entity: 'Anything' }]);
    vi.advanceTimersByTime(1);
    expect(seen).toHaveLength(1);
  });

  it('ignores malformed messages instead of throwing', async () => {
    const h = createHarness();
    const cb = vi.fn();
    h.client.subscribe('*', cb, { debounceMs: 0 });
    await flush();

    const handler = h.current().handlers.get('changes');
    if (!handler) throw new Error('changes handler was not registered');
    expect(() => {
      handler('nope');
      handler([null, 5, { noEntity: true }]);
    }).not.toThrow();
    vi.advanceTimersByTime(1);
    expect(cb).not.toHaveBeenCalled();
  });

  it('stops delivering after unsubscribe', async () => {
    const h = createHarness();
    const cb = vi.fn();
    const unsubscribe = h.client.subscribe(['Bookings'], cb, { debounceMs: 100 });
    await flush();

    h.current().emitChanges([{ entity: 'Bookings' }]);
    unsubscribe();
    vi.advanceTimersByTime(500);
    expect(cb).not.toHaveBeenCalled();
  });
});

describe('RealtimeClient connection lifetime', () => {
  it('shares one connection between subscribers and closes it with the last one', async () => {
    const h = createHarness();
    const a = h.client.subscribe(['Bookings'], vi.fn());
    const b = h.client.subscribe(['Tickets'], vi.fn());
    await flush();

    expect(h.connections).toHaveLength(1);
    expect(h.client.getStatus()).toBe('live');

    a();
    expect(h.current().stopCalls).toBe(0);
    b();
    await flush();
    expect(h.current().stopCalls).toBe(1);
    expect(h.client.getStatus()).toBe('offline');
  });

  it('sends a resync to every subscriber after an automatic reconnect', async () => {
    const h = createHarness();
    const cb = vi.fn();
    h.client.subscribe(['Bookings'], cb, { debounceMs: 0 });
    await flush();

    h.current().dropAndRecover();
    expect(h.client.getStatus()).toBe('live');
    vi.advanceTimersByTime(1);

    expect(cb).toHaveBeenCalledTimes(1);
    expect(cb.mock.calls[0][0][0]).toMatchObject({ entity: '*', action: 'resync' });
  });

  it('reports reconnecting while SignalR retries', async () => {
    const h = createHarness();
    h.client.retain();
    await flush();

    h.current().reconnecting.forEach((fn) => fn());
    expect(h.client.getStatus()).toBe('reconnecting');
  });

  it('retries with backoff after a failed start and connects when the network returns', async () => {
    const h = createHarness({ startError: new Error('network down'), retryDelaysMs: [1000, 5000] });
    h.client.subscribe(['Bookings'], vi.fn(), { debounceMs: 0 });
    await flush();
    expect(h.client.getStatus()).toBe('offline');
    expect(h.connections).toHaveLength(1);

    await vi.advanceTimersByTimeAsync(1000); // first retry, still failing
    expect(h.connections).toHaveLength(2);

    h.setStartError(null); // network is back
    await vi.advanceTimersByTimeAsync(5000); // second retry uses the next (longer) delay
    expect(h.connections).toHaveLength(3);
    expect(h.client.getStatus()).toBe('live');
  });

  it('resyncs after reconnecting on its own following a full close', async () => {
    const h = createHarness({ retryDelaysMs: [1000] });
    const cb = vi.fn();
    h.client.subscribe(['Bookings'], cb, { debounceMs: 0 });
    await flush();

    h.current().serverClose(); // we were live, now the socket is gone
    await vi.advanceTimersByTimeAsync(1000);
    await vi.advanceTimersByTimeAsync(1);

    expect(h.client.getStatus()).toBe('live');
    expect(cb).toHaveBeenCalledTimes(1);
    expect(cb.mock.calls[0][0][0]).toMatchObject({ entity: '*', action: 'resync' });
  });

  it('treats the real negotiate-404 error text as the kill switch too', async () => {
    const h = createHarness({
      startError: new Error("Failed to complete negotiation with the server: Error: Not Found: Status code '404'"),
    });
    h.client.subscribe(['Bookings'], vi.fn());
    await flush();

    expect(h.client.getStatus()).toBe('offline');
    await vi.advanceTimersByTimeAsync(120_000);
    expect(h.connections).toHaveLength(1);
    expect(h.sessionLost.count).toBe(0);
  });

  it('treats a negotiate 401 as a lost session', async () => {
    const h = createHarness({
      startError: new Error("Failed to complete negotiation with the server: Error: Unauthorized: Status code '401'"),
    });
    h.client.subscribe(['Bookings'], vi.fn());
    await flush();

    expect(h.sessionLost.count).toBe(1);
    await vi.advanceTimersByTimeAsync(120_000);
    expect(h.connections).toHaveLength(1); // no retry loop
  });

  it('leaves at once when SignalR starts reconnecting after the token expired', async () => {
    const h = createHarness();
    h.client.subscribe(['Bookings'], vi.fn());
    await flush();

    h.setToken(null); // the stored session just expired; the server closed the socket
    h.current().reconnecting.forEach((fn) => fn());

    expect(h.sessionLost.count).toBe(1);
    expect(h.client.getStatus()).toBe('offline');
    expect(h.connections[0].stopCalls).toBe(1);
  });

  it('gives up quietly when the hub is disabled (404) - no retry storm', async () => {
    const h = createHarness({ startError: { statusCode: 404 } });
    h.client.subscribe(['Bookings'], vi.fn());
    await flush();

    expect(h.client.getStatus()).toBe('offline');
    await vi.advanceTimersByTimeAsync(120_000);
    expect(h.connections).toHaveLength(1);
    expect(h.sessionLost.count).toBe(0);
  });

  it('never connects without a session and asks the app to return to login', async () => {
    const h = createHarness({ token: null });
    h.client.subscribe(['Bookings'], vi.fn());
    await flush();

    expect(h.connections).toHaveLength(0);
    expect(h.client.getStatus()).toBe('offline');
    expect(h.sessionLost.count).toBe(1);
  });

  it('returns to login when the server closes the socket and the token has expired', async () => {
    const h = createHarness();
    h.client.subscribe(['Bookings'], vi.fn());
    await flush();

    h.setToken(null); // the stored session expired
    h.current().serverClose(new Error('Authentication expired'));

    expect(h.sessionLost.count).toBe(1);
    expect(h.client.getStatus()).toBe('offline');
  });

  it('restarts on its own when the server closes the socket but the session is still valid', async () => {
    const h = createHarness({ retryDelaysMs: [1000] });
    h.client.subscribe(['Bookings'], vi.fn());
    await flush();

    h.current().serverClose();
    expect(h.client.getStatus()).toBe('offline');
    await vi.advanceTimersByTimeAsync(1000);
    expect(h.connections).toHaveLength(2);
    expect(h.client.getStatus()).toBe('live');
  });
});

describe('startLiveRefresh', () => {
  it('reloads on a matching push', async () => {
    const h = createHarness();
    const load = vi.fn();
    const stop = startLiveRefresh(['Tickets'], load, 15_000, { client: h.client, debounceMs: 0 });
    await flush();

    h.current().emitChanges([{ entity: 'Tickets' }]);
    vi.advanceTimersByTime(1);
    expect(load).toHaveBeenCalledTimes(1);
    stop();
  });

  it('does not poll while the hub is live - only the 60 s safety net fires', async () => {
    const h = createHarness();
    const load = vi.fn();
    const stop = startLiveRefresh(['Tickets'], load, 4_000, { client: h.client });
    await flush();
    h.current().emitChanges([{ entity: 'Payments' }]); // first message proves push works
    expect(h.client.isPushHealthy()).toBe(true);

    vi.advanceTimersByTime(SAFETY_NET_MS - 4_000);
    expect(load).not.toHaveBeenCalled();

    vi.advanceTimersByTime(8_000); // now past the safety-net period
    expect(load).toHaveBeenCalledTimes(1);
    stop();
  });

  it('keeps polling at the original cadence until push has been seen to work', async () => {
    // Connected, but the server has never sent anything (e.g. change capture not deployed yet).
    const h = createHarness();
    const load = vi.fn();
    const stop = startLiveRefresh(['Tickets'], load, 4_000, { client: h.client });
    await flush();

    expect(h.client.isLive()).toBe(true);
    expect(h.client.isPushHealthy()).toBe(false);
    vi.advanceTimersByTime(12_000);
    expect(load).toHaveBeenCalledTimes(3);
    stop();
  });

  it('has no blind spots registered now that Chunk 3 announces every bulk-SQL change', () => {
    expect(BULK_SQL_BLIND_SPOTS.size).toBe(0);
  });

  it('keeps the original cadence for a registered blind-spot table, even when push works', async () => {
    const h = createHarness();
    const load = vi.fn();
    const stop = startLiveRefresh(['SeatHolds'], load, 5_000, {
      client: h.client,
      blindSpots: new Set(['SeatHolds']),
    });
    await flush();
    h.current().emitChanges([{ entity: 'Payments' }]); // push demonstrably works
    expect(h.client.isPushHealthy()).toBe(true);

    vi.advanceTimersByTime(15_000);
    expect(load).toHaveBeenCalledTimes(3); // still polling every 5 s
    stop();
  });

  it('falls back to the original cadence while the hub is offline', async () => {
    const h = createHarness({ startError: { statusCode: 404 } });
    const load = vi.fn();
    const stop = startLiveRefresh(['Tickets'], load, 4_000, { client: h.client });
    await flush();

    vi.advanceTimersByTime(12_000);
    expect(load).toHaveBeenCalledTimes(3);
    stop();
  });

  it('stops everything on cleanup', async () => {
    const h = createHarness({ startError: { statusCode: 404 } });
    const load = vi.fn();
    const stop = startLiveRefresh(['Tickets'], load, 1_000, { client: h.client });
    await flush();
    stop();

    vi.advanceTimersByTime(10_000);
    expect(load).not.toHaveBeenCalled();
  });

  it('survives a load() that throws or rejects', async () => {
    const h = createHarness({ startError: { statusCode: 404 } });
    const load = vi.fn().mockRejectedValue(new Error('boom'));
    const stop = startLiveRefresh(['Tickets'], load, 1_000, { client: h.client });
    await flush();

    expect(() => vi.advanceTimersByTime(3_000)).not.toThrow();
    stop();
  });
});
