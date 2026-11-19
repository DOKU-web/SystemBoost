// Pre-registration (src/preorder.js, table preorders in src/stats.js): the site's popup and the dashboard's 사전예약 tab.
// Fake KV, the real Stats class on node:sqlite; global fetch refuses everything: nothing leaves this machine.
//   node test_preorder.mjs
import assert from "node:assert/strict";
import { generateKeyPairSync } from "node:crypto";
import worker from "./src/index.js";
import { fakeStats } from "./fake_stats.mjs";
import { PRE, preEmail } from "./src/preorder.js";
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

let ip = 0;
const call = (path, init = {}, addr) => worker.fetch(new Request("https://lic.example" + path,
  { ...init, headers: { "CF-Connecting-IP": addr || "10.9.0." + (++ip & 255), ...(init.headers || {}) } }), env);
const j = async (path, init, addr) => { const r = await call(path, init, addr); return { http: r.status, cors: r.headers.get("access-control-allow-origin"), ...(await r.json()) }; };
const pre = (b, addr) => j("/preorder", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(b) }, addr);
const adm = (path, body, auth = TOKEN) => j("/admin/api" + path, { method: body ? "POST" : "GET", headers: { Authorization: "Bearer " + auth, "Content-Type": "application/json" }, body: body ? JSON.stringify(body) : undefined });
const rows = () => st.rows("SELECT * FROM preorders ORDER BY at");
const realNow = Date.now;
const at = (iso, f) => { Date.now = () => Date.parse(iso); try { return f(); } finally { Date.now = realNow; } };

// open before the deadline, for anyone (CORS), and the preflight answers
Date.now = () => Date.parse("2026-11-20T03:00:00Z");
let r = await j("/preorder");
assert.deepEqual([r.http, r.open, r.until, r.cors], [200, true, PRE.until, "*"]);
const opt = await call("/preorder", { method: "OPTIONS" });
assert.equal(opt.status, 204);
assert.match(opt.headers.get("access-control-allow-headers"), /content-type/);

// consent and email are checked; nothing is written for a refusal
assert.deepEqual([(await pre({ email: "a@b.com", plan: "ultra" })).error], ["consent"]);
assert.deepEqual([(await pre({ email: "not-mail", consent: true })).error], ["email"]);
assert.deepEqual([(await pre({ email: "a".repeat(250) + "@b.com", consent: true })).error], ["email"]);
assert.equal(rows().length, 0);

// a sign-up; the same address in other case is "already" and writes nothing; no KV write at all
r = await pre({ email: "  Fan@Example.com ", plan: "ultra", lang: "ko", consent: true });
assert.deepEqual([r.http, r.ok, r.already, r.cors], [200, true, undefined, "*"]);
r = await pre({ email: "fan@example.COM", plan: "free", lang: "en", consent: true });
assert.deepEqual([r.ok, r.already], [true, true]);
assert.equal(rows().length, 1);
assert.deepEqual([rows()[0].email, rows()[0].plan, rows()[0].lang], ["fan@example.com", "ultra", "ko"]);
assert.equal(kvWrites, 0, "pre-registration writes no KV");
// unknown plan and language fall back; nothing else is stored (no address, no user agent)
await pre({ email: "two@example.com", plan: "lifetime", lang: "xx", consent: true });
assert.deepEqual(Object.keys(rows()[1]).sort(), ["at", "email", "id", "lang", "plan"]);
assert.deepEqual([rows()[1].plan, rows()[1].lang], ["undecided", "en"]);
assert.equal(preEmail("x@y.z"), null);

// per address: 5 an hour
_forgetIpLimits();
for (let i = 0; i < 5; i++) assert.equal((await pre({ email: `ip${i}@example.com`, consent: true }, "10.8.8.8")).ok, true);
r = await pre({ email: "ip6@example.com", consent: true }, "10.8.8.8");
assert.deepEqual([r.http, r.error], [429, "busy"]);

