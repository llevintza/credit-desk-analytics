import { routes } from './app.routes';
import { appConfig } from './app.config';

describe('app routes and config', () => {
  it('has no routes in the phase-0 scaffold', () => {
    expect(routes).toEqual([]);
  });

  it('registers router and http providers', () => {
    expect(appConfig.providers.length).toBeGreaterThan(0);
  });
});
