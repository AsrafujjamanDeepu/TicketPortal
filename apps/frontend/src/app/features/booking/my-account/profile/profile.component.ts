import { CommonModule } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { CustomerProfile, CustomerProfileCreateRequest, CustomerProfileUpdateRequest, Gender } from '@ticketportal-mono/models';
import { ApiService } from '../../../../core/services/api.service';
import { AuthService } from '../../../../core/services/auth.service';
import { ToastService } from '../../../../core/services/toast.service';
import { TpButtonDirective, TpCardComponent, TpSpinnerComponent } from '../../../../shared/ui';
import { AccountNavComponent } from '../account-nav/account-nav.component';

const GENDERS: Gender[] = ['Unknown', 'Male', 'Female', 'Other'];

/**
 * A CustomerProfile isn't created at registration (see PeopleDtos.cs) — it's created lazily,
 * the first time a customer needs one. Most customers will get theirs implicitly the first
 * time they book (BookingsController resolves-or-creates one), so this screen mainly EDITS an
 * existing profile — but it still needs to handle "no profile yet" gracefully for a customer
 * who lands here before ever booking anything.
 */
@Component({
  selector: 'tp-profile',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule, AccountNavComponent, TpCardComponent, TpButtonDirective, TpSpinnerComponent],
  template: `
    <div class="tp-page tp-profile-page">
      <h2>My Account</h2>
      <tp-account-nav />

      @if (loading()) {
        <tp-spinner size="lg" />
      } @else {
        <tp-card>
          <form [formGroup]="form" (ngSubmit)="submit()">
            <div class="tp-form-grid">
              <label>
                National ID (optional)
                <input type="text" formControlName="nationalIdNumber" />
              </label>
              <label>
                Date of Birth
                <input type="date" formControlName="dateOfBirth" />
              </label>
              <label>
                Gender
                <select formControlName="gender">
                  @for (g of genders; track g) {
                    <option [value]="g">{{ g }}</option>
                  }
                </select>
              </label>
              <label>
                Emergency Contact Phone
                <input type="text" formControlName="emergencyContactPhone" />
              </label>
              <label>
                Preferred Language Code
                <input type="text" formControlName="preferredLanguageCode" placeholder="en" />
              </label>
            </div>
            <div class="tp-profile-page__actions">
              <button tpButton variant="primary" type="submit" [disabled]="saving()">
                {{ saving() ? 'Saving…' : 'Save Profile' }}
              </button>
            </div>
          </form>
        </tp-card>
      }
    </div>
  `,
  styles: [
    `
      .tp-profile-page {
        max-width: 640px;
      }

      .tp-form-grid {
        display: grid;
        grid-template-columns: repeat(2, 1fr);
        gap: var(--tp-space-4);
      }

      label {
        display: flex;
        flex-direction: column;
        gap: var(--tp-space-2);
        font-size: 13px;
        font-weight: 600;
        color: var(--tp-text-muted);
      }

      input,
      select {
        border: 1px solid var(--tp-border);
        border-radius: var(--tp-radius-sm);
        padding: 10px var(--tp-space-3);
        font-size: 14px;
        font-family: var(--tp-font-body);
        color: var(--tp-text);
      }

      .tp-profile-page__actions {
        display: flex;
        justify-content: flex-end;
        margin-top: var(--tp-space-5);
      }
    `,
  ],
})
export class ProfileComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly toast = inject(ToastService);

  protected readonly genders = GENDERS;
  protected readonly loading = signal(true);
  protected readonly saving = signal(false);
  private existingProfile: CustomerProfile | null = null;

  protected readonly form = this.fb.nonNullable.group({
    nationalIdNumber: [''],
    dateOfBirth: [''],
    gender: this.fb.nonNullable.control<Gender>('Unknown', Validators.required),
    emergencyContactPhone: [''],
    preferredLanguageCode: ['en'],
  });

  ngOnInit(): void {
    this.api.get<CustomerProfile[]>('customerprofiles').subscribe({
      next: (profiles) => {
        this.existingProfile = profiles[0] ?? null;
        if (this.existingProfile) {
          this.form.patchValue({
            nationalIdNumber: this.existingProfile.nationalIdNumber ?? '',
            dateOfBirth: this.existingProfile.dateOfBirth ?? '',
            gender: this.existingProfile.gender,
            emergencyContactPhone: this.existingProfile.emergencyContactPhone ?? '',
            preferredLanguageCode: this.existingProfile.preferredLanguageCode ?? 'en',
          });
        }
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  submit(): void {
    if (this.form.invalid) return;
    const values = this.form.getRawValue();
    this.saving.set(true);

    if (this.existingProfile) {
      const request: CustomerProfileUpdateRequest = {
        nationalIdNumber: values.nationalIdNumber || undefined,
        dateOfBirth: values.dateOfBirth || undefined,
        gender: values.gender,
        emergencyContactPhone: values.emergencyContactPhone || undefined,
        preferredLanguageCode: values.preferredLanguageCode || undefined,
        rowVersion: this.existingProfile.rowVersion,
      };
      this.api.put<CustomerProfile>(`customerprofiles/${this.existingProfile.id}`, request).subscribe({
        next: (profile) => this.onSaved(profile),
        error: () => this.saving.set(false),
      });
    } else {
      const currentUser = this.auth.currentUser();
      if (!currentUser) {
        this.saving.set(false);
        return;
      }
      const request: CustomerProfileCreateRequest = {
        userId: currentUser.userId,
        nationalIdNumber: values.nationalIdNumber || undefined,
        dateOfBirth: values.dateOfBirth || undefined,
        gender: values.gender,
        emergencyContactPhone: values.emergencyContactPhone || undefined,
        preferredLanguageCode: values.preferredLanguageCode || undefined,
      };
      this.api.post<CustomerProfile>('customerprofiles', request).subscribe({
        next: (profile) => this.onSaved(profile),
        error: () => this.saving.set(false),
      });
    }
  }

  private onSaved(profile: CustomerProfile): void {
    this.existingProfile = profile;
    this.saving.set(false);
    this.toast.success('Profile saved.');
  }
}
