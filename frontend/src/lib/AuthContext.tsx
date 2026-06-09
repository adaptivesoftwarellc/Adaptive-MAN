import { createContext, useContext, useEffect, useState, type ReactNode } from 'react';
import { api, USE_MOCKS } from './api';
import {
  canAccessAdmin,
  clearSession,
  getStoredUser,
  getToken,
  setSession,
  UNAUTHORIZED_EVENT,
  type AuthUser,
} from './auth';

interface AuthState {
  user: AuthUser | null;
  isAuthenticated: boolean;
  isAdmin: boolean;
  login: (email: string, password: string) => Promise<void>;
  logout: () => void;
}

// In demo/mock mode the dashboard runs fully client-side with no backend, so there's nothing to log
// in against — synthesize an admin so the existing demo UX (including the admin nav) is unchanged.
const MOCK_USER: AuthUser = { email: 'demo@adaptive.local', display_name: 'Demo (mock data)', role: 'Admin' };

const AuthCtx = createContext<AuthState | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<AuthUser | null>(() => (USE_MOCKS ? MOCK_USER : getStoredUser()));

  useEffect(() => {
    if (USE_MOCKS) return;
    const onUnauthorized = () => setUser(null);
    window.addEventListener(UNAUTHORIZED_EVENT, onUnauthorized);
    return () => window.removeEventListener(UNAUTHORIZED_EVENT, onUnauthorized);
  }, []);

  const login = async (email: string, password: string) => {
    const res = await api.login(email, password);
    setSession(res.token, res.user);
    setUser(res.user);
  };

  const logout = () => {
    clearSession();
    setUser(null);
  };

  const isAuthenticated = USE_MOCKS || (user !== null && getToken() !== null);

  return (
    <AuthCtx.Provider value={{ user, isAuthenticated, isAdmin: canAccessAdmin(user), login, logout }}>
      {children}
    </AuthCtx.Provider>
  );
}

// eslint-disable-next-line react-refresh/only-export-components -- provider + its hook live together by convention
export function useAuth(): AuthState {
  const ctx = useContext(AuthCtx);
  if (!ctx) throw new Error('useAuth must be used within an AuthProvider');
  return ctx;
}
