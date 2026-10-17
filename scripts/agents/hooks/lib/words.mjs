/** Shared word helpers for the guard's analysers. */
export const base = (s) => s.slice(s.lastIndexOf("/") + 1);
export const ASSIGN = /^[A-Za-z_]\w*=/;

/** Index of a segment's command word: the first word after `VAR=x` assignments; -1 if none. */
export const headIndex = (seg) => seg.words.findIndex((w) => w.quoted || !ASSIGN.test(w.text));

/** The command word of a segment, or null. */
export const head = (seg) => seg.words[headIndex(seg)] ?? null;
