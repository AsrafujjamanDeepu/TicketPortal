import { DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ApiError } from '@ticketportal-mono/models';
import { Observable, catchError, map, throwError } from 'rxjs';
import { ApiService } from '../../../core/services/api.service';
import { TpButtonDirective, TpCardComponent } from '../../../shared/ui';

/** One ticket as returned by GET tickets/verify/{ticketNumber} (and inside verify-pnr). */
interface VerifiedTicket {
  ticketNumber: string;
  status: string;
  validForBoarding: boolean;
  checkedIn: boolean;
  seatNumber: string;
}

/** GET tickets/verify/{ticketNumber} — one ticket plus its trip. */
interface TicketVerification extends VerifiedTicket {
  tripCode: string;
  operatorName: string | null;
  departureTerminal: string | null;
  arrivalTerminal: string | null;
  departureTimeUtc: string;
}

/** GET tickets/verify-pnr/{pnr} — a booking's trip plus every ticket issued on that PNR. */
interface PnrVerification {
  pnr: string | null; // null when the lookup was by ticket number
  tripCode: string;
  operatorName: string | null;
  departureTerminal: string | null;
  arrivalTerminal: string | null;
  departureTimeUtc: string;
  tickets: VerifiedTicket[];
}

function fromTicket(t: TicketVerification): PnrVerification {
  return {
    pnr: null,
    tripCode: t.tripCode,
    operatorName: t.operatorName,
    departureTerminal: t.departureTerminal,
    arrivalTerminal: t.arrivalTerminal,
    departureTimeUtc: t.departureTimeUtc,
    tickets: [t],
  };
}

@Component({
  selector: 'tp-ticket-verify',
  standalone: true,
  imports: [DatePipe, ReactiveFormsModule, TpButtonDirective, TpCardComponent],
  template: `
    <div class="tp-page tp-ticket-verify-page">
      <tp-card>
        <h2>Verify a ticket</h2>
        <p class="tp-muted">Enter the PNR or a ticket number from the passenger's booking. Verification only shows boarding information.</p>
        <form [formGroup]="form" (ngSubmit)="verify()">
          <input formControlName="reference" placeholder="PNR or ticket number, e.g. PNR1A2B3C4D" autocomplete="off" />
          <button tpButton variant="primary" type="submit" [disabled]="form.invalid || loading()">
            {{ loading() ? 'Checking…' : 'Verify ticket' }}
          </button>
        </form>
        @if (error()) { <p class="tp-error-text">{{ error() }}</p> }
      </tp-card>

      @if (result(); as value) {
        <tp-card class="tp-ticket-result" [class.tp-ticket-result--valid]="validCount(value) > 0" [class.tp-ticket-result--invalid]="validCount(value) === 0">
          <div class="tp-ticket-result__heading">
            <div>
              <p class="tp-muted">{{ value.pnr ? 'PNR ' + value.pnr : 'Ticket ' + value.tickets[0].ticketNumber }}</p>
              <h3>{{ heading(value) }}</h3>
            </div>
          </div>
          <dl>
            <div><dt>Route</dt><dd>{{ value.departureTerminal }} → {{ value.arrivalTerminal }}</dd></div>
            <div><dt>Departure</dt><dd>{{ value.departureTimeUtc | date: 'medium' }}</dd></div>
            <div><dt>Operator</dt><dd>{{ value.operatorName || '—' }}</dd></div>
            <div><dt>Trip</dt><dd>{{ value.tripCode }}</dd></div>
          </dl>
          <ul class="tp-ticket-list">
            @for (t of value.tickets; track t.ticketNumber) {
              <li>
                <span>Seat <strong>{{ t.seatNumber }}</strong> · {{ t.ticketNumber }}</span>
                <span class="tp-ticket-status" [class.tp-ticket-status--bad]="!t.validForBoarding">
                  {{ t.status }}{{ t.validForBoarding ? '' : ' — not valid' }}
                </span>
              </li>
            }
          </ul>
        </tp-card>
      }
    </div>
  `,
  styles: [`
    .tp-ticket-verify-page { max-width: 680px; display: flex; flex-direction: column; gap: var(--tp-space-4); padding-top: var(--tp-space-6); }
    h2, h3 { font-family: var(--tp-font-heading); }
    h2 { margin-top: 0; }
    form { display: flex; gap: var(--tp-space-3); margin-top: var(--tp-space-4); }
    input { flex: 1; min-width: 0; border: 1px solid var(--tp-border); border-radius: var(--tp-radius-sm); padding: 10px var(--tp-space-3); font: inherit; text-transform: uppercase; }
    .tp-error-text { margin: var(--tp-space-3) 0 0; color: var(--tp-danger); font-size: 13px; }
    .tp-ticket-result { border-left: 4px solid var(--tp-danger); }
    .tp-ticket-result--valid { border-left-color: var(--tp-success); }
    .tp-ticket-result__heading { display: flex; align-items: start; justify-content: space-between; gap: var(--tp-space-3); }
    .tp-ticket-result h3 { margin: var(--tp-space-1) 0 0; }
    .tp-ticket-status { border-radius: var(--tp-radius-pill); padding: 4px 9px; color: var(--tp-text-muted); background: var(--tp-surface-alt); font-size: 12px; font-weight: 700; }
    .tp-ticket-status--bad { color: var(--tp-danger); }
    .tp-ticket-list { list-style: none; margin: var(--tp-space-4) 0 0; padding: var(--tp-space-3) 0 0; border-top: 1px solid var(--tp-border); display: grid; gap: var(--tp-space-2); }
    .tp-ticket-list li { display: flex; align-items: center; justify-content: space-between; gap: var(--tp-space-3); }
    dl { margin: var(--tp-space-4) 0 0; display: grid; gap: var(--tp-space-2); }
    dl div { display: flex; justify-content: space-between; gap: var(--tp-space-4); }
    dt { color: var(--tp-text-muted); } dd { margin: 0; font-weight: 600; text-align: right; }
    @media (max-width: 560px) { form { flex-direction: column; } }
  `],
})
export class TicketVerifyComponent {
  private readonly fb = inject(FormBuilder);
  private readonly api = inject(ApiService);
  protected readonly loading = signal(false);
  protected readonly error = signal('');
  protected readonly result = signal<PnrVerification | null>(null);
  protected readonly form = this.fb.nonNullable.group({ reference: ['', Validators.required] });

