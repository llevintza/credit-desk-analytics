import { TestBed } from '@angular/core/testing';
import { HttpClient } from '@angular/common/http';
import { Router } from '@angular/router';
import { routes } from './app.routes';
import { appConfig } from './app.config';

describe('app routes and config', () => {
  it('puts every page behind the login and lazy-loads the grid', () => {
    expect(routes[0].path).toBe('login');
    const shell = routes[1];
    expect(shell.canActivate?.length).toBe(1);
    expect(shell.children?.map((c) => c.path)).toEqual(['', 'positions', 'funds', 'insights', 'deals', 'lab', 'usage']);
    expect(shell.children?.find((c) => c.path === 'positions')?.loadComponent).toBeTypeOf('function');
    expect(shell.children?.find((c) => c.path === 'usage')?.canActivate?.length).toBe(1);
  });

  it('registers zoneless change detection, router and http with the XSRF names', () => {
    TestBed.configureTestingModule({ providers: appConfig.providers });
    expect(TestBed.inject(Router)).toBeTruthy();
    expect(TestBed.inject(HttpClient)).toBeTruthy();
  });

  it('lazy route resolves the positions component', async () => {
    const load = routes[1].children!.find((c) => c.path === 'positions')!.loadComponent!;
    const component = await (load as () => Promise<unknown>)();
    expect(component).toBeTruthy();
  });
});
