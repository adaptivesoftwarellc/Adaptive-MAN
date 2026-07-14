/** @type {import('tailwindcss').Config} */

// The slate scale is CSS-variable-driven so dark mode can invert it in one place
// (see index.css :root / .dark). Components keep using slate-* utilities unchanged.
const sl = (n) => `rgb(var(--sl-${n}) / <alpha-value>)`;

export default {
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  darkMode: 'class',
  theme: {
    extend: {
      fontFamily: {
        sans: [
          'Inter',
          'ui-sans-serif',
          'system-ui',
          '-apple-system',
          'BlinkMacSystemFont',
          '"Segoe UI"',
          'Roboto',
          'Helvetica',
          'Arial',
          'sans-serif',
        ],
        mono: [
          'ui-monospace',
          'SFMono-Regular',
          'Menlo',
          'Consolas',
          '"Liberation Mono"',
          'monospace',
        ],
      },
      colors: {
        // Semantic brand accent (indigo). Aliased so the palette can be re-themed in one place.
        brand: {
          50: '#eef2ff',
          100: '#e0e7ff',
          200: '#c7d2fe',
          300: '#a5b4fc',
          400: '#818cf8',
          500: '#6366f1',
          600: '#4f46e5',
          700: '#4338ca',
          800: '#3730a3',
          900: '#312e81',
          950: '#1e1b4b',
        },
        // Theme-aware neutral scale (inverts in dark mode via CSS variables).
        slate: {
          50: sl(50),
          100: sl(100),
          200: sl(200),
          300: sl(300),
          400: sl(400),
          500: sl(500),
          600: sl(600),
          700: sl(700),
          800: sl(800),
          900: sl(900),
          950: sl(950),
        },
        // Fixed dark palette for the app shell (sidebar) — identical in light and dark mode,
        // so the shell stays dark while the content area follows the theme. Values are the
        // stock Tailwind slate hexes.
        shell: {
          100: '#f1f5f9',
          200: '#e2e8f0',
          300: '#cbd5e1',
          400: '#94a3b8',
          500: '#64748b',
          600: '#475569',
          700: '#334155',
          800: '#1e293b',
          900: '#0f172a',
          950: '#020617',
        },
      },
      boxShadow: {
        card: '0 1px 2px 0 rgb(15 23 42 / 0.04), 0 1px 3px 0 rgb(15 23 42 / 0.06)',
        'card-hover': '0 6px 16px -4px rgb(15 23 42 / 0.12), 0 2px 6px -2px rgb(15 23 42 / 0.06)',
        drawer: '-12px 0 40px -12px rgb(15 23 42 / 0.30)',
      },
      borderRadius: {
        xl: '0.75rem',
        '2xl': '1rem',
      },
      keyframes: {
        'fade-in': {
          '0%': { opacity: '0', transform: 'translateY(4px)' },
          '100%': { opacity: '1', transform: 'translateY(0)' },
        },
        'slide-in-right': {
          '0%': { transform: 'translateX(100%)' },
          '100%': { transform: 'translateX(0)' },
        },
        'backdrop-in': {
          '0%': { opacity: '0' },
          '100%': { opacity: '1' },
        },
        'scale-in': {
          '0%': { opacity: '0', transform: 'scale(0.96)' },
          '100%': { opacity: '1', transform: 'scale(1)' },
        },
      },
      animation: {
        'fade-in': 'fade-in 0.25s ease-out both',
        'slide-in-right': 'slide-in-right 0.22s cubic-bezier(0.16, 1, 0.3, 1) both',
        'backdrop-in': 'backdrop-in 0.18s ease-out both',
        'scale-in': 'scale-in 0.18s cubic-bezier(0.16, 1, 0.3, 1) both',
      },
    },
  },
  plugins: [],
};