  protected validCount(value: PnrVerification): number {
    return value.tickets.filter((t) => t.validForBoarding).length;
  }

  protected heading(value: PnrVerification): string {
    const valid = this.validCount(value);
    if (valid === 0) return 'Not valid for boarding';
    if (valid === value.tickets.length) return 'Valid for boarding';
    return `${valid} of ${value.tickets.length} tickets valid for boarding`;
  }

  verify(): void {
    if (this.form.invalid) return;
    const reference = this.form.controls.reference.value.trim();
    if (!reference) return;
    this.loading.set(true);
    this.error.set('');
    this.result.set(null);
    this.lookup(reference).subscribe({
      next: (result) => { this.result.set(result); this.loading.set(false); },
      error: (error: ApiError) => {
        this.error.set(error.status === 404 ? 'No ticket found. Check the PNR or ticket number and try again.' : error.message || 'Could not verify this ticket.');
        this.loading.set(false);
      },
    });
  }

  /**
   * A PNR (Booking.Pnr) and a ticket number (Ticket.TicketNumber) are two different references
   * served by two different endpoints, and the customer doesn't know which one they're holding.
   * Try the PNR first (it's what the booking confirmation leads with); only on a 404 fall back to
   * the ticket number. Any other failure (network, 500) is surfaced as-is.
   */
  private lookup(reference: string): Observable<PnrVerification> {
    const encoded = encodeURIComponent(reference);
    return this.api.get<PnrVerification>(`tickets/verify-pnr/${encoded}`).pipe(
      catchError((error: ApiError) =>
        error.status === 404
          ? this.api.get<TicketVerification>(`tickets/verify/${encoded}`).pipe(map(fromTicket))
          : throwError(() => error),
      ),
    );
  }
}
