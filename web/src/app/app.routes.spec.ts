import { TestBed } from '@angular/core/testing';
import { HttpClient } from '@angular/common/http';
import { Router } from '@angular/router';
import { routes } from './app.routes';
import { appConfig } from './app.config';

describe('app routes and config', () => {
  it('has no routes in the phase-0 scaffold', () => {
    expect(routes).toEqual([]);
  });

  it('registers router and http providers', () => {
    TestBed.configureTestingModule({ providers: appConfig.providers });
    expect(TestBed.inject(Router)).toBeTruthy();
    expect(TestBed.inject(HttpClient)).toBeTruthy();
    expect(appConfig.providers.length).toBe(3);
  });
});
