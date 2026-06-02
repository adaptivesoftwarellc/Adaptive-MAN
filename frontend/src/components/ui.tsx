/** Shared, reusable UI primitives so every page stays visually consistent. */
import { useEffect } from 'react';
import type { ReactNode } from 'react';
import { RefreshIcon, XIcon } from './icons';

// ---------------------------------------------------------------------------
// PageHeader
// ---------------------------------------------------------------------------

export function PageHeader({
  title,
  description,
  actions,
}: {
  title: string;
  description?: string;
  actions?: ReactNode;
}) {
  return (
    <div className="mb-5 flex flex-wrap items-start justify-between gap-3">
      <div>
        <h1 className="text-xl font-semibold tracking-tight text-slate-900">{title}</h1>
        {description && <p className="mt-0.5 text-sm text-slate-500">{description}</p>}
      </div>
      {actions && <div className="flex items-center gap-2">{actions}</div>}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Badge
// ---------------------------------------------------------------------------

export type BadgeColor = 'gray' | 'red' | 'amber' | 'green' | 'blue' | 'indigo' | 'purple';

const BADGE_STYLES: Record<BadgeColor, string> = {
  gray: 'bg-slate-100 text-slate-600 ring-slate-200',
  red: 'bg-rose-50 text-rose-700 ring-rose-200',
  amber: 'bg-amber-50 text-amber-700 ring-amber-200',
  green: 'bg-emerald-50 text-emerald-700 ring-emerald-200',
  blue: 'bg-sky-50 text-sky-700 ring-sky-200',
  indigo: 'bg-brand-50 text-brand-700 ring-brand-200',
  purple: 'bg-purple-50 text-purple-700 ring-purple-200',
};

export function Badge({
  children,
  color = 'gray',
  className = '',
}: {
  children: ReactNode;
  color?: BadgeColor;
  className?: string;
}) {
  return (
    <span
      className={`inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-xs font-medium ring-1 ring-inset ${BADGE_STYLES[color]} ${className}`}
    >
      {children}
    </span>
  );
}

// ---------------------------------------------------------------------------
// Card surface
// ---------------------------------------------------------------------------

export function Panel({ children, className = '' }: { children: ReactNode; className?: string }) {
  return (
    <div className={`rounded-xl border border-slate-200 bg-white shadow-card ${className}`}>
      {children}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Skeleton
// ---------------------------------------------------------------------------

export function Skeleton({ className = '' }: { className?: string }) {
  return <div className={`shimmer rounded-md bg-slate-200/70 ${className}`} />;
}

// ---------------------------------------------------------------------------
// Modal — centered dialog with backdrop, click-outside and Escape to close.
// ---------------------------------------------------------------------------

const MODAL_WIDTHS = {
  sm: 'max-w-md',
  md: 'max-w-lg',
  lg: 'max-w-2xl',
} as const;

export function Modal({
  header,
  onClose,
  size = 'md',
  children,
}: {
  header: ReactNode;
  onClose: () => void;
  size?: keyof typeof MODAL_WIDTHS;
  children: ReactNode;
}) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [onClose]);

  return (
    <div
      className="fixed inset-0 z-30 flex animate-backdrop-in items-center justify-center bg-slate-900/40 p-4 backdrop-blur-sm"
      onClick={onClose}
      role="dialog"
      aria-modal="true"
    >
      <div
        className={`flex max-h-[85vh] w-full ${MODAL_WIDTHS[size]} animate-scale-in flex-col overflow-hidden rounded-2xl bg-white shadow-2xl`}
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex shrink-0 items-start justify-between border-b border-slate-200 px-6 py-4">
          <div>{header}</div>
          <button
            onClick={onClose}
            className="-mr-1.5 rounded-lg p-1.5 text-slate-400 transition hover:bg-slate-100 hover:text-slate-600"
            aria-label="Close"
          >
            <XIcon />
          </button>
        </div>
        <div className="min-h-0 flex-1 overflow-y-auto scrollbar-thin">{children}</div>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// EmptyState
// ---------------------------------------------------------------------------

export function EmptyState({
  icon,
  title,
  description,
  tone = 'default',
  onRetry,
}: {
  icon?: ReactNode;
  title: string;
  description?: string;
  tone?: 'default' | 'error';
  /** When provided, renders a "Retry" button (typically wired to a React Query refetch). */
  onRetry?: () => void;
}) {
  return (
    <div className="flex flex-col items-center justify-center px-6 py-14 text-center">
      {icon && (
        <div
          className={`mb-3 flex h-12 w-12 items-center justify-center rounded-full ${
            tone === 'error' ? 'bg-rose-50 text-rose-500' : 'bg-slate-100 text-slate-400'
          }`}
        >
          {icon}
        </div>
      )}
      <div className="text-sm font-medium text-slate-700">{title}</div>
      {description && <div className="mt-1 max-w-sm text-sm text-slate-400">{description}</div>}
      {onRetry && (
        <button
          onClick={onRetry}
          className="mt-4 inline-flex items-center gap-1.5 rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-xs font-medium text-slate-700 shadow-sm transition hover:bg-slate-50"
        >
          <RefreshIcon className="h-3.5 w-3.5" /> Retry
        </button>
      )}
    </div>
  );
}
