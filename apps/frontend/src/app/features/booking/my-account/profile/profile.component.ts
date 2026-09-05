import { CommonModule } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { CustomerProfile, CustomerProfileCreateRequest, CustomerProfileUpdateRequest, Gender } from '@ticketportal-mono/models';
import { ApiService } from '../../../../core/services/api.service';
import { AuthService } from '../../../../core/services/auth.service';
import { ToastService } from '../../../../core/services/toast.service';
import { TpButtonDirective, TpCardComponent, TpSpinnerComponent } from '../../../../shared/ui';
import { AccountNavComponent } from '../account-nav/account-nav.component';

const GENDERS: Gender[] = ['Unknown', 'Male', 'Female', 'Other'];

/** 'YYYY-MM-DD', built from local date parts (not toISOString) so the day never shifts across a UTC boundary. */
function toIsoDateString(value: Date | null): string | undefined {
  if (!value) return undefined;
  const y = value.getFullYear();
  const m = String(value.getMonth() + 1).padStart(2, '0');
  const d = String(value.getDate()).padStart(2, '0');
  return `${y}-${m}-${d}`;
}

function parseIsoDateString(value: string | null | undefined): Date | null {
  if (!value) return null;
  const [y, m, d] = value.slice(0, 10).split('-').map(Number);
  if (!y || !m || !d) return null;
  return new Date(y, m - 1, d);
}

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
  imports: [
    CommonModule,
    ReactiveFormsModule,
    AccountNavComponent,
    TpCardComponent,
    TpButtonDirective,
    TpSpinnerComponent,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatDatepickerModule,
  ],
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
              <mat-form-field appearance="outline">
                <mat-label>National ID (optional)</mat-label>
                <input matInput formControlName="nationalIdNumber" />
              </mat-form-field>

              <mat-form-field appearance="outline">
                <mat-label>Date of Birth</mat-label>
                <input matInput [matDatepicker]="dobPicker" formControlName="dateOfBirth" />
                <mat-datepicker-toggle matIconSuffix [for]="dobPicker" />
                <mat-datepicker #dobPicker startView="multi-year" />
              </mat-form-field>

              <mat-form-field appearance="outline">
                <mat-label>Gender</mat-label>
                <mat-select formControlName="gender">
                  @for (g of genders; track g) {
                    <mat-option [value]="g">{{ g }}</mat-option>
                  }
                </mat-select>
              </mat-form-field>

              <mat-form-field appearance="outline">
                <mat-label>Emergency Contact Phone</mat-label>
                <input matInput formControlName="emergencyContactPhone" />
              </mat-form-field>

              <mat-form-field appearance="outline">
                <mat-label>Preferred Language Code</mat-label>
                <input matInput formControlName="preferredLanguageCode" placeholder="en" />
              </mat-form-field>
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
        gap: var(--tp-space-2) var(--tp-space-4);
      }

      .tp-form-grid mat-form-field {
        width: 100%;
      }

      .tp-profile-page__actions {
        display: flex;
        justify-content: flex-end;
        margin-top: var(--tp-space-3);
      }

      @media (max-width: 560px) {
        .tp-form-grid {
          grid-template-columns: 1fr;
        }
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
    dateOfBirth: this.fb.control<Date | null>(null),
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
            dateOfBirth: parseIsoDateString(this.existingProfile.dateOfBirth),
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
    const dateOfBirth = toIsoDateString(values.dateOfBirth);
    this.saving.set(true);

    if (this.existingProfile) {
      const request: CustomerProfileUpdateRequest = {
        nationalIdNumber: values.nationalIdNumber || undefined,
        dateOfBirth,
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
        dateOfBirth,
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
