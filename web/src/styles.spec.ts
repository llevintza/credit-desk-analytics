/**
 * #253: the global .sr-only rule (As-of label, the status bar's "approximately") hides its text visually but keeps it
 * in the accessibility tree. jsdom does no layout, so this checks the computed declarations of the real rule, applied
 * to a real element: clip-path (clip is deprecated), plus the margin/padding/border reset that keeps the 1px box from
 * taking space or showing a border wherever it sits.
 */
describe('.sr-only (styles.scss)', () => {
  let styles: string;
  let style: HTMLStyleElement;
  let span: HTMLSpanElement;

  beforeAll(async () => {
    // The unit-test build has neither Node types nor a text loader for .scss, so read the source as written (ng test runs from web/).
    const fs = (await import('node:fs' as string)) as { readFileSync(path: string, encoding: 'utf8'): string };
    styles = fs.readFileSync('src/styles.scss', 'utf8');
  });

  beforeEach(() => {
    const rule = /^\.sr-only\s*\{[^}]*\}/m.exec(styles)?.[0];
    expect(rule, '.sr-only rule in styles.scss').toBeDefined();
    style = document.createElement('style');
    style.textContent = rule!;
    document.head.append(style);
    span = document.createElement('span');
    span.className = 'sr-only';
    span.textContent = 'approximately ';
    document.body.append(span);
  });

  afterEach(() => {
    style.remove();
    span.remove();
  });

  it('is visually hidden: a 1px clipped box with no margin, padding or border footprint', () => {
    const cs = getComputedStyle(span);
    expect(cs.position).toBe('absolute');
    expect(cs.width).toBe('1px');
    expect(cs.height).toBe('1px');
    expect(cs.margin).toBe('-1px');
    expect(cs.padding).toBe('0px');
    expect(cs.borderWidth).toBe('0px');
    expect(cs.overflow).toBe('hidden');
    expect(cs.getPropertyValue('clip-path')).toBe('inset(50%)');
    expect(cs.whiteSpace).toBe('nowrap');
  });

  it('stays reachable by screen readers: not display:none, not visibility:hidden, not aria-hidden', () => {
    const cs = getComputedStyle(span);
    expect(cs.display).not.toBe('none');
    expect(cs.visibility).not.toBe('hidden');
    expect(span.closest('[aria-hidden="true"]')).toBeNull();
    expect(span.textContent).toBe('approximately ');
  });
});
