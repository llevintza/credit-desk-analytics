import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { AgGridAngular } from 'ag-grid-angular';
import type { ColDef, GridApi, GridOptions, GridReadyEvent } from 'ag-grid-community';
import { catchError, map, of } from 'rxjs';
import { AuthService } from '../core/auth.service';
import { formatCell, isNumeric } from '../core/format';
import { ScopeService } from '../core/scope.service';
import { CatalogColumn, GridRequest } from '../data-access/api.types';
import { DeskApi } from '../data-access/desk-api';
import { Row } from '../data-access/to-rows';
import { insightSources } from '../insights/source-stream';
import { deskGridTheme, registerGridModules } from '../positions/grid-setup';
import { defaultPreset, page } from '../positions/presets';
import { LabClient, ResourceTimings } from './lab-client';
import {
  BlockSize, ColumnSet, Encoding, LabRun, LabSettings, MsgPackDecode, P3Mode, PayloadFormat, Target, acceptFor, cacheSummary, cells,
  columnHeaders, decodeBlock, defaultSettings, encodingHeader, encodingOf, keepRuns, labHeaders, maxRuns, moneyColumns, network,
  parseServerTiming, scenario, sum, toMarkdown,
} from './lab-metrics';

registerGridModules();

/** performance.mark names: a run's decode and grid render show up under these in a DevTools trace. */
export const parseMark = 'lab:parse';
export const renderMark = 'lab:render';

/** Times `fn` between two performance marks (README §6 P5) and returns its result with the measured ms. */
export function timed<T>(name: string, fn: () => T): { value: T; ms: number } {
  performance.mark(`${name}:start`);
  const value = fn();
  performance.mark(`${name}:end`);
  return { value, ms: performance.measure(name, `${name}:start`, `${name}:end`).duration };
}

/** Resolves after the next frame has been painted: a frame callback, then a task after it. */
export function afterPaint(): Promise<void> {
  return new Promise((resolve) => {
    const next = () => setTimeout(resolve, 0);
    if (typeof requestAnimationFrame === 'function') requestAnimationFrame(next);
    else next();
  });
}

let decoder: Promise<MsgPackDecode> | undefined;

/** The MessagePack decoder, imported on the first MessagePack run: no other page or run pays for it (ADR-0028). */
export const loadMsgPack = (): Promise<MsgPackDecode> => (decoder ??= import('@msgpack/msgpack').then((m) => m.decode));

interface Choice<T> { value: T; label: string; }

/**
 * P5 Performance Lab (README §6): runs the P1 block query and the P3 board's reads with the toggles applied and
 * shows what each run really cost: bytes on the wire and decoded (Resource Timing), TTFB and download, the client's
 * decode and grid render (performance marks), and the server's Server-Timing and cache status. The last 20 runs
 * stay in a table that copies as markdown for a PR or an ADR. Nothing is computed from constants: every number is
 * read from the run's own response.
 */
@Component({
  selector: 'app-performance-lab',
  imports: [AgGridAngular],
  templateUrl: './performance-lab.html',
  styleUrl: './performance-lab.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [ResourceTimings, LabClient],
})
export class PerformanceLab {
  private readonly api = inject(DeskApi);
  private readonly client = inject(LabClient);
  private readonly auth = inject(AuthService);
  protected readonly scope = inject(ScopeService);

  protected readonly targets: Choice<Target>[] = [{ value: 'p1', label: 'P1 positions block' }, { value: 'p3', label: 'P3 insights board' }];
  protected readonly formats: Choice<PayloadFormat>[] = [
    { value: 'rows', label: 'Row JSON' }, { value: 'columnar', label: 'Columnar JSON' }, { value: 'msgpack', label: 'MessagePack' },
  ];
  protected readonly sizes: Choice<BlockSize>[] = [100, 200, 500].map((n) => ({ value: n as BlockSize, label: String(n) }));
  protected readonly columnSets: Choice<ColumnSet>[] = [{ value: 'displayed', label: `Displayed (${defaultPreset})` }, { value: 'all', label: 'All' }];
  protected readonly encodings: Choice<Encoding>[] = [
    { value: 'default', label: 'Browser' }, { value: 'br', label: 'br' }, { value: 'gzip', label: 'gzip' }, { value: 'identity', label: 'None' },
  ];
  protected readonly p3Modes: Choice<P3Mode>[] = [{ value: 'per-source', label: '5 per-source calls' }, { value: 'naive', label: 'Naive 20 calls' }];
  protected readonly headers = columnHeaders;
  /** Scenario, Encoding and Cache read left-aligned; every other column is a number. */
  protected readonly textColumns = new Set([1, 6, 13]);
  protected readonly maxRuns = maxRuns;

