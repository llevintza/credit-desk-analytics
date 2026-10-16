import { isFramed, refuseFraming } from './frame-guard';

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
});
