import { startLiveRefresh } from '@/lib/realtime';
import { api } from '@/lib/api';

// Real backend contract (ActivityLogsController):
//   GET api/ActivityLogs        [Authorize, Admin/Staff only] -> ActivityLogResponseDto[]
//     Anyone else gets an EMPTY ARRAY back, not a 403 — see the controller's GetAll.
//   GET api/ActivityLogs/{id}   [Authorize, Admin/Staff only] -> 403 otherwise
// Read-only, no POST/PUT/DELETE anywhere: a trail table is never client-writable. Nothing in
// the codebase writes here yet either (flagged as follow-up work in the controller comment) —
// so an empty list here is expected, not a bug, until that instrumentation lands.

export interface ActivityLogResponseDto {
  id: string;
  userId?: string | null;
  action: string;
  entityName?: string | null;
  entityId?: string | null;
  ipAddress?: string | null;
  createdAtUtc: string;
  updatedAtUtc?: string | null;
  rowVersion: string;
}

const POLL_INTERVAL_MS = 15000;

/** GET api/ActivityLogs — empty array (not a throw) if the caller isn't Admin/Staff. */
export async function getAllActivityLogs(): Promise<ActivityLogResponseDto[]> {
  const res = await api.get('api/ActivityLogs');
  return res.data || [];
}

/** GET api/ActivityLogs/{id} — 403/404 propagate as thrown errors. */
export async function getActivityLogById(id: string): Promise<ActivityLogResponseDto | undefined> {
  try {
    const res = await api.get(`api/ActivityLogs/${id}`);
    return res.data;
  } catch (err: any) {
    const status = err?.status ?? err?.response?.status;
    if (status === 404) return undefined;
    throw err;
  }
}

/**
 * Live feed: reloads on SignalR push (ActivityLogs) as new entries land; POLL_INTERVAL_MS is
 * only the fallback while the hub is offline. Returns an unsubscribe function.
 */
export function subscribeToActivityLogPolling(onData: (list: ActivityLogResponseDto[]) => void): () => void {
  let cancelled = false;
  const tick = async () => {
    try {
      const data = await getAllActivityLogs();
      if (!cancelled) onData(data);
    } catch {
      // transient poll errors are silently retried on the next tick
    }
  };
  const stopLive = startLiveRefresh(['ActivityLogs'], tick, POLL_INTERVAL_MS);
  return () => {
    cancelled = true;
    stopLive();
  };
}