  protected readonly settings = signal<LabSettings>(defaultSettings);
  protected readonly runs = signal<LabRun[]>([]);
  protected readonly running = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly copied = signal<string | null>(null);
  /** Shown when the clipboard is unavailable (an http origin, a denied permission): the markdown to copy by hand. */
  protected readonly fallback = signal<string | null>(null);
  protected readonly isAdmin = this.auth.isAdmin;

  private readonly catalog = toSignal(this.api.columns().pipe(catchError(() => of<CatalogColumn[]>([]))), { initialValue: [] as CatalogColumn[] });
  private readonly displayed = toSignal(
    this.api.presets(page).pipe(
      map((presets) => presets.find((p) => p.name === defaultPreset && p.builtIn)?.state.columns ?? []),
      catchError(() => of<string[]>([])),
    ),
    { initialValue: [] as string[] },
  );
  private readonly money = computed(() => moneyColumns(this.catalog()));
  /** The columns a P1 run asks for. */
  protected readonly requested = computed(() => (this.settings().columns === 'all' ? this.catalog().map((c) => c.name) : this.displayed()));
  protected readonly ready = computed(() => this.scope.ready() && this.catalog().length > 0 && this.displayed().length > 0);
  protected readonly table = computed(() => this.runs().map((r) => ({ id: r.id, cells: cells(r) })));

  protected readonly gridOptions: GridOptions = {
    theme: deskGridTheme,
    rowModelType: 'clientSide',
    suppressMovableColumns: true,
    onGridReady: (e: GridReadyEvent) => (this.grid = e.api),
  };
  private grid?: GridApi;
  private nextId = 1;

  constructor() {
    this.scope.load();
  }

  protected set<K extends keyof LabSettings>(key: K, value: LabSettings[K]): void {
    this.settings.update((s) => ({ ...s, [key]: value }));
  }

  /** One run with the current toggles; another click while it runs does nothing. */
  protected async run(): Promise<void> {
    if (this.running() || !this.ready()) return;
    this.running.set(true);
    this.error.set(null);
    const s = this.settings();
    try {
      const run = s.target === 'p1' ? await this.runP1(s) : await this.runP3(s);
      this.runs.update((runs) => keepRuns(runs, run));
    } catch (e: unknown) {
      this.error.set(describe(e));
    } finally {
      this.running.set(false);
    }
  }

  protected clear(): void {
    this.runs.set([]);
    this.copied.set(null);
    this.fallback.set(null);
  }

  protected async copy(): Promise<void> {
    const md = toMarkdown(this.runs());
    this.fallback.set(null);
    try {
      await navigator.clipboard.writeText(md);
      const n = this.runs().length;
      this.copied.set(`Copied ${n} run${n === 1 ? '' : 's'} as markdown.`);
    } catch {
      this.copied.set(null);
      this.fallback.set(md);
    }
  }

  private scopeParams(): Pick<GridRequest, 'asOf' | 'portfolioIds'> {
    const asOf = this.scope.asOf();
    return { ...(asOf ? { asOf } : {}), portfolioIds: this.scope.selected() };
  }

  private async runP1(s: LabSettings): Promise<LabRun> {
    const columns = this.requested();
    const request: GridRequest = { ...this.scopeParams(), startRow: 0, endRow: s.blockSize, columns };
    const msgpack = s.format === 'msgpack' ? await loadMsgPack() : undefined;
    const id = this.nextId++;
    const { res, entry } = await this.client.send('POST', DeskApi.positionsUrl, { Accept: acceptFor(s.format), ...labHeaders(s) }, request);
    const parsed = timed(`${parseMark}:${id}`, () => decodeBlock(s.format, res.body!, this.money(), msgpack));
    const renderMs = await this.render(id, parsed.value.rows, parsed.value.columns);
    const net = entry ? network(entry) : null;
    return {
      id, scenario: scenario(s, columns.length), requests: 1,
      rows: parsed.value.rows.length, columns: parsed.value.columns.length,
      wireBytes: net?.wire ?? null, decodedBytes: net?.decoded ?? null,
      encoding: encodingOf(res.headers.get(encodingHeader), res.headers.get('Content-Encoding'), net),
      ttfbMs: net?.ttfb ?? null, downloadMs: net?.download ?? null, parseMs: parsed.ms, renderMs,
      totalMs: net ? net.end - net.start : null,
      server: parseServerTiming(res.headers.get('Server-Timing')),
      cache: res.headers.get('X-Cache') ?? '?',
    };
  }