// per day cap (Stats /pre/add max): full -> busy
const cap = await st.obj.fetch(new Request("https://stats/pre/add", { method: "POST", body: JSON.stringify({ id: "f".repeat(32), email: "cap@example.com", plan: "free", lang: "en", max: 1 }) }));
assert.equal((await cap.json()).error, "full");

// the object down: busy, never a 500
st.down = true;
r = await pre({ email: "down@example.com", consent: true });
assert.deepEqual([r.http, r.error], [503, "busy"]);
st.down = false;

// admin: unauthorised is refused; the list has counts and no email anywhere
assert.equal((await adm("/preorders", undefined, "wrong-token-xx")).http, 401);
const list = await adm("/preorders");
assert.equal(list.http, 200);
assert.deepEqual([list.count, list.fresh, list.open, list.shown], [7, 7, true, true]);
assert.equal(list.byPlan.ultra, 1);
assert.equal(list.byLang.ko, 1);
assert.ok(!JSON.stringify(list).includes("@"), "no email in the list");
assert.equal(list.recent.length, 7);
// 확인 clears the new count; a later sign-up is new again
assert.equal((await adm("/preorders/seen", {})).ok, true);
assert.equal((await adm("/preorders")).fresh, 0);
Date.now = () => Date.parse("2026-11-20T04:00:00Z");
_forgetIpLimits();
await pre({ email: "late@example.com", plan: "biz", consent: true });
assert.equal((await adm("/preorders")).fresh, 1);

// export: every email as CSV, logged; formula-looking cells are quoted
await pre({ email: "=cmd@example.com", consent: true });
const ex = await adm("/preorders/export", {});
assert.equal(ex.count, 9);
assert.match(ex.csv, /^email,plan,lang,at\r\n/);
assert.match(ex.csv, /\r\nfan@example\.com,ultra,ko,2026-11-20T03:00:00\.000Z\r\n/);
assert.match(ex.csv, /"=cmd@example\.com"/);
const log = st.rows("SELECT action, detail FROM audit_log");
assert.ok(log.some(x => x.action === "사전예약 내보내기" && x.detail === "9건"));
assert.ok(!log.some(x => x.detail.includes("@")), "the log names no address");
const acc = st.rows("SELECT action FROM admin_access");
assert.ok(acc.some(x => x.action === "사전예약 메일 내보내기"), "the export is in the admin access log");

// delete by email (any case)
assert.deepEqual([(await adm("/preorders/delete", { email: "LATE@example.com" })).deleted], [true]);
assert.deepEqual([(await adm("/preorders/delete", { email: "late@example.com" })).deleted], [false]);
assert.equal((await adm("/preorders/delete", { email: "nope" })).http, 400);
assert.equal(rows().length, 8);

// closed after the deadline (and by PREORDER=off); the tab still shows
Date.now = () => Date.parse("2026-11-27T15:00:00Z");
assert.equal((await j("/preorder")).open, false);
r = await pre({ email: "after@example.com", consent: true });
assert.deepEqual([r.http, r.error], [403, "closed"]);
const late = await adm("/preorders");
assert.deepEqual([late.open, late.shown, late.count], [false, true, 8]);
Date.now = () => Date.parse("2026-11-21T00:00:00Z");
env.PREORDER = "off";
assert.equal((await j("/preorder")).open, false);
delete env.PREORDER;
env.PREORDER_UNTIL = "2026-11-20T00:00:00Z";
assert.equal((await j("/preorder")).open, false);
delete env.PREORDER_UNTIL;

// on 2027-02-28 KST everything is deleted: by the hourly tick and by any call; the tab goes away
at("2027-02-27T14:59:00Z", () => st.obj.prePurge());
assert.equal(rows().length, 8, "kept until the day");
at("2027-02-27T15:00:00Z", () => st.obj.prePurge());   // what the hourly tick runs
assert.equal(rows().length, 0);
Date.now = () => Date.parse("2027-02-27T16:00:00Z");
const gone = await adm("/preorders");
assert.deepEqual([gone.count, gone.shown, gone.open], [0, false, false]);
Date.now = realNow;

console.log("test_preorder: ok");
