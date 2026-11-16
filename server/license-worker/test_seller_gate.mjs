// 0.8.14: nothing new is sold in live mode while the seller's details are incomplete (전자상거래법 seller disclosure).
// /status offers no plan, /order, /checkout and a card plan change answer 503 {"error":"seller info"}; test mode is as
// before; key checks, renewals, webhooks, an "I paid" note on an existing order, support and the seller's own approval
// never look at it. The dashboard's GET /summary names the empty fields.
// Fake KV, fake Stats object, fake Lemon Squeezy, signed webhooks: nothing leaves this machine.
//   node test_seller_gate.mjs
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
  SALES_MODE: "off",
  CARD_LIVE_FROM: "2000-01-01T00:00:00Z",
  LS_API_KEY: "lsk_made_up",
  LS_SIGNING_SECRET: SECRET,
  STATS: st.binding,
};

// fake Lemon Squeezy: a checkout and a subscription PATCH (plan change)
const lsCalls = [];
globalThis.fetch = async (url, init = {}) => {
  url = String(url);
  lsCalls.push((init.method || "GET") + " " + url);
  if (url === "https://api.lemonsqueezy.com/v1/checkouts") return new Response(JSON.stringify({ data: { attributes: { url: "https://shop.example/checkout/x" } } }), { status: 201 });
  if (/^https:\/\/api\.lemonsqueezy\.com\/v1\/subscriptions\/\d+$/.test(url)) return new Response(JSON.stringify({ data: { id: "1" } }), { status: 200 });
  assert.fail("only the fake Lemon Squeezy is called: " + url);
};

let ipN = 0;   // a new address per call: the per-address limits are not what this file tests
const call = (path, init = {}) => worker.fetch(new Request("https://lic.example" + path, { ...init, headers: { "CF-Connecting-IP": "10.7." + (ipN >> 8 & 255) + "." + (++ipN & 255), ...(init.headers || {}) } }), env);
const j = async (path, init) => { const r = await call(path, init); return { http: r.status, ...(await r.json()) }; };
const postJ = (path, b) => j(path, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(b) });
const adm = (path, body) => j("/admin/api" + path, { method: body ? "POST" : "GET", headers: { Authorization: "Bearer " + TOKEN, "Content-Type": "application/json" }, body: body ? JSON.stringify(body) : undefined });
const kid = key => createHash("sha256").update(key.trim()).digest("hex").slice(0, 16);
const V = { monthly: "101", yearly: "102", ultra_monthly: "201", ultra_yearly: "202", biz_monthly: "301", biz_yearly: "302" };
const sub = (id, variant) => ({ type: "subscriptions", id: String(id), attributes: { status: "active", variant_id: Number(variant), order_id: 9000 + id, user_email: `p${id}@example.com`, renews_at: "2026-12-30T10:00:00.000000Z" } });
const hook = async (event, data) => {
  const raw = JSON.stringify({ meta: { event_name: event, test_mode: false, custom_data: {} }, data });
  const r = await call("/webhook/lemonsqueezy", { method: "POST", body: raw, headers: { "X-Signature": createHmac("sha256", SECRET).update(raw).digest("hex") } });
  return { http: r.status, ...(await r.json()) };
};
const FULL = { name: "Test Seller", rep: "Test Rep", address: "Test Address 1", bizNo: "000-00-00000", mailOrderNo: "2026-Test-0000", email: "seller@example.com", phone: "000-0000-0000", hours: "" };
const setSeller = seller => adm("/support/config", { seller, refundDays: 7, faq: [] });
const manual = plan => j(`/order?plan=${plan}&method=manual`, { method: "POST" });
const card = plan => j(`/order?plan=${plan}&method=card`, { method: "POST" });
const status = () => j("/status");
const summary = () => adm("/summary");

await adm("/card", { store: "42", variants: V });
await adm("/pay-links", { monthly: "https://pay.example/m", yearly: "https://pay.example/y" });

// ------------------------------------------------------------ 1. live, complete: sells as before
assert.equal((await setSeller(FULL)).http, 200);
await adm("/sales-mode", { mode: "live" });
let s = await status();
assert.equal(s.sales, "live");
assert.ok(s.methods.manual.includes("yearly") && s.methods.card.includes("monthly"), JSON.stringify(s));
const before = await manual("yearly");
assert.equal(before.http, 200, JSON.stringify(before));
const created = await hook("subscription_created", sub(1, V.monthly));
assert.equal(created.http, 200, JSON.stringify(created));
const subKey = kv.get("sub:1");
assert.ok(subKey, "the card subscription has a key");
let sm = await summary();
assert.deepEqual([sm.sellerMissing, sm.sellerBlocked], [[], false]);

