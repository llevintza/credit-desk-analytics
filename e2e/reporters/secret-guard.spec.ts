import { expect, test } from '@playwright/test';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { deflateRawSync } from 'node:zlib';
import SecretGuard, { findSecret, scanDir, zipEntries } from './secret-guard';

// A made-up value for these checks only; the real one comes from Desk.UserAdmin per run.
const secret = 'not-a-real-pw/"7f';

/** A minimal zip of the given entries, deflated (method 8) or stored (method 0). */
function zip(entries: Record<string, string>, method: 0 | 8 = 8): Buffer {
  const locals: Buffer[] = [];
  const centrals: Buffer[] = [];
  let offset = 0;
  for (const [name, text] of Object.entries(entries)) {
    const raw = Buffer.from(text);
    const data = method === 8 ? deflateRawSync(raw) : raw;
    const nameBytes = Buffer.from(name);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(method, 8);
    local.writeUInt32LE(data.length, 18);
    local.writeUInt32LE(raw.length, 22);
    local.writeUInt16LE(nameBytes.length, 26);
    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0);
    central.writeUInt16LE(method, 10);
    central.writeUInt32LE(data.length, 20);
    central.writeUInt32LE(raw.length, 24);
    central.writeUInt16LE(nameBytes.length, 28);
    central.writeUInt32LE(offset, 42);
    locals.push(local, nameBytes, data);
    centrals.push(central, nameBytes);
    offset += 30 + nameBytes.length + data.length;
  }
  const cd = Buffer.concat(centrals);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(Object.keys(entries).length, 8);
  end.writeUInt16LE(Object.keys(entries).length, 10);
  end.writeUInt32LE(cd.length, 12);
  end.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, cd, end]);
}

/** The html reporter's shape: the report data is a base64 zip in a script tag of index.html. */
function reportPage(report: Buffer): string {
  return `<html><script id="playwrightReportBase64" type="application/zip">data:application/zip;base64,${report.toString('base64')}</script></html>`;
}

test('finds the secret as typed, JSON-escaped or URL-encoded', () => {
  expect(findSecret(Buffer.from(`Fill "${secret}"`), [secret])).toBe(true);
  expect(findSecret(Buffer.from(JSON.stringify({ title: secret })), [secret])).toBe(true);
  expect(findSecret(Buffer.from(`?p=${encodeURIComponent(secret)}`), [secret])).toBe(true);
  expect(findSecret(Buffer.from('Evaluate'), [secret])).toBe(false);
});

test('finds the secret inside a zip, stored or deflated, and inside the report page\'s base64 zip', () => {
  expect(zipEntries(zip({ 'a.json': 'x', 'b.json': 'y' }, 0)).map(String)).toEqual(['x', 'y']);
  expect(zipEntries(zip({ 'a.json': 'x' })).map(String)).toEqual(['x']);
  expect(findSecret(zip({ 'trace.trace': `{"title":"Fill \\"${secret}\\""}` }), [secret])).toBe(true);
  expect(findSecret(zip({ 'trace.trace': '{"title":"Evaluate"}' }, 0), [secret])).toBe(false);
  expect(findSecret(Buffer.from(reportPage(zip({ 'report.json': JSON.stringify({ title: `Fill "${secret}"` }) }))), [secret])).toBe(true);
  expect(findSecret(Buffer.from(reportPage(zip({ 'report.json': '{"title":"Evaluate"}' }))), [secret])).toBe(false);
});

test('a bad zip or an empty secret finds nothing', () => {
  expect(zipEntries(Buffer.from('PK\x05\x06'))).toEqual([]);
  expect(zipEntries(Buffer.from('not a zip'))).toEqual([]);
  // A stored entry the central directory claims is deflated: 0xff is a reserved block type, so it won't inflate.
  const broken = zip({ 'a.json': '\xff\xff' }, 0);
  broken.writeUInt16LE(8, broken.lastIndexOf(Buffer.from([0x50, 0x4b, 0x01, 0x02])) + 10);
  expect(zipEntries(broken)).toEqual([]);
  expect(findSecret(Buffer.from('anything'), [''])).toBe(false);
});

