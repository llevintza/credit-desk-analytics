// A clean Angular-like entry: relative static and dynamic imports only. Strings that merely look like imports
// (code generators, error text) are not imports.
import { a } from "./chunk-a.js";
export * from "./chunk-a.js";
const lazy = () => import(`./lazy/chunk-b.js`);
const docs = `import { Grid } from 'third-party-grid';`;
const text = "Updated property from" + a;
console.log(lazy, docs, text);
