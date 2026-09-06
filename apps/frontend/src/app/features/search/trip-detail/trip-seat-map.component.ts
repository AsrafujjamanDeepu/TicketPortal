import { CommonModule } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Trip, TripSearchResult, TripSeat } from '@ticketportal-mono/models';
import { ApiService } from '../../../core/services/api.service';
import {
  TpButtonDirective,
  TpCardComponent,
  TpEmptyStateComponent,
  TpSpinnerComponent,
} from '../../../shared/ui';
import { SearchApiService } from '../services/search-api.service';

interface FastNavState {
  searchResult?: TripSearchResult;
}

/**
 * Piece 2 — seat map + hold. Selecting seats here is purely local UI state; nothing is reserved
 * on the backend until "Hold Seats & Continue" calls SeatHoldsController.Create. On success we
 * hand off to Piece 3's checkout by holdToken alone (see booking/checkout/checkout-start.component.ts,
 * which looks the hold up again by that token) rather than passing the hold object through
 * router state, so a refresh mid-checkout still works.
 *
 * If a couple of seats get taken by someone else between opening this page and clicking Hold,
 * the backend replies 409 (ErrorInterceptor already toasts the message) — we just refetch the
 * trip so the grid reflects reality and drop any now-unavailable seats from the selection.
 */
