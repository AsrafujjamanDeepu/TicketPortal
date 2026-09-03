import { Component, inject, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AuthService } from '../../../core/services/auth.service';
import { ToastService } from '../../../core/services/toast.service';
import { TpButtonDirective, TpCardComponent } from '../../../shared/ui';

/**
 * Every self-signup account lands in the "Customer" role on the backend —
 * there's no role picker here on purpose. Operator/Staff/Admin accounts are
 * created by an existing Admin (AdminController -> POST /staff), which is
 * Piece 7's job, not this page's.
 */
@Component({
  selector: 'tp-register',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, TpButtonDirective, TpCardComponent],
  template: `
    <div class="tp-auth-page">
      <tp-card class="tp-auth-card">
        <h2>Create your account</h2>
        <p class="tp-muted">Book bus tickets across every operator on TicketPortal.</p>

        <form [formGroup]="form" (ngSubmit)="submit()">
          <label>
            Full name
            <input type="text" formControlName="fullName" autocomplete="name" />
          </label>

          <label>
            Username
            <input type="text" formControlName="userName" autocomplete="username" />
          </label>

          <label>
            Email
            <input type="email" formControlName="email" autocomplete="email" />
          </label>

          <label>
            Password
            <input type="password" formControlName="password" autocomplete="new-password" />
          </label>

          <button tpButton variant="primary" type="submit" [disabled]="form.invalid || submitting()" style="width: 100%">
            {{ submitting() ? 'Creating account…' : 'Sign up' }}
          </button>
        </form>

        <p class="tp-auth-card__switch">
          Already have an account? <a routerLink="/auth/login">Log in</a>
        </p>
      </tp-card>
    </div>
  `,
  styles: [
    `
      .tp-auth-page {
        display: flex;
        justify-content: center;
        padding: var(--tp-space-7) var(--tp-space-5);
      }

      .tp-auth-card {
        width: 100%;
        max-width: 420px;
      }

      form {
        display: flex;
        flex-direction: column;
        gap: var(--tp-space-4);
        margin-top: var(--tp-space-5);
      }

      label {
        display: flex;
        flex-direction: column;
        gap: var(--tp-space-2);
        font-size: 13px;
        font-weight: 600;
        color: var(--tp-text-muted);
      }

      input {
        border: 1px solid var(--tp-border);
        border-radius: var(--tp-radius-sm);
        padding: 10px var(--tp-space-3);
        font-size: 14px;
        font-family: var(--tp-font-body);
        color: var(--tp-text);
      }

      input:focus {
        outline: none;
        border-color: var(--tp-yellow-dark);
        box-shadow: 0 0 0 3px var(--tp-yellow-tint);
      }

      .tp-auth-card__switch {
        text-align: center;
        font-size: 13px;
        color: var(--tp-text-muted);
        margin-top: var(--tp-space-5);
        margin-bottom: 0;
      }

      .tp-auth-card__switch a {
        color: var(--tp-yellow-dark);
        font-weight: 600;
      }
    `,
  ],
})
export class RegisterComponent {
  private readonly fb = inject(FormBuilder);
  private readonly auth = inject(AuthService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);

  protected readonly submitting = signal(false);

  protected readonly form = this.fb.nonNullable.group({
    fullName: ['', Validators.required],
    userName: ['', Validators.required],
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required, Validators.minLength(6)]],
  });

  submit(): void {
    if (this.form.invalid) return;

    this.submitting.set(true);
    this.auth.register(this.form.getRawValue()).subscribe({
      next: () => {
        this.toast.success('Account created — please log in.');
        this.router.navigate(['/auth/login']);
      },
      error: () => this.submitting.set(false),
    });
  }
}
