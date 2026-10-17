/**
 * Glob and brace words the guard can't name literally (#512 review; G4, G18, G20): what a
 * glob or brace word could expand to, checked against the names a rule protects.
 */
const base = (s) => s.slice(s.lastIndexOf("/") + 1);

/** `a{b,c}d` → [abd, acd], recursively; a word without a brace list is itself. */
export function braces(w) {
  const m = /^(.*?)\{([^{}]*,[^{}]*)\}(.*)$/.exec(w);
  return m ? m[2].split(",").flatMap((x) => braces(`${m[1]}${x}${m[3]}`)) : [w];
}

/** One path segment's glob as a RegExp; like bash, a leading `.` must be literal. */
export function globRe(p) {
  const src = p.replace(/[.+^$()|\\{}]/g, "\\$&").replace(/\*/g, ".*").replace(/\?/g, ".").replace(/\[!/g, "[^");
  return new RegExp(`^${p.startsWith(".") ? "" : "(?!\\.)"}${src}$`, "i");
}

/**
 * Could the word's last segment expand to one of `names`? A pattern with no literal letter
 * (`*`, `?.?`) is skipped: it names files in the cwd, not a program.
 */
export const couldName = (text, names) =>
  braces(text).some((w) => /[a-z]/i.test(base(w).replace(/\[[^\]]*\]/g, "")) && names.some((n) => globRe(base(w)).test(n)));

// Names that stand for "a secret file" (adapters.md: `.env`, `.env.*` but `.env.example`).
const ENV_NAMES = [".env", ".env.local", ".env.dev", ".env.development", ".env.prod", ".env.production", ".env.qa", ".env.test", ".env.staging", ".env.x"];

/** Could the word expand to `.env`, `.env.*` (not `.env.example`) or a path under `secrets/`? */
export function couldBeSecret(text) {
  return braces(text).some((w) => {
    const parts = w.split("/");
    if (parts.slice(0, -1).some((p) => globRe(p).test("secrets"))) return true;
    const b = parts.at(-1);
    return ENV_NAMES.some((n) => globRe(b).test(n)) || (/^\.env\./i.test(b) && /[*?[]/.test(b) && b.toLowerCase() !== ".env.example");
  });
}
