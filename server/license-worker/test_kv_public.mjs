// 0.8.14: the day's public share of KV writes (index.js publicKvDay: default 400, env PUBLIC_KV_DAY moves it within
// 50..100000). With the share used up, requests anyone can make are refused (orders, "I paid"), while key
// checks, renewals, signed Lemon Squeezy webhooks and the seller's approval still go through.
// Fake KV, fake Stats object, signed webhooks; global fetch refuses everything: nothing leaves this machine.
//   node test_kv_public.mjs
import assert from "node:assert/strict";
import { createHash, createHmac, generateKeyPairSync } from "node:crypto";
import worker from "./src/index.js";
import { fakeStats } from "./fake_stats.mjs";

const kv = new Map(), md = new Map();
const st = fakeStats();
const { privateKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
const SECRET = "made-up-signing-secret";
const TOKEN = "T".repeat(32);
const env = {
  LICENSES: {
    get: async k => kv.get(k) ?? null,
    getWithMetadata: async k => ({ value: kv.get(k) ?? null, metadata: md.get(k) ?? null }),
    put: async (k, v, o) => { kv.set(k, v); md.set(k, o?.metadata ?? null); },
    delete: async k => { kv.delete(k); md.delete(k); },
    list: async ({ prefix, limit = 1000 }) => ({ keys: [...kv.keys()].filter(k => k.startsWith(prefix)).sort().slice(0, limit).map(name => ({ name, metadata: md.get(name) })), list_complete: true }),
  },
  LICENSE_PRIVATE_KEY: privateKey.export({ type: "pkcs8", format: "pem" }),
  ADMIN_TOKEN: TOKEN,
  PUBLIC_URL: "https://lic.example",
  SALES_MODE: "test",
  LS_SIGNING_SECRET: SECRET,
  STATS: st.binding,
};
globalThis.fetch = async url => assert.fail("no network in this test: " + url);
kv.set("config:pay_urls", JSON.stringify({ yearly: "https://pay.example/yearly" }));
kv.set("config:ls", JSON.stringify({ store: "42", variants: { monthly: "101", yearly: "102" } }));

let ipN = 0;
const call = (path, init = {}) => worker.fetch(new Request("https://lic.example" + path, { ...init, headers: { "CF-Connecting-IP": "10.2." + (ipN >> 8 & 255) + "." + (++ipN & 255), ...(init.headers || {}) } }), env);
const j = async (path, init) => { const r = await call(path, init); return { http: r.status, ...(await r.json()) }; };
const postJ = (path, b) => j(path, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(b) });
const adm = (path, body) => j("/admin/api" + path, { method: body ? "POST" : "GET", headers: { Authorization: "Bearer " + TOKEN, "Content-Type": "application/json" }, body: body ? JSON.stringify(body) : undefined });
const day = () => new Date().toISOString().slice(0, 10);
const PUB = () => "kv:pub:" + day();
const n = name => Number(st.obj.peek({ names: [name] }).n[name]) || 0;
const fill = cap => st.obj.take({ items: [{ name: PUB(), ttl: 172800, by: cap - n(PUB()) }] });
const reset = () => st.db.exec("DELETE FROM counters WHERE name LIKE 'kv:%'");
const order = () => j("/order?plan=yearly&method=manual", { method: "POST" });
const kid = key => createHash("sha256").update(key).digest("hex").slice(0, 16);
const hook = async (event, data) => {
  const raw = JSON.stringify({ meta: { event_name: event, test_mode: true, custom_data: {} }, data });
  const r = await call("/webhook/lemonsqueezy", { method: "POST", body: raw, headers: { "X-Signature": createHmac("sha256", SECRET).update(raw).digest("hex") } });
  return { http: r.status, ...(await r.json()) };
};
const sub = id => ({ type: "subscriptions", id: String(id), attributes: { status: "active", variant_id: 101, order_id: 9000 + id, user_email: `p${id}@example.com`, renews_at: "2026-12-30T10:00:00.000000Z" } });

// before: an order to approve later, and a card subscription with a key
const before = await order();
assert.equal(before.http, 200);
assert.equal((await hook("subscription_created", sub(1))).http, 200);
const subKey = kv.get("sub:1");
assert.ok(subKey);

// 1. the default share is 400; used up, the public requests are refused before any write
assert.deepEqual((await adm("/summary")).budget.pub, [n(PUB()), 400]);
fill(400);
const ord = await order();
assert.deepEqual([ord.http, ord.error], [429, "busy"]);
const p = await postJ("/order/paid", { code: before.code, payer: "홍길동" });
assert.deepEqual([p.http, p.error], [429, "busy"]);

// ...the rest still goes through, uncounted
assert.equal((await j("/check?id=" + kid(subKey))).revoked, false, "key check");
const rn = await postJ("/renew", { sub: "1", key: subKey });
assert.equal(rn.http, 200, "renewal: " + JSON.stringify(rn));
const created = await hook("subscription_created", sub(2));
assert.equal(created.http, 200, "a signed webhook: " + JSON.stringify(created));
assert.ok(kv.get("sub:2"), "its key was stored");
const renewal = await hook("subscription_payment_success", { type: "subscription-invoices", id: "7001", attributes: { subscription_id: 1, billing_reason: "renewal", status: "paid", user_email: "p1@example.com" } });
assert.equal(renewal.http, 200, JSON.stringify(renewal));
const ap = await adm("/orders/approve", { code: before.code });
assert.equal(ap.http, 200, "the seller's approval: " + JSON.stringify(ap));
assert.ok(ap.key);
const hand = await adm("/issue", { plan: "monthly", note: "선물" });
assert.ok(hand.key, "a key made by hand");
assert.equal(n(PUB()), 400, "none of these were counted");

// 2. env PUBLIC_KV_DAY moves the share, everywhere it is used
env.PUBLIC_KV_DAY = "600";
assert.deepEqual((await adm("/summary")).budget.pub, [400, 600]);
assert.equal((await order()).http, 200, "room again under the larger share");
fill(600);
assert.equal((await order()).http, 429);
reset();
env.PUBLIC_KV_DAY = 100;   // a number works too
fill(99);
assert.equal((await order()).http, 200, "1 left: an order takes 1");
assert.equal((await order()).http, 429, "0 left");
reset();
// out of bounds or not a whole number: the default
for (const v of ["abc", "", "49", "100001", "12.5", "-5", " 600 x", 3.5, null, undefined, {}]) {
  env.PUBLIC_KV_DAY = v;
  assert.equal((await adm("/summary")).budget.pub[1], 400, JSON.stringify(v));
}
for (const v of ["50", " 800 ", 100000]) {
  env.PUBLIC_KV_DAY = v;
  assert.equal((await adm("/summary")).budget.pub[1], Number(v), JSON.stringify(v));
}
delete env.PUBLIC_KV_DAY;

// no 3-day trial (dropped before 0.8.14 shipped: the free plan is the way to try): POST /trial is no route, as
// before, and GET /status has no "trial" field
assert.deepEqual([(await postJ("/trial", { device: "0".repeat(64) })).http], [404]);
assert.deepEqual(Object.keys(await j("/status")).filter(k => k === "trial"), []);

console.log("test_kv_public: all passed");
