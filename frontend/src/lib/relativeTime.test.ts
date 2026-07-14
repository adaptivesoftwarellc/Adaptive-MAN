import { describe, it, expect } from 'vitest';
import { relativeTime } from './relativeTime';

describe('relativeTime', () => {
  const now = new Date('2026-07-14T12:00:00Z').getTime();

  it('renders just-now for fresh and future timestamps', () => {
    expect(relativeTime(now - 10_000, now)).toBe('just now');
    expect(relativeTime(now + 5_000, now)).toBe('just now');
  });

  it('renders minutes, hours and days', () => {
    expect(relativeTime(now - 5 * 60_000, now)).toBe('5m ago');
    expect(relativeTime(now - 3 * 3_600_000, now)).toBe('3h ago');
    expect(relativeTime(now - 2 * 86_400_000, now)).toBe('2d ago');
  });

  it('falls back to a date beyond two weeks and dashes on garbage', () => {
    expect(relativeTime(now - 30 * 86_400_000, now)).not.toContain('ago');
    expect(relativeTime('not-a-date', now)).toBe('—');
  });
});
