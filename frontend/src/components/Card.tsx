import { Link } from 'react-router-dom';
import type { ReactNode } from 'react';
import { ArrowRightIcon } from './icons';

export type CardAccent = 'red' | 'orange' | 'amber' | 'green' | 'indigo' | 'violet' | 'cyan';

const ACCENTS: Record<CardAccent, string> = {
  red: 'bg-rose-50 text-rose-600 dark:bg-rose-500/10 dark:text-rose-400',
  orange: 'bg-orange-50 text-orange-600 dark:bg-orange-500/10 dark:text-orange-400',
  amber: 'bg-amber-50 text-amber-600 dark:bg-amber-500/10 dark:text-amber-400',
  green: 'bg-emerald-50 text-emerald-600 dark:bg-emerald-500/10 dark:text-emerald-400',
  indigo: 'bg-brand-50 text-brand-600 dark:bg-brand-500/10 dark:text-brand-400',
  violet: 'bg-violet-50 text-violet-600 dark:bg-violet-500/10 dark:text-violet-400',
  cyan: 'bg-cyan-50 text-cyan-600 dark:bg-cyan-500/10 dark:text-cyan-400',
};

export interface CardDelta {
  /** Value over the immediately preceding window of equal length. */
  previous: number;
  /** For error-like metrics an increase is bad (rose); for usage metrics it's good (emerald). */
  upIsBad?: boolean;
}

interface CardProps {
  title: string;
  total: number | string;
  to?: string;
  icon?: ReactNode;
  accent?: CardAccent;
  delta?: CardDelta;
  children?: ReactNode;
}

function DeltaChip({ current, delta }: { current: number; delta: CardDelta }) {
  const { previous, upIsBad = false } = delta;
  if (previous === 0 && current === 0) return null;

  const pct = previous === 0 ? null : Math.round(((current - previous) / previous) * 100);
  const up = current > previous;
  const flat = current === previous;
  const good = flat ? null : up !== upIsBad;
  const tone = flat
    ? 'text-slate-400'
    : good
      ? 'text-emerald-600 dark:text-emerald-400'
      : 'text-rose-600 dark:text-rose-400';
  const label = flat ? '±0%' : `${up ? '▲' : '▼'} ${pct === null ? 'new' : `${Math.abs(pct)}%`}`;

  return (
    <span
      className={`text-xs font-medium tabular-nums ${tone}`}
      title={`Previous window: ${previous.toLocaleString()}`}
    >
      {label}
    </span>
  );
}

export function Card({ title, total, to, icon, accent = 'indigo', delta, children }: CardProps) {
  const totalNum = typeof total === 'number' ? total : null;
  const ariaLabel =
    totalNum !== null
      ? `${title}: ${totalNum.toLocaleString()} in the selected window${
          delta ? `, previous window ${delta.previous.toLocaleString()}` : ''
        }`
      : `${title}: ${total}`;

  const inner = (
    <div
      aria-label={ariaLabel}
      className="group h-full rounded-xl border border-slate-200 bg-white p-4 shadow-card transition duration-200 hover:-translate-y-0.5 hover:border-slate-300 hover:shadow-card-hover motion-reduce:transition-none motion-reduce:hover:translate-y-0"
    >
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
      <div className="mt-2 flex items-baseline gap-2">
        <span className="text-3xl font-semibold tabular-nums tracking-tight text-slate-900">
          {totalNum !== null ? totalNum.toLocaleString() : total}
        </span>
        {delta && totalNum !== null && <DeltaChip current={totalNum} delta={delta} />}
      </div>
      {children && <div className="mt-3 h-12">{children}</div>}
    </div>
  );
  return to ? (
    <Link to={to} className="block rounded-xl">
      {inner}
    </Link>
  ) : (
    inner
  );
}
