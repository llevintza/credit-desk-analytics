import { performance as nodePerformance } from 'node:perf_hooks';

// In a VM context `performance` is jsdom's, which has no mark() or clearResourceTimings().
// The forks/threads pools expose Node's; keep that so specs can spy on the real methods.
Object.defineProperty(globalThis, 'performance', {
  value: nodePerformance,
  configurable: true,
  writable: true,
});

// Node's performance is one object per worker thread, shared by every spec file that worker runs: start each
// file with an empty timeline so marks, measures and resource entries can't leak between files (#247).
nodePerformance.clearMarks();
nodePerformance.clearMeasures();
nodePerformance.clearResourceTimings();
