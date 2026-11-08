// Local test of card payments (Lemon Squeezy): fake KV, a fake Lemon Squeezy API, signed webhooks, and the
// worker's real routes. Global fetch is replaced first, so nothing here reaches Lemon Squeezy.
//   node test_card.mjs
import assert from "node:assert/strict";
import { createHmac, generateKeyPairSync, verify } from "node:crypto";
import worker from "./src/index.js";

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
  LS_STORE_ID: "REPLACE", LS_VARIANT_MONTHLY: "REPLACE", LS_VARIANT_YEARLY: "REPLACE",
};

// ---- fake Lemon Squeezy API
const sent = [];
let lsAnswer = () => new Response(JSON.stringify({ data: { attributes: { url: "https://shop.example/checkout/abc" } } }), { status: 201 });
globalThis.fetch = async (url, init = {}) => {
  url = String(url);
  assert.equal(url, "https://api.lemonsqueezy.com/v1/checkouts", "only the fake Lemon Squeezy is called");
  sent.push({ auth: init.headers.Authorization, body: JSON.parse(init.body) });
  return lsAnswer();
};

const call = (path, init) => worker.fetch(new Request("https://lic.example" + path, init), env);
const j = async (path, init) => { const r = await call(path, init); return { http: r.status, ...(await r.json()) }; };
const adm = (path, body) => j("/admin/api" + path, { method: body ? "POST" : "GET", headers: { Authorization: "Bearer " + env.ADMIN_TOKEN, "Content-Type": "application/json" }, body: body ? JSON.stringify(body) : undefined });
const hook = async (event, data, { test = false, custom = {}, secret = SECRET } = {}) => {
  const raw = JSON.stringify({ meta: { event_name: event, test_mode: test, custom_data: custom }, data });
  const r = await call("/webhook/lemonsqueezy", { method: "POST", body: raw, headers: { "X-Signature": createHmac("sha256", secret).update(raw).digest("hex") } });
  return { http: r.status, ...(await r.json()) };
};
const payload = key => JSON.parse(Buffer.from(key.split(".")[0], "base64url"));
const signed = key => { const [d, s] = key.split("."); return verify("sha256", Buffer.from(d, "base64url"), { key: publicKey, dsaEncoding: "ieee-p1363" }, Buffer.from(s, "base64url")); };
const days = iso => (Date.parse(iso) - Date.now()) / 864e5;
const order = async plan => j(`/order?plan=${plan}&method=card`, { method: "POST" });
const V = { monthly: "101", yearly: "102", ultra_monthly: "201", ultra_yearly: "202" };
const sub = (id, variant, extra = {}) => ({ type: "subscriptions", id: String(id), attributes: { status: "active", variant_id: Number(variant), order_id: 9000 + id, user_email: `b${id}@example.com`, renews_at: "2026-12-30T10:00:00.000000Z", ...extra } });
const invoice = (subId, reason, extra = {}) => ({ type: "subscription-invoices", id: String(7000 + subId), attributes: { subscription_id: subId, billing_reason: reason, status: "paid", user_email: `b${subId}@example.com`, ...extra } });

// nothing set up: no card checkout, whatever the mode
assert.deepEqual((await j("/status")).methods.card, []);
await adm("/sales-mode", { mode: "test" });
assert.deepEqual((await j("/status")).methods.card, [], "placeholders in wrangler.toml are not product numbers");
let s = await adm("/card");
assert.deepEqual([s.apiKey, s.signing, s.store, s.ready], [false, false, "", []]);
assert.equal(s.webhook, "https://lic.example/webhook/lemonsqueezy");
assert.ok(!JSON.stringify(s).includes("lsk_"), "secrets are only reported as set or not");

// the dashboard takes numbers only, one product per plan
assert.match((await adm("/card", { store: "abc" })).error, /숫자/);
assert.match((await adm("/card", { store: "1", variants: { monthly: "x1" } })).error, /월간.*숫자/);
assert.match((await adm("/card", { store: "1", variants: { monthly: "5", yearly: "5" } })).error, /같은 상품/);
s = await adm("/card", { store: " 42 ", variants: V });
assert.equal(s.store, "42");
assert.deepEqual(s.variants, V);
assert.deepEqual(s.ready, [], "no API key yet");
assert.ok((await adm("/log")).log.some(l => l.action === "카드 결제 설정"));

// with the owner's secrets in place, all four plans open
env.LS_API_KEY = "lsk_fake_api_key"; env.LS_SIGNING_SECRET = SECRET;
assert.deepEqual((await adm("/card")).ready, ["monthly", "yearly", "ultra_monthly", "ultra_yearly"]);
assert.deepEqual((await j("/status")).methods.card, ["monthly", "yearly", "ultra_monthly", "ultra_yearly"]);
assert.deepEqual((await adm("/summary")).autoReady, ["monthly", "yearly", "ultra_monthly", "ultra_yearly"]);

