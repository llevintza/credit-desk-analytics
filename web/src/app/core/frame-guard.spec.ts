import { isFramed, refuseFraming, startUnlessFramed } from './frame-guard';

/**
 * #66: the GitHub Pages copy can't send frame-ancestors or X-Frame-Options (a meta CSP ignores frame-ancestors,
 * ADR-0024), so the app itself declines to start inside a frame. The Render copy is already refused by the browser.
 */
describe('frame guard (#66)', () => {
  const top = {} as Window;

  it('treats a top-level window as not framed', () => {
    const win = { self: top, top } as Window;
    expect(isFramed(win)).toBe(false);
  });

  it('treats a window whose top is another window as framed', () => {
    expect(isFramed({ self: {} as Window, top } as Window)).toBe(true);
  });

  it('fails closed when the top window cannot be read', () => {
    const win = {
      self: top,
      get top(): Window {
        throw new DOMException('Blocked', 'SecurityError');
      },
    } as unknown as Window;
    expect(isFramed(win)).toBe(true);
  });

  it('leaves a top-level page alone so the app can bootstrap', () => {
    const doc = document.implementation.createHTMLDocument('t');
    doc.body.innerHTML = '<app-root></app-root>';
    expect(refuseFraming({ self: top, top } as Window, doc)).toBe(false);
    expect(doc.querySelector('app-root')).not.toBeNull();
  });

  it('replaces the app root with a plain notice when framed', () => {
    const doc = document.implementation.createHTMLDocument('t');
    doc.body.innerHTML = '<app-root></app-root>';
    expect(refuseFraming({ self: {} as Window, top } as Window, doc)).toBe(true);
    expect(doc.querySelector('app-root')).toBeNull();
    expect(doc.body.textContent).toBe('Credit Desk Analytics cannot be shown inside a frame. Open it in its own tab.');
    expect(doc.body.children).toHaveLength(1);
    expect(doc.body.firstElementChild?.tagName).toBe('P');
  });

  it('announces the notice as an alert, styled with the design tokens', () => {
    const doc = document.implementation.createHTMLDocument('t');
    doc.body.innerHTML = '<app-root></app-root>';
    refuseFraming({ self: {} as Window, top } as Window, doc);
    const notice = doc.body.firstElementChild as HTMLElement;
    expect(notice.getAttribute('role')).toBe('alert');
    expect(notice.style.margin).toBe('16px');
    expect(notice.style.padding).toBe('12px 16px');
    expect(notice.style.border).toBe('1px solid var(--line)');
    expect(notice.style.borderRadius).toBe('4px');
    expect(notice.style.background).toBe('var(--surface-2)');
    expect(notice.style.color).toBe('var(--text)');
  });

  it('starts the app at top level and never when framed', async () => {
    const doc = document.implementation.createHTMLDocument('t');
    doc.body.innerHTML = '<app-root></app-root>';
    const start = vi.fn(() => Promise.resolve('started'));
    await expect(startUnlessFramed({ self: top, top } as Window, doc, start)).resolves.toBe('started');
    expect(start).toHaveBeenCalledTimes(1);
    await expect(startUnlessFramed({ self: {} as Window, top } as Window, doc, start)).resolves.toBeUndefined();
    expect(start).toHaveBeenCalledTimes(1);
    expect(doc.querySelector('app-root')).toBeNull();
  });
});
