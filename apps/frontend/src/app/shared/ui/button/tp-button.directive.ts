import { Directive, HostBinding, Input } from '@angular/core';

export type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger';
export type ButtonSize = 'sm' | 'md' | 'lg';

/**
 * Attribute directive, not a wrapper component — apply it straight to a
 * native <button>, so (click), [disabled], type="submit" inside a form,
 * etc. all keep working exactly like a normal button.
 *
 *   <button tpButton variant="primary" (click)="save()">Save</button>
 *   <button tpButton variant="secondary" size="sm">Cancel</button>
 *   <button tpButton variant="danger" [disabled]="isDeleting">Delete</button>
 */
@Directive({
  selector: 'button[tpButton]',
  standalone: true,
})
export class TpButtonDirective {
  @Input() variant: ButtonVariant = 'primary';
  @Input() size: ButtonSize = 'md';

  @HostBinding('class') get hostClass(): string {
    return `tp-btn tp-btn--${this.variant} tp-btn--${this.size}`;
  }
}
