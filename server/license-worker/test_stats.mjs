// Local test of the statistics logic: real SQLite (node:sqlite) and a fake clock, so days can pass.
//   node test_stats.mjs
// The platform itself is covered by running the worker under `wrangler dev`.
import assert from "node:assert/strict";
import { DatabaseSync } from "node:sqlite";
import { Stats, cleanPing, countDownloads } from "./src/stats.js";

const db = new DatabaseSync(":memory:");
const sql = {
  exec(query, ...args) {
    if (!args.length && query.includes(";")) { db.exec(query); return { toArray: () => [], [Symbol.iterator]: () => [][Symbol.iterator]() }; }
    const st = db.prepare(query);
    const rows = /^\s*(SELECT)/i.test(query) ? st.all(...args) : (st.run(...args), []);
    return { toArray: () => rows, [Symbol.iterator]: () => rows[Symbol.iterator]() };
  },
};
const DAY = 864e5, MIN = 60e3;
let clock = Date.parse("2026-11-06T03:00:00Z");   // 12:00 in Korea
Date.now = () => clock;

let github = () => new Response(JSON.stringify(releases(10)), { status: 200 });
globalThis.fetch = async () => github();
const releases = n => [
  { tag_name: "v0.8.6", published_at: "2026-11-06T10:00:00Z", draft: false, assets: [
    { name: "SystemBoost-Windows-Setup.exe", download_count: n }, { name: "SystemBoost-win-Setup.exe", download_count: 1 },
    { name: "SystemBoost-Windows-Portable.zip", download_count: 2 }, { name: "SystemBoost-win-Portable.zip", download_count: 1 },
    { name: "SystemBoost-macOS-AppleSilicon.pkg", download_count: 3 }, { name: "SystemBoost-osx-arm64-Setup.pkg", download_count: 1 },
    { name: "SystemBoost-osx-x64-Portable.zip", download_count: 1 },
    { name: "SystemBoost-0.8.6-full.nupkg", download_count: 4 }, { name: "SystemBoost-0.8.6-osx-x64-delta.nupkg", download_count: 1 },
    { name: "releases.win.json", download_count: 500 }, { name: "RELEASES", download_count: 500 } ] },
  { tag_name: "v0.8.7-draft", draft: true, assets: [{ name: "SystemBoost-Windows-Setup.exe", download_count: 99 }] },
];

const s = new Stats({ storage: { sql } });
const id = n => n.toString(16).padStart(32, "0");
const ping = (n, extra = {}) => s.ping(cleanPing({ id: id(n), ver: "0.8.6", os: "win", plan: "free", fg: 0, boost: 0, ...extra }, "KR"));

// what is accepted
const good = { id: id(1), ver: "0.8.6", os: "win", plan: "free", fg: 2, boost: 1 };
assert.equal(cleanPing(good, "KR").country, "KR");
assert.equal(cleanPing(good, "T1").country, "XX");
assert.equal(cleanPing(good, undefined).country, "XX");
assert.deepEqual(Object.keys(cleanPing({ ...good, email: "a@b.c", key: "x" }, "KR")).sort(), ["boost", "country", "fg", "id", "os", "plan", "ver"]);
for (const bad of [null, {}, { ...good, id: "zz" }, { ...good, ver: "<b>" }, { ...good, os: "linux" }, { ...good, plan: "gold" }, { ...good, fg: 5 }, { ...good, boost: 2 }, { ...good, id: 7 }])
  assert.equal(cleanPing(bad, "KR"), null);

// first start, then the app keeps pinging
assert.deepEqual(ping(1), { next: 60 });
let r = await s.read();
assert.equal(r.live.total, 1); assert.equal(r.today.active, 1); assert.equal(r.today.installs, 1); assert.equal(r.today.seconds, 0); assert.equal(r.installs, 1);
clock += 5e3; ping(1);                                  // a ping right after the last one changes nothing
assert.equal(s.one("SELECT last_seen FROM installs").last_seen, clock - 5e3);
clock += 55e3; ping(1, { fg: 4 });
clock += 60e3; ping(1, { fg: 4, boost: 1 });
r = await s.read();
assert.equal(r.today.active, 1, "still one person"); assert.equal(r.today.installs, 1); assert.equal(r.today.seconds, 120); assert.equal(r.today.frameGenSeconds, 120);
assert.equal(r.live.frameGen, 1); assert.equal(r.live.boost, 1); assert.deepEqual(r.live.multipliers, { 4: 1 });

// the app closes: it leaves "live" after the window, and the gap is not usage
clock += 151e3;
r = await s.read();
assert.equal(r.liveFor, 150); assert.equal(r.live.total, 0); assert.equal(r.today.active, 1);
clock += 3600e3; ping(1);
assert.equal((await s.read()).today.seconds, 120, "time with the app closed does not count");