// a checkout per plan, with that plan's product and nothing a buyer could use to change the plan
for (const plan of Object.keys(V)) {
  const r = await order(plan);
  assert.equal(r.http, 200, plan);
  assert.equal(r.method, "card");
  assert.equal(r.url, "https://shop.example/checkout/abc");
  const b = sent.at(-1).body.data;
  assert.equal(sent.at(-1).auth, "Bearer lsk_fake_api_key");
  assert.equal(b.relationships.variant.data.id, V[plan]);
  assert.equal(b.relationships.store.data.id, "42");
  assert.deepEqual(b.attributes.checkout_data.custom, { session: r.code });
  assert.equal(b.attributes.product_options.redirect_url, "https://lic.example/thanks?session=" + r.code);
}
lsAnswer = () => new Response("{}", { status: 422 });
assert.equal((await order("monthly")).http, 502);
assert.ok((await adm("/log")).log.some(l => l.action === "결제창을 만들지 못함"));
lsAnswer = () => { throw new TypeError("network"); };
assert.equal((await order("monthly")).http, 502);
lsAnswer = () => new Response(JSON.stringify({ data: { attributes: { url: "https://shop.example/checkout/abc" } } }), { status: 201 });

// webhooks must be signed
assert.equal((await hook("subscription_created", sub(1, V.monthly), { secret: "wrong", test: true })).http, 401);
assert.equal((await call("/webhook/lemonsqueezy", { method: "POST", body: "{}" })).status, 401);
const badRaw = "not json";
assert.equal((await call("/webhook/lemonsqueezy", { method: "POST", body: badRaw, headers: { "X-Signature": createHmac("sha256", SECRET).update(badRaw).digest("hex") } })).status, 400);

// test mode: test purchases get a 1-day key, real ones are ignored
let o = await order("ultra_monthly");
assert.equal((await hook("subscription_created", sub(2, V.ultra_monthly), { custom: { session: o.code } })).ignored, "sales mode test");
assert.equal((await hook("subscription_created", sub(2, V.ultra_monthly), { test: true, custom: { session: o.code } })).http, 200);
let c = await j("/claim?session=" + o.code);
assert.equal(c.status, "ready");
let p = payload(c.key);
assert.ok(signed(c.key));
assert.equal(p.plan, "ultra", "an Ultra product gives an Ultra key");
assert.equal(p.test, true);
assert.ok(days(p.expires) < 1.01);

// live
await adm("/sales-mode", { mode: "live" });
assert.equal((await hook("subscription_created", sub(3, V.monthly), { test: true })).ignored, "sales mode live");

// premium monthly: key for the paid period, order approved, first payment gives no second key
o = await order("monthly");
assert.equal((await j("/claim?session=" + o.code)).status, "pending");
const before = [...kv.keys()].filter(k => k.startsWith("lic:")).length;
await hook("subscription_created", sub(10, V.monthly), { custom: { session: o.code } });
await hook("subscription_payment_success", invoice(10, "initial"), { custom: { session: o.code } });
await hook("subscription_updated", sub(10, V.monthly), { custom: { session: o.code } });
assert.equal([...kv.keys()].filter(k => k.startsWith("lic:")).length, before + 1, "one purchase, one key");
c = await j("/claim?session=" + o.code);
p = payload(c.key);
assert.equal(p.plan, "premium");
assert.equal(p.sub, "10");
assert.equal(p.expires.slice(0, 10), "2027-01-02", "renews_at + 3 days");
let ord = (await adm("/orders?status=all")).orders.find(x => x.code === o.code);
assert.equal(ord.status, "approved"); assert.equal(ord.method, "card"); assert.equal(ord.plan, "monthly");
assert.equal((await j("/renew?sub=10")).key, c.key);
assert.ok((await (await call("/thanks?session=" + o.code)).text()).includes("결제가 완료"));

// a buyer who adds plan=ultra_yearly to the checkout address still gets what the product is
o = await order("monthly");
await hook("subscription_created", sub(11, V.monthly), { custom: { session: o.code, plan: "ultra_yearly" } });
p = payload((await j("/claim?session=" + o.code)).key);
assert.equal(p.plan, "premium");
assert.ok(days(p.expires) < 60);

// Ultra yearly: renewals keep the plan and last a year
o = await order("ultra_yearly");
await hook("subscription_created", sub(12, V.ultra_yearly, { renews_at: "2027-11-08T00:00:00.000000Z" }), { custom: { session: o.code } });
let first = (await j("/claim?session=" + o.code)).key;
assert.equal(payload(first).plan, "ultra");
assert.equal(payload(first).expires.slice(0, 10), "2027-11-11");
await hook("subscription_payment_success", invoice(12, "renewal"), { custom: { session: o.code } });
const renewed = (await j("/renew?sub=12")).key;
assert.notEqual(renewed, first);
assert.equal(payload(renewed).plan, "ultra");
assert.ok(days(payload(renewed).expires) > 368 && days(payload(renewed).expires) < 370);
assert.equal((await j("/claim?session=" + o.code)).key, first, "a renewal does not touch the buyer's order");
assert.ok((await adm("/log")).log.some(l => l.action === "자동 갱신"));

