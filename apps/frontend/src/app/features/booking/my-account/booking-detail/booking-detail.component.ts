import { CommonModule } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { forkJoin } from 'rxjs';
import {
  Booking,
  BookingPassenger,
  CancellationRequest,
  CancellationRequestCreateRequest,
  Payment,
  Refund,
  Review,
  ReviewCreateRequest,
  ReviewUpdateRequest,
  Ticket,
} from '@ticketportal-mono/models';
import { ApiService } from '../../../../core/services/api.service';
import { ToastService } from '../../../../core/services/toast.service';
import { TpButtonDirective, TpCardComponent, TpModalComponent, TpSpinnerComponent, TpStatusPillComponent } from '../../../../shared/ui';
import { TripDisplayContext, TripDisplayService } from '../../services/trip-display.service';

const CANCELLABLE_STATUSES = ['PendingPayment', 'Confirmed'];

@Component({
  selector: 'tp-booking-detail',
  standalone: true,
  imports: [CommonModule, FormsModule, TpCardComponent, TpButtonDirective, TpStatusPillComponent, TpSpinnerComponent, TpModalComponent],
  template: `
    <div class="tp-page tp-detail-page">
      @if (loading()) {
        <div class="tp-loading-block">
          <tp-spinner size="lg" />
        </div>
      } @else if (booking(); as b) {
        <div class="tp-detail-header">
          <div>
            <h2>{{ b.pnr }}</h2>
            <p class="tp-muted">Booked {{ b.createdAtUtc | date: 'medium' }}</p>
          </div>
          <tp-status-pill [status]="b.status" />
        </div>

        @if (context(); as ctx) {
          <tp-card class="tp-section-card">
            <h3>{{ ctx.operatorName }} · {{ ctx.trip.tripCode }}</h3>
            <p class="tp-muted">{{ ctx.boardingTerminalName }} → {{ ctx.droppingTerminalName }}</p>
            <p class="tp-muted">
              Departs {{ ctx.trip.departureTimeUtc | date: 'medium' }} · Arrives {{ ctx.trip.arrivalTimeUtc | date: 'medium' }}
            </p>
          </tp-card>
        }

        <tp-card class="tp-section-card">
          <h3>Passengers</h3>
          <table class="tp-simple-table">
            <thead>
              <tr>
                <th>Name</th>
                <th>Type</th>
                <th>Seat</th>
                <th style="text-align: right">Fare</th>
                <th>Status</th>
              </tr>
            </thead>
            <tbody>
              @for (p of b.passengers; track p.id) {
                <tr>
                  <td>{{ p.fullName }}</td>
                  <td>{{ p.passengerType }}</td>
                  <td>{{ ticketFor(p)?.seatNumberSnapshot ?? '—' }}</td>
                  <td style="text-align: right">
                    @if (ticketFor(p); as t) {
                      {{ t.finalFare | number: '1.2-2' }} {{ b.currency }}
                    }
                  </td>
                  <td>
                    @if (ticketFor(p); as t) {
                      <tp-status-pill [status]="t.status" />
                    } @else {
                      <span class="tp-muted">Not yet issued</span>
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </tp-card>

        <tp-card class="tp-section-card">
          <h3>Payment</h3>
          @if (latestPayment(); as pay) {
            <p>{{ pay.method }} · {{ pay.amount | number: '1.2-2' }} {{ pay.currency }} <tp-status-pill [status]="pay.status" /></p>
          } @else {
            <p class="tp-muted">No payment recorded yet.</p>
          }
        </tp-card>

        <tp-card class="tp-section-card">
          <div class="tp-section-card__header">
            <h3>Cancellation &amp; Refunds</h3>
            @if (canRequestCancellation()) {
              <button tpButton variant="danger" size="sm" (click)="openCancelModal()">Request Cancellation</button>
            }
          </div>

          @if (cancellationRequests().length === 0 && refunds().length === 0) {
            <p class="tp-muted">No cancellation or refund activity on this booking.</p>
          } @else {
            @for (cr of cancellationRequests(); track cr.id) {
              <p>
                Cancellation requested {{ cr.requestedAtUtc | date: 'medium' }}
                <tp-status-pill [status]="cr.status" />
                @if (cr.approvedRefundAmount !== null) {
                  — approved refund {{ cr.approvedRefundAmount | number: '1.2-2' }} {{ b.currency }}
                }
              </p>
            }
            @for (r of refunds(); track r.id) {
              <p>Refund of {{ r.amount | number: '1.2-2' }} {{ r.currency }} <tp-status-pill [status]="r.status" /></p>
            }
          }
        </tp-card>

        @if (b.status === 'Completed') {
          <tp-card class="tp-section-card">
            <div class="tp-section-card__header">
              <h3>Your Review</h3>
              @if (!myReview()) {
                <button tpButton variant="secondary" size="sm" (click)="openReviewModal()">Leave a Review</button>
              }
            </div>
            @if (myReview(); as r) {
              <p>{{ '★'.repeat(r.rating) }}{{ '☆'.repeat(5 - r.rating) }}</p>
              @if (r.comment) {
                <p class="tp-muted">{{ r.comment }}</p>
              }
              <button tpButton variant="ghost" size="sm" (click)="openReviewModal()">Edit Review</button>
            } @else {
              <p class="tp-muted">You haven't reviewed this trip yet.</p>
            }
          </tp-card>
        }
      }
    </div>

    <tp-modal [open]="cancelModalOpen()" title="Request Cancellation" (closed)="cancelModalOpen.set(false)">
      <label class="tp-modal-label">
        Cancel
        <select [(ngModel)]="cancelTicketId">
          <option [ngValue]="null">Entire booking</option>
          @for (t of tickets(); track t.id) {
            <option [ngValue]="t.id">Seat {{ t.seatNumberSnapshot }} only</option>
          }
        </select>
      </label>
      <label class="tp-modal-label">
        Reason
        <textarea rows="3" [(ngModel)]="cancelReason" placeholder="Let us know why you're cancelling"></textarea>
      </label>
      <div modal-footer>
        <button tpButton variant="secondary" (click)="cancelModalOpen.set(false)">Back</button>
        <button tpButton variant="danger" [disabled]="!cancelReason().trim() || submittingCancel()" (click)="submitCancellation()">
          {{ submittingCancel() ? 'Submitting…' : 'Submit Request' }}
        </button>
      </div>
    </tp-modal>

    <tp-modal [open]="reviewModalOpen()" title="Rate This Trip" (closed)="reviewModalOpen.set(false)">
      <label class="tp-modal-label">
        Rating
        <select [(ngModel)]="reviewRating">
          @for (n of [1, 2, 3, 4, 5]; track n) {
            <option [ngValue]="n">{{ n }} star{{ n > 1 ? 's' : '' }}</option>
          }
        </select>
      </label>
      <label class="tp-modal-label">
        Comment (optional)
        <textarea rows="3" [(ngModel)]="reviewComment" placeholder="How was the trip?"></textarea>
      </label>
      <div modal-footer>
        <button tpButton variant="secondary" (click)="reviewModalOpen.set(false)">Back</button>
        <button tpButton variant="primary" [disabled]="submittingReview()" (click)="submitReview()">
          {{ submittingReview() ? 'Saving…' : 'Save Review' }}
        </button>
      </div>
    </tp-modal>
  `,
  styles: [
    `
      .tp-detail-page {
        max-width: 820px;
      }

      .tp-loading-block {
        display: flex;
        justify-content: center;
        padding: var(--tp-space-7) 0;
      }

      .tp-detail-header {
        display: flex;
        justify-content: space-between;
        align-items: flex-start;
        margin-bottom: var(--tp-space-5);
      }

      .tp-section-card {
        margin-bottom: var(--tp-space-4);
      }

      .tp-section-card__header {
        display: flex;
        justify-content: space-between;
        align-items: center;
        margin-bottom: var(--tp-space-3);
      }

      .tp-section-card__header h3 {
        margin: 0;
      }

      .tp-simple-table {
        width: 100%;
        border-collapse: collapse;
        font-size: 14px;
      }

      .tp-simple-table th {
        text-align: left;
        color: var(--tp-text-muted);
        font-weight: 600;
        font-size: 12px;
        text-transform: uppercase;
        border-bottom: 1px solid var(--tp-border);
        padding: var(--tp-space-2);
      }

      .tp-simple-table td {
        padding: var(--tp-space-2);
        border-bottom: 1px solid var(--tp-border);
      }

      .tp-modal-label {
        display: flex;
        flex-direction: column;
        gap: var(--tp-space-2);
        font-size: 13px;
        font-weight: 600;
        color: var(--tp-text-muted);
        margin-bottom: var(--tp-space-4);
      }

      .tp-modal-label select,
      .tp-modal-label textarea {
        border: 1px solid var(--tp-border);
        border-radius: var(--tp-radius-sm);
        padding: 10px var(--tp-space-3);
        font-size: 14px;
        font-family: var(--tp-font-body);
        color: var(--tp-text);
        resize: vertical;
      }
    `,
  ],
})
export class BookingDetailComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);
  private readonly tripDisplay = inject(TripDisplayService);

  protected readonly loading = signal(true);
  protected readonly booking = signal<Booking | null>(null);
  protected readonly tickets = signal<Ticket[]>([]);
  protected readonly payments = signal<Payment[]>([]);
  protected readonly cancellationRequests = signal<CancellationRequest[]>([]);
  protected readonly refunds = signal<Refund[]>([]);
  protected readonly myReview = signal<Review | null>(null);
  protected readonly context = signal<TripDisplayContext | null>(null);

  protected readonly latestPayment = signal<Payment | null>(null);

  protected readonly cancelModalOpen = signal(false);
  protected readonly cancelTicketId = signal<string | null>(null);
  protected readonly cancelReason = signal('');
  protected readonly submittingCancel = signal(false);

  protected readonly reviewModalOpen = signal(false);
  protected readonly reviewRating = signal(5);
  protected readonly reviewComment = signal('');
  protected readonly submittingReview = signal(false);

  private bookingId = '';

  ngOnInit(): void {
    this.bookingId = this.route.snapshot.paramMap.get('id') ?? '';
    if (!this.bookingId) {
      this.loading.set(false);
      return;
    }
    this.loadAll();
  }

  private loadAll(): void {
    forkJoin({
      booking: this.api.get<Booking>(`bookings/${this.bookingId}`),
      tickets: this.api.get<Ticket[]>('tickets'),
      payments: this.api.get<Payment[]>('payments'),
      cancellationRequests: this.api.get<CancellationRequest[]>('cancellationrequests'),
      refunds: this.api.get<Refund[]>('refunds'),
      reviews: this.api.get<Review[]>('reviews'),
    }).subscribe({
      next: ({ booking, tickets, payments, cancellationRequests, refunds, reviews }) => {
        this.booking.set(booking);
        this.tickets.set(tickets.filter((t) => t.bookingId === booking.id));
        const myPayments = payments.filter((p) => p.bookingId === booking.id).sort((a, b) => (a.transactionDateUtc < b.transactionDateUtc ? 1 : -1));
        this.payments.set(myPayments);
        this.latestPayment.set(myPayments[0] ?? null);
        this.cancellationRequests.set(cancellationRequests.filter((c) => c.bookingId === booking.id));
        this.refunds.set(refunds.filter((r) => r.bookingId === booking.id));

        const existingReview = reviews.find((r) => r.bookingId === booking.id) ?? null;
        this.myReview.set(existingReview);
        if (existingReview) {
          this.reviewRating.set(existingReview.rating);
          this.reviewComment.set(existingReview.comment ?? '');
        }

        this.loading.set(false);
        this.tripDisplay.loadContext(booking.tripId).subscribe((ctx) => this.context.set(ctx));
      },
      error: () => {
        this.loading.set(false);
        this.router.navigate(['/my-bookings']);
      },
    });
  }

  ticketFor(passenger: BookingPassenger): Ticket | undefined {
    return this.tickets().find((t) => t.bookingPassengerId === passenger.id);
  }

  canRequestCancellation(): boolean {
    const b = this.booking();
    if (!b || !CANCELLABLE_STATUSES.includes(b.status)) return false;
    // Only block on a whole-booking request already in flight — a per-ticket request doesn't
    // stop the customer from also cancelling the rest of the booking separately.
    return !this.cancellationRequests().some((c) => c.ticketId === null && (c.status === 'Requested' || c.status === 'Approved'));
  }

  openCancelModal(): void {
    this.cancelTicketId.set(null);
    this.cancelReason.set('');
    this.cancelModalOpen.set(true);
  }

  submitCancellation(): void {
    const b = this.booking();
    if (!b || !this.cancelReason().trim()) return;

    const request: CancellationRequestCreateRequest = {
      bookingId: b.id,
      ticketId: this.cancelTicketId() ?? undefined,
      reason: this.cancelReason().trim(),
    };

    this.submittingCancel.set(true);
    this.api.post<CancellationRequest>('cancellationrequests', request).subscribe({
      next: () => {
        this.submittingCancel.set(false);
        this.cancelModalOpen.set(false);
        this.toast.success('Cancellation request submitted.');
        this.loadAll();
      },
      error: () => this.submittingCancel.set(false),
    });
  }

  openReviewModal(): void {
    this.reviewModalOpen.set(true);
  }

  submitReview(): void {
    const b = this.booking();
    if (!b) return;

    this.submittingReview.set(true);
    const existing = this.myReview();

    if (existing) {
      const request: ReviewUpdateRequest = {
        rating: this.reviewRating(),
        comment: this.reviewComment().trim() || undefined,
        rowVersion: existing.rowVersion,
      };
      this.api.put<Review>(`reviews/${existing.id}`, request).subscribe({
        next: (review) => this.onReviewSaved(review),
        error: () => this.submittingReview.set(false),
      });
    } else {
      const request: ReviewCreateRequest = {
        tripId: b.tripId,
        bookingId: b.id,
        rating: this.reviewRating(),
        comment: this.reviewComment().trim() || undefined,
      };
      this.api.post<Review>('reviews', request).subscribe({
        next: (review) => this.onReviewSaved(review),
        error: () => this.submittingReview.set(false),
      });
    }
  }

  private onReviewSaved(review: Review): void {
    this.submittingReview.set(false);
    this.reviewModalOpen.set(false);
    this.myReview.set(review);
    this.toast.success('Thanks for your review!');
  }
}
