import { DestroyRef } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RealtimeService } from './realtime.service';
import { runSilently } from './silent-request';

export interface LiveRefreshOptions {
  /** Coalescing window in ms (default 300) — see RealtimeService.watch. */
  debounceMs?: number;
  /**
   * Return true to skip a reload right now, e.g. while the screen is mid-way through something a re-fetch
   * would disturb. A skipped signal is dropped, not queued: the next change (or reconnect) refreshes again.
   */
  skipWhile?: () => boolean;
}

/**
 * One line per screen: re-run `reload` whenever one of `entities` changes on the server, until the
 * component is destroyed.
 *
 *   liveRefresh(this.destroyRef, this.realtime, ['Bookings', 'Payments'], () => this.load(true));
 *
 * `reload` should refresh lists in place and stay quiet — no full-screen spinner (the `silent` flag on the
 * screen's load method) and no touching of forms or open modals. It also runs once after any reconnect.
 *
 * `reload` runs inside `runSilently()`, so the global loading bar and the error toasts ignore the requests it
 * makes (see ./silent-request.ts).
 */
export function liveRefresh(
  destroyRef: DestroyRef,
  realtime: RealtimeService,
  entities: readonly string[],
  reload: () => void,
  options: LiveRefreshOptions = {},
): void {
  realtime
    .watch(entities, { debounceMs: options.debounceMs })
    .pipe(takeUntilDestroyed(destroyRef))
    .subscribe(() => {
      if (options.skipWhile?.()) return;
      runSilently(reload);
    });
}

/**
 * The seat-map flavour of `liveRefresh`: joins the trip's group on the server while the component lives,
 * and re-runs `reload` whenever a seat on that trip is taken, held or freed. Anonymous visitors may use
 * it — the seat map is public, and the server only ever sends them the `SeatAvailability` signal.
 *
 *   liveSeats(this.destroyRef, this.realtime, tripId, () => this.refreshSeats(tripId));
 */
export function liveSeats(
  destroyRef: DestroyRef,
  realtime: RealtimeService,
  tripId: string,
  reload: () => void,
  options: LiveRefreshOptions = {},
): void {
  realtime
    .watchSeats(tripId, { debounceMs: options.debounceMs })
    .pipe(takeUntilDestroyed(destroyRef))
    .subscribe(() => {
      if (options.skipWhile?.()) return;
      runSilently(reload);
    });
}