// ------------------------------------------------------------ 2. live, one required field empty: nothing new is sold
assert.equal((await setSeller({ ...FULL, mailOrderNo: "   " })).http, 200, "blank is empty");
s = await status();
assert.equal(s.sales, "live");
assert.deepEqual(s.methods, { card: [], manual: [] }, "no plan offered");
for (const r of [await manual("yearly"), await card("monthly")]) assert.deepEqual([r.http, r.error], [503, "seller info"]);
const nOrders = [...kv.keys()].filter(k => k.startsWith("ord:")).length;
const co = await j("/checkout?plan=monthly&session=abcdefgh-12345678", { method: "POST" });
assert.deepEqual([co.http, co.error], [503, "seller info"]);
const ch = await postJ("/subscription/change", { key: subKey, plan: "yearly" });
assert.deepEqual([ch.http, ch.error], [503, "seller info"], "a plan change is a new payment");
assert.equal([...kv.keys()].filter(k => k.startsWith("ord:")).length, nOrders, "no order stored");
assert.equal(lsCalls.filter(c => c.startsWith("PATCH") || c.includes("/checkouts")).length, 0, "Lemon Squeezy was not asked");
sm = await summary();
assert.deepEqual([sm.sellerMissing, sm.sellerBlocked], [["mailOrderNo"], true], "the dashboard names the empty field");

// ...while everything that is not a new sale goes on
assert.equal((await j("/check?id=" + kid(subKey))).revoked, false, "key check");
const rn = await postJ("/renew", { sub: "1", key: subKey });
assert.equal(rn.http, 200, "renewal: " + JSON.stringify(rn));
const pay = await hook("subscription_payment_success", { type: "subscription-invoices", id: "7001", attributes: { subscription_id: 1, billing_reason: "renewal", status: "paid", user_email: "p1@example.com" } });
assert.equal(pay.http, 200, "Lemon Squeezy webhook: " + JSON.stringify(pay));
const created2 = await hook("subscription_created", sub(2, V.yearly));
assert.equal(created2.http, 200, "a payment Lemon Squeezy already took is still honoured");
assert.ok(kv.get("sub:2"));
const paid = await postJ("/order/paid", { code: before.code, payer: "홍길동", msg: "" });
assert.equal(paid.status, "paid", "an \"I paid\" note on an existing order: " + JSON.stringify(paid));
const ap = await adm("/orders/approve", { code: before.code, email: "buyer@example.com" });
assert.equal(ap.http, 200, "the seller's approval: " + JSON.stringify(ap));
assert.ok(ap.key);
const tk = await postJ("/support/ticket", { cat: "pay", subject: "결제 문의", text: "결제가 안 돼요", contact: "buyer@example.com", consent: true, lang: "ko" });
assert.equal(tk.http, 201, "support: " + JSON.stringify(tk));

// several empty fields, in SELLER_FIELDS order; the hours are optional
await setSeller({ ...FULL, name: "", phone: "", hours: "" });
assert.deepEqual((await summary()).sellerMissing, ["name", "phone"]);
assert.equal((await manual("monthly")).http, 503);
// a config never saved at all: every required field is missing
kv.delete("config:support");
assert.deepEqual((await summary()).sellerMissing, ["name", "rep", "address", "bizNo", "mailOrderNo", "email", "phone"]);
assert.deepEqual((await status()).methods, { card: [], manual: [] });

// ------------------------------------------------------------ 3. test mode: unchanged, though the details are empty
await adm("/sales-mode", { mode: "test" });
s = await status();
assert.equal(s.sales, "test");
assert.ok(s.methods.manual.includes("yearly"), JSON.stringify(s));
const tOrder = await manual("yearly");
assert.equal(tOrder.http, 200, JSON.stringify(tOrder));
assert.equal((await card("monthly")).http, 200);
sm = await summary();
assert.equal(sm.sellerBlocked, false, "test mode is not blocked");
assert.equal(sm.sellerMissing.length, 7, "the dashboard still says what is missing");

// ------------------------------------------------------------ 4. live again with every field: sales are open again
await setSeller(FULL);
await adm("/sales-mode", { mode: "live" });
assert.ok((await status()).methods.manual.length > 0);
assert.equal((await manual("monthly")).http, 200);
assert.equal((await j("/checkout?plan=monthly&session=abcdefgh-12345678", { method: "POST" })).http, 200);

console.log("test_seller_gate: all passed");
