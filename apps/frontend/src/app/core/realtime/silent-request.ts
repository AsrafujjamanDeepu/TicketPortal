import { HttpContextToken, HttpRequest } from '@angular/common/http';

/**
 * Marks an HTTP request as a background refresh. The loading interceptor then skips the global
 * top-of-page progress bar for it, and the error interceptor stays quiet about a failure (a failed
 * 401 still logs the user out — an expired session matters even when nobody clicked).
 *
 * Normally you never set this yourself: `liveRefresh()` / `liveSeats()` run every reload inside
 * `runSilently()`, which marks all requests that reload makes — including the ones it starts later
 * from inside a response callback. Set the token explicitly only for a request made outside those
 * helpers:
 *
 *   this.http.get(url, { context: new HttpContext().set(SILENT_REQUEST, true) })
 */
export const SILENT_REQUEST = new HttpContextToken<boolean>(() => false);

const ZONE_KEY = 'tpSilentRefresh';

interface ZoneLike {
  fork(spec: { name: string; properties: Record<string, unknown> }): { run<T>(fn: () => T): T };
  get(key: string): unknown;
}

function currentZone(): ZoneLike | undefined {
  return (globalThis as { Zone?: { current: ZoneLike } }).Zone?.current;
}

/**
 * Runs `fn` so that every HTTP request it starts — now, or later from a promise / subscription / timer
 * callback it set up — counts as a silent background refresh (see SILENT_REQUEST).
 *
 * Why a zone: a screen's reload usually calls services that call `HttpClient` several layers down (some
 * chain a second request from inside the first one's response), so there is no single place to attach a
 * flag to each request. Zone.js (already part of this app — provideZoneChangeDetection) carries a value
 * along an async call chain, which is exactly what is needed. Without Zone.js this just runs `fn`: the
 * only effect is that a live refresh would then show the progress bar.
 */
export function runSilently<T>(fn: () => T): T {
  const zone = currentZone();
  if (!zone) return fn();
  return zone.fork({ name: 'tp-silent-refresh', properties: { [ZONE_KEY]: true } }).run(fn);
}

/** True for a request tagged with SILENT_REQUEST, or one started inside `runSilently()`. */
export function isSilentRequest(req: HttpRequest<unknown>): boolean {
  return req.context.get(SILENT_REQUEST) || currentZone()?.get(ZONE_KEY) === true;
}
