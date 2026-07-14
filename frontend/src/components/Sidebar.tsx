import { useState } from 'react';
import { Link, NavLink, useLocation } from 'react-router-dom';
import type { ReactNode } from 'react';
import {
  builtinViews,
  viewHref,
  loadUserViews,
  addUserView,
  deleteUserView,
} from '../lib/presets';
import type { DashboardView, ViewPage } from '../lib/presets';
import { useFilters } from '../lib/filters';
import { useAuth } from '../lib/AuthContext';
import { USE_MOCKS, setMockMode } from '../lib/api';
import {
  ActivityIcon,
  AlertTriangleIcon,
  BellIcon,
  ListIcon,
  ClockIcon,
  GridIcon,
  BeakerIcon,
  ArrowRightIcon,
  ChevronLeftIcon,
  ChevronRightIcon,
  ChevronDownIcon,
  PlusIcon,
  XIcon,
  TrendingUpIcon,
  SunIcon,
  MoonIcon,
  MonitorIcon,
} from './icons';
import { getThemeMode, nextThemeMode, setThemeMode } from '../lib/theme';
import type { ThemeMode } from '../lib/theme';

// `adminOnly` links are hidden unless the current user has the Admin role (Issue 8.6 UI gating).
const links: { to: string; label: string; icon: ReactNode; adminOnly?: boolean }[] = [
  { to: '/health', label: 'Health', icon: <ActivityIcon /> },
  { to: '/errors', label: 'Errors', icon: <AlertTriangleIcon /> },
  { to: '/events', label: 'Events', icon: <ListIcon /> },
  { to: '/insights', label: 'Insights', icon: <TrendingUpIcon /> },
  { to: '/alerts', label: 'Alerts', icon: <BellIcon /> },
  { to: '/sessions', label: 'Sessions', icon: <ClockIcon /> },
  { to: '/admin/apps', label: 'Apps', icon: <GridIcon />, adminOnly: true },
  { to: '/admin/audit', label: 'Audit log', icon: <ListIcon />, adminOnly: true },
];

const COLLAPSE_KEY = 'observability:sidebar-collapsed';

function pageFromPath(pathname: string): ViewPage | null {
  if (pathname.startsWith('/health')) return 'health';
  if (pathname.startsWith('/errors')) return 'errors';
  if (pathname.startsWith('/events')) return 'events';
  if (pathname.startsWith('/insights')) return 'insights';
  if (pathname.startsWith('/alerts')) return 'alerts';
  if (pathname.startsWith('/sessions')) return 'sessions';
  return null;
}

const cap = (s: string) => s.charAt(0).toUpperCase() + s.slice(1);

