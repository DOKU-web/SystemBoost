// The day's share of KV writes for requests anyone can make (Workers free plan: 1,000 KV writes a day for the worker).
//   node test_kv_budget.mjs
// New orders, "I paid" notes and referral / invite gifts share PUBLIC_KV_DAY (400) writes a day, counted in the Stats
// object; REF_DAILY (60) gifts a day. The seller's approvals are never counted or refused. While the object is down
// the old KV caps hold.
import assert from "node:assert/strict";
import { generateKeyPairSync } from "node:crypto";
import worker from "./src/index.js";
import { fakeStats } from "./fake_stats.mjs";

const kv = new Map(), md = new Map();
let puts = 0;
const st = fakeStats();
const { privateKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
const env = {
  LICENSES: {
    get: async k => kv.get(k) ?? null,
    getWithMetadata: async k => ({ value: kv.get(k) ?? null, metadata: md.get(k) ?? null }),
    put: async (k, v, o) => { puts++; kv.set(k, v); md.set(k, o?.metadata ?? null); },
    delete: async k => { kv.delete(k); md.delete(k); },
    list: async ({ prefix, limit = 1000 }) => ({ keys: [...kv.keys()].filter(k => k.startsWith(prefix)).sort().slice(0, limit).map(name => ({ name, metadata: md.get(name) })), list_complete: true }),
  },
  LICENSE_PRIVATE_KEY: privateKey.export({ type: "pkcs8", format: "pem" }),
  ADMIN_TOKEN: "T".repeat(32),
  STATS: st.binding,
};
kv.set("config:pay_urls", JSON.stringify({ yearly: "https://pay.example/yearly", monthly: "https://pay.example/monthly" }));

let ipN = 0;   // a new address per call: the per-address hour limits are not what this file tests
const call = (path, init = {}) => worker.fetch(new Request("https://lic.example" + path, { ...init, headers: { "CF-Connecting-IP": "10.9." + (ipN >> 8 & 255) + "." + (++ipN & 255), ...(init.headers || {}) } }), env);
const j = async (path, init) => { const r = await call(path, init); return { http: r.status, ...(await r.json()) }; };
const adm = (path, body) => j("/admin/api" + path, { method: "POST", headers: { Authorization: "Bearer " + env.ADMIN_TOKEN, "Content-Type": "application/json" }, body: JSON.stringify(body) });
const day = () => new Date().toISOString().slice(0, 10);
const n = name => Number(st.obj.peek({ names: [name] }).n[name]) || 0;
const PUB = () => "kv:pub:" + day(), REF = () => "kv:ref:" + day();
const order = () => j("/order?plan=yearly&method=manual", { method: "POST" });
const paid = (code, payer = "홍길동", msg = "") => j("/order/paid", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ code, payer, msg }) });
const device = i => String(i).padStart(32, "0");
const gift = (code, i) => j("/referral", { method: "POST", body: JSON.stringify({ code, device: device(i) }) });

// 1. counted in the object, no KV counter: an order is 1, an "I paid" note 1, a gift 3 (and 1 gift)
const o1 = await order();
assert.equal(o1.http, 200, JSON.stringify(o1));
assert.equal(n(PUB()), 1);
assert.equal(kv.has("count:orders:" + day()), false, "no KV write for counting orders");
assert.equal((await paid(o1.code)).status, "paid");
assert.equal(n(PUB()), 2);
assert.equal((await paid(o1.code)).status, "paid", "the same note again");
assert.equal(n(PUB()), 2, "the same note again writes nothing and counts nothing");
assert.equal((await adm("/referrals", { code: "FREEPLAN", days: 3, max: 1000 })).code, "FREEPLAN");
const g1 = await gift("FREEPLAN", 1);
assert.ok(g1.key, JSON.stringify(g1));
assert.deepEqual([n(PUB()), n(REF())], [5, 1]);
assert.equal(kv.has("count:ref:" + day()), false, "no KV write for counting gifts");

// 2. REF_DAILY: 60 gifts a day
st.obj.take({ items: [{ name: REF(), ttl: 172800, by: 59 }] });
assert.deepEqual([(await gift("FREEPLAN", 2)).http, (await gift("FREEPLAN", 2)).error], [429, "busy"]);
assert.equal(kv.has("refdev:" + device(2)), false, "a refused gift marks no device");
// a KV count of the day (from before the update, or a day the object was down) still counts
st.db.exec("DELETE FROM counters");
kv.set("count:ref:" + day(), "60");
assert.equal((await gift("FREEPLAN", 3)).error, "busy");
kv.delete("count:ref:" + day());
assert.ok((await gift("FREEPLAN", 3)).key);

// 3. the day's share used up: orders, notes and gifts are refused before any KV write; the seller is not
const o2 = await order();
st.obj.take({ items: [{ name: PUB(), ttl: 172800, by: 400 - n(PUB()) }] });
assert.equal(n(PUB()), 400);
const before = puts;
assert.deepEqual([(await order()).http, (await order()).error], [429, "busy"]);
assert.deepEqual([(await paid(o2.code)).http, (await paid(o2.code)).error], [429, "busy"]);
assert.equal(md.get("ord:" + o2.code)?.status, "requested", "the refused note left the order as it was");
assert.deepEqual([(await gift("FREEPLAN", 4)).http, (await gift("FREEPLAN", 4)).error], [429, "busy"]);
assert.equal(puts, before, "nothing was written while the share was used up");
const iss = await adm("/issue", { code: o1.code, plan: "yearly" });
assert.ok(iss.key, "the seller's approval is never refused: " + JSON.stringify(iss));
const man = await adm("/issue", { plan: "monthly", note: "선물" });
assert.ok(man.key, "a key made by hand is never refused");
assert.equal(n(PUB()), 400, "the seller's writes are not counted");
// a new day (UTC) starts from 0
st.db.exec("DELETE FROM counters");
assert.equal((await order()).http, 200);

// 4. the object cannot be reached: the old KV caps (300 orders, REF_DAILY gifts), as before
st.down = true;
assert.equal((await order()).http, 200);
assert.equal(kv.get("count:orders:" + day()), "1");
kv.set("count:orders:" + day(), "300");
assert.deepEqual([(await order()).http, (await order()).error], [429, "busy"]);
assert.ok((await gift("FREEPLAN", 5)).key, "a gift while the object is down");
assert.equal(kv.get("count:ref:" + day()), "1", "counted in KV while the object is down");
kv.set("count:ref:" + day(), "60");
assert.equal((await gift("FREEPLAN", 6)).error, "busy");
const o3code = [...kv.keys()].filter(k => k.startsWith("ord:")).map(k => k.slice(4)).find(c => md.get("ord:" + c)?.status === "requested");
assert.equal((await paid(o3code)).status, "paid", "an \"I paid\" note while the object is down (PAID_EDITS still bounds it)");
st.down = false;

console.log("kv budget tests passed");
