import { Component, inject, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { AuthService } from '../../../core/services/auth.service';
import { ToastService } from '../../../core/services/toast.service';
import { TpButtonDirective, TpCardComponent } from '../../../shared/ui';

@Component({
  selector: 'tp-login',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, TpButtonDirective, TpCardComponent],
  template: `
    <div class="tp-auth-page">
      <tp-card class="tp-auth-card">
        <h2>Log in</h2>
        <p class="tp-muted">Welcome back to TicketPortal.</p>

        <form [formGroup]="form" (ngSubmit)="submit()">
          <label>
            Username
            <input type="text" formControlName="userName" autocomplete="username" />
          </label>

          <label>
            Password
            <input type="password" formControlName="password" autocomplete="current-password" />
          </label>

          <button tpButton variant="primary" type="submit" [disabled]="form.invalid || submitting()" style="width: 100%">
            {{ submitting() ? 'Logging in…' : 'Log in' }}
          </button>
        </form>

        <p class="tp-auth-card__switch">
          Don't have an account? <a routerLink="/auth/register">Sign up</a>
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
        max-width: 380px;
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
export class LoginComponent {
  private readonly fb = inject(FormBuilder);
  private readonly auth = inject(AuthService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected readonly submitting = signal(false);

  protected readonly form = this.fb.nonNullable.group({
    userName: ['', Validators.required],
    password: ['', Validators.required],
  });

  submit(): void {
    if (this.form.invalid) return;

    this.submitting.set(true);
    this.auth.login(this.form.getRawValue()).subscribe({
      next: () => {
        this.toast.success('Logged in successfully.');
        const returnUrl = this.route.snapshot.queryParamMap.get('returnUrl');
        const user = this.auth.currentUser();
        this.router.navigateByUrl(returnUrl || (user ? this.auth.homeRouteFor(user) : '/search'));
      },
      error: () => this.submitting.set(false),
    });
  }
}