export function Sidebar() {
  const location = useLocation();
  const { filters } = useFilters();
  const { user, isAdmin, logout } = useAuth();
  const visibleLinks = links.filter((l) => !l.adminOnly || isAdmin);

  const [collapsed, setCollapsed] = useState<boolean>(() => {
    try {
      return localStorage.getItem(COLLAPSE_KEY) === '1';
    } catch {
      return false;
    }
  });
  const [userViews, setUserViews] = useState<DashboardView[]>(loadUserViews);
  const [showSave, setShowSave] = useState(false);
  const [name, setName] = useState('');

  const currentPage = pageFromPath(location.pathname);
  const canSave = currentPage !== null && !!filters.app && !!filters.env;

  const isViewActive = (v: DashboardView) =>
    v.page === currentPage && v.app === filters.app && v.env === filters.env && v.range === filters.range;

  const toggle = () => {
    setCollapsed((c) => {
      const next = !c;
      try {
        localStorage.setItem(COLLAPSE_KEY, next ? '1' : '0');
      } catch {
        /* ignore */
      }
      return next;
    });
  };

  const handleSave = () => {
    if (!canSave || !currentPage) return;
    const label = name.trim() || `${cap(currentPage)} · ${filters.env}`;
    // Page-specific config (e.g. the Insights metrics/interval/breakdown/agg) lives in the URL —
    // capture it so reopening the view restores the exact chart, not the page defaults.
    const current = new URLSearchParams(location.search);
    const extras = new URLSearchParams();
    for (const key of ['metrics', 'interval', 'breakdown', 'agg', 'category', 'event_name']) {
      const v = current.get(key);
      if (v) extras.set(key, v);
    }
    setUserViews(
      addUserView({
        label,
        page: currentPage,
        app: filters.app,
        env: filters.env,
        range: filters.range,
        from: filters.from,
        to: filters.to,
        params: extras.size > 0 ? extras.toString() : undefined,
      }),
    );
    setName('');
    setShowSave(false);
  };

  return (
    <aside
      className={`flex shrink-0 flex-col border-r border-shell-800/60 bg-shell-900 text-shell-100 transition-[width] duration-200 ease-in-out ${
        collapsed ? 'w-[68px]' : 'w-60'
      }`}
    >
      {/* Brand + collapse toggle */}
      <div className={`flex items-center py-5 ${collapsed ? 'flex-col gap-3 px-2' : 'gap-3 px-5'}`}>
        <div className="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg bg-gradient-to-br from-brand-500 to-brand-700 text-sm font-bold text-white shadow-sm">
          AO
        </div>
        {!collapsed && (
          <div className="min-w-0 flex-1 leading-tight">
            <div className="truncate text-sm font-semibold text-white">Adaptive</div>
            <div className="truncate text-xs text-shell-400">Observability</div>
          </div>
        )}
        <button
          onClick={toggle}
          className="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg text-shell-400 transition hover:bg-shell-800 hover:text-shell-200"
          title={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
          aria-label={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
        >
          {collapsed ? <ChevronRightIcon /> : <ChevronLeftIcon />}
        </button>
      </div>

      <nav className="flex flex-1 flex-col gap-0.5 overflow-y-auto px-3 pb-4 scrollbar-thin">
        {!collapsed && <SectionLabel>Dashboards</SectionLabel>}
        {visibleLinks.map((l) => (
          <NavLink
            key={l.to}
            to={l.to}
            title={collapsed ? l.label : undefined}
            className={({ isActive }) =>
              `group relative flex items-center rounded-lg py-2 text-sm font-medium transition ${
                collapsed ? 'justify-center px-0' : 'gap-3 px-3'
              } ${
                isActive ? 'bg-shell-800 text-white' : 'text-shell-400 hover:bg-shell-800/60 hover:text-shell-100'
              }`
            }
          >
            {({ isActive }) => (
              <>
                {isActive && (
                  <span className="absolute left-0 top-1/2 h-5 w-0.5 -translate-y-1/2 rounded-full bg-brand-400" />
                )}
                <span className={isActive ? 'text-brand-300' : 'text-shell-500 group-hover:text-shell-300'}>
                  {l.icon}
                </span>
                {!collapsed && l.label}
              </>
            )}
          </NavLink>
        ))}

        {!collapsed && (
          <CollapsibleSection title="Quick views" storageKey="observability:section:quick-views">
            {builtinViews.map((v) => (
              <ViewLink key={v.id} view={v} active={isViewActive(v)} />
            ))}
            {userViews.map((v) => (
              <ViewLink
                key={v.id}
                view={v}
                active={isViewActive(v)}
                onDelete={() => setUserViews(deleteUserView(v.id))}
              />
            ))}

            {showSave ? (
                <div className="mt-1 px-1">
                  <input
                    autoFocus
                    value={name}
                    onChange={(e) => setName(e.target.value)}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter') handleSave();
                      if (e.key === 'Escape') {
                        setShowSave(false);
                        setName('');
                      }
                    }}
                    placeholder="View name"
                    className="w-full rounded-md border border-shell-700 bg-shell-800 px-2 py-1 text-xs text-shell-100 placeholder:text-shell-500"
                  />
                  <div className="mt-1 flex gap-1">
                    <button
                      onClick={handleSave}
                      className="flex-1 rounded-md bg-brand-600 px-2 py-1 text-xs font-medium text-white transition hover:bg-brand-500"
                    >
                      Save
                    </button>
                    <button
                      onClick={() => {
                        setShowSave(false);
                        setName('');
                      }}
                      className="rounded-md px-2 py-1 text-xs text-shell-400 transition hover:text-shell-200"
                    >
                      Cancel
                    </button>
                  </div>
                </div>
              ) : (
                <button
                  onClick={() => setShowSave(true)}
                  disabled={!canSave}
                  title={canSave ? 'Save the current app / environment / range as a view' : 'Pick an app & environment first'}
                  className="mt-1 flex items-center gap-2 rounded-lg px-3 py-1.5 text-xs font-medium text-shell-400 transition hover:bg-shell-800/60 hover:text-shell-200 disabled:cursor-not-allowed disabled:opacity-40"
                >
                  <PlusIcon className="h-3.5 w-3.5" /> Save current view
                </button>
              )}
            </CollapsibleSection>
        )}
      </nav>

      {/* Signed-in user + sign out */}
      {user && (
        <div className="border-t border-shell-800/60 px-3 py-3">
          {collapsed ? (
            <button
              onClick={logout}
              className="flex w-full items-center justify-center rounded-lg py-2 text-shell-400 transition hover:bg-shell-800/60 hover:text-shell-200"
              title={`${user.email} (${user.role}) — sign out`}
              aria-label="Sign out"
            >
              <span className="flex h-6 w-6 items-center justify-center rounded-full bg-shell-700 text-[10px] font-semibold text-shell-200">
                {user.email.charAt(0).toUpperCase()}
              </span>
            </button>
          ) : (
            <div className="flex items-center gap-3 rounded-lg px-1 py-1">
              <span className="flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-shell-700 text-[11px] font-semibold text-shell-200">
                {user.email.charAt(0).toUpperCase()}
              </span>
              <span className="min-w-0 flex-1 leading-tight">
                <span className="block truncate text-xs font-medium text-shell-200">{user.email}</span>
                <span className="block text-[11px] text-shell-500">{user.role}</span>
              </span>
              <button
                onClick={logout}
                className="shrink-0 rounded-md px-2 py-1 text-[11px] font-medium text-shell-400 transition hover:bg-shell-800 hover:text-shell-200"
                title="Sign out"
              >
                Sign out
              </button>
            </div>
          )}
        </div>
      )}

      {/* Theme toggle (light → dark → system) */}
      <div className="border-t border-shell-800/60 px-3 py-3">
        <ThemeToggle collapsed={collapsed} />
      </div>

      {/* Demo-data toggle */}
      <div className="border-t border-shell-800/60 px-3 py-3">
        {collapsed ? (
          <button
            onClick={() => setMockMode(!USE_MOCKS)}
            className="flex w-full items-center justify-center rounded-lg py-2 transition hover:bg-shell-800/60"
            title={USE_MOCKS ? 'Demo data: on (click to use live backend)' : 'Demo data: off (click to show sample reports)'}
            aria-label="Toggle demo data"
          >
            <BeakerIcon className={`h-4 w-4 ${USE_MOCKS ? 'text-amber-300' : 'text-shell-500'}`} />
          </button>
        ) : (
          <button
            onClick={() => setMockMode(!USE_MOCKS)}
            className="flex w-full items-center gap-3 rounded-lg px-3 py-2 text-left text-xs transition hover:bg-shell-800/60"
            title="Toggle sample data for the dashboard"
          >
            <BeakerIcon className={`h-4 w-4 shrink-0 ${USE_MOCKS ? 'text-amber-300' : 'text-shell-500'}`} />
            <span className="min-w-0 flex-1">
              <span className="block font-medium text-shell-200">Demo data</span>
              <span className="block truncate text-[11px] text-shell-500">
                {USE_MOCKS ? 'Showing sample reports' : 'Using live backend'}
              </span>
            </span>
            <Switch on={USE_MOCKS} />
          </button>
        )}
      </div>
    </aside>
  );
}

