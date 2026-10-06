// C7-3: the page envelope returned by GET /api/Bookings and GET /api/Tickets when ?page= and/or
// ?pageSize= is sent (see apps/api DTO/PagedResult.cs). Without those parameters the API still
// returns a plain, capped array - the list pages below always ask for a page explicitly.
export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
  hasNextPage: boolean;
  hasPreviousPage: boolean;
}

/** The API caps pageSize at 100; keep requests at or below it. */
export const MAX_PAGE_SIZE = 100;
