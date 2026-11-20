// For the tests: the Stats Durable Object (src/stats.js) on node:sqlite, behind a binding like env.STATS.
// ctl.down = true makes every call fail (the object cannot be reached); ctl.calls counts calls.
//   import { fakeStats } from "./fake_stats.mjs";  const st = fakeStats();  env.STATS = st.binding;
// fakeStats(OtherStats): the same around another version's class (test_accounts_gate.mjs runs the server of 776dd71).
// fakeStats(Stats, db): around a database that already has tables and rows (as left by an earlier version).
// ctl.trace = []: every query the object runs is pushed there (test_preorder.mjs checks their plans).
// fakeStats(Stats, db, { storage, env }): more storage methods (setAlarm, getAlarm) and the env the object is given
// (test_preorder_notify.mjs).
import { DatabaseSync } from "node:sqlite";
import { Stats } from "./src/stats.js";

export function fakeStats(StatsClass = Stats, db = new DatabaseSync(":memory:"), opts = {}) {
  const ctl = { down: false, calls: 0, db, trace: null };
  const obj = new StatsClass({ storage: { sql: { exec(query, ...args) {
    if (ctl.trace) ctl.trace.push(query);
    if (!args.length && query.includes(";")) { db.exec(query); return { toArray: () => [], [Symbol.iterator]: () => [][Symbol.iterator]() }; }
    const st = db.prepare(query), rows = /^\s*SELECT/i.test(query) ? st.all(...args) : (st.run(...args), []);
    return { toArray: () => rows, [Symbol.iterator]: () => rows[Symbol.iterator]() };
  } }, ...(opts.storage || {}) } }, opts.env);
  ctl.obj = obj;
  ctl.binding = { idFromName: name => name, get: () => ({ fetch: async (u, init) => {
    ctl.calls++;
    if (ctl.down) throw new Error("Durable Object down");
    return obj.fetch(new Request(u, init));
  } }) };
  ctl.rows = (query, ...args) => db.prepare(query).all(...args);
  return ctl;
}