function ViewLink({
  view,
  active,
  onDelete,
}: {
  view: DashboardView;
  active: boolean;
  onDelete?: () => void;
}) {
  return (
    <div className="group/item relative">
      <Link
        to={viewHref(view)}
        className={`flex items-center justify-between rounded-lg py-1.5 pl-3 text-xs transition ${
          onDelete ? 'pr-8' : 'pr-3'
        } ${active ? 'bg-shell-800 text-white' : 'text-shell-400 hover:bg-shell-800/60 hover:text-shell-200'}`}
      >
        <span className="truncate">{view.label}</span>
        {!onDelete && (
          <ArrowRightIcon className="h-3 w-3 shrink-0 opacity-0 transition group-hover/item:opacity-60" />
        )}
      </Link>
      {onDelete && (
        <button
          onClick={(e) => {
            e.preventDefault();
            onDelete();
          }}
          className="absolute right-1.5 top-1/2 -translate-y-1/2 rounded p-1 text-shell-500 opacity-0 transition hover:bg-shell-700 hover:text-rose-300 group-hover/item:opacity-100"
          title="Delete view"
          aria-label="Delete view"
        >
          <XIcon className="h-3 w-3" />
        </button>
      )}
    </div>
  );
}

function SectionLabel({ children, className = '' }: { children: ReactNode; className?: string }) {
  return (
    <div className={`px-3 pb-1 pt-2 text-[10px] font-semibold uppercase tracking-wider text-shell-500 ${className}`}>
      {children}
    </div>
  );
}

