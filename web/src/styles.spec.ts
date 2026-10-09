/**
 * #253: the global .sr-only rule (As-of label, the status bar's "approximately") hides its text visually but keeps it
 * in the accessibility tree. jsdom does no layout, so this checks the computed declarations, not the rendered box
 * (e2e/tests/a11y.spec.ts does that in a real browser). #316: the whole of styles.scss is compiled with sass and
 * applied, so a later rule in the file that overrides .sr-only fails here too, and a competing rule with a real
 * margin, padding and border goes first, so the reset has something to reset.
 */
describe('.sr-only (compiled styles.scss)', () => {
  let css: string;
  const injected: HTMLElement[] = [];
  let span: HTMLSpanElement;

  beforeAll(async () => {
    // The unit-test build has neither Node types nor a loader for .scss, so compile the source as written (ng test
    // runs from web/). node_modules resolves the @use of the self-hosted font.
    const sass = (await import('sass' as string)) as { compile(path: string, options: { loadPaths: string[] }): { css: string } };
    css = sass.compile('src/styles.scss', { loadPaths: ['node_modules'] }).css;
  });

  function addStyle(text: string): void {
    const style = document.createElement('style');
    style.textContent = text;
    document.head.append(style);
    injected.push(style);
  }

  beforeEach(() => {
    addStyle('span { position: static; margin: 7px; padding: 5px; border: 3px solid; clip: auto; clip-path: none; }');
    addStyle(css);
    // The As-of label's place in the shell (shell.html), so a contextual override such as `.field span` counts too.
    const header = document.createElement('header');
    header.className = 'topbar';
    header.innerHTML = '<label class="field"><span class="sr-only">As-of date</span><select></select></label>';
    document.body.append(header);
    injected.push(header);
    span = header.querySelector('.sr-only')!;
  });

  afterEach(() => {
    injected.splice(0).forEach((el) => el.remove());
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
    // The fallback for browsers without clip-path (R300-03).
    expect(cs.getPropertyValue('clip')).toBe('rect(0px, 0px, 0px, 0px)');
    expect(cs.whiteSpace).toBe('nowrap');
  });

  it('stays reachable by screen readers: not display:none, not visibility:hidden', () => {
    const cs = getComputedStyle(span);
    expect(cs.display).not.toBe('none');
    expect(cs.visibility).not.toBe('hidden');
  });
});
