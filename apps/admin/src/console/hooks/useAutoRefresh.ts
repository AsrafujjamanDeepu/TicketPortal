import { useCallback, useEffect, useRef, useState } from "react";
import { startLiveRefresh, type EntityFilter } from "../lib/realtime";

/**
 * Load `fetcher` now and keep the result fresh.
 *
 * Without `entities` it polls every `intervalMs`, exactly as before. With `entities` (EF table
 * names, e.g. ["OperatorInvoices"]) a SignalR push triggers the reload instead, and
 * `intervalMs` becomes the fallback cadence used only while the hub is offline (the timer
 * is otherwise just a 60 s safety net - see startLiveRefresh in lib/realtime.ts).
 */
export function useAutoRefresh<T>(fetcher: () => Promise<T>, intervalMs = 5000, entities?: EntityFilter) {
  const [data, setData] = useState<T | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [lastSyncedAt, setLastSyncedAt] = useState<Date | null>(null);
  const timerRef = useRef<number | null>(null);
  const fetcherRef = useRef(fetcher);
  fetcherRef.current = fetcher;

  const load = useCallback(async (silent = false) => {
    if (!silent) setLoading(true);
    try {
      const res = await fetcherRef.current();
      setData(res);
      setError(null);
      setLastSyncedAt(new Date());
    } catch (e: any) {
      setError(e?.message ?? "Failed to load");
    } finally {
      setLoading(false);
    }
  }, []);

  const entitiesKey = entities === undefined ? null : entities === "*" ? "*" : [...entities].sort().join("|");

  useEffect(() => {
    load();
    if (entities !== undefined) {
      return startLiveRefresh(entities, () => load(true), intervalMs);
    }
    timerRef.current = window.setInterval(() => load(true), intervalMs);
    return () => {
      if (timerRef.current) window.clearInterval(timerRef.current);
    };
    // `entities` is tracked through `entitiesKey`, so an inline array does not restart the effect.
  }, [intervalMs, entitiesKey]);

  return { data, loading, error, lastSyncedAt, reload: load };
}
