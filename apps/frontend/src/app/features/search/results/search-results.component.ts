import { CommonModule } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { Terminal, TripSearchResult } from '@ticketportal-mono/models';
import { ApiService } from '../../../core/services/api.service';
import {
  TpButtonDirective,
  TpCardComponent,
  TpEmptyStateComponent,
  TpSpinnerComponent,
} from '../../../shared/ui';
import { SearchApiService } from '../services/search-api.service';

type SortMode = 'departure' | 'price';

/**
 * Piece 2 — results list. The URL's query params (fromTerminalId/toTerminalId/date) are the
 * source of truth for what was searched, not component state, so this page works from a
 * refresh, a shared link, or the browser back button, and the small "edit search" bar re-runs
 * the search in place instead of bouncing back to the home screen.
 */
@Component({
  selector: 'tp-search-results',
  standalone: true,
  imports: [
    CommonModule,
    ReactiveFormsModule,
    TpCardComponent,
    TpButtonDirective,
    TpEmptyStateComponent,
    TpSpinnerComponent,
  ],
  template: `
    <div class="tp-page tp-search-results">
      <tp-card class="tp-edit-search">
        <form [formGroup]="form" (ngSubmit)="editSearch()" class="tp-edit-search-form">
          <label class="tp-field">
            <span>From</span>
            <select formControlName="fromTerminalId">
              @for (t of terminals(); track t.id) {
                <option [value]="t.id">{{ t.name }} — {{ t.city }}</option>
              }
            </select>
          </label>
          <label class="tp-field">
            <span>To</span>
            <select formControlName="toTerminalId">
              @for (t of terminals(); track t.id) {
                <option [value]="t.id">{{ t.name }} — {{ t.city }}</option>
              }
            </select>
          </label>
          <label class="tp-field">
            <span>Date</span>
            <input type="date" formControlName="date" />
          </label>
          <button tpButton variant="secondary" type="submit" [disabled]="form.invalid">
            Update Search
          </button>
        </form>
      </tp-card>

      @if (loading()) {
        <tp-spinner />
      } @else if (!searched()) {
        <tp-empty-state
          title="Start a search"
          message="Pick a from/to terminal and a date above to see available buses."
        />
      } @else if (sortedResults().length === 0) {
        <tp-empty-state
          title="No buses found"
          message="No trips matched that route and date. Try a nearby date or a different terminal."
        />
      } @else {
        <div class="tp-results-toolbar">
          <p class="tp-muted">{{ sortedResults().length }} bus(es) found</p>
          <label class="tp-sort-field">
            Sort by
            <select [value]="sortBy()" (change)="setSort($any($event.target).value)">
              <option value="departure">Departure time</option>
              <option value="price">Lowest fare</option>
            </select>
          </label>
        </div>

        <div class="tp-trip-list">
          @for (trip of sortedResults(); track trip.tripId) {
            <tp-card class="tp-trip-card" [hoverable]="true">
              <div class="tp-trip-card__main">
                <div class="tp-trip-card__operator">
                  @if (logoUrl(trip); as logo) {
                    <img [src]="logo" [alt]="trip.busOperatorName" class="tp-trip-card__logo" />
                  }
                  <div>
                    <div class="tp-trip-card__operator-name">{{ trip.busOperatorName }}</div>
                    <div class="tp-muted tp-trip-card__bus">
                      {{ trip.busType }} @if (trip.busModel) {, {{ trip.busModel }}}
                    </div>
                  </div>
                </div>

                <div class="tp-trip-card__route">
                  <div class="tp-trip-card__time-block">
                    <span class="tp-trip-card__time">{{ trip.departureTimeUtc | date: 'h:mm a' }}</span>
                    <span class="tp-muted">{{ trip.departureTerminalName }}</span>
                  </div>
                  <div class="tp-trip-card__duration">
                    <span class="tp-muted">{{ durationLabel(trip) }}</span>
                    <div class="tp-trip-card__line"></div>
                  </div>
                  <div class="tp-trip-card__time-block">
                    <span class="tp-trip-card__time">{{ trip.arrivalTimeUtc | date: 'h:mm a' }}</span>
                    <span class="tp-muted">{{ trip.arrivalTerminalName }}</span>
                  </div>
                </div>

                <div class="tp-trip-card__amenities">
                  @if (trip.hasWifi) {
                    <span class="tp-chip">Wifi</span>
                  }
                  @if (trip.hasToilet) {
                    <span class="tp-chip">Toilet</span>
                  }
                  @if (trip.isWheelchairAccessible) {
                    <span class="tp-chip">Wheelchair accessible</span>
                  }
                </div>
              </div>

              <div class="tp-trip-card__side">
                <div class="tp-trip-card__fare">
                  @if (trip.lowestAvailableFare !== null) {
                    <span class="tp-trip-card__price">{{ trip.currency }} {{ trip.lowestAvailableFare }}</span>
                  } @else {
                    <span class="tp-muted">Sold out</span>
                  }
                  <span class="tp-muted">{{ trip.availableSeatCount }} / {{ trip.totalSeatCount }} seats left</span>
                </div>
                <button
                  tpButton
                  variant="primary"
                  [disabled]="trip.availableSeatCount === 0"
                  (click)="selectTrip(trip)"
                >
                  Select Seats
                </button>
              </div>
            </tp-card>
          }
        </div>
      }
    </div>
  `,
  styles: [
    `
      .tp-search-results {
        display: flex;
        flex-direction: column;
        gap: 20px;
        padding-top: 24px;
      }

      .tp-edit-search-form {
        display: grid;
        grid-template-columns: 1fr 1fr auto auto;
        gap: 12px;
        align-items: end;
      }

      .tp-field {
        display: flex;
        flex-direction: column;
        gap: 6px;
        font-size: 13px;
        font-weight: 600;
        color: var(--tp-text-muted);
      }

      .tp-field select,
      .tp-field input {
        border: 1px solid var(--tp-border);
        border-radius: 8px;
        padding: 9px 10px;
        font-size: 14px;
        font-family: var(--tp-font-body);
        color: var(--tp-text);
        background: var(--tp-surface);
      }

      .tp-results-toolbar {
        display: flex;
        align-items: center;
        justify-content: space-between;
      }

      .tp-sort-field {
        display: flex;
        align-items: center;
        gap: 8px;
        font-size: 13px;
        color: var(--tp-text-muted);
      }

      .tp-sort-field select {
        border: 1px solid var(--tp-border);
        border-radius: 8px;
        padding: 6px 8px;
        font-size: 13px;
      }

      .tp-trip-list {
        display: flex;
        flex-direction: column;
        gap: 14px;
      }

      .tp-trip-card {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 20px;
        flex-wrap: wrap;
      }

      .tp-trip-card__main {
        display: flex;
        align-items: center;
        gap: 28px;
        flex-wrap: wrap;
        flex: 1;
      }

      .tp-trip-card__operator {
        display: flex;
        align-items: center;
        gap: 10px;
        min-width: 160px;
      }

      .tp-trip-card__logo {
        width: 36px;
        height: 36px;
        border-radius: 8px;
        object-fit: cover;
      }

      .tp-trip-card__operator-name {
        font-weight: 600;
      }

      .tp-trip-card__bus {
        font-size: 12px;
      }

      .tp-trip-card__route {
        display: flex;
        align-items: center;
        gap: 14px;
      }

      .tp-trip-card__time-block {
        display: flex;
        flex-direction: column;
        gap: 2px;
      }

      .tp-trip-card__time {
        font-weight: 700;
        font-size: 16px;
      }

      .tp-trip-card__duration {
        display: flex;
        flex-direction: column;
        align-items: center;
        gap: 4px;
        font-size: 12px;
        min-width: 80px;
      }

      .tp-trip-card__line {
        width: 100%;
        height: 1px;
        background: var(--tp-border);
      }

      .tp-trip-card__amenities {
        display: flex;
        gap: 6px;
        flex-wrap: wrap;
      }

      .tp-chip {
        font-size: 11px;
        font-weight: 600;
        padding: 3px 9px;
        border-radius: 999px;
        background: var(--tp-surface-alt);
        color: var(--tp-text-muted);
      }

      .tp-trip-card__side {
        display: flex;
        flex-direction: column;
        align-items: flex-end;
        gap: 10px;
      }

      .tp-trip-card__fare {
        display: flex;
        flex-direction: column;
        align-items: flex-end;
        gap: 2px;
      }

      .tp-trip-card__price {
        font-weight: 700;
        font-size: 18px;
        color: var(--tp-text);
      }

      @media (max-width: 760px) {
        .tp-edit-search-form {
          grid-template-columns: 1fr;
        }

        .tp-trip-card {
          flex-direction: column;
          align-items: stretch;
        }

        .tp-trip-card__side {
          align-items: stretch;
        }
      }
    `,
  ],
})
export class SearchResultsComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly fb = inject(FormBuilder);
  private readonly api = inject(ApiService);
  private readonly searchApi = inject(SearchApiService);

  protected readonly terminals = signal<Terminal[]>([]);
  protected readonly results = signal<TripSearchResult[]>([]);
  protected readonly loading = signal(false);
  protected readonly searched = signal(false);
  protected readonly sortBy = signal<SortMode>('departure');

  protected readonly form = this.fb.nonNullable.group({
    fromTerminalId: ['', Validators.required],
    toTerminalId: ['', Validators.required],
    date: ['', Validators.required],
  });

  ngOnInit(): void {
    this.searchApi.terminals().subscribe((terminals) => this.terminals.set(terminals));

    this.route.queryParamMap.subscribe((params) => {
      const fromTerminalId = params.get('fromTerminalId') ?? '';
      const toTerminalId = params.get('toTerminalId') ?? '';
      const date = params.get('date') ?? '';
      this.form.patchValue({ fromTerminalId, toTerminalId, date }, { emitEvent: false });

      if (fromTerminalId && toTerminalId && date) {
        this.runSearch(fromTerminalId, toTerminalId, date);
      } else {
        this.searched.set(false);
        this.results.set([]);
      }
    });
  }

  protected sortedResults(): TripSearchResult[] {
    const items = [...this.results()];
    if (this.sortBy() === 'price') {
      items.sort((a, b) => (a.lowestAvailableFare ?? Infinity) - (b.lowestAvailableFare ?? Infinity));
    } else {
      items.sort((a, b) => new Date(a.departureTimeUtc).getTime() - new Date(b.departureTimeUtc).getTime());
    }
    return items;
  }

  protected setSort(value: SortMode): void {
    this.sortBy.set(value);
  }

  protected logoUrl(trip: TripSearchResult): string | null {
    return this.api.resolveAssetUrl(trip.busOperatorLogoUrl);
  }

  protected durationLabel(trip: TripSearchResult): string {
    const ms = new Date(trip.arrivalTimeUtc).getTime() - new Date(trip.departureTimeUtc).getTime();
    const totalMinutes = Math.max(0, Math.round(ms / 60000));
    const hours = Math.floor(totalMinutes / 60);
    const minutes = totalMinutes % 60;
    return `${hours}h ${minutes}m`;
  }

  editSearch(): void {
    if (this.form.invalid) return;
    const { fromTerminalId, toTerminalId, date } = this.form.getRawValue();
    this.router.navigate(['/search/results'], { queryParams: { fromTerminalId, toTerminalId, date } });
  }

  selectTrip(trip: TripSearchResult): void {
    this.router.navigate(['/search/trip', trip.tripId], {
      state: { searchResult: trip },
      queryParams: this.route.snapshot.queryParams,
    });
  }

  private runSearch(fromTerminalId: string, toTerminalId: string, date: string): void {
    this.loading.set(true);
    this.searchApi.searchTrips({ fromTerminalId, toTerminalId, date }).subscribe({
      next: (results) => {
        this.results.set(results);
        this.loading.set(false);
        this.searched.set(true);
      },
      error: () => {
        this.results.set([]);
        this.loading.set(false);
        this.searched.set(true);
      },
    });
  }
}
