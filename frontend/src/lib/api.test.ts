import { describe, it, expect } from 'vitest';
import { buildQuery } from './api';

describe('buildQuery', () => {
  it('omits undefined, null and empty-string values', () => {
    expect(buildQuery({ app: 'x', env: '', from: undefined })).toBe('?app=x');
  });

  it('keeps page=0 (zero is a valid page, not "empty")', () => {
    const qs = buildQuery({ page: 0, pageSize: 25 });
    expect(qs).toContain('page=0');
    expect(qs).toContain('pageSize=25');
  });

  it('url-encodes keys and values', () => {
    expect(buildQuery({ event_name: 'page viewed' })).toBe('?event_name=page%20viewed');
  });

  it('sends the backend-expected param names for the errors filter', () => {
    const qs = buildQuery({ app: 'a', env: 'e', category: 'server', sort: 'occurrence_count' });
    expect(qs).toContain('category=server');
    expect(qs).toContain('sort=occurrence_count');
  });

  it('returns an empty string when there is nothing to send', () => {
    expect(buildQuery({ a: undefined, b: '' })).toBe('');
  });
});
