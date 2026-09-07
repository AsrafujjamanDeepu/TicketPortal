import { CommonModule } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { Terminal } from '@ticketportal-mono/models';
import { TpButtonDirective, TpCardComponent, TpSpinnerComponent } from '../../../shared/ui';
import { SearchApiService } from '../services/search-api.service';

function todayIso(): string {
  return new Date().toISOString().slice(0, 10);
}

/**
 * Piece 2 — landing screen. Loads the terminal list once (Terminals are near-static reference
 * data, not worth re-fetching per keystroke) and lets the user pick from/to/date before handing
 * off to SearchResultsComponent via query params — the URL is the source of truth for a search,
 * so results are bookmarkable/shareable and survive a refresh.
 */
@Component({
  selector: 'tp-search-home',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule, TpCardComponent, TpButtonDirective, TpSpinnerComponent],
  template: `
    <div class="tp-page tp-search-home">
      <section class="tp-hero">
        <h1>Book your next bus trip</h1>
        <p class="tp-muted">
          Search live seat availability across every operator on TicketPortal.
        </p>
      </section>

      <tp-card class="tp-search-card">
        @if (loadingTerminals()) {
          <tp-spinner />
        } @else if (loadError()) {
          <p class="tp-error-text">Couldn't load terminals. Please refresh and try again.</p>
        } @else {
          <form [formGroup]="form" (ngSubmit)="search()" class="tp-search-form">
            <label class="tp-field">
              <span>From</span>
              <select formControlName="fromTerminalId">
                <option value="" disabled>Select terminal</option>
                @for (t of terminals(); track t.id) {
                  <option [value]="t.id">{{ t.name }} — {{ t.city }}</option>
                }
              </select>
            </label>

            <button type="button" class="tp-swap-btn" title="Swap terminals" (click)="swap()">
              ⇄
            </button>

            <label class="tp-field">
              <span>To</span>
              <select formControlName="toTerminalId">
                <option value="" disabled>Select terminal</option>
                @for (t of terminals(); track t.id) {
                  <option [value]="t.id">{{ t.name }} — {{ t.city }}</option>
                }
              </select>
            </label>

            <label class="tp-field">
              <span>Date</span>
              <input type="date" formControlName="date" [min]="today" />
            </label>

            <button tpButton variant="primary" type="submit" [disabled]="form.invalid || sameTerminalError()">
              Search Buses
            </button>
          </form>

          @if (sameTerminalError()) {
            <p class="tp-error-text">Departure and arrival terminals must be different.</p>
          }
        }
      </tp-card>
    </div>
  `,
  styles: [
    `
      .tp-search-home {
        display: flex;
        flex-direction: column;
        gap: 28px;
        padding-top: 32px;
      }

      .tp-hero {
        text-align: center;
      }

      .tp-hero h1 {
        font-size: 32px;
        margin-bottom: 8px;
      }

      .tp-search-card {
        max-width: 900px;
        margin: 0 auto;
        width: 100%;
      }

      .tp-search-form {
        display: grid;
        grid-template-columns: minmax(0, 1fr) auto minmax(0, 1fr) auto auto;
        gap: 14px;
        align-items: end;
      }

      .tp-field {
        display: flex;
        flex-direction: column;
        gap: 6px;
        font-size: 13px;
        font-weight: 600;
        color: var(--tp-text-muted);
        min-width: 0;
      }

      .tp-field select,
      .tp-field input {
        border: 1px solid var(--tp-border);
        border-radius: 8px;
        padding: 10px 12px;
        font-size: 14px;
        font-family: var(--tp-font-body);
        color: var(--tp-text);
        background: var(--tp-surface);
        width: 100%;
        min-width: 0;
      }

      .tp-field select:focus,
      .tp-field input:focus {
        outline: none;
        border-color: var(--tp-yellow-dark);
        box-shadow: 0 0 0 3px var(--tp-yellow-tint);
      }

      .tp-swap-btn {
        height: 40px;
        width: 40px;
        border-radius: 999px;
        border: 1px solid var(--tp-border);
        background: var(--tp-surface);
        cursor: pointer;
        font-size: 16px;
        color: var(--tp-text-muted);
      }

      .tp-swap-btn:hover {
        color: var(--tp-text);
        border-color: var(--tp-yellow-dark);
      }

      .tp-error-text {
        color: var(--tp-danger);
        font-size: 13px;
        margin: 12px 0 0;
      }

      @media (max-width: 760px) {
        .tp-search-form {
          grid-template-columns: 1fr;
        }

        .tp-swap-btn {
          justify-self: center;
        }
      }
    `,
  ],
})
export class SearchHomeComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly router = inject(Router);
  private readonly api = inject(SearchApiService);

  protected readonly terminals = signal<Terminal[]>([]);
  protected readonly loadingTerminals = signal(true);
  protected readonly loadError = signal(false);
  protected readonly today = todayIso();

  protected readonly form = this.fb.nonNullable.group({
    fromTerminalId: ['', Validators.required],
    toTerminalId: ['', Validators.required],
    date: [todayIso(), Validators.required],
  });

  ngOnInit(): void {
    this.api.terminals().subscribe({
      next: (terminals) => {
        this.terminals.set(terminals);
        this.loadingTerminals.set(false);
      },
      error: () => {
        this.loadingTerminals.set(false);
        this.loadError.set(true);
      },
    });
  }

  protected sameTerminalError(): boolean {
    const { fromTerminalId, toTerminalId } = this.form.getRawValue();
    return !!fromTerminalId && !!toTerminalId && fromTerminalId === toTerminalId;
  }

  swap(): void {
    const { fromTerminalId, toTerminalId } = this.form.getRawValue();
    this.form.patchValue({ fromTerminalId: toTerminalId, toTerminalId: fromTerminalId });
  }

  search(): void {
    if (this.form.invalid || this.sameTerminalError()) return;
    const { fromTerminalId, toTerminalId, date } = this.form.getRawValue();
    this.router.navigate(['/search/results'], { queryParams: { fromTerminalId, toTerminalId, date } });
  }
}
