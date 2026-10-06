import { Injectable, inject } from '@angular/core';
import { EMPTY, Observable, expand, reduce } from 'rxjs';
import { ApiService } from '../../../core/services/api.service';
import { Booking } from '@ticketportal-mono/models';

/** The page envelope GET /api/bookings returns when ?page=/?pageSize= is sent (C7-3). */
interface BookingsPage {
  items: Booking[];
  page: number;
  hasNextPage: boolean;
}

// The API caps pageSize at 100. Safety stop so a runaway loop can never hammer the server:
// 50 pages x 100 = 5,000 bookings, far beyond one operator's desk.
const PAGE_SIZE = 100;
const MAX_PAGES = 50;

/**
 * GET /api/bookings — already scoped server-side to the caller's own
 * operator for Staff/Operator (BookingsController.GetAll). Used to resolve
 * a bare bookingId into something a staff member can actually recognize
 * (PNR, contact name) on the cancellations/refunds desk and the complaints
 * screen — there's no dedicated "lookup one booking by id" list endpoint,
 * so this reads the whole in-scope list once and the caller indexes it.
 *
 * C7-3: an unpaged GET /api/bookings is now capped at the newest 200 rows, which would silently
 * drop older bookings from this index. So the list is read page by page (100 per request, newest
 * first, stable order) until the API says there is no next page.
 */
@Injectable({ providedIn: 'root' })
export class BookingsLookupService {
  private readonly api = inject(ApiService);

  list(): Observable<Booking[]> {
    const fetchPage = (page: number) =>
      this.api.get<BookingsPage>('bookings', { page, pageSize: PAGE_SIZE });

    return fetchPage(1).pipe(
      expand((result) =>
        result.hasNextPage && result.page < MAX_PAGES ? fetchPage(result.page + 1) : EMPTY,
      ),
      reduce((all, result) => all.concat(result.items), [] as Booking[]),
    );
  }
}
