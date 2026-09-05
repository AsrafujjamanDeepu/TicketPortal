import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import { ApiService } from '../../../core/services/api.service';
import {
  Schedule,
  ScheduleCreateRequest,
  ScheduleUpdateRequest,
  Trip,
  TripCreateRequest,
  TripStatusHistory,
  TripUpdateRequest,
} from '@ticketportal-mono/models';

/** Screen 4 (Trips & Scheduling). See TripsController + SchedulesController + TripStatusHistoriesController. */
@Injectable({ providedIn: 'root' })
export class TripsService {
  private readonly api = inject(ApiService);

  /** Trips.GetAll is unscoped — always returns every operator's trips — filter client-side. */
  listTrips(busOperatorId: string): Observable<Trip[]> {
    return this.api.get<Trip[]>('trips').pipe(map((trips) => trips.filter((t) => t.busOperatorId === busOperatorId)));
  }

  getTrip(id: string): Observable<Trip> {
    return this.api.get<Trip>(`trips/${id}`);
  }

  createTrip(dto: TripCreateRequest): Observable<Trip> {
    return this.api.post<Trip>('trips', dto);
  }

  /** Also used for pure status-change actions (delay/cancel/depart/arrive) — build the payload
   * from the currently-loaded Trip (tripSeats included unchanged) with just status/delayReason
   * edited; the backend logs a TripStatusHistory row automatically on any status change. */
  updateTrip(id: string, dto: TripUpdateRequest): Observable<Trip> {
    return this.api.put<Trip>(`trips/${id}`, dto);
  }

  deleteTrip(id: string): Observable<void> {
    return this.api.delete<void>(`trips/${id}`);
  }

  uploadTripCoverImage(id: string, file: File): Observable<{ imageUrl: string }> {
    return this.api.postForm<{ imageUrl: string }>(`trips/${id}/images`, file);
  }

  /** Read-only, auto-scoped server-side — filter client-side to one trip for its timeline. */
  listStatusHistory(tripId: string): Observable<TripStatusHistory[]> {
    return this.api.get<TripStatusHistory[]>('tripstatushistories').pipe(map((h) => h.filter((x) => x.tripId === tripId)));
  }

  listSchedules(busOperatorId: string): Observable<Schedule[]> {
    return this.api
      .get<Schedule[]>('schedules')
      .pipe(map((schedules) => schedules.filter((s) => s.busOperatorId === busOperatorId)));
  }

  createSchedule(dto: ScheduleCreateRequest): Observable<Schedule> {
    return this.api.post<Schedule>('schedules', dto);
  }

  updateSchedule(id: string, dto: ScheduleUpdateRequest): Observable<Schedule> {
    return this.api.put<Schedule>(`schedules/${id}`, dto);
  }

  deleteSchedule(id: string): Observable<void> {
    return this.api.delete<void>(`schedules/${id}`);
  }
}