// a second install elsewhere: breakdowns (scan is reused for five minutes)
s.ping(cleanPing({ id: id(2), ver: "0.8.5", os: "mac", plan: "ultra", fg: 8, boost: 0 }, "JP"));
r = await s.read();
assert.equal(r.live.total, 2); assert.equal(r.today.active, 2); assert.equal(r.installs, 2);
assert.equal(r.month, 1, "scan not due yet");
clock += 6 * MIN;
r = await s.read();
assert.equal(r.month, 2); assert.equal(r.week, 2);
assert.deepEqual(r.plans.map(p => [p.k, p.n]).sort(), [["free", 1], ["ultra", 1]]);
assert.deepEqual(r.systems.map(p => p.k).sort(), ["mac", "win"]);
assert.deepEqual(r.versions.map(p => p.k).sort(), ["0.8.5", "0.8.6"]);
assert.deepEqual(r.countries.map(p => p.k).sort(), ["JP", "KR"]);

// a new Korean day starts at 15:00 UTC, not at midnight UTC
clock = Date.parse("2026-11-06T14:59:00Z"); ping(1);
assert.equal((await s.read()).today.active, 2);
clock = Date.parse("2026-11-06T15:01:00Z"); ping(1);
r = await s.read();
assert.equal(r.today.active, 1, "next Korean day"); assert.equal(r.today.installs, 0); assert.equal(r.installs, 2);
assert.equal(r.history.length, 30); assert.equal(r.history.at(-1).day, "2026-11-07"); assert.equal(r.history.at(-1).active, 1); assert.equal(r.history.at(-2).active, 2);

// the first day of counting has nothing to subtract, later days do
assert.equal(r.downloads.installers, 19);
assert.deepEqual(r.downloads.total, { winSetup: 11, winPortable: 3, mac: 5, updates: 5 }, "update checks and drafts are not downloads");
assert.equal(r.history.at(-2).downloads, null);
github = () => new Response(JSON.stringify(releases(15)), { status: 200 });
clock += 6 * MIN; r = await s.read();
assert.equal(r.history.at(-1).downloads, 5); assert.equal(r.history.at(-1).updates, 0); assert.equal(r.downloadsError, "");
assert.equal(r.history.at(-30).downloads, null);

// GitHub refuses the worker: the old numbers stay and the dashboard is told
github = () => new Response("{}", { status: 403 });
const before = r.downloads.installers;
clock += 6 * MIN; r = await s.read();
assert.equal(r.downloadsError, "GitHub 403"); assert.equal(r.downloads.installers, before);
let asked = 0; github = () => { asked++; return new Response("{}", { status: 403 }); };
clock += 15e3; await s.read();
assert.equal(asked, 0, "not asked again on every refresh");
// ...and the list from the owner's browser is taken instead
s.saveDownloads(releases(30));
r = await s.read();
assert.equal(r.downloads.installers, 39); assert.equal(r.history.at(-1).downloads, 20);
github = () => { throw new Error("offline"); };
clock += 6 * MIN; r = await s.read();
assert.equal(r.downloadsError, "GitHub 연결 실패"); assert.equal(r.downloads.installers, 39);
assert.throws(() => s.saveDownloads({ nope: 1 }));

// a missing day in the totals: the next day takes the difference from the last one seen
github = () => new Response(JSON.stringify(releases(40)), { status: 200 });
clock += 2 * DAY; r = await s.read();
assert.equal(r.history.at(-1).downloads, 10); assert.equal(r.history.at(-2).downloads, 0);

// old installs go away, the daily totals do not
clock += 91 * DAY;
await s.tick();
assert.equal(s.one("SELECT COUNT(*) AS n FROM installs").n, 0);
s.scan = null; r = await s.read();
assert.equal(r.installs, 2); assert.equal(r.month, 0);

// busier days: apps slow down, so the free plan's limits are not reached
const paced = people => { clock += DAY; for (let i = 0; i < people; i++) ping(1000 + i + clock % 100000); return ping(999999).next; };
assert.equal(paced(50), 60); assert.equal(paced(98), 60);
assert.equal(paced(100), 300); assert.equal(paced(400), 900); assert.equal(paced(1500), 3600);

// made-up installs stop at the daily cap
clock += DAY;
for (let i = 0; i < 3005; i++) ping(5000000 + i);
assert.equal((await s.read()).today.installs, 3000);

// file names
assert.deepEqual(countDownloads([{ tag_name: "v1", assets: [{ name: "SystemBoost-macOS-Intel.pkg", download_count: 2 }, { name: "SystemBoost-osx-arm64-Portable.zip", download_count: 1 }] }]).total,
                 { winSetup: 0, winPortable: 0, mac: 3, updates: 0 });
console.log("ok");
