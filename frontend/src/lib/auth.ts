// Issue 8.6 — client-side auth state. Leaf module: only touches localStorage and the window event
// bus, so api.ts can depend on it without a cycle. The actual login network call lives in api.ts.

export type UserRole = 'Admin' | 'Developer' | 'Viewer' | 'AppOwner';

export interface AuthUser {
  email: string;
  display_name: string;
  role: UserRole;
}

const TOKEN_KEY = 'observability:token';
const USER_KEY = 'observability:user';

/** Fired when a request comes back 401 so the AuthProvider can drop to the login screen. */
export const UNAUTHORIZED_EVENT = 'observability:unauthorized';

export function getToken(): string | null {
  try {
    return localStorage.getItem(TOKEN_KEY);
  } catch {
    return null;
  }
}

export function getStoredUser(): AuthUser | null {
  try {
    const raw = localStorage.getItem(USER_KEY);
    return raw ? (JSON.parse(raw) as AuthUser) : null;
  } catch {
    return null;
  }
}

export function setSession(token: string, user: AuthUser): void {
  try {
    localStorage.setItem(TOKEN_KEY, token);
    localStorage.setItem(USER_KEY, JSON.stringify(user));
  } catch {
    /* ignore */
  }
}

export function clearSession(): void {
  try {
    localStorage.removeItem(TOKEN_KEY);
    localStorage.removeItem(USER_KEY);
  } catch {
    /* ignore */
  }
}

export function canAccessAdmin(user: AuthUser | null): boolean {
  return user?.role === 'Admin';
}

/** Clears the session and notifies listeners. Called by the API client on a 401. */
export function notifyUnauthorized(): void {
  clearSession();
  try {
    window.dispatchEvent(new Event(UNAUTHORIZED_EVENT));
  } catch {
    /* ignore */
  }
}
