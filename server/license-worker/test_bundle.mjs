// Local test of the Pro plan and bundle keys (5 or 10 PCs): fake KV, the worker's real routes, and nothing
// that leaves this machine (global fetch only answers as a fake Lemon Squeezy).
//   node test_bundle.mjs
import assert from "node:assert/strict";
import { createHmac, generateKeyPairSync, verify } from "node:crypto";
import worker from "./src/index.js";
import { cleanPing } from "./src/stats.js";

const kv = new Map(), md = new Map();
const { privateKey, publicKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
const SECRET = "test-signing-secret";
const env = {
  LICENSES: {
    get: async k => kv.get(k) ?? null,
    getWithMetadata: async k => ({ value: kv.get(k) ?? null, metadata: md.get(k) ?? null }),
    put: async (k, v, o) => { kv.set(k, v); md.set(k, o?.metadata ?? null); },
    delete: async k => { kv.delete(k); md.delete(k); },
    list: async ({ prefix, limit = 1000 }) => ({ keys: [...kv.keys()].filter(k => k.startsWith(prefix)).sort().slice(0, limit).map(name => ({ name, metadata: md.get(name) })), list_complete: true }),
  },
  LICENSE_PRIVATE_KEY: privateKey.export({ type: "pkcs8", format: "pem" }),
  ADMIN_TOKEN: "T".repeat(32),
  PUBLIC_URL: "https://lic.example",
  SALES_MODE: "off",
  BUNDLE_FROM: "2099-01-01T00:00:00Z",     // bundles not open yet
  CARD_LIVE_FROM: "2000-01-01T00:00:00Z",
};
globalThis.fetch = async url => {
  assert.equal(String(url), "https://api.lemonsqueezy.com/v1/checkouts", "only the fake Lemon Squeezy is called");
  return new Response(JSON.stringify({ data: { attributes: { url: "https://shop.example/checkout/pro" } } }), { status: 201 });
};

const call = (path, init) => worker.fetch(new Request("https://lic.example" + path, init), env);
const j = async (path, init) => { const r = await call(path, init); return { http: r.status, ...(await r.json()) }; };
const adm = (path, body) => j("/admin/api" + path, { method: body ? "POST" : "GET", headers: { Authorization: "Bearer " + env.ADMIN_TOKEN, "Content-Type": "application/json" }, body: body ? JSON.stringify(body) : undefined });
const payload = key => JSON.parse(Buffer.from(key.split(".")[0], "base64url"));
const signed = key => { const [d, s] = key.split("."); return verify("sha256", Buffer.from(d, "base64url"), { key: publicKey, dsaEncoding: "ieee-p1363" }, Buffer.from(s, "base64url")); };
const days = iso => (Date.parse(iso) - Date.now()) / 864e5;
const order = (plan, extra = "") => j(`/order?plan=${plan}&method=manual${extra}`, { method: "POST" });
const hook = async (event, data, custom = {}) => {
  const raw = JSON.stringify({ meta: { event_name: event, test_mode: false, custom_data: custom }, data });
  const r = await call("/webhook/lemonsqueezy", { method: "POST", body: raw, headers: { "X-Signature": createHmac("sha256", SECRET).update(raw).digest("hex") } });
  return { http: r.status, ...(await r.json()) };
};

await adm("/sales-mode", { mode: "live" });

// payment links: bundles take a link but no discount-ticket link
await adm("/pay-links", { pro_yearly: "https://pay.example/py", yearly_x5: "https://pay.example/y5", pro_yearly_x10: "https://pay.example/p10",
                          yearly_x5_ticket: "https://pay.example/nope" });
assert.deepEqual(Object.keys((await adm("/pay-links")).pay), ["pro_yearly", "yearly_x5", "pro_yearly_x10"], "no ticket link for a bundle");
assert.match((await adm("/pay-links", { ultra_yearly_x5: "http://x" })).error, /울트라 연간 5대 묶음/);

// before the licence agreement allows bundles, only the single Pro key is offered
assert.deepEqual((await j("/status")).methods.manual, ["pro_yearly"]);
assert.equal((await order("yearly_x5")).http, 503, "bundles wait for 2026-12-09 KST");
env.BUNDLE_FROM = "2000-01-01T00:00:00Z";
assert.deepEqual((await j("/status")).methods.manual, ["pro_yearly", "yearly_x5", "pro_yearly_x10"]);
assert.equal((await order("yearly_x5", "&ticket=1")).http, 409, "a bundle takes no discount ticket");
assert.equal((await j("/order?plan=pro_yearly_x10&method=card", { method: "POST" })).http, 503, "bundles are never card checkouts");
assert.equal((await order("pro_yearly_x7")).http, 400);

// a Pro bundle for 10 PCs: ten separate Pro keys for a year
const b10 = await order("pro_yearly_x10");
assert.equal(b10.url, "https://pay.example/p10");
await j("/order/paid?code=" + b10.code, { method: "POST", body: JSON.stringify({ payer: "주식회사 예시", msg: "10대" }) });
const a10 = await adm("/orders/approve", { code: b10.code });
assert.equal(a10.keys.length, 10);
assert.equal(new Set(a10.keys).size, 10, "every PC gets its own key");
assert.equal(new Set(a10.ids).size, 10);
for (const k of a10.keys) {
  assert.ok(signed(k));
  const p = payload(k);
  assert.equal(p.plan, "pro");
  assert.ok(days(p.expires) > 368 && days(p.expires) < 370);
}
assert.equal(a10.key, a10.keys[0]);
let c = await j("/claim?session=" + b10.code);
assert.equal(c.key, a10.keys[0], "old apps still get one key");
assert.deepEqual(c.keys, a10.keys, "new apps get all ten");
const o10 = (await adm("/orders?status=approved")).orders.find(o => o.code === b10.code);
assert.equal(o10.seats, 10);
assert.equal(o10.plan, "pro_yearly_x10");
const notes = (await adm("/keys?status=all")).keys.filter(k => k.note.includes(b10.code)).map(k => k.note);
assert.equal(notes.length, 10);
assert.ok(notes.some(n => n.includes("10대 묶음 1/10")) && notes.some(n => n.includes("10대 묶음 10/10")));
assert.ok((await adm("/log")).log.some(l => l.action === "키 발급" && l.detail.includes("10대 묶음") && l.detail.includes("프로")));

// one shared key is turned off on its own; the other nine keep working
await adm("/revoke", { id: a10.ids[3], reason: "공유" });
assert.equal((await j("/check?id=" + a10.ids[3])).revoked, true);
for (const id of a10.ids.filter((_, i) => i !== 3)) assert.notEqual((await j("/check?id=" + id)).revoked, true);

// a premium bundle for 5 PCs
const b5 = await order("yearly_x5");
const a5 = await adm("/orders/approve", { code: b5.code });
assert.deepEqual(a5.keys.map(k => payload(k).plan), Array(5).fill("premium"));
assert.equal((await j("/claim?session=" + b5.code)).keys.length, 5);

// one Pro key: no list
const p1 = await order("pro_yearly");
const ap1 = await adm("/orders/approve", { code: p1.code });
assert.equal(payload(ap1.key).plan, "pro");
assert.equal(ap1.keys.length, 1);
c = await j("/claim?session=" + p1.code);
assert.equal(c.key, ap1.key);
assert.equal(c.keys, undefined);

// issuing by hand: tier, period and number of keys from the form
let r = await adm("/issue", { tier: "pro", plan: "monthly", seats: "5", note: "회사 업무용 5대" });
assert.equal(r.keys.length, 5);
assert.ok(r.keys.every(k => payload(k).plan === "pro" && days(payload(k).expires) > 33 && days(payload(k).expires) < 35));
r = await adm("/issue", { tier: "ultra", plan: "yearly", note: "리뷰" });
assert.deepEqual([r.keys.length, payload(r.key).plan], [1, "ultra"]);
r = await adm("/issue", { tier: "gold", plan: "yearly", note: "x" });
assert.equal(payload(r.key).plan, "premium", "an unknown tier is premium");
assert.match((await adm("/issue", { tier: "pro", plan: "yearly", seats: 21, note: "x" })).error, /1~20/);
assert.match((await adm("/issue", { tier: "pro", plan: "yearly", seats: 0, note: "x" })).error, /1~20/);
const m3 = await order("pro_yearly");
r = await adm("/issue", { code: m3.code, tier: "pro", plan: "yearly", seats: 3 });
assert.equal((await j("/claim?session=" + m3.code)).keys.length, 3, "a hand-issued bundle reaches the buyer's app too");

// card: single Pro plans can be card subscriptions, bundles cannot
let s = await adm("/card", { store: "42", variants: { pro_monthly: "301", pro_yearly: "302", yearly_x5: "999" } });
assert.equal(s.variants.yearly_x5, undefined, "no card product for a bundle");
assert.equal(s.variants.pro_yearly, "302");
env.LS_API_KEY = "lsk_fake_api_key"; env.LS_SIGNING_SECRET = SECRET;
assert.deepEqual((await j("/status")).methods.card, ["pro_monthly", "pro_yearly"]);
const co = await j("/order?plan=pro_yearly&method=card", { method: "POST" });
assert.equal(co.method, "card");
const subData = { type: "subscriptions", id: "55", attributes: { status: "active", variant_id: 302, order_id: 9055, user_email: "pro@example.com", renews_at: "2027-11-09T10:00:00.000000Z" } };
assert.equal((await hook("subscription_created", subData, { session: co.code })).http, 200);
c = await j("/claim?session=" + co.code);
assert.equal(payload(c.key).plan, "pro", "a Pro card product gives a Pro key");
assert.ok(days(payload(c.key).expires) > 360);

// referral codes and usage statistics know Pro
assert.equal((await adm("/referrals", { code: "PROTEST", plan: "pro", days: 3, max: 5 })).ref.plan, "pro");
assert.equal((await adm("/referrals", { code: "GOLDTEST", plan: "gold", days: 3, max: 5 })).ref.plan, "premium");
assert.equal(cleanPing({ id: "a".repeat(32), ver: "0.8.10", os: "win", plan: "pro", fg: 2, boost: 1 }, "KR")?.plan, "pro");

console.log("pro plan and bundle tests passed");
