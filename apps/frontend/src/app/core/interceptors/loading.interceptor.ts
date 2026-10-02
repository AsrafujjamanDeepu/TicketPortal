import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { finalize } from 'rxjs';
import { isSilentRequest } from '../realtime/silent-request';
import { LoadingService } from '../services/loading.service';

/**
 * Increments/decrements LoadingService's counter around every request so
 * the top-of-page progress bar in ShellComponent "just works" for every
 * feature module without them wiring up their own spinners for basic
 * page-level loads. Use a local loading flag instead for anything more
 * granular (e.g. a single button's own busy state).
 *
 * Background refreshes pushed by SignalR (see core/realtime/silent-request.ts) are skipped: they would
 * otherwise flash the bar every time anyone, anywhere, changes a row this screen shows.
 */
export const loadingInterceptor: HttpInterceptorFn = (req, next) => {
  if (isSilentRequest(req)) return next(req);

  const loading = inject(LoadingService);
  loading.start();
  return next(req).pipe(finalize(() => loading.stop()));
};
