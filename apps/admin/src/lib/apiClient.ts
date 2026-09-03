import type { ApiError } from '@ticketportal-mono/models';

const BASE_URL = import.meta.env.VITE_API_BASE_URL as string;
const STORAGE_KEY = 'tp_admin_auth';

interface StoredSession {
  token: string;
  expiresAtUtc: string;
  userId: string;
  userName: string;
  roles: string[];
}

export function getStoredSession(): StoredSession | null {
  const raw = localStorage.getItem(STORAGE_KEY);
  if (!raw) return null;
  try {
    const parsed: StoredSession = JSON.parse(raw);
    if (new Date(parsed.expiresAtUtc).getTime() <= Date.now()) {
      localStorage.removeItem(STORAGE_KEY);
      return null;
    }
    return parsed;
  } catch {
    localStorage.removeItem(STORAGE_KEY);
    return null;
  }
}

export function storeSession(session: StoredSession): void {
  localStorage.setItem(STORAGE_KEY, JSON.stringify(session));
}

export function clearSession(): void {
  localStorage.removeItem(STORAGE_KEY);
}

/**
 * Same job as Angular's ApiService + ErrorInterceptor combined, since React
 * has no interceptor pipeline of its own: prefixes VITE_API_BASE_URL,
 * attaches the bearer token, and normalizes the backend's several error
 * body shapes into one ApiError (see libs/shared/models for that shape and
 * WHY there's more than one — same backend, same caveats as the Angular app).
 *
 * Usage:
 *   const bookings = await apiFetch<Booking[]>('bookings');
 *   const result = await apiFetch<AuthResponse>('account/login', { method: 'POST', body: loginRequest });
 */
export async function apiFetch<T>(
  path: string,
  options: { method?: string; body?: unknown; skipAuth?: boolean } = {},
): Promise<T> {
  const session = getStoredSession();
  const headers: Record<string, string> = { 'Content-Type': 'application/json' };
  if (session && !options.skipAuth) {
    headers['Authorization'] = `Bearer ${session.token}`;
  }

  const cleanPath = path.startsWith('/') ? path.slice(1) : path;
  const response = await fetch(`${BASE_URL}/${cleanPath}`, {
    method: options.method ?? 'GET',
    headers,
    body: options.body !== undefined ? JSON.stringify(options.body) : undefined,
  });

  if (response.status === 401) {
    clearSession();
    window.location.href = '/login';
  }

  if (!response.ok) {
    throw await normalizeError(response);
  }

  if (response.status === 204) {
    return undefined as T;
  }

  return (await response.json()) as T;
}

async function normalizeError(response: Response): Promise<ApiError> {
  const status = response.status;
  let body: unknown;
  try {
    body = await response.json();
  } catch {
    body = null;
  }

  if (body && typeof body === 'object' && typeof (body as any).message === 'string') {
    return { status, message: (body as any).message };
  }

  if (body && typeof body === 'object' && (body as any).errors && typeof (body as any).errors === 'object') {
    const fieldErrors = (body as any).errors as Record<string, string[]>;
    const firstMessage = Object.values(fieldErrors).flat()[0];
    return { status, message: firstMessage || (body as any).title || 'Please check the highlighted fields.', fieldErrors };
  }

  if (Array.isArray(body) && body.every((x) => typeof x === 'string')) {
    return { status, message: body.join(' ') };
  }

  if (typeof body === 'string' && body.trim().length > 0) {
    return { status, message: body };
  }

  return { status, message: response.statusText || 'Something went wrong. Please try again.' };
}
