// @vitest-environment jsdom
import { act, createElement, useState } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createHarness, flush, type Harness } from '../lib/realtime.testkit';
import { useRealtime, useRealtimeStatus } from './useRealtime';

// REALTIME_SIGNALR_PLAN.md, Chunk 7: the React hooks against a mocked connection.

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

let container: HTMLDivElement;
let root: Root;
let harness: Harness;

beforeEach(() => {
  vi.useFakeTimers();
  harness = createHarness();
  container = document.createElement('div');
  document.body.appendChild(container);
  root = createRoot(container);
});

afterEach(() => {
  act(() => root.unmount());
  container.remove();
  vi.useRealTimers();
});

describe('useRealtime', () => {
  it('calls the latest callback when a subscribed entity changes', async () => {
    const calls: number[] = [];

    function Probe() {
      const [n, setN] = useState(0);
      useRealtime(['Bookings'], () => {
        calls.push(n); // proves the callback is never stale
        setN((x) => x + 1);
      }, { client: harness.client, debounceMs: 10 });
      return createElement('span', null, String(n));
    }

    await act(async () => {
      root.render(createElement(Probe));
      await flush();
    });

    await act(async () => {
      harness.current().emitChanges([{ entity: 'Bookings' }]);
      vi.advanceTimersByTime(10);
    });
    await act(async () => {
      harness.current().emitChanges([{ entity: 'Bookings' }]);
      vi.advanceTimersByTime(10);
    });

    expect(calls).toEqual([0, 1]);
    expect(container.textContent).toBe('2');
  });

  it('ignores other entities and does not resubscribe for an inline array literal', async () => {
    const cb = vi.fn();
    function Probe() {
      const [, force] = useState(0);
      useRealtime(['Bookings', 'Tickets'], cb, { client: harness.client, debounceMs: 0 });
      return createElement('button', { onClick: () => force((x) => x + 1) });
    }
    await act(async () => {
      root.render(createElement(Probe));
      await flush();
    });
    const connectionsBefore = harness.connections.length;

    await act(async () => {
      container.querySelector('button')!.click(); // re-render with a new array literal
    });
    await act(async () => {
      harness.current().emitChanges([{ entity: 'Payments' }]);
      vi.advanceTimersByTime(5);
    });

    expect(cb).not.toHaveBeenCalled();
    expect(harness.connections.length).toBe(connectionsBefore); // no reconnect churn
    expect(harness.current().stopCalls).toBe(0);
  });

  it('stops listening and closes the connection on unmount', async () => {
    const cb = vi.fn();
    function Probe() {
      useRealtime(['Bookings'], cb, { client: harness.client, debounceMs: 0 });
      return null;
    }
    await act(async () => {
      root.render(createElement(Probe));
      await flush();
    });
    const conn = harness.current();

    await act(async () => {
      root.render(null);
      await flush();
    });
    conn.emitChanges([{ entity: 'Bookings' }]);
    vi.advanceTimersByTime(5);

    expect(cb).not.toHaveBeenCalled();
    expect(conn.stopCalls).toBe(1);
  });

  it('does nothing while disabled', async () => {
    function Probe() {
      useRealtime(['Bookings'], vi.fn(), { client: harness.client, enabled: false });
      return null;
    }
    await act(async () => {
      root.render(createElement(Probe));
      await flush();
    });
    expect(harness.connections).toHaveLength(0);
  });
});

describe('useRealtimeStatus', () => {
  it('tracks live -> reconnecting -> live', async () => {
    function Probe() {
      return createElement('span', null, useRealtimeStatus(harness.client));
    }
    await act(async () => {
      root.render(createElement(Probe));
      await flush();
    });
    expect(container.textContent).toBe('live');

    await act(async () => {
      harness.current().reconnecting.forEach((fn) => fn());
    });
    expect(container.textContent).toBe('reconnecting');

    await act(async () => {
      harness.current().reconnected.forEach((fn) => fn());
    });
    expect(container.textContent).toBe('live');
  });

  it('shows offline when the hub is disabled (kill switch)', async () => {
    harness = createHarness({ startError: { statusCode: 404 } });
    function Probe() {
      return createElement('span', null, useRealtimeStatus(harness.client));
    }
    await act(async () => {
      root.render(createElement(Probe));
      await flush();
    });
    expect(container.textContent).toBe('offline');
  });
});
