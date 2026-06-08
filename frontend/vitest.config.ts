import { defineConfig } from 'vitest/config';

// Unit tests for the dashboard's pure logic in src/lib. jsdom gives us localStorage for the
// usePageSize hook; the rest of the suite is plain functions. Component/visual tests are
// intentionally out of scope for now (see frontend/README.md).
export default defineConfig({
  test: {
    environment: 'jsdom',
    include: ['src/**/*.test.ts'],
    setupFiles: ['./vitest.setup.ts'],
    clearMocks: true,
  },
});
