import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { App } from './app';

describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
  });

  it('shows "waking" until /health answers, then the build version', async () => {
    const fixture = TestBed.createComponent(App);
    const http = TestBed.inject(HttpTestingController);
    await fixture.whenStable();
    const bar = () => (fixture.nativeElement as HTMLElement).querySelector('[data-testid=statusbar]')!.textContent!;
    expect(bar()).toContain('Waking the server');

    http.expectOne('/health').flush({ status: 'ok', version: 'abcdef1234' });
    await fixture.whenStable();
    expect(bar()).toContain('API ready');
    expect(bar()).toContain('build abcdef1');
    http.verify();
  });

  it('shows "unreachable" when /health fails', async () => {
    const fixture = TestBed.createComponent(App);
    const http = TestBed.inject(HttpTestingController);
    await fixture.whenStable();
    http.expectOne('/health').flush('down', { status: 503, statusText: 'Service Unavailable' });
    await fixture.whenStable();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('API unreachable');
  });
});
