import { Injectable, inject } from '@angular/core';
import { Observable, forkJoin, map, of, switchMap } from 'rxjs';
import {
  BusOperator,
  SeatHold,
  SeatHoldCreateRequest,
  Terminal,
  Trip,
  TripSearchQuery,
  TripSearchResult,
} from '@ticketportal-mono/models';
import { ApiService } from '../../../core/services/api.service';

// Refresh-safe fallback for the seat-map header — see loadTripHeaderContext below.
export interface TripHeaderContext {
  trip: Trip;
  operatorName: string;
  operatorLogoUrl: string | null;
  departureTerminalName: string;
  arrivalTerminalName: string;
}

/**
 * Piece 2 — Customer Portal: Search & Discovery. Thin wrapper over the real backend surface
 * this feature owns:
 *   - TerminalsController.GetAll  — the from/to picker on the home screen
 *   - TripsController.Search      — the results list
 *   - TripsController.GetById     — the seat map (full TripSeats array)
 *   - SeatHoldsController.Create  — "Hold Seats", which hands off to Piece 3's checkout via
 *     holdToken (see booking/checkout/checkout-start.component.ts)
 *
 * Every one of these is [Authorize]'d on the backend with no anonymous override, which is why
 * search.routes.ts guards this whole feature rather than pretending there's a logged-out
 * browsing mode.
 */
@Injectable({ providedIn: 'root' })
export class SearchApiService {
  private readonly api = inject(ApiService);

  terminals(): Observable<Terminal[]> {
    return this.api.get<Terminal[]>('terminals');
  }

  searchTrips(query: TripSearchQuery): Observable<TripSearchResult[]> {
    return this.api.get<TripSearchResult[]>('trips/search', {
      fromTerminalId: query.fromTerminalId,
      toTerminalId: query.toTerminalId,
      date: query.date,
      minAvailableSeats: query.minAvailableSeats,
    });
  }

  getTrip(tripId: string): Observable<Trip> {
    return this.api.get<Trip>(`trips/${tripId}`);
  }

  /**
   * The normal path into the seat map is clicking a result card, which already has the
   * operator/terminal names on it (TripSearchResult) — TripSeatMapComponent passes that via
   * router navigation `state` so no extra calls are needed. This is only the fallback for a
   * hard refresh or a bookmarked/shared link straight to a trip, where that state is gone and
   * all we have is the id in the URL.
   */
  loadTripHeaderContext(tripId: string): Observable<TripHeaderContext> {
    return this.getTrip(tripId).pipe(
      switchMap((trip) =>
        forkJoin({
          trip: of(trip),
          operator: this.api.get<BusOperator>(`busoperators/${trip.busOperatorId}`),
          from: this.api.get<Terminal>(`terminals/${trip.departureTerminalId}`),
          to: this.api.get<Terminal>(`terminals/${trip.arrivalTerminalId}`),
        }),
      ),
      map(({ trip, operator, from, to }) => ({
        trip,
        operatorName: operator.name,
        operatorLogoUrl: operator.logoUrl ?? null,
        departureTerminalName: from.name,
        arrivalTerminalName: to.name,
      })),
    );
  }

  holdSeats(request: SeatHoldCreateRequest): Observable<SeatHold> {
    return this.api.post<SeatHold>('seatholds', request);
  }
}
