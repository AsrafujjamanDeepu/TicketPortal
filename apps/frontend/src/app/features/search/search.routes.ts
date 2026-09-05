import { Component } from '@angular/core';
import { Routes } from '@angular/router';
import { TpCardComponent, TpEmptyStateComponent } from '../../shared/ui';

/**
 * PIECE 2 STARTING POINT — Customer Portal: Search & Discovery.
 *
 * Replace this placeholder with: landing/hero search widget, results list
 * (ApiService.get<TripSearchResult[]>('trips/search', { fromTerminalId,
 * toTerminalId, date })), trip details, and the seat map + seat-hold flow
 * (POST 'seatholds'). See core/models/trip.model.ts and seat-hold.model.ts
 * for the exact response shapes already wired up for you.
 *
 * As this grows, feel free to split into search/home, search/results,
 * search/trip-details, search/seat-map subfolders — this single file is
 * just the starting stub.
 */
@Component({
  selector: 'tp-search-placeholder',
  standalone: true,
  imports: [TpCardComponent, TpEmptyStateComponent],
  template: `
    <div class="tp-page">
      <tp-card>
        <tp-empty-state
          title="Search & Discovery — Piece 2"
          message="Landing page, route search, results list, trip details, and seat map/hold go here. See the README for the exact endpoints to wire up."
        />
      </tp-card>
    </div>
  `,
})
export class SearchPlaceholderComponent {}

export const SEARCH_ROUTES: Routes = [
  {
    path: '',
    component: SearchPlaceholderComponent,
    title: 'Search Trips — TicketPortal',
  },
];
