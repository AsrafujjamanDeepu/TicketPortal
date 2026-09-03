import { Component, Input } from '@angular/core';

/**
 * Usage:
 *   <tp-empty-state title="No trips found" message="Try a different date or route." />
 *   <tp-empty-state title="No bookings yet">
 *     <button tpButton variant="primary">Search Trips</button>
 *   </tp-empty-state>
 *
 * Use this for every "zero results" screen (empty search results, empty
 * booking history, empty fleet list, ...) instead of a blank page or a
 * one-off "No data" text — keeps that state consistent everywhere.
 */
@Component({
  selector: 'tp-empty-state',
  standalone: true,
  template: `
    <div class="tp-empty">
      <div class="tp-empty__icon">🍌</div>
      <h4>{{ title }}</h4>
      @if (message) {
        <p class="tp-muted">{{ message }}</p>
      }
      <ng-content />
    </div>
  `,
  styles: [
    `
      .tp-empty {
        display: flex;
        flex-direction: column;
        align-items: center;
        text-align: center;
        gap: var(--tp-space-2);
        padding: var(--tp-space-7) var(--tp-space-5);
        color: var(--tp-text);
      }

      .tp-empty__icon {
        font-size: 40px;
        margin-bottom: var(--tp-space-2);
      }

      .tp-empty h4 {
        margin: 0;
      }

      .tp-empty p {
        margin: 0 0 var(--tp-space-3);
        max-width: 320px;
      }
    `,
  ],
})
export class TpEmptyStateComponent {
  @Input({ required: true }) title!: string;
  @Input() message?: string;
}
