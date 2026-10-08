import {
  CellStyleModule, ColumnApiModule, ColumnAutoSizeModule, DateFilterModule,
  EventApiModule, InfiniteRowModelModule, ModuleRegistry, NumberFilterModule, PinnedRowModule, RenderApiModule,
  RowApiModule, RowStyleModule, ScrollApiModule, TextFilterModule, TooltipModule, ValidationModule, themeQuartz,
  type Module,
} from 'ag-grid-community';
import { isDevMode } from '@angular/core';

/**
 * AG Grid Community only (AGENTS.md): just the modules the positions grid uses, so the lazy chunk carries no
 * unused features. ValidationModule (helpful console errors) only in development builds.
 */
export function gridModules(dev: boolean): Module[] {
  return [
    InfiniteRowModelModule, ColumnApiModule, RowApiModule, ScrollApiModule, RenderApiModule, EventApiModule,
    TextFilterModule, NumberFilterModule, DateFilterModule, TooltipModule, PinnedRowModule, CellStyleModule,
    RowStyleModule, ColumnAutoSizeModule,
    ...(dev ? [ValidationModule] : []),
  ];
}

export function registerGridModules(): void {
  ModuleRegistry.registerModules(gridModules(isDevMode()));
}

/**
 * Quartz in compact density (README §9.1: ~24 px rows, ~28 px header), coloured from the app's CSS variables so
 * the dark/light theme and the colorblind palette apply to the grid without rebuilding it.
 */
export const deskGridTheme = themeQuartz.withParams({
  backgroundColor: 'var(--surface-1)',
  foregroundColor: 'var(--text)',
  headerBackgroundColor: 'var(--surface-2)',
  headerTextColor: 'var(--muted)',
  borderColor: 'var(--line)',
  chromeBackgroundColor: 'var(--surface-2)',
  oddRowBackgroundColor: 'var(--zebra)',
  rowHoverColor: 'var(--hover)',
  accentColor: 'var(--accent)',
  fontFamily: 'var(--font-ui)',
  fontSize: 12,
  headerFontSize: 12,
  rowHeight: 24,
  headerHeight: 28,
  spacing: 4,
  wrapperBorderRadius: 0,
});
