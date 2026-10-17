/**
 * Clickjacking guard for the GitHub Pages copy (#66, ADR-0024). Pages can't send response headers, and a meta CSP
 * ignores frame-ancestors, so nothing stops another site framing that copy; the app instead refuses to start in a
 * frame. The Render copy sends frame-ancestors 'none' and X-Frame-Options: DENY, so a browser never gets this far.
 * A sandboxed frame without allow-scripts runs no script at all, so it shows nothing either.
 */
export function isFramed(win: Window): boolean {
  try {
    return win.top !== win.self;
  } catch {
    return true; // can't tell: assume framed
  }
}

/**
 * Returns true (and shows a notice in place of the app) when the page is framed; the caller then skips bootstrap.
 * The notice is an alert so screen readers announce it, styled inline with the global tokens (styles.css is loaded
 * before main.js runs); CSSOM assignments aren't inline-style markup, so they never trip style-src.
 */
export function refuseFraming(win: Window, doc: Document): boolean {
  if (!isFramed(win)) return false;
  const notice = doc.createElement('p');
  notice.setAttribute('role', 'alert');
  Object.assign(notice.style, {
    margin: '16px',
    padding: '12px 16px',
    border: '1px solid var(--line)',
    borderRadius: '4px',
    background: 'var(--surface-2)',
    color: 'var(--text)',
  });
  notice.textContent = 'Credit Desk Analytics cannot be shown inside a frame. Open it in its own tab.';
  doc.body.replaceChildren(notice);
  return true;
}

/** Runs start (the app's bootstrap) unless the page is framed, so main.ts itself has nothing to decide. */
export function startUnlessFramed(win: Window, doc: Document, start: () => Promise<unknown>): Promise<unknown> {
  return refuseFraming(win, doc) ? Promise.resolve() : start();
}
