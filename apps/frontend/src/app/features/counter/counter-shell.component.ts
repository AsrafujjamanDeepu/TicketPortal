import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

/**
 * Piece 5 root screen — a thin sub-nav (same visual language as
 * shared/ui/tabs, built with real routerLinks instead of tp-tabs' index
 * switch so each screen is a real, bookmarkable/back-button-friendly URL)
 * plus a <router-outlet> for the six screens below it. Mounted once at
 * /counter — see counter.routes.ts for the child route list.
 */
@Component({
  selector: 'tp-counter-shell',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  template: `
    <div class="tp-page">
      <header class="tp-counter-header">
        <h1>Counter &amp; Agent Operations</h1>
        <p class="tp-muted">Walk-in sales, agent roster, cancellations/refunds, staff HR, and complaints.</p>
      </header>

      <nav class="tp-tabs" role="tablist">
        <a class="tp-tab" routerLink="walk-in" routerLinkActive="tp-tab--active">Walk-in Booking</a>
        <a class="tp-tab" routerLink="setup" routerLinkActive="tp-tab--active">Counter Setup</a>
        <a class="tp-tab" routerLink="agents" routerLinkActive="tp-tab--active">Agents</a>
        <a class="tp-tab" routerLink="cancellations" routerLinkActive="tp-tab--active">Cancellations &amp; Refunds</a>
        <a class="tp-tab" routerLink="staff" routerLinkActive="tp-tab--active">Staff HR</a>
        <a class="tp-tab" routerLink="complaints" routerLinkActive="tp-tab--active">Complaints</a>
      </nav>

      <router-outlet />
    </div>
  `,
  styles: [
    `
      .tp-counter-header {
        margin-bottom: var(--tp-space-5);
      }

      .tp-counter-header p {
        margin: 0;
      }

      /* Re-uses the same .tp-tabs/.tp-tab visual language as
         shared/ui/tabs/tp-tabs.component.ts, but with real routerLinks —
         tp-tabs itself only supports index-based switching, not routes. */
      .tp-tabs {
        display: flex;
        gap: var(--tp-space-2);
        border-bottom: 1px solid var(--tp-border);
        margin-bottom: var(--tp-space-5);
        flex-wrap: wrap;
      }

      .tp-tab {
        display: inline-block;
        padding: var(--tp-space-3) var(--tp-space-2);
        font-family: var(--tp-font-body);
        font-weight: 600;
        font-size: 14px;
        color: var(--tp-text-muted);
        cursor: pointer;
        border-bottom: 2px solid transparent;
        margin-bottom: -1px;
        transition: color var(--tp-transition-fast), border-color var(--tp-transition-fast);
      }

      .tp-tab:hover {
        color: var(--tp-text);
      }

      .tp-tab--active {
        color: var(--tp-text);
        border-bottom-color: var(--tp-yellow-dark);
      }
    `,
  ],
})
export class CounterShellComponent {}
