import { defineConfig } from 'vitest/config';

// Merged by the Angular unit-test builder (angular.json `runnerConfig`); the builder still owns
// include, coverage scope and reporters. vmThreads runs every spec file in its own VM context
// (per-file isolation, #247) but creates jsdom once per worker instead of once per file (#260).
export default defineConfig({
  test: {
    pool: 'vmThreads',
    setupFiles: ['./vitest.setup.ts'],
  },
});
