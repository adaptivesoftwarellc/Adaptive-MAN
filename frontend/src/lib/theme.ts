/**
 * Theme management — light / dark / system, persisted in localStorage and applied as a
 * `dark` class on <html> (Tailwind `darkMode: 'class'`). The palette itself is driven by
 * CSS variables in index.css, so components keep using slate-* utilities unchanged.
 */

export type ThemeMode = 'light' | 'dark' | 'system';

const KEY = 'observability:theme';
const media = () => window.matchMedia('(prefers-color-scheme: dark)');

export function getThemeMode(): ThemeMode {
  try {
    const stored = localStorage.getItem(KEY);
    if (stored === 'light' || stored === 'dark' || stored === 'system') return stored;
  } catch {
    /* ignore */
  }
  return 'system';
}

function resolveDark(mode: ThemeMode): boolean {
  return mode === 'dark' || (mode === 'system' && media().matches);
}

function apply(mode: ThemeMode): void {
  document.documentElement.classList.toggle('dark', resolveDark(mode));
}

export function setThemeMode(mode: ThemeMode): void {
  try {
    localStorage.setItem(KEY, mode);
  } catch {
    /* ignore */
  }
  apply(mode);
}

/** Call once at boot: applies the stored mode and tracks OS changes while in `system`. */
export function initTheme(): void {
  apply(getThemeMode());
  media().addEventListener('change', () => {
    if (getThemeMode() === 'system') apply('system');
  });
}

export const THEME_ORDER: ThemeMode[] = ['light', 'dark', 'system'];

export function nextThemeMode(current: ThemeMode): ThemeMode {
  return THEME_ORDER[(THEME_ORDER.indexOf(current) + 1) % THEME_ORDER.length];
}
