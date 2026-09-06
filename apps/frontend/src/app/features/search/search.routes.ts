import { Routes } from '@angular/router';
import { authGuard } from '../../core/guards/auth.guard';

/**
 * Piece 2 — Customer Portal: Search & Discovery.
 *
 * Every backend endpoint this feature calls (TerminalsController, TripsController,
 * SeatHoldsController) is [Authorize]'d with no anonymous override — there's no real
 * "browse without an account" mode to build here, including the home screen's terminal
 * picker. So the whole feature sits behind authGuard rather than rendering a page that can't
 * actually load anything until the first failed request bounces the user to /auth/login
 * anyway. Any authenticated role can search (not Customer-only) — the backend doesn't
 * restrict TripsController.Search by role either.
 */
export const SEARCH_ROUTES: Routes = [
  {
    path: '',
    canActivate: [authGuard],
    children: [
      {
        path: '',
        loadComponent: () =>
          import('./home/search-home.component').then((m) => m.SearchHomeComponent),
        title: 'Search Trips — TicketPortal',
      },
      {
        path: 'results',
        loadComponent: () =>
          import('./results/search-results.component').then((m) => m.SearchResultsComponent),
        title: 'Search Results — TicketPortal',
      },
      {
        path: 'trip/:tripId',
        loadComponent: () =>
          import('./trip-detail/trip-seat-map.component').then((m) => m.TripSeatMapComponent),
        title: 'Select Seats — TicketPortal',
      },
    ],
  },
];
