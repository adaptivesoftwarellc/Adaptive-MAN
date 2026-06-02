import { describe, it, expect, beforeEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { usePageSize } from './usePageSize';

describe('usePageSize', () => {
  beforeEach(() => localStorage.clear());

  it('defaults to 50 with nothing stored', () => {
    const { result } = renderHook(() => usePageSize());
    expect(result.current[0]).toBe(50);
  });

  it('reads a previously stored, allowed value', () => {
    localStorage.setItem('observability:page-size', '25');
    const { result } = renderHook(() => usePageSize());
    expect(result.current[0]).toBe(25);
  });

  it('ignores a stored value that is not an allowed option', () => {
    localStorage.setItem('observability:page-size', '999');
    const { result } = renderHook(() => usePageSize());
    expect(result.current[0]).toBe(50);
  });

  it('persists a new size to localStorage', () => {
    const { result } = renderHook(() => usePageSize());
    act(() => result.current[1](10));
    expect(result.current[0]).toBe(10);
    expect(localStorage.getItem('observability:page-size')).toBe('10');
  });
});
