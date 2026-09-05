import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, tap } from 'rxjs';
import { ApiService } from './api.service';
import { AppRole, AuthResponse, CurrentUser, LoginRequest, RegisterRequest } from '@ticketportal-mono/models';

const STORAGE_KEY = 'tp_auth';

/**
 * Central auth/session service. Every other piece should go through this —
 * never read localStorage directly, never decode the JWT yourself.
 *
 * State is exposed as signals (`currentUser`, `isAuthenticated`) so template
 * bindings and guards stay in sync without manual subscriptions.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly api = inject(ApiService);

  private readonly _currentUser = signal<CurrentUser | null>(this.restoreSession());

  /** The logged-in user, or null. Read-only signal — don't mutate directly. */
  readonly currentUser = this._currentUser.asReadonly();

  readonly isAuthenticated = computed(() => {
    const user = this._currentUser();
    return !!user && new Date(user.expiresAtUtc).getTime() > Date.now();
  });

  /** POST /api/account/register. Every self-signup account lands in the "Customer" role. */
  register(request: RegisterRequest): Observable<string> {
    return this.api.post<string>('account/register', request);
  }

  /** POST /api/account/login. Persists the session and updates `currentUser` on success. */
  login(request: LoginRequest): Observable<AuthResponse> {
    return this.api.post<AuthResponse>('account/login', request).pipe(
      tap((response) => this.persistSession(response)),
    );
  }

  logout(): void {
    localStorage.removeItem(STORAGE_KEY);
    this._currentUser.set(null);
  }

  getToken(): string | null {
    const user = this._currentUser();
    if (!user) return null;
    return this.readRawToken();
  }

  /** True if the current user has ANY of the given roles. */
  hasRole(...roles: AppRole[]): boolean {
    const user = this._currentUser();
    if (!user) return false;
    return roles.some((role) => user.roles.includes(role));
  }

  /** Where to send someone right after login, based on their primary role. */
  homeRouteFor(user: CurrentUser): string {
    if (user.roles.includes('Admin')) return '/admin';
    if (user.roles.includes('Staff')) return '/counter';
    if (user.roles.includes('Operator')) return '/operator';
    return '/search';
  }

  private persistSession(response: AuthResponse): void {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(response));
    this._currentUser.set({
      userId: response.userId,
      userName: response.userName,
      roles: response.roles,
      expiresAtUtc: response.expiresAtUtc,
    });
  }

  private restoreSession(): CurrentUser | null {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return null;

    try {
      const parsed: AuthResponse = JSON.parse(raw);
      if (new Date(parsed.expiresAtUtc).getTime() <= Date.now()) {
        localStorage.removeItem(STORAGE_KEY);
        return null;
      }
      return {
        userId: parsed.userId,
        userName: parsed.userName,
        roles: parsed.roles,
        expiresAtUtc: parsed.expiresAtUtc,
      };
    } catch {
      localStorage.removeItem(STORAGE_KEY);
      return null;
    }
  }

  private readRawToken(): string | null {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return null;
    try {
      const parsed: AuthResponse = JSON.parse(raw);
      return parsed.token;
    } catch {
      return null;
    }
  }
}
