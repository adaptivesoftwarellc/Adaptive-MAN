import type { ReactNode } from 'react';
import { Navigate, Outlet, Route, Routes, useLocation } from 'react-router-dom';
import { Sidebar } from './components/Sidebar';
import { FilterBar } from './components/FilterBar';
import { ErrorBoundary } from './components/ErrorBoundary';
import { HealthPage } from './pages/HealthPage';
import { ErrorsPage } from './pages/ErrorsPage';
import { EventsPage } from './pages/EventsPage';
import { SessionsPage } from './pages/SessionsPage';
import { SessionTimelinePage } from './pages/SessionTimelinePage';
import { AdminAppsPage } from './pages/AdminAppsPage';
import { AdminKeysPage } from './pages/AdminKeysPage';
import { AdminAuditPage } from './pages/AdminAuditPage';
import { LoginPage } from './pages/LoginPage';
import { useAuth } from './lib/AuthContext';

// Issue 8.6 — gate the app behind authentication; the Admin area additionally requires the Admin role.
function RequireAuth({ children }: { children: ReactNode }) {
  const { isAuthenticated } = useAuth();
  const location = useLocation();
  if (!isAuthenticated) return <Navigate to="/login" replace state={{ from: location }} />;
  return <>{children}</>;
}

function RequireAdmin({ children }: { children: ReactNode }) {
  const { isAdmin } = useAuth();
  if (!isAdmin) return <Navigate to="/health" replace />;
  return <>{children}</>;
}

function Layout() {
  const location = useLocation();
  return (
    <div className="flex h-screen bg-slate-50 text-slate-900">
      <Sidebar />
      <div className="flex flex-1 flex-col overflow-hidden">
        <FilterBar />
        <main className="flex-1 overflow-y-auto scrollbar-thin">
          <div className="mx-auto max-w-7xl">
            {/* Keyed by route so navigating away from a crashed page resets the boundary. */}
            <ErrorBoundary key={location.pathname}>
              <Outlet />
            </ErrorBoundary>
          </div>
        </main>
      </div>
    </div>
  );
}

export default function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route
        path="/"
        element={
          <RequireAuth>
            <Layout />
          </RequireAuth>
        }
      >
        <Route index element={<Navigate to="/health" replace />} />
        <Route path="health" element={<HealthPage />} />
        <Route path="errors" element={<ErrorsPage />} />
        <Route path="events" element={<EventsPage />} />
        <Route path="sessions" element={<SessionsPage />} />
        <Route path="sessions/:sessionId" element={<SessionTimelinePage />} />
        <Route
          path="admin/apps"
          element={
            <RequireAdmin>
              <AdminAppsPage />
            </RequireAdmin>
          }
        />
        <Route
          path="admin/keys/:slug/:env"
          element={
            <RequireAdmin>
              <AdminKeysPage />
            </RequireAdmin>
          }
        />
        <Route
          path="admin/audit"
          element={
            <RequireAdmin>
              <AdminAuditPage />
            </RequireAdmin>
          }
        />
        <Route path="*" element={<Navigate to="/health" replace />} />
      </Route>
    </Routes>
  );
}
