/**
 * Command palette (Ctrl/Cmd+K) — fast keyboard navigation for an ops tool: jump to a
 * page, open a saved view, or flip the theme without touching the mouse.
 */
import { useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { builtinViews, loadUserViews, viewHref } from '../lib/presets';
import { getThemeMode, nextThemeMode, setThemeMode } from '../lib/theme';
import { useAuth } from '../lib/AuthContext';
import { SearchIcon } from './icons';

const FOCUSABLE = 'input, button:not([disabled]), [tabindex]:not([tabindex="-1"])';

interface Command {
  id: string;
  label: string;
  hint: string;
  run: () => void;
}

export function CommandPalette() {
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [index, setIndex] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);
  const navigate = useNavigate();
  const { isAdmin } = useAuth();

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault();
        setOpen((o) => !o);
        setQuery('');
        setIndex(0);
      }
      if (e.key === 'Escape') setOpen(false);
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, []);

  useEffect(() => {
    if (open) inputRef.current?.focus();
  }, [open]);

  const commands = useMemo<Command[]>(() => {
    if (!open) return [];
    const go = (to: string) => () => {
      navigate(to);
      setOpen(false);
    };
    const pages: Command[] = [
      { id: 'p-health', label: 'Health overview', hint: 'Page', run: go('/health') },
      { id: 'p-errors', label: 'Errors', hint: 'Page', run: go('/errors') },
      { id: 'p-events', label: 'Events', hint: 'Page', run: go('/events') },
      { id: 'p-insights', label: 'Insights', hint: 'Page', run: go('/insights') },
      { id: 'p-alerts', label: 'Alerts', hint: 'Page', run: go('/alerts') },
      { id: 'p-sessions', label: 'Sessions', hint: 'Page', run: go('/sessions') },
      ...(isAdmin
        ? [
            { id: 'p-apps', label: 'Admin · Apps', hint: 'Page', run: go('/admin/apps') },
            { id: 'p-audit', label: 'Admin · Audit log', hint: 'Page', run: go('/admin/audit') },
          ]
        : []),
    ];
    const views: Command[] = [...builtinViews, ...loadUserViews()].map((v) => ({
      id: `v-${v.id}`,
      label: v.label,
      hint: 'Saved view',
      run: go(viewHref(v)),
    }));
    const actions: Command[] = [
      {
        id: 'a-theme',
        label: `Theme: switch to ${nextThemeMode(getThemeMode())}`,
        hint: 'Action',
        run: () => {
          setThemeMode(nextThemeMode(getThemeMode()));
          setOpen(false);
        },
      },
    ];
    return [...pages, ...views, ...actions];
  }, [open, isAdmin, navigate]);

  const matches = useMemo(() => {
    const q = query.trim().toLowerCase();
    const list = q ? commands.filter((c) => c.label.toLowerCase().includes(q)) : commands;
    return list.slice(0, 12);
  }, [commands, query]);

  useEffect(() => {
    setIndex((i) => Math.min(i, Math.max(0, matches.length - 1)));
  }, [matches.length]);

  if (!open) return null;

  return (
    <div
      className="fixed inset-0 z-40 flex items-start justify-center bg-black/50 p-4 pt-[15vh] backdrop-blur-sm"
      onClick={() => setOpen(false)}
      role="dialog"
      aria-modal="true"
      aria-label="Command palette"
    >
      <div
        className="w-full max-w-lg animate-scale-in overflow-hidden rounded-2xl bg-white shadow-2xl"
        onClick={(e) => e.stopPropagation()}
        onKeyDown={(e) => {
          // aria-modal promises the background is inert — trap Tab inside the panel.
          if (e.key !== 'Tab') return;
          const panel = e.currentTarget;
          const focusable = Array.from(panel.querySelectorAll<HTMLElement>(FOCUSABLE));
          if (focusable.length === 0) return;
          const first = focusable[0];
          const last = focusable[focusable.length - 1];
          if (e.shiftKey && document.activeElement === first) {
            e.preventDefault();
            last.focus();
          } else if (!e.shiftKey && document.activeElement === last) {
            e.preventDefault();
            first.focus();
          }
        }}
      >
        <div className="flex items-center gap-2 border-b border-slate-200 px-4">
          <SearchIcon className="h-4 w-4 shrink-0 text-slate-400" />
          <input
            ref={inputRef}
            value={query}
            onChange={(e) => {
              setQuery(e.target.value);
              setIndex(0);
            }}
            onKeyDown={(e) => {
              if (e.key === 'ArrowDown') {
                e.preventDefault();
                setIndex((i) => Math.min(i + 1, matches.length - 1));
              } else if (e.key === 'ArrowUp') {
                e.preventDefault();
                setIndex((i) => Math.max(i - 1, 0));
              } else if (e.key === 'Enter') {
                matches[index]?.run();
              }
            }}
            placeholder="Jump to a page or saved view…"
            aria-label="Search commands"
            className="w-full bg-transparent py-3.5 text-sm text-slate-800 outline-none placeholder:text-slate-400"
            style={{ boxShadow: 'none' }}
          />
          <kbd className="rounded border border-slate-200 px-1.5 py-0.5 text-[10px] font-medium text-slate-400">esc</kbd>
        </div>
        <ul className="max-h-80 overflow-y-auto py-1.5 scrollbar-thin" role="listbox">
          {matches.length === 0 && (
            <li className="px-4 py-6 text-center text-sm text-slate-400">No matches.</li>
          )}
          {matches.map((c, i) => (
            <li key={c.id} role="option" aria-selected={i === index}>
              <button
                onClick={c.run}
                onMouseEnter={() => setIndex(i)}
                className={`flex w-full items-center justify-between px-4 py-2 text-left text-sm transition ${
                  i === index ? 'bg-brand-50 text-brand-900 dark:bg-brand-500/15 dark:text-brand-200' : 'text-slate-700'
                }`}
              >
                <span className="truncate">{c.label}</span>
                <span className="ml-3 shrink-0 text-[10px] font-medium uppercase tracking-wider text-slate-400">
                  {c.hint}
                </span>
              </button>
            </li>
          ))}
        </ul>
      </div>
    </div>
  );
}
