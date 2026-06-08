import { describe, it, expect } from 'vitest';
import { mockErrors, mockEvents, mockSessions } from './mock';
import { errorCategory } from './catalog';

const base = { app: 'sch-ui', env: 'Production' };

describe('mockErrors', () => {
  it('filters to a single category and the rows derive to that category', () => {
    const res = mockErrors({ ...base, category: 'server', pageSize: 200 });
    expect(res.rows.length).toBeGreaterThan(0);
    expect(res.rows.every((r) => errorCategory(r) === 'server')).toBe(true);
  });

  it('frontend-category rows carry no exception_type or job_name', () => {
    const res = mockErrors({ ...base, category: 'frontend', pageSize: 200 });
    expect(res.rows.length).toBeGreaterThan(0);
    expect(res.rows.every((r) => r.exception_type === null && r.job_name === null)).toBe(true);
  });

  it('paginates (respects pageSize and reports total)', () => {
    const page0 = mockErrors({ ...base, page: 0, pageSize: 10 });
    expect(page0.rows.length).toBeLessThanOrEqual(10);
    expect(page0.page_size).toBe(10);
    expect(page0.total).toBeGreaterThan(page0.rows.length);
  });

  it('is deterministic for the same app + env', () => {
    const a = mockErrors({ ...base, pageSize: 50 });
    const b = mockErrors({ ...base, pageSize: 50 });
    expect(a.rows.map((r) => r.id)).toEqual(b.rows.map((r) => r.id));
  });
});

describe('mockEvents', () => {
  it('filters by event_name', () => {
    const res = mockEvents({ ...base, event_name: 'page_viewed', pageSize: 200 });
    expect(res.rows.length).toBeGreaterThan(0);
    expect(res.rows.every((e) => e.event_name === 'page_viewed')).toBe(true);
  });
});

describe('mockSessions', () => {
  it('errors_only returns only sessions with errors', () => {
    const res = mockSessions({ ...base, errors_only: true, pageSize: 200 });
    expect(res.rows.length).toBeGreaterThan(0);
    expect(res.rows.every((s) => s.has_error)).toBe(true);
  });
});
