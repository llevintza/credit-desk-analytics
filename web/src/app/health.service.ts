import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, of, startWith } from 'rxjs';

export interface Health { status: string; version: string; }
export type ApiState =
  | { kind: 'waking' }
  | { kind: 'ready'; version: string }
  | { kind: 'unreachable' };

/** Reads the static /health probe (never touches the database). */
@Injectable({ providedIn: 'root' })
export class HealthService {
  private readonly http = inject(HttpClient);

  state(): Observable<ApiState> {
    return this.http.get<Health>('/health').pipe(
      map((h): ApiState => ({ kind: 'ready', version: h.version })),
      catchError(() => of<ApiState>({ kind: 'unreachable' })),
      startWith<ApiState>({ kind: 'waking' }),
    );
  }
}
