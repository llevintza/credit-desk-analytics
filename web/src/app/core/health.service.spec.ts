import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { HealthService, type ApiState } from './health.service';

describe('HealthService', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
  });

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('emits waking then ready with the version and maintenance flag', () => {
    const states: ApiState[] = [];
    const sub = TestBed.inject(HealthService).state().subscribe((s) => states.push(s));
    expect(states).toEqual([{ kind: 'waking', attempt: 0 }]);
    TestBed.inject(HttpTestingController).expectOne('/health').flush({ status: 'ok', version: 'deadbeef', maintenance: true });
    expect(states.at(-1)).toEqual({ kind: 'ready', version: 'deadbeef', maintenance: true });
    sub.unsubscribe();
  });

  it('defaults maintenance to false', () => {
    const states: ApiState[] = [];
    const sub = TestBed.inject(HealthService).state().subscribe((s) => states.push(s));
    TestBed.inject(HttpTestingController).expectOne('/health').flush({ status: 'ok', version: 'v' });
    expect(states.at(-1)).toEqual({ kind: 'ready', version: 'v', maintenance: false });
    sub.unsubscribe();
  });

  it('keeps polling while the server wakes, then reports ready', () => {
    vi.useFakeTimers();
    try {
      const states: ApiState[] = [];
      const sub = TestBed.inject(HealthService).state().subscribe((s) => states.push(s));
      const http = TestBed.inject(HttpTestingController);
      http.expectOne('/health').flush('starting', { status: 502, statusText: 'Bad Gateway' });
      expect(states.at(-1)).toEqual({ kind: 'waking', attempt: 1 }); // progress for the login page
      vi.advanceTimersByTime(HealthService.retryEveryMs);
      http.expectOne('/health').flush({ status: 'ok', version: 'v2' });
      expect(states.at(-1)).toEqual({ kind: 'ready', version: 'v2', maintenance: false });
      sub.unsubscribe();
    } finally {
      vi.useRealTimers();
    }
  });

  it('gives up as unreachable after the last attempt', () => {
    vi.useFakeTimers();
    try {
      const states: ApiState[] = [];
      const sub = TestBed.inject(HealthService).state().subscribe((s) => states.push(s));
      const http = TestBed.inject(HttpTestingController);
      for (let i = 0; i <= HealthService.maxAttempts; i++) {
        http.expectOne('/health').error(new ProgressEvent('error'));
        vi.advanceTimersByTime(HealthService.retryEveryMs);
      }
      expect(states.at(-1)).toEqual({ kind: 'unreachable' });
      sub.unsubscribe();
    } finally {
      vi.useRealTimers();
    }
  });
});
