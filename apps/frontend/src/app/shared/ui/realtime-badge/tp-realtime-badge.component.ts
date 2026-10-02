import { Component, computed, inject } from '@angular/core';
import { RealtimeService, RealtimeState } from '../../../core/realtime/realtime.service';

const TONES: Record<RealtimeState, 'success' | 'warning' | 'neutral'> = {
  live: 'success',
  reconnecting: 'warning',
  offline: 'neutral',
};

const LABELS: Record<RealtimeState, string> = {
  live: 'Live',
  reconnecting: 'Reconnecting',
  offline: 'Offline',
};

const HINTS: Record<RealtimeState, string> = {
  live: 'Live updates are on — this page refreshes itself when something changes.',
  reconnecting: 'Reconnecting to live updates. The page will catch up as soon as the connection is back.',
  offline: 'Live updates are off. Pages still work; reload to see the latest changes.',
};

/**
 * Small connection-health pill for the shell: Live / Reconnecting / Offline. It stays out of the way until
 * a screen has actually asked for live updates (the connection opens lazily), so pages that never go live
 * don't show a misleading "Offline".
 *
 *   <tp-realtime-badge />
 *
 * Styling comes from the shared global .tp-pill classes, like <tp-status-pill>.
 */
@Component({
  selector: 'tp-realtime-badge',
  standalone: true,
  template: `
    @if (realtime.active()) {
      <span
        class="tp-realtime-badge"
        [class]="'tp-pill tp-pill--' + tone()"
        role="status"
        aria-live="polite"
        [attr.title]="hint()"
        [attr.data-state]="realtime.state()"
      >
        <span class="tp-realtime-badge__dot" aria-hidden="true"></span>{{ label() }}
      </span>
    }
  `,
  styles: [
    `
      .tp-realtime-badge {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        white-space: nowrap;
        cursor: default;
      }

      .tp-realtime-badge__dot {
        width: 7px;
        height: 7px;
        border-radius: 50%;
        background: currentColor;
      }

      [data-state='live'] .tp-realtime-badge__dot {
        animation: tp-realtime-pulse 2.4s ease-in-out infinite;
      }

      [data-state='reconnecting'] .tp-realtime-badge__dot {
        animation: tp-realtime-pulse 0.9s ease-in-out infinite;
      }

      @keyframes tp-realtime-pulse {
        0%,
        100% {
          opacity: 1;
        }
        50% {
          opacity: 0.35;
        }
      }

      @media (prefers-reduced-motion: reduce) {
        .tp-realtime-badge__dot {
          animation: none !important;
        }
      }
    `,
  ],
})
export class TpRealtimeBadgeComponent {
  protected readonly realtime = inject(RealtimeService);

  protected readonly tone = computed(() => TONES[this.realtime.state()]);
  protected readonly label = computed(() => LABELS[this.realtime.state()]);
  protected readonly hint = computed(() => HINTS[this.realtime.state()]);
}
