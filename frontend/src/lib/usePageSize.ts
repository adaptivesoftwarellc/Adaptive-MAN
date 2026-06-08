import { useState } from 'react';

/**
 * Rows-per-page preference, persisted to localStorage so the choice survives navigating
 * between pages and reloads. Shared key → one "rows per page" setting across all tables.
 */
const STORAGE_KEY = 'observability:page-size';
const DEFAULT_PAGE_SIZE = 50;
export const PAGE_SIZE_OPTIONS = [10, 25, 50];

export function usePageSize(): [number, (n: number) => void] {
  const [pageSize, setPageSizeState] = useState<number>(() => {
    try {
      const stored = Number(localStorage.getItem(STORAGE_KEY));
      return PAGE_SIZE_OPTIONS.includes(stored) ? stored : DEFAULT_PAGE_SIZE;
    } catch {
      return DEFAULT_PAGE_SIZE;
    }
  });

  const setPageSize = (n: number) => {
    setPageSizeState(n);
    try {
      localStorage.setItem(STORAGE_KEY, String(n));
    } catch {
      /* ignore */
    }
  };

  return [pageSize, setPageSize];
}
