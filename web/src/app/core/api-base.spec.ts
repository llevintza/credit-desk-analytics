import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { firstValueFrom } from 'rxjs';
import { API_BASE_URL, apiBase, apiBaseInterceptor, isApiUrl } from './api-base';

describe('API base URL (ADR-0024)', () => {
  let ctl: HttpTestingController | undefined;

  function setup(base?: string) {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([apiBaseInterceptor])),
        provideHttpClientTesting(),
        ...(base === undefined ? [] : [{ provide: API_BASE_URL, useValue: base }]),
      ],
    });
    ctl = TestBed.inject(HttpTestingController);
    return { http: TestBed.inject(HttpClient), ctl };
  }

  afterEach(() => {
    ctl?.verify();
    ctl = undefined;
  });

  it('normalises the configured origin', () => {
    expect(apiBase('')).toBe('');
    expect(apiBase(' https://api.example.test/ ')).toBe('https://api.example.test');
    expect(apiBase('https://api.example.test//')).toBe('https://api.example.test');
  });

  it('knows the API URLs DeskApi uses', () => {
    expect(isApiUrl('/api/me')).toBe(true);
    expect(isApiUrl('/health')).toBe(true);
    expect(isApiUrl('/healthz')).toBe(false);
    expect(isApiUrl('/apiary')).toBe(false);
    expect(isApiUrl('assets/x.json')).toBe(false);
    expect(isApiUrl('https://elsewhere.test/api/me')).toBe(false);
  });

  it('is the same origin by default (the build defines it as "")', () => {
    setup();
    expect(TestBed.inject(API_BASE_URL)).toBe('');
  });

  it('leaves every URL alone on the same origin', async () => {
    const { http, ctl } = setup('');
    const done = firstValueFrom(http.get('/api/me'));
    ctl.expectOne('/api/me').flush({});
    await done;
  });

  it('sends API and health calls to the configured origin and nothing else', async () => {
    const { http, ctl } = setup('https://api.example.test');
    const calls = [firstValueFrom(http.get('/api/me')), firstValueFrom(http.get('/health')), firstValueFrom(http.get('favicon.ico'))];
    ctl.expectOne('https://api.example.test/api/me').flush({});
    ctl.expectOne('https://api.example.test/health').flush({});
    ctl.expectOne('favicon.ico').flush('');
    await Promise.all(calls);
  });
});
