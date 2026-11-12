// Local test of the statistics logic: real SQLite (node:sqlite) and a fake clock, so days can pass.
//   node test_stats.mjs
// The platform itself is covered by running the worker under `wrangler dev`.
import assert from "node:assert/strict";
import { DatabaseSync } from "node:sqlite";
import { FEED, FEED_KINDS, Stats, cleanPing, countDownloads } from "./src/stats.js";

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
// the support feed (support.js writes it, the dashboard reads it): one row per buyer action, no text
const call = async (path, body) => (await s.fetch(new Request("https://stats" + path, { method: "POST", body: JSON.stringify(body) }))).json();
const T = n => "T-AAAA-" + "ABCDEFGH"[n % 8] + "BC" + "23456789"[Math.floor(n / 8) % 8];   // valid ticket numbers
assert.deepEqual(await call("/feed", {}), { events: [], last: 0, more: false }, "empty");
assert.deepEqual(await call("/event", { kind: "ticket", ticket: T(1), cat: "bug" }), { ok: true });
for (const bad of [null, {}, { kind: "admin_reply", ticket: T(1), cat: "bug" }, { kind: "reply", ticket: "T-0000-0000", cat: "bug" }, { kind: "reply", ticket: "<b>", cat: "bug" },
                   { kind: "reply", ticket: T(1) + " ", cat: "bug" }]) {
  const res = await s.fetch(new Request("https://stats/event", { method: "POST", body: JSON.stringify(bad) }));
  assert.equal(res.status, 400, JSON.stringify(bad));
}
await call("/event", { kind: "reply", ticket: T(1), cat: "<script>" });
assert.equal(s.one("SELECT cat FROM support_events ORDER BY seq DESC LIMIT 1").cat, "", "an odd category is stored empty");
assert.deepEqual(s.sql.exec("SELECT * FROM support_events ORDER BY seq").toArray().map(r => Object.keys(r).sort().join()), ["at,cat,kind,seq,ticket", "at,cat,kind,seq,ticket"], "these columns only");
for (const kind of FEED_KINDS) await call("/event", { kind, ticket: T(2), cat: "refund" });
let f = await call("/feed", { after: 0 });
assert.deepEqual(f.events.map(e => e.kind), ["ticket", "reply", ...FEED_KINDS]);
assert.deepEqual([f.last, f.more, f.events[0].at], [8, false, clock]);
f = await call("/feed", { after: 5 });
assert.deepEqual(f.events.map(e => e.seq), [6, 7, 8]);
// paging: at most 100 a call, oldest first; without after the newest 20
for (let i = 0; i < 242; i++) { clock += 1000; await call("/event", { kind: "reply", ticket: T(i % 40), cat: "howto" }); }
f = await call("/feed", { after: 0 });
assert.deepEqual([f.events.length, f.events[0].seq, f.last, f.more], [FEED.page, 1, 100, true]);
f = await call("/feed", { after: f.last }); assert.deepEqual([f.events[0].seq, f.last, f.more], [101, 200, true]);
f = await call("/feed", { after: f.last }); assert.deepEqual([f.events.length, f.last, f.more], [50, 250, false]);
f = await call("/feed", {});
assert.deepEqual(f.events.map(e => e.seq), Array.from({ length: FEED.recent }, (_, i) => 231 + i), "the newest 20, oldest first");
assert.deepEqual([f.last, f.more], [250, false]);
assert.deepEqual(await call("/feed", { after: 900 }), { events: [], last: 250, more: false }, "a cursor from an older object: go on from the end");
// a deleted ticket's rows go
const ofT3 = s.one("SELECT COUNT(*) AS n FROM support_events WHERE ticket = ?", T(3)).n;
assert.ok(ofT3 > 0);
await call("/forget", { ticket: T(3) });
assert.equal(s.one("SELECT COUNT(*) AS n FROM support_events WHERE ticket = ?", T(3)).n, 0);
assert.equal(s.one("SELECT COUNT(*) AS n FROM support_events").n, 250 - ofT3);
// at most the newest 1,000 rows
for (let i = 0; i < 1100; i++) await call("/event", { kind: "reply", ticket: T(5), cat: "howto" });
assert.equal(s.one("SELECT COUNT(*) AS n FROM support_events").n, FEED.keepRows);
assert.equal(s.one("SELECT MIN(seq) AS n FROM support_events").n, 1350 - FEED.keepRows + 1, "the oldest went");
// 30 days: older rows are not shown, and go at the next row or the hourly tick
clock += FEED.keepDays * DAY - 1000;
await call("/event", { kind: "close", ticket: T(6), cat: "pay" });
assert.equal(s.one("SELECT COUNT(*) AS n FROM support_events").n, FEED.keepRows, "29 days later all still kept");
clock += 2000;
f = await call("/feed", {});
assert.deepEqual(f.events.map(e => [e.seq, e.kind]), [[1351, "close"]], "the rows older than 30 days are no longer shown");
assert.equal((await call("/feed", { after: 0 })).events.length, 1);
await s.tick();
assert.equal(s.one("SELECT COUNT(*) AS n FROM support_events").n, 1, "and the tick deletes them");
clock += FEED.keepDays * DAY + 1;
await call("/event", { kind: "card", ticket: T(7), cat: "refund" });
assert.deepEqual(s.sql.exec("SELECT seq, kind FROM support_events").toArray().map(r => [r.seq, r.kind]), [[1352, "card"]], "a new row prunes too; seq keeps counting");