/** A collapsible sidebar section whose open/closed state persists in localStorage. */
function CollapsibleSection({
  title,
  storageKey,
  children,
}: {
  title: string;
  storageKey: string;
  children: ReactNode;
}) {
  const [open, setOpen] = useState<boolean>(() => {
    try {
      return localStorage.getItem(storageKey) !== '0';
    } catch {
      return true;
    }
  });

  const toggle = () => {
    setOpen((o) => {
      const next = !o;
      try {
        localStorage.setItem(storageKey, next ? '1' : '0');
      } catch {
        /* ignore */
      }
      return next;
    });
  };

  return (
    <div className="mt-5">
      <button
        onClick={toggle}
        aria-expanded={open}
        className="group flex w-full items-center justify-between rounded-md px-3 pb-1 pt-2 text-[10px] font-semibold uppercase tracking-wider text-shell-500 transition hover:text-shell-300"
      >
        <span>{title}</span>
        <ChevronDownIcon
          className={`h-3 w-3 text-shell-600 transition-transform duration-200 group-hover:text-shell-400 ${
            open ? '' : '-rotate-90'
          }`}
        />
      </button>
      {open && <div className="flex flex-col gap-0.5">{children}</div>}
    </div>
  );
}

const THEME_META: Record<ThemeMode, { label: string; icon: (cls: string) => ReactNode }> = {
  light: { label: 'Light', icon: (cls) => <SunIcon className={cls} /> },
  dark: { label: 'Dark', icon: (cls) => <MoonIcon className={cls} /> },
  system: { label: 'System', icon: (cls) => <MonitorIcon className={cls} /> },
};

function ThemeToggle({ collapsed }: { collapsed: boolean }) {
  const [mode, setMode] = useState<ThemeMode>(getThemeMode);
  const meta = THEME_META[mode];
  const cycle = () => {
    const next = nextThemeMode(mode);
    setThemeMode(next);
    setMode(next);
  };

  if (collapsed) {
    return (
      <button
        onClick={cycle}
        className="flex w-full items-center justify-center rounded-lg py-2 text-shell-400 transition hover:bg-shell-800/60 hover:text-shell-200"
        title={`Theme: ${meta.label} (click to change)`}
        aria-label={`Theme: ${meta.label}. Activate to cycle theme.`}
      >
        {meta.icon('h-4 w-4')}
      </button>
    );
  }

  return (
    <button
      onClick={cycle}
      className="flex w-full items-center gap-3 rounded-lg px-3 py-2 text-left text-xs transition hover:bg-shell-800/60"
      title="Cycle light / dark / system"
    >
      <span className="shrink-0 text-shell-400">{meta.icon('h-4 w-4')}</span>
      <span className="min-w-0 flex-1">
        <span className="block font-medium text-shell-200">Theme</span>
        <span className="block truncate text-[11px] text-shell-500">{meta.label}</span>
      </span>
    </button>
  );
}

function Switch({ on }: { on: boolean }) {
  return (
    <span
      className={`relative inline-flex h-4 w-7 shrink-0 items-center rounded-full transition ${
        on ? 'bg-amber-400' : 'bg-shell-600'
      }`}
    >
      <span
        className={`inline-block h-3 w-3 transform rounded-full bg-shell-100 shadow transition ${
          on ? 'translate-x-3.5' : 'translate-x-0.5'
        }`}
      />
    </span>
  );
}
