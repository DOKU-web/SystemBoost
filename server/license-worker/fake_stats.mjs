// For the tests: the Stats Durable Object (src/stats.js) on node:sqlite, behind a binding like env.STATS.
// ctl.down = true makes every call fail (the object cannot be reached); ctl.calls counts calls.
//   import { fakeStats } from "./fake_stats.mjs";  const st = fakeStats();  env.STATS = st.binding;
import { DatabaseSync } from "node:sqlite";
import { Stats } from "./src/stats.js";

export function fakeStats() {
  const db = new DatabaseSync(":memory:");
  const obj = new Stats({ storage: { sql: { exec(query, ...args) {
    if (!args.length && query.includes(";")) { db.exec(query); return { toArray: () => [], [Symbol.iterator]: () => [][Symbol.iterator]() }; }
    const st = db.prepare(query), rows = /^\s*SELECT/i.test(query) ? st.all(...args) : (st.run(...args), []);
    return { toArray: () => rows, [Symbol.iterator]: () => rows[Symbol.iterator]() };
  } } } });
  const ctl = { down: false, calls: 0, db, obj };
  ctl.binding = { idFromName: name => name, get: () => ({ fetch: async (u, init) => {
    ctl.calls++;
    if (ctl.down) throw new Error("Durable Object down");
    return obj.fetch(new Request(u, init));
  } }) };
  ctl.rows = (query, ...args) => db.prepare(query).all(...args);
  return ctl;
}