@Component({
  selector: 'tp-trip-seat-map',
  standalone: true,
  imports: [
    CommonModule,
    RouterLink,
    TpButtonDirective,
    TpCardComponent,
    TpEmptyStateComponent,
    TpSpinnerComponent,
  ],
  template: `
    <div class="tp-page tp-seat-map-page">
      @if (loading()) {
        <tp-spinner />
      } @else if (!trip()) {
        <tp-empty-state
          title="Trip not found"
          message="This trip may have been removed or is no longer available."
        >
          <a routerLink="/search">
            <button tpButton variant="primary">Back to search</button>
          </a>
        </tp-empty-state>
      } @else {
        <a class="tp-back-link" [routerLink]="['/search/results']" [queryParams]="backQueryParams">
          ← Back to results
        </a>

        <tp-card class="tp-trip-header">
          <div>
            <h2>{{ operatorName() }}</h2>
            <p class="tp-muted">
              {{ departureTerminalName() }} → {{ arrivalTerminalName() }} ·
              {{ trip()!.departureTimeUtc | date: 'medium' }}
            </p>
          </div>
          <span class="tp-muted tp-trip-code">Trip {{ trip()!.tripCode }}</span>
        </tp-card>

        <tp-card>
          <div class="tp-seat-legend">
            <span class="tp-legend-item"><span class="tp-seat-swatch tp-seat-swatch--available"></span>Available</span>
            <span class="tp-legend-item"><span class="tp-seat-swatch tp-seat-swatch--selected"></span>Selected</span>
            <span class="tp-legend-item"><span class="tp-seat-swatch tp-seat-swatch--taken"></span>Unavailable</span>
          </div>

          <div class="tp-seat-grid">
            @for (seat of trip()!.tripSeats; track seat.id) {
              <button
                type="button"
                class="tp-seat"
                [class.tp-seat--selected]="isSelected(seat)"
                [class.tp-seat--taken]="seat.status !== 'Available' && !isSelected(seat)"
                [disabled]="seat.status !== 'Available' && !isSelected(seat)"
                (click)="toggleSeat(seat)"
                [title]="seat.seatType + ' · ' + trip()!.currency + ' ' + seat.fare"
              >
                {{ seat.seatNumber }}
              </button>
            }
          </div>
        </tp-card>

        <tp-card class="tp-seat-summary">
          <div>
            <p class="tp-muted">Selected seats</p>
            <p class="tp-seat-summary__seats">
              @if (selectedSeatIds().length === 0) {
                None yet
              } @else {
                {{ selectedSeatNumbers().join(', ') }}
              }
            </p>
          </div>
          <div class="tp-seat-summary__total">
            <p class="tp-muted">Total fare</p>
            <p class="tp-seat-summary__amount">{{ trip()!.currency }} {{ selectedFareTotal() }}</p>
          </div>
          <button
            tpButton
            variant="primary"
            size="lg"
            [disabled]="selectedSeatIds().length === 0 || holding()"
            (click)="holdSeats()"
          >
            {{ holding() ? 'Holding…' : 'Hold Seats & Continue' }}
          </button>
        </tp-card>
      }
    </div>
  `,
  styles: [
    `
      .tp-seat-map-page {
        display: flex;
        flex-direction: column;
        gap: 18px;
        padding-top: 20px;
      }

      .tp-back-link {
        font-size: 13px;
        color: var(--tp-text-muted);
        text-decoration: none;
        width: fit-content;
      }

      .tp-back-link:hover {
        color: var(--tp-text);
      }

      .tp-trip-header {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 12px;
        flex-wrap: wrap;
      }

      .tp-trip-code {
        font-size: 13px;
      }

      .tp-seat-legend {
        display: flex;
        gap: 20px;
        margin-bottom: 18px;
        font-size: 13px;
        color: var(--tp-text-muted);
      }

      .tp-legend-item {
        display: flex;
        align-items: center;
        gap: 6px;
      }

      .tp-seat-swatch {
        width: 14px;
        height: 14px;
        border-radius: 4px;
        display: inline-block;
        border: 1px solid var(--tp-border);
      }

      .tp-seat-swatch--available {
        background: var(--tp-surface);
      }

      .tp-seat-swatch--selected {
        background: var(--tp-yellow);
        border-color: var(--tp-yellow-dark);
      }

      .tp-seat-swatch--taken {
        background: var(--tp-surface-alt);
      }

      .tp-seat-grid {
        display: flex;
        flex-wrap: wrap;
        gap: 10px;
      }

      .tp-seat {
        width: 56px;
        height: 44px;
        border-radius: 8px;
        border: 1px solid var(--tp-border);
        background: var(--tp-surface);
        color: var(--tp-text);
        font-weight: 600;
        font-size: 13px;
        cursor: pointer;
        transition: transform var(--tp-transition-fast);
      }

      .tp-seat:hover:not(:disabled) {
        border-color: var(--tp-yellow-dark);
        transform: translateY(-1px);
      }

      .tp-seat--selected {
        background: var(--tp-yellow);
        border-color: var(--tp-yellow-dark);
        color: var(--tp-text-on-yellow);
      }

      .tp-seat--taken {
        background: var(--tp-surface-alt);
        color: var(--tp-text-muted);
        cursor: not-allowed;
        text-decoration: line-through;
      }

      .tp-seat-summary {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 20px;
        flex-wrap: wrap;
        position: sticky;
        bottom: 16px;
        box-shadow: var(--tp-shadow-elevated);
      }

      .tp-seat-summary__seats {
        font-weight: 600;
      }

      .tp-seat-summary__amount {
        font-weight: 700;
        font-size: 20px;
      }
    `,
  ],
})
export class TripSeatMapComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly api = inject(ApiService);
  private readonly searchApi = inject(SearchApiService);

  protected readonly loading = signal(true);
  protected readonly holding = signal(false);
  protected readonly trip = signal<Trip | null>(null);
  protected readonly operatorName = signal('');
  protected readonly operatorLogoUrl = signal<string | null>(null);
  protected readonly departureTerminalName = signal('');
  protected readonly arrivalTerminalName = signal('');
  protected readonly selectedSeatIds = signal<string[]>([]);
  protected readonly backQueryParams = this.route.snapshot.queryParams;

  protected readonly selectedFareTotal = computed(() => {
    const trip = this.trip();
    if (!trip) return 0;
    return this.selectedSeatIds().reduce(
      (sum, id) => sum + (trip.tripSeats.find((s) => s.id === id)?.fare ?? 0),
      0,
    );
  });

  protected readonly selectedSeatNumbers = computed(() => {
    const trip = this.trip();
    if (!trip) return [] as string[];
    return this.selectedSeatIds()
      .map((id) => trip.tripSeats.find((s) => s.id === id)?.seatNumber)
      .filter((n): n is string => !!n);
  });

  ngOnInit(): void {
    const tripId = this.route.snapshot.paramMap.get('tripId');
    if (!tripId) {
      this.loading.set(false);
      return;
    }

    // history.state (not Router.getCurrentNavigation(), which is only non-null DURING
    // navigation) carries whatever `state` object SearchResultsComponent.selectTrip passed —
    // this is the fast path that avoids three extra lookups just to render the header.
    const fastState = history.state as FastNavState;
    const hasFastState = !!fastState?.searchResult && fastState.searchResult.tripId === tripId;

    if (hasFastState) {
      const result = fastState.searchResult!;
      this.operatorName.set(result.busOperatorName);
      this.operatorLogoUrl.set(this.api.resolveAssetUrl(result.busOperatorLogoUrl));
      this.departureTerminalName.set(result.departureTerminalName);
      this.arrivalTerminalName.set(result.arrivalTerminalName);
    }

    this.loadTrip(tripId, !hasFastState);
  }

  isSelected(seat: TripSeat): boolean {
    return this.selectedSeatIds().includes(seat.id);
  }

  toggleSeat(seat: TripSeat): void {
    if (seat.status !== 'Available' && !this.isSelected(seat)) return;
    this.selectedSeatIds.update((ids) =>
      ids.includes(seat.id) ? ids.filter((id) => id !== seat.id) : [...ids, seat.id],
    );
  }

  holdSeats(): void {
    const trip = this.trip();
    const seatIds = this.selectedSeatIds();
    if (!trip || seatIds.length === 0) return;

    this.holding.set(true);
    this.searchApi.holdSeats({ tripId: trip.id, tripSeatIds: seatIds }).subscribe({
      next: (hold) => {
        this.holding.set(false);
        this.router.navigate(['/my-bookings/checkout/start'], {
          queryParams: { holdToken: hold.holdToken },
        });
      },
      error: () => {
        this.holding.set(false);
        // ErrorInterceptor already toasted the backend's message (e.g. which seat numbers
        // were just taken) — refresh the seat map so the grid matches reality again.
        this.selectedSeatIds.set([]);
        this.loadTrip(trip.id, false);
      },
    });
  }

  private loadTrip(tripId: string, needsHeaderNames: boolean): void {
    this.loading.set(true);
    if (needsHeaderNames) {
      this.searchApi.loadTripHeaderContext(tripId).subscribe({
        next: (ctx) => {
          this.trip.set(ctx.trip);
          this.operatorName.set(ctx.operatorName);
          this.operatorLogoUrl.set(this.api.resolveAssetUrl(ctx.operatorLogoUrl));
          this.departureTerminalName.set(ctx.departureTerminalName);
          this.arrivalTerminalName.set(ctx.arrivalTerminalName);
          this.loading.set(false);
        },
        error: () => {
          this.trip.set(null);
          this.loading.set(false);
        },
      });
    } else {
      this.searchApi.getTrip(tripId).subscribe({
        next: (trip) => {
          this.trip.set(trip);
          this.loading.set(false);
        },
        error: () => {
          this.trip.set(null);
          this.loading.set(false);
        },
      });
    }
  }
}
