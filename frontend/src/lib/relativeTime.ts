/**
 * Compact relative timestamps for tables and freshness indicators.
 * Always pair with an absolute value in a `title` tooltip for precision.
 */

const MINUTE = 60_000;
const HOUR = 3_600_000;
const DAY = 86_400_000;

export function relativeTime(input: string | number | Date, now: number = Date.now()): string {
  const t = typeof input === 'number' ? input : new Date(input).getTime();
  if (Number.isNaN(t)) return '—';
  const diff = now - t;
  if (diff < 0) return 'just now';
  if (diff < 45_000) return 'just now';
  if (diff < HOUR) return `${Math.max(1, Math.round(diff / MINUTE))}m ago`;
  if (diff < DAY) return `${Math.round(diff / HOUR)}h ago`;
  if (diff < 14 * DAY) return `${Math.round(diff / DAY)}d ago`;
  return new Date(t).toLocaleDateString();
}
