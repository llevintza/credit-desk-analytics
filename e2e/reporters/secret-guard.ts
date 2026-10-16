import { readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { inflateRawSync } from 'node:zlib';
import type { FullConfig, FullResult, Reporter, Suite, TestCase, TestResult, TestStep } from '@playwright/test/reporter';

/**
 * #155: the e2e-report artifact is public, so the throwaway viewer's password must not be in it. Listed after the
 * html reporter, whose onEnd has written the report by the time this one's runs, and fails the run if any step title,
 * error or file under the report or output folders (including the report's embedded zip and trace zips) holds the
 * password. It fails closed: a run that logs in (any project but the guard's own checks) fails when DESK_PASSWORD is empty
 * or shorter than the app's minimum, since the scan would then pass without looking for anything.
 */
export default class SecretGuard implements Reporter {
  private readonly secrets: string[];
  private readonly dirs: string[];
  private root = process.cwd();
  private readonly leaks: string[] = [];
  private weakSecret = false;

  constructor(options: { dirs?: string[]; secrets?: string[] } = {}) {
    this.secrets = options.secrets ?? [process.env.DESK_PASSWORD ?? ''];
    this.dirs = options.dirs ?? ['playwright-report', 'test-results'];
  }

  onBegin(config: FullConfig, suite?: Suite): void {
    if (config.configFile) this.root = dirname(config.configFile);
    const logsIn = suite?.allTests().some((t) => t.parent.project()?.name !== selfCheckProject) ?? false;
    this.weakSecret = logsIn && this.secrets.some((s) => s.length < minSecretLength);
  }

  onStepEnd(test: TestCase, _result: TestResult, step: TestStep): void {
    // Never print the step's title: it holds the password.
    if (findSecret(Buffer.from(`${step.title}\n${step.error?.message ?? ''}`), this.secrets))
      this.leaks.push(`a step at ${step.location?.file ?? test.location.file}:${step.location?.line ?? '?'}`);
  }

  onTestEnd(test: TestCase, result: TestResult): void {
    const errors = result.errors.map((e) => `${e.message ?? ''}\n${e.stack ?? ''}`).join('\n');
    if (findSecret(Buffer.from(errors), this.secrets)) this.leaks.push(`an error of ${test.title}`);
  }

  async onEnd(): Promise<{ status?: FullResult['status'] } | void> {
    for (const dir of this.dirs) this.leaks.push(...scanDir(resolve(this.root, dir), this.secrets));
    if (this.weakSecret) {
      // Never print the value or its length.
      console.error(`\nsecret-guard (#155): DESK_PASSWORD is empty or shorter than ${minSecretLength} characters, so the leak check can't run. Set it to the Desk.UserAdmin password.`);
      return { status: 'failed' };
    }
    if (!this.leaks.length) return;
    console.error(`\nsecret-guard (#155): DESK_PASSWORD is in the run's output: ${this.leaks.join(', ')}`);
    return { status: 'failed' };
  }

  printsToStdio(): boolean {
    return false;
  }
}

/** The project that runs the guard's own checks (playwright.config.ts): it needs no password. */
const selfCheckProject = 'guard';

/** Identity's minimum (IdentityPolicy.MinPasswordLength); Desk.UserAdmin's generated passwords are longer. */
const minSecretLength = 14;

/** The forms a secret can take once written into a report: as typed, JSON-escaped, URL-encoded. */
function needles(secrets: string[]): Buffer[] {
  return secrets
    .filter((s) => s.length > 0)
    .flatMap((s) => [s, JSON.stringify(s).slice(1, -1), encodeURIComponent(s)])
    .map((s) => Buffer.from(s));
}

/** True when the bytes, or any zip inside them (a file, or a base64 data URL in a page), hold one of the secrets. */
export function findSecret(bytes: Buffer, secrets: string[], depth = 0): boolean {
  if (needles(secrets).some((n) => bytes.includes(n))) return true;
  if (depth > 2) return false;
  return embeddedZips(bytes).some((zip) => zipEntries(zip).some((entry) => findSecret(entry, secrets, depth + 1)));
}

/** Files under dir whose contents hold a secret, as paths relative to dir's parent. A missing dir has none. */
export function scanDir(dir: string, secrets: string[]): string[] {
  let names: string[];
  try {
    names = readdirSync(dir);
  } catch {
    return [];
  }
  return names.flatMap((name) => {
    const path = join(dir, name);
    if (statSync(path).isDirectory()) return scanDir(path, secrets);
    return findSecret(readFileSync(path), secrets) ? [path] : [];
  });
}

/** The bytes themselves when they are a zip, plus every base64 zip data URL in them (the html report's data). */
function embeddedZips(bytes: Buffer): Buffer[] {
  const zips = bytes.subarray(0, 4).equals(localHeader) ? [bytes] : [];
  const text = bytes.toString('latin1');
  for (const m of text.matchAll(/data:application\/zip;base64,([A-Za-z0-9+/=]+)/g)) zips.push(Buffer.from(m[1], 'base64'));
  return zips;
}

const localHeader = Buffer.from([0x50, 0x4b, 0x03, 0x04]);

/** The uncompressed entries of a zip (stored or deflated), read from its central directory. A bad zip has none. */
export function zipEntries(zip: Buffer): Buffer[] {
  const eocd = zip.lastIndexOf(Buffer.from([0x50, 0x4b, 0x05, 0x06]));
  if (eocd < 0 || eocd + 22 > zip.length) return [];
  const count = zip.readUInt16LE(eocd + 10);
  let at = zip.readUInt32LE(eocd + 16);
  const entries: Buffer[] = [];
  for (let i = 0; i < count && at + 46 <= zip.length && zip.readUInt32LE(at) === 0x02014b50; i++) {
    const method = zip.readUInt16LE(at + 10);
    const size = zip.readUInt32LE(at + 20);
    const offset = zip.readUInt32LE(at + 42);
    at += 46 + zip.readUInt16LE(at + 28) + zip.readUInt16LE(at + 30) + zip.readUInt16LE(at + 32);
    if (offset + 30 > zip.length) continue;
    const start = offset + 30 + zip.readUInt16LE(offset + 26) + zip.readUInt16LE(offset + 28);
    const data = zip.subarray(start, start + size);
    try {
      if (method === 0) entries.push(data);
      else if (method === 8) entries.push(inflateRawSync(data));
    } catch {
      // An entry that won't inflate can't be read by the report viewer either.
    }
  }
  return entries;
}
