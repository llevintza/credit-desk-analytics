import { x } from "https://cdn.example.test/lib.js";
import "left-pad";
const name = "./chunk.js";
import(name);
import("./chunk.js" + "?v=1");
import("https://cdn.example.test/lazy.js");
importScripts("https://cdn.example.test/worker.js");
document.head.appendChild(document.createElement("script"));
export { x };
