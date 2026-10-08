import { performance as nodePerformance } from 'node:perf_hooks';

// In a VM context `performance` is jsdom's, which has no mark() or clearResourceTimings().
// The forks/threads pools expose Node's; keep that so specs can spy on the real methods.
Object.defineProperty(globalThis, 'performance', {
  value: nodePerformance,
  configurable: true,
  writable: true,
});
