import { NavLink } from 'react-router-dom';
import { presets, presetHref } from '../lib/presets';

const links = [
  { to: '/health', label: 'Health' },
  { to: '/errors', label: 'Errors' },
  { to: '/events', label: 'Events' },
  { to: '/sessions', label: 'Sessions' },
  { to: '/admin/apps', label: 'Apps' },
];

export function Sidebar() {
  return (
    <aside className="flex w-48 flex-col border-r bg-slate-900 text-slate-100">
      <div className="px-5 py-4">
        <div className="text-sm font-semibold">adaptive-observability</div>
        <div className="text-xs text-slate-400">internal dashboard</div>
      </div>
      <nav className="flex flex-1 flex-col gap-1 px-2 pb-4">
        {links.map((l) => (
          <NavLink
            key={l.to}
            to={l.to}
            className={({ isActive }) =>
              `rounded px-3 py-2 text-sm transition ${
                isActive ? 'bg-slate-700 text-white' : 'text-slate-300 hover:bg-slate-800'
              }`
            }
          >
            {l.label}
          </NavLink>
        ))}

        <div className="mt-4 px-3 text-[10px] uppercase tracking-wider text-slate-500">
          Presets
        </div>
        {presets.map((p) => (
          <NavLink
            key={p.id}
            to={presetHref(p)}
            className={({ isActive }) =>
              `rounded px-3 py-1.5 text-xs transition ${
                isActive ? 'bg-slate-700 text-white' : 'text-slate-400 hover:bg-slate-800'
              }`
            }
          >
            {p.label}
          </NavLink>
        ))}
      </nav>
      <div className="px-5 py-3 text-[10px] text-slate-500">
        TODO: replace with real auth (Phase 8)
      </div>
    </aside>
  );
}
