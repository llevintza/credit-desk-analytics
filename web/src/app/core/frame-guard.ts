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

/** Returns true (and shows a plain notice in place of the app) when the page is framed; the caller then skips bootstrap. */
export function refuseFraming(win: Window, doc: Document): boolean {
  if (!isFramed(win)) return false;
  const notice = doc.createElement('p');
  notice.textContent = 'Credit Desk Analytics cannot be shown inside a frame. Open it in its own tab.';
  doc.body.replaceChildren(notice);
  return true;
}

/** Runs start (the app's bootstrap) unless the page is framed, so main.ts itself has nothing to decide. */
export function startUnlessFramed(win: Window, doc: Document, start: () => Promise<unknown>): Promise<unknown> {
  return refuseFraming(win, doc) ? Promise.resolve() : start();
}
