import { Link } from 'react-router-dom';
import type { ReactNode } from 'react';
import { ArrowRightIcon } from './icons';

export type CardAccent = 'red' | 'orange' | 'amber' | 'green' | 'indigo';

const ACCENTS: Record<CardAccent, string> = {
  red: 'bg-rose-50 text-rose-600',
  orange: 'bg-orange-50 text-orange-600',
  amber: 'bg-amber-50 text-amber-600',
  green: 'bg-emerald-50 text-emerald-600',
  indigo: 'bg-brand-50 text-brand-600',
};

interface CardProps {
  title: string;
  total: number | string;
  to?: string;
  icon?: ReactNode;
  accent?: CardAccent;
  children?: ReactNode;
}

export function Card({ title, total, to, icon, accent = 'indigo', children }: CardProps) {
  const inner = (
    <div className="group h-full rounded-xl border border-slate-200 bg-white p-4 shadow-card transition duration-200 hover:-translate-y-0.5 hover:border-slate-300 hover:shadow-card-hover">
      <div className="flex items-start justify-between">
        <div className="flex items-center gap-2">
          {icon && (
            <span className={`flex h-7 w-7 items-center justify-center rounded-lg ${ACCENTS[accent]}`}>
              {icon}
            </span>
          )}
          <span className="text-xs font-medium uppercase tracking-wider text-slate-500">{title}</span>
        </div>
        {to && (
          <ArrowRightIcon className="h-4 w-4 text-slate-300 transition group-hover:translate-x-0.5 group-hover:text-brand-500" />
        )}
      </div>
      <div className="mt-2 text-3xl font-semibold tabular-nums tracking-tight text-slate-900">
        {typeof total === 'number' ? total.toLocaleString() : total}
      </div>
      {children && <div className="mt-3 h-12">{children}</div>}
    </div>
  );
  return to ? (
    <Link to={to} className="block focus-visible:rounded-xl">
      {inner}
    </Link>
  ) : (
    inner
  );
}
