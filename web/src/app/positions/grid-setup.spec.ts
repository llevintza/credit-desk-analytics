import { InfiniteRowModelModule, ValidationModule } from 'ag-grid-community';
import { deskGridTheme, gridModules, registerGridModules } from './grid-setup';

describe('grid setup', () => {
  it('registers only Community modules, with validation in development only', () => {
    expect(gridModules(false)).toContain(InfiniteRowModelModule);
    expect(gridModules(false)).not.toContain(ValidationModule);
    expect(gridModules(true)).toContain(ValidationModule);
    expect(() => registerGridModules()).not.toThrow();
  });

  it('builds the compact Quartz theme from the app CSS variables', () => {
    expect(deskGridTheme).toBeTruthy();
  });
});
