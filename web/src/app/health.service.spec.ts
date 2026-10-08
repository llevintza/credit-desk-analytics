import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { HealthService, type ApiState } from './health.service';

describe('HealthService', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), HealthService],
    });
  });

  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
  });

  it('emits waking then ready with the version from /health', () => {
    const svc = TestBed.inject(HealthService);
    const http = TestBed.inject(HttpTestingController);
    const states: ApiState[] = [];
    const sub = svc.state().subscribe((s) => states.push(s));

    expect(states).toEqual([{ kind: 'waking' }]);
    http.expectOne('/health').flush({ status: 'ok', version: 'deadbeefcafebabe' });
    expect(states).toEqual([
      { kind: 'waking' },
      { kind: 'ready', version: 'deadbeefcafebabe', maintenance: false },
    ]);
    sub.unsubscribe();
  });

  it('reports maintenance mode from /health', () => {
    const svc = TestBed.inject(HealthService);
    const http = TestBed.inject(HttpTestingController);
    const states: ApiState[] = [];
    const sub = svc.state().subscribe((s) => states.push(s));

    http.expectOne('/health').flush({ status: 'ok', version: 'v', maintenance: true });
    expect(states[states.length - 1]).toEqual({ kind: 'ready', version: 'v', maintenance: true });
    sub.unsubscribe();
  });

  it('emits waking then unreachable when /health fails', () => {
    const svc = TestBed.inject(HealthService);
    const http = TestBed.inject(HttpTestingController);
    const states: ApiState[] = [];
    const sub = svc.state().subscribe((s) => states.push(s));

    expect(states).toEqual([{ kind: 'waking' }]);
    http.expectOne('/health').flush('down', { status: 503, statusText: 'Service Unavailable' });
    expect(states).toEqual([{ kind: 'waking' }, { kind: 'unreachable' }]);
    sub.unsubscribe();
  });

  it('emits unreachable on a network error', () => {
    const svc = TestBed.inject(HealthService);
    const http = TestBed.inject(HttpTestingController);
    const states: ApiState[] = [];
    const sub = svc.state().subscribe((s) => states.push(s));

    http.expectOne('/health').error(new ProgressEvent('error'));
    expect(states[states.length - 1]).toEqual({ kind: 'unreachable' });
    sub.unsubscribe();
  });
});
