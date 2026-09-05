import { CommonModule } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { CustomerAddress, CustomerAddressCreateRequest, CustomerAddressUpdateRequest } from '@ticketportal-mono/models';
import { ApiService } from '../../../../core/services/api.service';
import { ToastService } from '../../../../core/services/toast.service';
import { TpButtonDirective, TpCardComponent, TpEmptyStateComponent, TpModalComponent, TpSpinnerComponent } from '../../../../shared/ui';
import { AccountNavComponent } from '../account-nav/account-nav.component';

@Component({
  selector: 'tp-addresses',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule, AccountNavComponent, TpCardComponent, TpButtonDirective, TpEmptyStateComponent, TpModalComponent, TpSpinnerComponent],
  template: `
    <div class="tp-page tp-addresses-page">
      <h2>My Account</h2>
      <tp-account-nav />

      <div class="tp-addresses-page__header">
        <h3>Saved Addresses</h3>
        <button tpButton variant="primary" size="sm" (click)="openCreate()">Add Address</button>
      </div>

      @if (loading()) {
        <tp-spinner size="lg" />
      } @else if (addresses().length === 0) {
        <tp-empty-state title="No saved addresses" message="Save an address to speed up future bookings." />
      } @else {
        <div class="tp-address-grid">
          @for (a of addresses(); track a.id) {
            <tp-card class="tp-address-card">
              <div class="tp-address-card__header">
                <strong>{{ a.label }}</strong>
                @if (a.isDefault) {
                  <span class="tp-default-badge">Default</span>
                }
              </div>
              <p class="tp-muted">{{ a.addressLine }}</p>
              <p class="tp-muted">{{ a.city }}, {{ a.district }}, {{ a.country }}</p>
              <div class="tp-address-card__actions">
                <button tpButton variant="ghost" size="sm" (click)="openEdit(a)">Edit</button>
                <button tpButton variant="ghost" size="sm" (click)="remove(a)">Delete</button>
              </div>
            </tp-card>
          }
        </div>
      }
    </div>

    <tp-modal [open]="modalOpen()" [title]="editing() ? 'Edit Address' : 'Add Address'" (closed)="modalOpen.set(false)">
      <form [formGroup]="form" class="tp-address-form">
        <label>
          Label
          <input type="text" formControlName="label" placeholder="Home, Office…" />
        </label>
        <label>
          Address Line
          <input type="text" formControlName="addressLine" />
        </label>
        <label>
          City
          <input type="text" formControlName="city" />
        </label>
        <label>
          District
          <input type="text" formControlName="district" />
        </label>
        <label>
          Country
          <input type="text" formControlName="country" />
        </label>
        <label class="tp-checkbox-label">
          <input type="checkbox" formControlName="isDefault" />
          Set as default address
        </label>
      </form>
      <div modal-footer>
        <button tpButton variant="secondary" (click)="modalOpen.set(false)">Back</button>
        <button tpButton variant="primary" [disabled]="form.invalid || saving()" (click)="save()">
          {{ saving() ? 'Saving…' : 'Save' }}
        </button>
      </div>
    </tp-modal>
  `,
  styles: [
    `
      .tp-addresses-page__header {
        display: flex;
        justify-content: space-between;
        align-items: center;
        margin-bottom: var(--tp-space-4);
      }

      .tp-addresses-page__header h3 {
        margin: 0;
      }

      .tp-address-grid {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(240px, 1fr));
        gap: var(--tp-space-4);
      }

      .tp-address-card__header {
        display: flex;
        justify-content: space-between;
        align-items: center;
        margin-bottom: var(--tp-space-2);
      }

      .tp-default-badge {
        font-size: 11px;
        font-weight: 700;
        color: var(--tp-text-on-yellow);
        background: var(--tp-yellow);
        border-radius: var(--tp-radius-sm);
        padding: 2px var(--tp-space-2);
      }

      .tp-address-card__actions {
        display: flex;
        gap: var(--tp-space-2);
        margin-top: var(--tp-space-3);
      }

      .tp-address-form {
        display: flex;
        flex-direction: column;
        gap: var(--tp-space-3);
      }

      .tp-address-form label {
        display: flex;
        flex-direction: column;
        gap: var(--tp-space-2);
        font-size: 13px;
        font-weight: 600;
        color: var(--tp-text-muted);
      }

      .tp-address-form input[type='text'] {
        border: 1px solid var(--tp-border);
        border-radius: var(--tp-radius-sm);
        padding: 10px var(--tp-space-3);
        font-size: 14px;
        font-family: var(--tp-font-body);
      }

      .tp-checkbox-label {
        flex-direction: row !important;
        align-items: center;
        gap: var(--tp-space-2) !important;
      }
    `,
  ],
})
export class AddressesComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);

  protected readonly loading = signal(true);
  protected readonly saving = signal(false);
  protected readonly modalOpen = signal(false);
  protected readonly editing = signal<CustomerAddress | null>(null);
  protected readonly addresses = signal<CustomerAddress[]>([]);

  protected readonly form = this.fb.nonNullable.group({
    label: ['', Validators.required],
    addressLine: ['', Validators.required],
    city: ['', Validators.required],
    district: ['', Validators.required],
    country: ['Bangladesh', Validators.required],
    isDefault: [false],
  });

  ngOnInit(): void {
    this.load();
  }

  private load(): void {
    this.loading.set(true);
    this.api.get<CustomerAddress[]>('customeraddresses').subscribe({
      next: (addresses) => {
        this.addresses.set(addresses);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  openCreate(): void {
    this.editing.set(null);
    this.form.reset({ label: '', addressLine: '', city: '', district: '', country: 'Bangladesh', isDefault: false });
    this.modalOpen.set(true);
  }

  openEdit(address: CustomerAddress): void {
    this.editing.set(address);
    this.form.reset({
      label: address.label,
      addressLine: address.addressLine,
      city: address.city,
      district: address.district,
      country: address.country,
      isDefault: address.isDefault,
    });
    this.modalOpen.set(true);
  }

  save(): void {
    if (this.form.invalid) return;
    const values = this.form.getRawValue();
    const editing = this.editing();
    this.saving.set(true);

    if (editing) {
      const request: CustomerAddressUpdateRequest = { ...values, rowVersion: editing.rowVersion };
      this.api.put<CustomerAddress>(`customeraddresses/${editing.id}`, request).subscribe({
        next: () => this.onSaved(),
        error: () => this.saving.set(false),
      });
    } else {
      const request: CustomerAddressCreateRequest = values;
      this.api.post<CustomerAddress>('customeraddresses', request).subscribe({
        next: () => this.onSaved(),
        error: () => this.saving.set(false),
      });
    }
  }

  private onSaved(): void {
    this.saving.set(false);
    this.modalOpen.set(false);
    this.toast.success('Address saved.');
    this.load();
  }

  remove(address: CustomerAddress): void {
    this.api.delete(`customeraddresses/${address.id}`).subscribe(() => {
      this.toast.success('Address removed.');
      this.load();
    });
  }
}