// plan change in the customer portal: monthly -> Ultra monthly gives an Ultra key
await hook("subscription_updated", sub(10, V.ultra_monthly));
assert.equal(payload((await j("/renew?sub=10")).key).plan, "ultra");

// updated before created: no key until created arrives
await hook("subscription_updated", sub(13, V.monthly));
assert.equal((await j("/renew?sub=13")).status, "inactive");
await hook("subscription_created", sub(13, V.monthly));
assert.equal((await j("/renew?sub=13")).status, "ready");

// a product that is not one of ours: no key, and the log says why
o = await order("monthly");
await hook("subscription_created", sub(14, "999"), { custom: { session: o.code } });
assert.equal((await j("/claim?session=" + o.code)).status, "pending");
assert.ok((await adm("/log")).log.some(l => l.action.includes("어떤 플랜인지 몰라서")));
// a renewal for a subscription never seen
assert.equal((await hook("subscription_payment_success", invoice(15, "renewal"))).ignored, "unknown variant");
// statuses that are not paid for
assert.match((await hook("subscription_created", sub(16, V.monthly, { status: "unpaid" }))).ignored, /status/);

// cancelled: the key runs out, no renewals
await hook("subscription_updated", sub(12, V.ultra_yearly, { status: "cancelled" }));
assert.equal((await j("/renew?sub=12")).status, "inactive");

// refunds revoke, in every mode
await adm("/sales-mode", { mode: "off" });
o = await order("ultra_monthly");
assert.equal(o.http, 503, "sales off closes checkout");
await adm("/sales-mode", { mode: "live" });
o = await order("ultra_monthly");
await hook("subscription_created", sub(20, V.ultra_monthly), { custom: { session: o.code } });
const k20 = (await j("/claim?session=" + o.code)).key;
await adm("/sales-mode", { mode: "off" });
await hook("order_refunded", { type: "orders", id: "9020", attributes: {} });
const id20 = (await adm("/lookup", { key: k20 })).id;
assert.equal((await j("/check?id=" + id20)).revoked, true, "refund of the first payment");
await adm("/sales-mode", { mode: "live" });
await hook("subscription_created", sub(21, V.monthly));
await hook("subscription_payment_success", invoice(21, "renewal"));
const k21 = (await j("/renew?sub=21")).key;
await hook("subscription_payment_refunded", { type: "subscription-invoices", id: "1", attributes: { subscription_id: 21 } });
assert.equal((await j("/check?id=" + (await adm("/lookup", { key: k21 })).id)).revoked, true, "refund of a renewal");
assert.equal((await j("/renew?sub=21")).status, "inactive");

// the dashboard's key list shows what each key came from
const listed = (await adm("/keys?status=all")).keys.filter(k => k.src === "subscription");
assert.ok(listed.some(k => k.plan === "ultra" && k.note.includes("울트라 연간 자동 갱신")));
assert.ok(listed.some(k => k.note.includes("월간 카드 결제")));

// bank transfer / direct payment still works next to cards, and the buyer picks
assert.equal((await j("/order?plan=yearly&method=manual", { method: "POST" })).http, 503, "no link, no manual order");
assert.match((await adm("/pay-links", { yearly: "http://pay.example/y" })).error, /https/);
await adm("/pay-links", { yearly: "https://pay.example/y", ultra_monthly: "https://pay.example/um" });
assert.deepEqual((await j("/status")).methods.manual, ["yearly", "ultra_monthly"]);
assert.equal((await j("/order?plan=yearly", { method: "POST" })).method, "card", "card first when both are open");
const m = await j("/order?plan=yearly&method=manual", { method: "POST" });
assert.equal(m.url, "https://pay.example/y");
assert.equal((await j("/claim?session=" + m.code)).order, "requested");
assert.equal((await j("/order/paid?code=" + m.code, { method: "POST", body: JSON.stringify({ payer: "홍길동", msg: "토스" }) })).status, "paid");
assert.equal((await adm("/summary")).counts.ordersPaid, 1);
const approved = await adm("/orders/approve", { code: m.code });
assert.equal((await j("/claim?session=" + m.code)).key, approved.key);
assert.equal(payload(approved.key).plan, "premium");
assert.ok(days(payload(approved.key).expires) > 368);
const m2 = await j("/order?plan=ultra_monthly&method=manual", { method: "POST" });
await adm("/orders/reject", { code: m2.code, reason: "입금이 없어요" });
assert.deepEqual(await j("/claim?session=" + m2.code), { http: 200, status: "rejected", reason: "입금이 없어요" });
const m3 = await j("/order?plan=ultra_monthly&method=manual", { method: "POST" });
assert.equal(payload((await adm("/orders/approve", { code: m3.code })).key).plan, "ultra");
assert.equal((await j("/claim?session=../../x")).http, 400);

// nothing a secret: no API key or signing secret in KV or in any dashboard reply
for (const [k, v] of kv) assert.ok(!String(v).includes("lsk_fake_api_key") && !String(v).includes(SECRET), k);
console.log("card payment tests passed");
