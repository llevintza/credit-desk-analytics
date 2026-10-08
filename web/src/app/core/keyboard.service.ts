import { Injectable, signal } from '@angular/core';

/** Page-level actions the global shortcuts trigger (README §9.3). The active page registers what it supports. */
export interface PageActions {
  focusQuickFilter?: () => void;
  exportCsv?: () => void;
  nextPreset?: () => void;
}

@Injectable({ providedIn: 'root' })
export class KeyboardService {
  readonly actions = signal<PageActions>({});

  register(actions: PageActions): void {
    this.actions.set(actions);
  }

  clear(): void {
    this.actions.set({});
  }
}