  /**
   * The board's reads, all at once as the board sends them: one per source, or one per grid. TTFB is the first
   * byte of the first response and Total the end of the last, both from the run's start; bytes, decode and server
   * times are summed over the calls.
   */
  private async runP3(s: LabSettings): Promise<LabRun> {
    const id = this.nextId++;
    const calls = s.p3 === 'naive'
      ? insightSources.flatMap(({ source, grids }) => grids.map((grid) => ({ source, grid })))
      : insightSources.map(({ source }) => ({ source, grid: undefined as string | undefined }));
    const { asOf, portfolioIds } = this.scopeParams();
    const headers = labHeaders(s);
    const started = performance.now();
    const exchanges = await Promise.all(calls.map(({ source, grid }) => {
      const params = new URLSearchParams();
      if (asOf) params.set('asOf', asOf);
      if (portfolioIds?.length) params.set('portfolioIds', portfolioIds.join(','));
      if (grid) params.set('grid', grid);
      const query = params.toString();
      return this.client.send('GET', `/api/insights/${source}${query ? `?${query}` : ''}`, headers);
    }));
    const parse = timed(`${parseMark}:${id}`, () => exchanges.forEach((x) => JSON.parse(new TextDecoder().decode(x.res.body!))));
    const nets = exchanges.map((x) => (x.entry ? network(x.entry) : null));
    const known = nets.filter((n) => n !== null);
    const allKnown = known.length === exchanges.length;
    const timings = exchanges.map((x) => parseServerTiming(x.res.headers.get('Server-Timing')));
    const server = timings.every((t) => t !== null)
      ? { db: sum(timings.map((t) => t!.db))!, ser: sum(timings.map((t) => t!.ser))!, total: sum(timings.map((t) => t!.total))! }
      : null;
    const encodings = new Set(exchanges.map((x, i) => encodingOf(x.res.headers.get(encodingHeader), x.res.headers.get('Content-Encoding'), nets[i])));
    return {
      id, scenario: scenario(s, 0), requests: exchanges.length, rows: null, columns: null,
      wireBytes: allKnown ? sum(known.map((n) => n.wire)) : null,
      decodedBytes: allKnown ? sum(known.map((n) => n.decoded)) : null,
      encoding: [...encodings].join(' / '),
      ttfbMs: allKnown && known.every((n) => n.firstByte !== null) ? Math.min(...known.map((n) => n.firstByte!)) - started : null,
      downloadMs: null, parseMs: parse.ms, renderMs: null,
      totalMs: allKnown ? Math.max(...known.map((n) => n.end)) - started : null,
      server, cache: cacheSummary(exchanges.map((x) => x.res.headers.get('X-Cache'))),
    };
  }

  /** Hands the rows to the grid and times it up to the next painted frame (README §6 P5 grid render). */
  private async render(id: number, rows: Row[], columns: string[]): Promise<number | null> {
    const grid = this.grid;
    if (!grid) return null;
    grid.setGridOption('columnDefs', this.colDefs(columns));
    const name = `${renderMark}:${id}`;
    performance.mark(`${name}:start`);
    grid.setGridOption('rowData', rows);
    await afterPaint();
    performance.mark(`${name}:end`);
    return performance.measure(name, `${name}:start`, `${name}:end`).duration;
  }

  private colDefs(columns: string[]): ColDef[] {
    const byName = new Map(this.catalog().map((c) => [c.name, c]));
    return columns.map((name): ColDef => {
      const c = byName.get(name);
      if (!c) return { field: name, headerName: name };
      return {
        field: name, headerName: c.header, width: isNumeric(c.kind) ? 110 : 150,
        type: isNumeric(c.kind) ? 'rightAligned' : undefined,
        valueFormatter: (p) => formatCell(c.kind, p.value),
      };
    });
  }
}

/** A failed run in words: the status and title the API sent, or the error's message. */
function describe(e: unknown): string {
  if (e instanceof HttpErrorResponse) return e.status === 0 ? 'Run failed: the API could not be reached.' : `Run failed: HTTP ${e.status}.`;
  return `Run failed: ${e instanceof Error ? e.message : String(e)}`;
}
