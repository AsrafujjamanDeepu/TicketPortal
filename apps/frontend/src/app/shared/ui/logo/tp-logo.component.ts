import { Component, Input } from '@angular/core';

let nextLogoId = 0;

/**
 * The TicketPortal brand mark — a single reusable SVG badge that replaces
 * the old placeholder emoji icons that used to stand in for a real logo.
 * A winding route resolving into a pin reads as "journey / destination"
 * without leaning on a literal cartoon bus.
 *
 * Usage:
 *   <tp-logo />                      // 32px badge only
 *   <tp-logo [size]="40" />
 *   <tp-logo [wordmark]="true" />    // badge + "TicketPortal" wordmark, for navbars/headers
 *   <tp-logo tone="light" [wordmark]="true" />  // for dark surfaces (footer, hero panels)
 */
@Component({
  selector: 'tp-logo',
  standalone: true,
  template: `
    <span class="tp-logo" [class.tp-logo--light]="tone === 'light'">
      <svg
        [attr.width]="size"
        [attr.height]="size"
        viewBox="0 0 40 40"
        fill="none"
        xmlns="http://www.w3.org/2000/svg"
        aria-hidden="true"
      >
        <defs>
          <linearGradient [attr.id]="gradientId" x1="4" y1="2" x2="36" y2="38" gradientUnits="userSpaceOnUse">
            <stop offset="0" stop-color="#FFD25E" />
            <stop offset="0.55" stop-color="#FFA726" />
            <stop offset="1" stop-color="#FF7A1A" />
          </linearGradient>
        </defs>
        <rect width="40" height="40" rx="11" [attr.fill]="'url(#' + gradientId + ')'" />
        <path
          d="M10.5 27.5C10.5 18 15.5 13.5 20 13.5C24.5 13.5 24 19.5 18.5 20.7C14 21.7 14.5 26.3 19 27.2C23 28 26.5 25 27.5 19.5"
          stroke="white"
          stroke-width="2.6"
          stroke-linecap="round"
          fill="none"
        />
        <circle cx="27.7" cy="14" r="2.9" fill="white" />
      </svg>
      @if (wordmark) {
        <span class="tp-logo__word">TicketPortal</span>
      }
    </span>
  `,
  styles: [
    `
      .tp-logo {
        display: inline-flex;
        align-items: center;
        gap: var(--tp-space-2);
        line-height: 1;
      }

      .tp-logo svg {
        display: block;
        flex-shrink: 0;
        filter: drop-shadow(0 4px 10px rgba(255, 138, 20, 0.35));
      }

      .tp-logo__word {
        font-family: var(--tp-font-heading);
        font-weight: 700;
        font-size: 18px;
        letter-spacing: -0.01em;
        color: var(--tp-text);
      }

      .tp-logo--light .tp-logo__word {
        color: var(--tp-ink-text);
      }
    `,
  ],
})
export class TpLogoComponent {
  @Input() size = 32;
  @Input() wordmark = false;
  @Input() tone: 'dark' | 'light' = 'dark';

  protected readonly gradientId = `tp-logo-grad-${nextLogoId++}`;
}
