import { CommonModule } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { forkJoin } from 'rxjs';
import { Booking } from '@ticketportal-mono/models';
import { ApiService } from '../../../../core/services/api.service';
import { TpButtonDirective, TpCardComponent, TpSpinnerComponent, TpStatusPillComponent } from '../../../../shared/ui';
import { downloadTicketPdf } from '../../../../shared/tickets/ticket-pdf';
import { TicketQrCardComponent } from '../../../../shared/tickets/ticket-qr-card.component';
import { TicketView } from '../../../../shared/tickets/ticket.types';
import { CheckoutStateService } from '../../services/checkout-state.service';
import { TripDisplayContext, TripDisplayService } from '../../services/trip-display.service';

/**
 * Deliberately re-fetches everything from the API by the bookingId route param instead of
 * trusting CheckoutStateService — this screen also has to work as a standalone deep link (a
 * "View E-Ticket" button from booking history, or a page refresh right after paying), where
 * the in-memory checkout state won't be there.
 */
@Component({
  selector: 'tp-checkout-confirmation',
  standalone: true,
  imports: [CommonModule, TpCardComponent, TpButtonDirective, TpSpinnerComponent, TpStatusPillComponent, TicketQrCardComponent],
  template: `
    <div class="tp-page tp-confirmation-page">
      @if (loading()) {
        <div class="tp-loading-block">
          <tp-spinner size="lg" />
          <p class="tp-muted">Loading your ticket…</p>
        </div>
      } @else if (booking(); as b) {
        <div class="tp-confirmation-header">
          <h2>Booking Confirmed</h2>
          <p class="tp-muted">PNR <strong>{{ b.pnr }}</strong> · {{ tickets().length }} ticket(s) issued</p>
        </div>

        @if (context(); as ctx) {
          <tp-card class="tp-trip-summary">
            <h3>{{ ctx.operatorName }} · {{ ctx.trip.tripCode }}</h3>
            <p class="tp-muted">{{ ctx.boardingTerminalName }} → {{ ctx.droppingTerminalName }}</p>
            <p class="tp-muted">
              Departs {{ ctx.trip.departureTimeUtc | date: 'medium' }} · Arrives {{ ctx.trip.arrivalTimeUtc | date: 'medium' }}
            </p>
          </tp-card>
        }

        @if (b.requiresExternalConfirmation) {
          <tp-card class="tp-external-confirmation-note">
            <strong>Awaiting operator confirmation.</strong>
            <span>
              This operator manages its own booking system. Your seats are held and your payment is recorded, but the
              operator's system still needs to confirm the seat — this normally happens automatically within a few
              minutes. Check My Bookings for the latest status.
            </span>
          </tp-card>
        }

        <div class="tp-ticket-grid">
          @for (t of tickets(); track t.id) {
            <tp-card class="tp-ticket-card">
              <div class="tp-ticket-card__header">
                <span class="tp-ticket-card__seat">Seat {{ t.seatNumberSnapshot }}</span>
                <tp-status-pill [status]="t.status" />
              </div>
              <tp-ticket-qr-card [ticket]="t" (qrReady)="setQr(t.id, $event)" />
              <div class="tp-ticket-card__actions">
                <button tpButton variant="secondary" type="button" (click)="downloadPdf(t)" [disabled]="!qrs()[t.id] || downloadingId() === t.id">
                  {{ downloadingId() === t.id ? 'Preparing PDF…' : 'Download PDF' }}
                </button>
              </div>
            </tp-card>
          }
        </div>

        <div class="tp-confirmation-page__actions">
          <button tpButton variant="secondary" (click)="print()">Print / Save PDF</button>
          <button tpButton variant="primary" (click)="viewInMyBookings(b.id)">View in My Bookings</button>
        </div>
      }
    </div>
  `,
  styles: [
    `
      .tp-confirmation-page {
        max-width: 800px;
      }

      .tp-loading-block {
        display: flex;
        flex-direction: column;
        align-items: center;
        gap: var(--tp-space-3);
        padding: var(--tp-space-7) 0;
      }

      .tp-confirmation-header {
        margin-bottom: var(--tp-space-5);
      }

      .tp-trip-summary {
        margin-bottom: var(--tp-space-5);
      }

      .tp-external-confirmation-note {
        display: flex;
        flex-direction: column;
        gap: var(--tp-space-1);
        margin-bottom: var(--tp-space-5);
        border-left: 4px solid var(--tp-warning, #d97706);
        font-size: 13px;
        color: var(--tp-text-muted);
      }

      .tp-external-confirmation-note strong {
        color: var(--tp-text);
        font-family: var(--tp-font-heading);
      }

      .tp-ticket-grid {
        display: grid;
        grid-template-columns: 1fr;
        gap: var(--tp-space-4);
        margin-bottom: var(--tp-space-5);
      }

      .tp-ticket-card__header {
        display: flex;
        justify-content: space-between;
        align-items: center;
        margin-bottom: var(--tp-space-2);
      }

      .tp-ticket-card__seat {
        font-weight: 700;
        font-size: 15px;
      }

      .tp-ticket-card__actions {
        display: flex;
        justify-content: flex-end;
        margin-top: var(--tp-space-3);
        padding-top: var(--tp-space-3);
        border-top: 1px dashed var(--tp-border);
      }

      .tp-confirmation-page__actions {
        display: flex;
        justify-content: flex-end;
        gap: var(--tp-space-3);
      }

      @media print {
        .tp-confirmation-page__actions,
        .tp-ticket-card__actions {
          display: none;
        }
      }
    `,
  ],
})
export class ConfirmationComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly api = inject(ApiService);
  private readonly tripDisplay = inject(TripDisplayService);
  private readonly checkoutState = inject(CheckoutStateService);

  protected readonly loading = signal(true);
  protected readonly booking = signal<Booking | null>(null);
  // GET /tickets returns TicketResponseDto (passenger/trip/bus context included) - that is what TicketView models.
  protected readonly tickets = signal<TicketView[]>([]);
  // QR data URL per ticket id, filled in by <tp-ticket-qr-card> (qrReady) and reused for the PDF download.
  protected readonly qrs = signal<Record<string, string | null>>({});
  protected readonly downloadingId = signal<string | null>(null);
  protected readonly context = signal<TripDisplayContext | null>(null);

  ngOnInit(): void {
    const bookingId = this.route.snapshot.paramMap.get('bookingId');
    if (!bookingId) {
      this.loading.set(false);
      return;
    }

    forkJoin({
      booking: this.api.get<Booking>(`bookings/${bookingId}`),
      tickets: this.api.get<TicketView[]>('tickets'),
    }).subscribe({
      next: ({ booking, tickets }) => {
        this.booking.set(booking);
        this.tickets.set(tickets.filter((t) => t.bookingId === booking.id));
        this.loading.set(false);
        // The checkout wizard is done — clear the in-progress state so a stray back-navigation
        // can't re-enter checkout/passengers or checkout/payment with a stale hold.
        this.checkoutState.reset();

        this.tripDisplay.loadContext(booking.tripId).subscribe((ctx) => this.context.set(ctx));
      },
      error: () => this.loading.set(false),
    });
  }

  print(): void {
    window.print();
  }

  protected setQr(ticketId: string, dataUrl: string | null): void {
    this.qrs.update((m) => ({ ...m, [ticketId]: dataUrl }));
  }

  protected async downloadPdf(ticket: TicketView): Promise<void> {
    const qr = this.qrs()[ticket.id];
    if (!qr) return;
    this.downloadingId.set(ticket.id);
    try {
      await downloadTicketPdf(ticket, qr);
    } finally {
      this.downloadingId.set(null);
    }
  }

  viewInMyBookings(bookingId: string): void {
    this.router.navigate(['/my-bookings', bookingId]);
  }
}
