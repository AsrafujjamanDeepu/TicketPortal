import { Component } from '@angular/core';

@Component({
  selector: 'tp-footer',
  standalone: true,
  template: `
    <footer class="tp-footer">
      <div class="tp-footer__inner">
        <span>© {{ year }} TicketPortal</span>
        <span class="tp-muted">Multi-tenant bus ticket booking platform</span>
      </div>
    </footer>
  `,
  styles: [
    `
      .tp-footer {
        border-top: 1px solid var(--tp-border);
        background: var(--tp-bg-soft);
        margin-top: var(--tp-space-7);
      }

      .tp-footer__inner {
        max-width: 1160px;
        margin: 0 auto;
        padding: var(--tp-space-5);
        display: flex;
        justify-content: space-between;
        font-size: 13px;
        color: var(--tp-text-muted);
      }
    `,
  ],
})
export class FooterComponent {
  protected readonly year = new Date().getFullYear();
}