test('scans a folder tree, and a missing folder has nothing', () => {
  const dir = mkdtempSync(join(tmpdir(), 'secret-guard-'));
  try {
    mkdirSync(join(dir, 'report', 'data'), { recursive: true });
    writeFileSync(join(dir, 'report', 'index.html'), reportPage(zip({ 'report.json': '{"title":"Evaluate"}' })));
    writeFileSync(join(dir, 'report', 'data', 'trace.zip'), zip({ 'trace.trace': secret }));
    expect(scanDir(join(dir, 'report'), [secret])).toEqual([join(dir, 'report', 'data', 'trace.zip')]);
    expect(scanDir(join(dir, 'missing'), [secret])).toEqual([]);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('the reporter fails the run on a leak in a step, an error or a file, and passes a clean one', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'secret-guard-'));
  const config = { configFile: join(dir, 'playwright.config.ts') } as never;
  const testCase = { title: 'log in', location: { file: 'auth.setup.ts', line: 1, column: 1 } } as never;
  const step = (title: string) => ({ title, location: { file: 'auth.setup.ts', line: 13, column: 1 } }) as never;
  const errors = (message: string) => ({ errors: [{ message }] }) as never;
  const logged = console.error;
  console.error = () => {};
  try {
    mkdirSync(join(dir, 'playwright-report'));
    writeFileSync(join(dir, 'playwright-report', 'index.html'), reportPage(zip({ 'report.json': '{"title":"Evaluate"}' })));

    const clean = new SecretGuard({ secrets: [secret] });
    clean.onBegin(config);
    clean.onStepEnd(testCase, {} as never, step('Evaluate'));
    clean.onTestEnd(testCase, errors(''));
    expect(await clean.onEnd()).toBeUndefined();
    expect(clean.printsToStdio()).toBe(false);

    const inStep = new SecretGuard({ secrets: [secret] });
    inStep.onBegin(config);
    inStep.onStepEnd(testCase, {} as never, step(`Fill "${secret}"`));
    expect(await inStep.onEnd()).toEqual({ status: 'failed' });

    const inError = new SecretGuard({ secrets: [secret] });
    inError.onBegin(config);
    inError.onTestEnd(testCase, errors(`expected ${secret}`));
    expect(await inError.onEnd()).toEqual({ status: 'failed' });

    writeFileSync(join(dir, 'playwright-report', 'index.html'), reportPage(zip({ 'report.json': `{"title":"Fill ${secret}"}` })));
    const inFile = new SecretGuard({ secrets: [secret] });
    inFile.onBegin(config);
    expect(await inFile.onEnd()).toEqual({ status: 'failed' });

    // The defaults: DESK_PASSWORD (unset, or the real one, which isn't in these files), and no config file.
    const defaults = new SecretGuard({ dirs: [dir] });
    defaults.onBegin({} as never);
    expect(await defaults.onEnd()).toBeUndefined();
  } finally {
    console.error = logged;
    rmSync(dir, { recursive: true, force: true });
  }
});

test('fails closed when the run needs the password and DESK_PASSWORD is empty, unset or too short', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'secret-guard-'));
  const config = { configFile: join(dir, 'playwright.config.ts') } as never;
  const suite = (...projects: string[]) => ({ allTests: () => projects.map((name) => ({ parent: { project: () => ({ name }) } })) }) as never;
  const printed: string[] = [];
  const logged = console.error;
  const saved = process.env.DESK_PASSWORD;
  console.error = (message: string) => printed.push(message);
  try {
    for (const secrets of [[''], ['13-characters']]) {
      const weak = new SecretGuard({ dirs: [dir], secrets });
      weak.onBegin(config, suite('guard', 'setup', 'chromium'));
      expect(await weak.onEnd()).toEqual({ status: 'failed' });
    }
    expect(printed.join('\n')).toContain('DESK_PASSWORD is empty or shorter than 14 characters');
    expect(printed.join('\n')).not.toContain('13-characters');

    delete process.env.DESK_PASSWORD;
    const unset = new SecretGuard({ dirs: [dir] });
    unset.onBegin(config, suite('setup'));
    expect(await unset.onEnd()).toEqual({ status: 'failed' });

    // The guard's own checks need no password: a run of only the guard project passes without one.
    printed.length = 0;
    const selfCheck = new SecretGuard({ dirs: [dir], secrets: [''] });
    selfCheck.onBegin(config, suite('guard', 'guard'));
    expect(await selfCheck.onEnd()).toBeUndefined();
    expect(printed).toEqual([]);

    const strong = new SecretGuard({ dirs: [dir], secrets: ['14-characters!'] });
    strong.onBegin(config, suite('setup', 'chromium'));
    expect(await strong.onEnd()).toBeUndefined();
  } finally {
    console.error = logged;
    if (saved === undefined) delete process.env.DESK_PASSWORD;
    else process.env.DESK_PASSWORD = saved;
    rmSync(dir, { recursive: true, force: true });
  }
});
