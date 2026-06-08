import { describe, it, expect } from 'vitest';
import { resolveRange } from './filters';
import type { DashboardFilters } from './filters';

const f = (over: Partial<DashboardFilters>): DashboardFilters => ({ app: 'a', env: 'e', range: '24h', ...over });

describe('resolveRange', () => {
  it('passes custom from/to straight through', () => {
    const r = resolveRange(f({ range: 'custom', from: '2026-01-01T00:00:00.000Z', to: '2026-01-02T00:00:00.000Z' }));
    expect(r).toEqual({ from: '2026-01-01T00:00:00.000Z', to: '2026-01-02T00:00:00.000Z' });
  });

  it('produces a window that ends now and starts earlier', () => {
    const r = resolveRange(f({ range: '24h' }));
    expect(new Date(r.from!).getTime()).toBeLessThan(new Date(r.to!).getTime());
  });

  it('1h preset spans one hour', () => {
    const r = resolveRange(f({ range: '1h' }));
    const deltaMin = (new Date(r.to!).getTime() - new Date(r.from!).getTime()) / 60_000;
    // Allow a DST hour of slack, but it must be roughly one hour.
    expect(deltaMin).toBeGreaterThan(55);
    expect(deltaMin).toBeLessThan(65);
  });

  it('24h preset spans about a day', () => {
    const r = resolveRange(f({ range: '24h' }));
    const deltaH = (new Date(r.to!).getTime() - new Date(r.from!).getTime()) / 3_600_000;
    expect(deltaH).toBeGreaterThan(23);
    expect(deltaH).toBeLessThan(25);
  });

  it('7d preset spans about a week', () => {
    const r = resolveRange(f({ range: '7d' }));
    const deltaD = (new Date(r.to!).getTime() - new Date(r.from!).getTime()) / 86_400_000;
    expect(deltaD).toBeGreaterThan(6.5);
    expect(deltaD).toBeLessThan(7.5);
  });
});
