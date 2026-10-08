import { Injectable, signal } from '@angular/core';
import { RequestInfo } from '../data-access/api.types';

/** What the status bar shows (README §9.2). Pages write it; the shell reads it. */
@Injectable({ providedIn: 'root' })
export class StatusService {
  readonly visibleRows = signal<number | null>(null);
  readonly totalRows = signal<number | null>(null);
  readonly lastRequest = signal<RequestInfo | null>(null);

  reset(): void {
    this.visibleRows.set(null);
    this.totalRows.set(null);
    this.lastRequest.set(null);
  }
}
