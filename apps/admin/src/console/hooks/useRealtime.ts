// docs/02-Project-Concept-and-Solution.md (Real-time updates), Chunk 6 — React bindings for lib/realtime.ts.

import { useEffect, useRef, useSyncExternalStore } from 'react';
import {
  realtime,
  type EntityFilter,
  type RealtimeChange,
  type RealtimeClient,
  type RealtimeStatus,
} from '../lib/realtime';

export interface UseRealtimeOptions {
  /** Coalesce a burst of changes into one callback. Default 300 ms. */
  debounceMs?: number;
  /** Set false to pause the subscription (e.g. while a modal form is open). Default true. */
  enabled?: boolean;
  /** Test seam; the app always uses the shared connection. */
  client?: RealtimeClient;
}

/**
 * Run `callback` whenever one of `entities` (EF table names, or '*') changes on the server,
 * and once after every reconnect. The callback always sees the latest render's closure, so it
 * may freely use current state/props.
 *
 *   useRealtime(['Bookings', 'Payments'], () => load(true));
 */
export function useRealtime(
  entities: EntityFilter,
  callback: (changes: RealtimeChange[]) => void,
  options: UseRealtimeOptions = {},
): void {
  const { debounceMs = 300, enabled = true, client = realtime } = options;
  const callbackRef = useRef(callback);
  callbackRef.current = callback;

  const key = entities === '*' ? '*' : [...entities].sort().join('|');

  useEffect(() => {
    if (!enabled) return;
    return client.subscribe(entities, (changes) => callbackRef.current(changes), { debounceMs });
    // `entities` is represented by `key`, so an inline array literal does not resubscribe.
  }, [key, debounceMs, enabled, client]);
}

/** Live connection state for the badge. Holds the connection open while mounted. */
export function useRealtimeStatus(client: RealtimeClient = realtime): RealtimeStatus {
  useEffect(() => client.retain(), [client]);
  return useSyncExternalStore(client.onStatusChange, client.getStatus, client.getStatus);
}