// ---- counters (support budget, per-address limits), the admin gate and access log, the audit record
{
  const take = items => call("/take", { items });
  assert.deepEqual(await take([{ name: "x:a", ttl: 60, max: 2 }]), { ok: true, n: { "x:a": 1 } });
  await take([{ name: "x:a", ttl: 60, max: 2 }]);
  assert.deepEqual(await take([{ name: "x:b", ttl: 60, max: 5 }, { name: "x:a", ttl: 60, max: 2 }]), { ok: false, over: "x:a", n: { "x:b": 0, "x:a": 2 } }, "all or none");
  assert.deepEqual(await call("/peek", { names: ["x:a", "x:b"] }), { n: { "x:a": 2, "x:b": 0 } }, "x:b was not counted");
  assert.deepEqual(await take([{ name: "x:c", ttl: 60, by: 3 }]), { ok: true, n: { "x:c": 3 } });
  for (const bad of [[], [{ name: "bad name", ttl: 60 }], [{ name: "x:d", ttl: 0 }], [{ name: "x:d", ttl: 401 * 86400 }], [{ name: "x:d", ttl: 60, by: 0 }],
                     [{ name: "x:d", ttl: 60, by: 1001 }], [{ name: "x:d", ttl: 60, max: -1 }], Array(9).fill({ name: "x:e", ttl: 60 })])
    assert.deepEqual(await take(bad), { error: "bad items" });
  assert.deepEqual(await call("/peek", { names: ["has space"] }), { error: "bad names" });
  clock += 61e3;   // past its end time a count is 0 again, and the tick deletes it
  assert.deepEqual(await call("/peek", { names: ["x:a"] }), { n: { "x:a": 0 } });
  assert.deepEqual(await take([{ name: "x:a", ttl: 60, max: 2 }]), { ok: true, n: { "x:a": 1 } }, "starts again");
  clock += 61e3;
  await s.tick();
  assert.equal(s.one("SELECT COUNT(*) AS n FROM counters WHERE name LIKE 'x:%'").n, 0);

  // the admin gate: 5 misses lock an address for 15 minutes from its last miss, 20 an hour lock everyone
  const ip = "0123456789abcdef", H = "2026-12-01T05";
  let hour = H;
  const gate = (extra, who = ip) => call("/gate", { ip: who, hour, ipMax: 5, allMax: 20, ...extra });
  for (let i = 1; i <= 5; i++) assert.deepEqual(await gate({ fail: true }), { ip: i, all: i });
  assert.deepEqual(await gate({ route: "GET /keys", action: "키 목록", target: "all" }), { locked: true, ip: 5, all: 5 });
  assert.equal(s.one("SELECT COUNT(*) AS n FROM admin_access").n, 0, "a refused call writes no row");
  clock += 15 * MIN + 1;
  assert.deepEqual(await gate({ route: "GET /keys", action: "키 목록", target: "all" }), { ok: true, login: true });
  assert.deepEqual(await gate({ route: "GET /summary" }), { ok: true, login: false });
  assert.deepEqual(s.sql.exec("SELECT ip, action, target FROM admin_access ORDER BY seq").toArray().map(r => [r.ip, r.action, r.target]),
                   [[ip, "login", ""], [ip, "키 목록", "all"]], "a login, then what was shown; the summary shows no personal data");
  for (let i = 0; i < 15; i++) await gate({ fail: true }, (i + 1).toString(16).padStart(16, "a"));
  assert.deepEqual(await gate({ route: "GET /summary" }), { locked: true, ip: 0, all: 20 }, "20 misses in the hour: the right password too");
  hour = "2026-12-01T06";
  assert.deepEqual(await gate({ route: "GET /summary" }), { ok: true, login: false }, "the next hour");
  for (const bad of [{ ip: "x", hour: H, ipMax: 5, allMax: 20 }, { ip, hour: "2026-12-01", ipMax: 5, allMax: 20 }, { ip, hour: H, ipMax: "5", allMax: 20 }])
    assert.deepEqual(await call("/gate", bad), { error: "bad gate" });
  // a session lasts 30 quiet minutes
  clock += 31 * MIN;
  assert.deepEqual(await gate({ route: "GET /orders", action: "주문 목록", target: "open" }), { ok: true, login: true });
  // the log in pages of 100, newest first
  for (let i = 0; i < 120; i++) await gate({ route: "GET /key", action: "키 원문", target: i.toString(16).padStart(16, "0") });
  const p1 = await call("/access/read", {});
  assert.deepEqual([p1.rows.length, p1.more, p1.keepDays, p1.rows[0].target], [100, true, 366, (119).toString(16).padStart(16, "0")]);
  const p2 = await call("/access/read", { before: Math.min(...p1.rows.map(r => r.seq)) });
  assert.equal(p2.rows.length, s.one("SELECT COUNT(*) AS n FROM admin_access").n - 100);
  assert.equal(p2.more, false);
  // kept 366 days: older rows are not shown, and go with the next row or tick
  clock += 365 * DAY;
  assert.ok((await call("/access/read", {})).rows.length === 100);
  clock += 2 * DAY;
  assert.equal((await call("/access/read", {})).rows.length, 0, "older than a year and a day: not shown");
  await gate({ route: "GET /keys", action: "키 목록", target: "all" });
  assert.equal(s.one("SELECT COUNT(*) AS n FROM admin_access").n, 2, "and deleted: only the new login and view are left");
  assert.deepEqual(await call("/access", { ip: "nope" }), { error: "bad access" });

  // the audit record: 180 days
  assert.deepEqual(await call("/audit", { who: "admin", action: "키 발급", detail: "0123456789abcdef · 코드 없음" }), { ok: true });
  assert.deepEqual(await call("/audit", { who: "admin" }), { error: "bad audit" });
  assert.deepEqual((await call("/audit/read", {})).log.map(l => [l.who, l.action, l.detail]), [["admin", "키 발급", "0123456789abcdef · 코드 없음"]]);
  clock += 181 * DAY;
  assert.deepEqual((await call("/audit/read", {})).log, [], "older than 180 days: not shown");
  await s.tick();
  assert.equal(s.one("SELECT COUNT(*) AS n FROM audit_log").n, 0, "and the tick deletes it");
}
console.log("ok");
