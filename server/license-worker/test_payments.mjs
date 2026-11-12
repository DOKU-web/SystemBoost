// Payments review fixes (2026-11-12): renewal of an expired subscription key, webhooks that fail part way, "I paid"
// without a KV write per call, per-IP limits, plan change of a card subscription, cancelling a subscription on refund
// or block, early renewal (기간 연장) of a bank-transfer key, one key per approved order, the /thanks page.
// Fake KV (it can fail a put on purpose and refuses metadata over Cloudflare's 1,024 bytes), a fake Lemon Squeezy
// API, signed webhooks and the worker's real routes. Global fetch answers only as the fake: nothing leaves this machine.
//   node test_payments.mjs
import assert from "node:assert/strict";
import { createHash, createHmac, generateKeyPairSync, sign } from "node:crypto";
import { readFileSync } from "node:fs";
import worker from "./src/index.js";
import { _forgetIpLimits } from "./src/iplimit.js";

const kv = new Map(), md = new Map(), opts = new Map();
let failOn = null;          // key => true: that put throws, as a KV write does when the day's quota is gone
const writes = [];          // every put / delete, in order
const { privateKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
const forger = generateKeyPairSync("ec", { namedCurve: "P-256" });
const SECRET = "made-up-signing-secret";
const TOKEN = "made-up-admin-token-0123456789";
const env = {
  LICENSES: {
    get: async k => kv.get(k) ?? null,
    getWithMetadata: async k => ({ value: kv.get(k) ?? null, metadata: md.get(k) ?? null }),
    put: async (k, v, o) => {
      if (failOn?.(k)) throw new Error("KV put failed (test): " + k);
      if (o?.metadata && Buffer.byteLength(JSON.stringify(o.metadata)) > 1024) throw new Error("KV metadata over 1024 bytes: " + k);
      writes.push(k); kv.set(k, v); md.set(k, o?.metadata ?? null); opts.set(k, o || {});
    },
    delete: async k => { writes.push("-" + k); kv.delete(k); md.delete(k); opts.delete(k); },
    list: async ({ prefix, limit = 1000 }) => ({ keys: [...kv.keys()].filter(k => k.startsWith(prefix)).sort().slice(0, limit).map(name => ({ name, metadata: md.get(name) })), list_complete: true }),
  },
  LICENSE_PRIVATE_KEY: privateKey.export({ type: "pkcs8", format: "pem" }),
  ADMIN_TOKEN: TOKEN,
  PUBLIC_URL: "https://lic.example",
  SALES_MODE: "off",
  CARD_LIVE_FROM: "2000-01-01T00:00:00Z",
  BUNDLE_FROM: "2000-01-01T00:00:00Z",
  LS_API_KEY: "lsk_made_up",
  LS_SIGNING_SECRET: SECRET,
};

// ---- fake Lemon Squeezy: checkouts, subscription PATCH (plan change) and DELETE (cancel)
const ls = [];
let lsPatch = () => new Response(JSON.stringify({ data: { id: "1" } }), { status: 200 });
let lsDelete = () => new Response(JSON.stringify({ data: { id: "1", attributes: { status: "cancelled" } } }), { status: 200 });
globalThis.fetch = async (url, init = {}) => {
  url = String(url);
  const method = init.method || "GET";
  assert.equal(init.headers?.Authorization, "Bearer " + env.LS_API_KEY, "the API key goes along");
  if (url === "https://api.lemonsqueezy.com/v1/checkouts" && method === "POST")
    return new Response(JSON.stringify({ data: { attributes: { url: "https://shop.example/checkout/x" } } }), { status: 201 });
  const m = /^https:\/\/api\.lemonsqueezy\.com\/v1\/subscriptions\/(\d+)$/.exec(url);
  assert.ok(m, "only the fake Lemon Squeezy is called: " + url);
  ls.push({ method, sub: m[1], body: init.body ? JSON.parse(init.body) : null });
  if (method === "PATCH") return lsPatch(m[1]);
  if (method === "DELETE") return lsDelete(m[1]);
  assert.fail("unexpected Lemon Squeezy call " + method + " " + url);
};

const call = (path, init = {}, ip = "10.0.0.1") =>
  worker.fetch(new Request("https://lic.example" + path, { ...init, headers: { "CF-Connecting-IP": ip, ...(init.headers || {}) } }), env);
const j = async (path, init, ip) => { const r = await call(path, init, ip); return { http: r.status, ...(await r.json()) }; };
const postJ = (path, b, ip) => j(path, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(b) }, ip);
const adm = (path, body) => j("/admin/api" + path, { method: body ? "POST" : "GET", headers: { Authorization: "Bearer " + TOKEN, "Content-Type": "application/json" }, body: body ? JSON.stringify(body) : undefined });
const payload = key => JSON.parse(Buffer.from(key.split(".")[0], "base64url"));
const kid = key => createHash("sha256").update(key.trim()).digest("hex").slice(0, 16);
const days = iso => (Date.parse(iso) - Date.now()) / 864e5;
const lics = () => [...kv.keys()].filter(k => k.startsWith("lic:")).length;
const log = async () => (await adm("/log")).log;
const renew = (sub, key) => postJ("/renew", { sub: String(sub), key });
let chN = 0;   // a new address per call unless the test names one (the per-IP limit is tested on its own)
const change = (key, plan, ip) => postJ("/subscription/change", { key, plan }, ip ?? "10.8." + Math.floor(++chN / 250) + "." + (chN % 250));
const V = { monthly: "101", yearly: "102", ultra_monthly: "201", ultra_yearly: "202", biz_monthly: "301", biz_yearly: "302" };
const sub = (id, variant, extra = {}) => ({ type: "subscriptions", id: String(id), attributes: { status: "active", variant_id: Number(variant), order_id: 9000 + id, user_email: `p${id}@example.com`, renews_at: "2026-12-30T10:00:00.000000Z", ...extra } });
const invoice = (invId, subId) => ({ type: "subscription-invoices", id: String(invId), attributes: { subscription_id: subId, billing_reason: "renewal", status: "paid", user_email: `p${subId}@example.com` } });
const hook = async (event, data, custom = {}, test = false) => {
  const raw = JSON.stringify({ meta: { event_name: event, test_mode: test, custom_data: custom }, data });
  const r = await call("/webhook/lemonsqueezy", { method: "POST", body: raw, headers: { "X-Signature": createHmac("sha256", SECRET).update(raw).digest("hex") } });
  return { http: r.status, ...(await r.json()) };
};
const cardOrder = async plan => (await j(`/order?plan=${plan}&method=card`, { method: "POST" })).code;
const manualOrder = (plan, key, ip) => j(`/order?plan=${plan}&method=manual`, { method: "POST", headers: { "Content-Type": "application/json" }, ...(key === undefined ? {} : { body: JSON.stringify({ key }) }) }, ip);
const order = async code => (await adm("/orders?status=all")).orders.find(o => o.code === code);

await adm("/card", { store: "42", variants: V });
await adm("/pay-links", { monthly: "https://pay.example/m", yearly: "https://pay.example/y", ultra_yearly: "https://pay.example/uy", yearly_x5: "https://pay.example/y5" });
await adm("/sales-mode", { mode: "live" });

// ------------------------------------------------------------ 1. /renew takes an expired key of the subscription
// The first key of subscription 1 ran out on 2026-01-04 (renews_at + 3 days) while the app was closed; the renewal
// payment came later. The holder of the expired key still gets the next key.
await hook("subscription_created", sub(1, V.monthly, { renews_at: "2026-01-01T00:00:00.000000Z" }));
const k1old = kv.get("sub:1");
assert.ok(days(payload(k1old).expires) < 0, "the first key has run out");
await hook("subscription_payment_success", invoice(7001, 1));
const k1new = kv.get("sub:1");
assert.notEqual(k1new, k1old);
let r = await renew(1, k1old);
assert.equal(r.status, "ready", "an expired key of the subscription renews");
assert.equal(r.key, k1new);
assert.ok(days(payload(r.key).expires) > 30);
// still refused: another subscription's key, a forged key, a key that is not the issued text, a blocked key
await hook("subscription_created", sub(2, V.monthly, { renews_at: "2026-01-01T00:00:00.000000Z" }));
const k2old = kv.get("sub:2");
assert.equal((await renew(1, k2old)).http, 403, "another subscription's (expired) key");
const forgedBody = Buffer.from(JSON.stringify({ ...payload(k1old), expires: "2099-01-01T00:00:00Z" }));
const forged = forgedBody.toString("base64url") + "." + sign("sha256", forgedBody, { key: forger.privateKey, dsaEncoding: "ieee-p1363" }).toString("base64url");
assert.equal((await renew(1, forged)).http, 403, "a forged key");
assert.equal((await renew(1, k1old + "x")).http, 403, "not the issued text");
await adm("/revoke", { id: kid(k1old), reason: "공유" });
assert.equal((await renew(1, k1old)).status, "inactive", "a blocked expired key renews nothing");
assert.equal((await renew(1, k1new)).status, "inactive", "blocking a key ends the subscription's renewals");
// an expired key of a cancelled subscription: nothing to give
await hook("subscription_updated", sub(2, V.monthly, { status: "cancelled" }));
assert.equal((await renew(2, k2old)).status, "inactive");
// light on KV: no writes
let w0 = writes.length;
await renew(1, k1old); await renew(2, k2old);
assert.equal(writes.length, w0, "/renew never writes");

// ------------------------------------------------------------ 2. a webhook that fails part way, then is retried
// subscription_created: the order put fails (the day's writes ran out). Lemon Squeezy retries; the retry must hand
// out the key that was signed, not a second one, and only then is the event marked handled.
let code = await cardOrder("ultra_monthly");
let n0 = lics();
failOn = k => k.startsWith("ord:");
r = await hook("subscription_created", sub(10, V.ultra_monthly), { session: code });
assert.equal(r.http, 500, "the failed write fails the webhook, so Lemon Squeezy retries");
assert.equal(lics(), n0 + 1, "the key was signed and registered");
assert.match(kv.get("hook:created:10"), /^pending:[0-9a-f]{16}$/, "noted as signed, not as handled");
const signedFirst = kv.get("hook:created:10").slice("pending:".length);
assert.equal((await order(code)).status, "requested", "the order was not stored");
failOn = null;
writes.length = 0;
r = await hook("subscription_created", sub(10, V.ultra_monthly), { session: code });
assert.deepEqual([r.http, r.ignored], [200, undefined], "the retry is not taken for a repeat");
assert.equal(lics(), n0 + 1, "no second key for one payment");
let c = await j("/claim?session=" + code);
assert.equal(c.status, "ready");
assert.equal(kid(c.key), signedFirst, "the app gets the key signed on the first try");
assert.equal(kv.get("sub:10"), c.key);
assert.equal(kv.get("hook:created:10"), "1", "now handled");
assert.equal((await order(code)).status, "approved");
assert.ok(writes.lastIndexOf("hook:created:10") > writes.lastIndexOf("ord:" + code), "handled is written after the order");
assert.ok(writes.lastIndexOf("hook:created:10") > writes.lastIndexOf("session:" + code), "and after the key for the app");
assert.ok(writes.lastIndexOf("hook:created:10") > writes.lastIndexOf("sub:10"));
assert.equal((await hook("subscription_created", sub(10, V.ultra_monthly), { session: code })).ignored, "repeat");
assert.equal(lics(), n0 + 1);
// the put that notes the signed key fails: nothing was stored, so the retry signs once
n0 = lics();
failOn = k => k.startsWith("hook:created:11");
assert.equal((await hook("subscription_created", sub(11, V.monthly))).http, 500);
assert.equal(lics(), n0, "nothing stored before the note");
failOn = null;
assert.equal((await hook("subscription_created", sub(11, V.monthly))).http, 200);
assert.equal(lics(), n0 + 1);
// the key's own registration fails after the note: the retry signs a new key, still one in the registry
n0 = lics();
failOn = k => k.startsWith("lic:");
assert.equal((await hook("subscription_created", sub(12, V.monthly))).http, 500);
assert.match(kv.get("hook:created:12"), /^pending:/);
failOn = null;
assert.equal((await hook("subscription_created", sub(12, V.monthly))).http, 200);
assert.equal(lics(), n0 + 1, "one registered key");
assert.deepEqual(JSON.parse(kv.get("subkeys:12")), [kid(kv.get("sub:12"))]);
assert.equal(kv.get("hook:created:12"), "1");
// a renewal invoice: the e-mail lookup put fails, the retry gives the same key
const before11 = kv.get("sub:11");
n0 = lics();
failOn = k => k.startsWith("email:");
assert.equal((await hook("subscription_payment_success", invoice(7011, 11))).http, 500);
failOn = null;
assert.equal((await hook("subscription_payment_success", invoice(7011, 11))).http, 200);
assert.equal(lics(), n0 + 1, "one key for one invoice");
assert.notEqual(kv.get("sub:11"), before11);
assert.equal((await hook("subscription_payment_success", invoice(7011, 11))).ignored, "repeat");
assert.equal(lics(), n0 + 1);
// a plan change (subscription_updated): the subscription record fails, the retry gives the same key
n0 = lics();
failOn = k => k.startsWith("subplan:11");
const upd = sub(11, V.ultra_monthly, { updated_at: "2026-11-20T01:02:03.000000Z" });
assert.equal((await hook("subscription_updated", upd)).http, 500);
failOn = null;
assert.equal((await hook("subscription_updated", upd)).http, 200);
assert.equal(lics(), n0 + 1, "one key for one plan change");
assert.equal(payload(kv.get("sub:11")).plan, "ultra");
assert.equal(kv.get("subplan:11"), "ultra_monthly");
assert.equal((await hook("subscription_updated", upd)).http, 200);
assert.equal(lics(), n0 + 1);
// markers from the worker before this one ("1") still count as handled
kv.set("hook:invoice:7999", "1");
assert.equal((await hook("subscription_payment_success", invoice(7999, 11))).ignored, "repeat");
// privacy: the subscription's key list expires 400 days after its last write; the markers hold no key text
assert.equal(opts.get("subkeys:11").expirationTtl, 60 * 60 * 24 * 400);
for (const [k, v] of kv) if (k.startsWith("hook:")) assert.ok(!String(v).includes("."), "a marker holds an id at most: " + k);

// ------------------------------------------------------------ 3. "I paid": a write only when something changes
_forgetIpLimits();
const paid = (c, b, ip) => j("/order/paid?code=" + c, { method: "POST", body: JSON.stringify(b) }, ip);
const ordWrites = c => writes.filter(k => k === "ord:" + c).length;
const mo = (await manualOrder("yearly")).code;
writes.length = 0;
assert.equal((await paid(mo, { payer: "홍길동", msg: "토스" })).status, "paid");
assert.equal(ordWrites(mo), 1, "requested -> paid writes once");
for (let i = 0; i < 10; i++) assert.equal((await paid(mo, { payer: "홍길동", msg: "토스" })).status, "paid");
assert.equal((await postJ("/order/paid", { code: mo, payer: "홍길동", msg: "토스" })).status, "paid");
assert.equal(ordWrites(mo), 1, "the same note again writes nothing");
const paidAt = (await order(mo)).paidAt;
for (let i = 1; i <= 5; i++) assert.equal((await paid(mo, { payer: "홍길동" + i })).status, "paid", "a changed name is kept");
assert.equal(ordWrites(mo), 6);
r = await paid(mo, { payer: "누군가" });
assert.deepEqual([r.http, r.error], [429, "too many changes"], "changes per order are capped");
assert.equal(ordWrites(mo), 6, "no write past the cap");
assert.equal((await order(mo)).payer, "홍길동5");
assert.equal((await order(mo)).paidAt, paidAt, "the time it was first marked paid stays");
assert.equal((await paid(mo, { payer: "홍길동5" })).status, "paid", "the same note still answers");
// per IP: 30 an hour, then 429 without reading or writing the order
_forgetIpLimits();
const mo2 = (await manualOrder("monthly")).code;
writes.length = 0;
for (let i = 0; i < 30; i++) await paid(mo2, { payer: "김" }, "10.5.5.5");
r = await paid(mo2, { payer: "다른 이름" }, "10.5.5.5");
assert.deepEqual([r.http, r.error], [429, "busy"]);
assert.equal((await order(mo2)).payer, "김");
assert.equal((await paid(mo2, { payer: "김" }, "10.5.5.6")).status, "paid", "another address is not held up");
assert.equal((await paid(mo2, { payer: "김" }, "2001:db8:1:2:3::4")).status, "paid");
assert.equal(ordWrites(mo2), 1);
// a card order is still never marked paid by the buyer, and costs no write
const co = await cardOrder("monthly");
writes.length = 0;
assert.equal((await paid(co, { payer: "x" }, "10.5.5.7")).status, "requested");
assert.equal(writes.length, 0);

// ------------------------------------------------------------ 4. per-IP limits on the other public routes that write
_forgetIpLimits();
for (let i = 0; i < 20; i++) assert.equal((await manualOrder("yearly", undefined, "10.6.0.1")).http, 200);
let w = writes.length;
assert.deepEqual(await manualOrder("yearly", undefined, "10.6.0.1"), { http: 429, error: "busy" }, "orders: 20 an hour per address");
assert.equal(writes.length, w, "a refused order writes nothing");
assert.equal((await manualOrder("yearly", undefined, "10.6.0.2")).http, 200);
for (let i = 0; i < 20; i++) assert.equal((await j("/checkout?plan=monthly&session=" + "s".repeat(20), { method: "POST" }, "10.6.0.3")).http, 200);
assert.equal((await j("/checkout?plan=monthly&session=" + "s".repeat(20), { method: "POST" }, "10.6.0.3")).http, 429, "checkouts: 20 an hour");
await adm("/referrals", { code: "IPTEST", days: 1, max: 1000 });
const dev = n => createHash("md5").update("device" + n).digest("hex");
for (let i = 0; i < 30; i++) assert.ok((await postJ("/referral", { code: "IPTEST", device: dev(i) }, "10.6.0.4")).key);
w = writes.length;
assert.deepEqual(await postJ("/referral", { code: "IPTEST", device: dev(99) }, "10.6.0.4"), { http: 429, error: "busy" }, "referral gifts: 30 an hour per address");
assert.equal(writes.length, w);
assert.ok((await postJ("/referral", { code: "IPTEST", device: dev(99) }, "10.6.0.5")).key);
_forgetIpLimits();

// ------------------------------------------------------------ 5. plan change of a card subscription
code = await cardOrder("monthly");
await hook("subscription_created", sub(30, V.monthly), { session: code });
const k30 = (await j("/claim?session=" + code)).key;
ls.length = 0;
w = writes.length;
r = await change(k30, "ultra_monthly");
assert.deepEqual([r.http, r.ok, r.plan], [200, true, "ultra_monthly"]);
assert.equal(ls.length, 1);
assert.deepEqual(ls[0], { method: "PATCH", sub: "30", body: { data: { type: "subscriptions", id: "30", attributes: { variant_id: 201 } } } },
                 "the subscription is switched to the Ultra product; no checkout is made");
assert.deepEqual(writes.slice(w).filter(k => !k.startsWith("log:")), [], "only a log line is written");
assert.ok((await log()).some(l => l.action === "플랜 변경 요청" && l.detail.includes("프로 월간 → 울트라 월간")));
// Lemon Squeezy's subscription_updated then gives the new key, which the holder fetches through /renew
await hook("subscription_updated", sub(30, V.ultra_monthly, { updated_at: "2026-11-21T00:00:00.000000Z" }));
r = await renew(30, k30);
assert.equal(payload(r.key).plan, "ultra");
assert.equal(payload(r.key).sub, "30");
const k30u = r.key;
assert.deepEqual([(await change(k30u, "ultra_monthly")).http, (await change(k30u, "ultra_monthly")).error], [409, "same plan"]);
assert.equal((await change(k30, "yearly")).http, 200, "the older key of the same subscription may ask too");
// refused: no key, forged, other text, blocked, a key without a subscription, a bundle or unknown plan
ls.length = 0;
assert.equal((await change("", "ultra_monthly")).http, 403);
assert.equal((await change(forged, "ultra_monthly")).http, 403);
assert.equal((await change(k30 + " x", "ultra_monthly")).http, 403);
const manualKey = (await adm("/issue", { plan: "yearly", note: "manual" })).key;
assert.equal((await change(manualKey, "ultra_monthly")).http, 403, "a bank-transfer key has no subscription");
for (const p of ["yearly_x5", "constructor", "", "lifetime"]) assert.equal((await change(k30, p)).http, 400, "plan " + p);
assert.equal(ls.length, 0, "nothing reaches Lemon Squeezy for those");
// a plan with no card product
await adm("/card", { store: "42", variants: { ...V, biz_monthly: "" } });
assert.deepEqual([(await change(k30, "biz_monthly")).http, (await change(k30, "biz_monthly")).error], [409, "plan closed"]);
await adm("/card", { store: "42", variants: V });
// Lemon Squeezy refuses or cannot be reached
lsPatch = () => new Response("{}", { status: 422 });
assert.deepEqual([(await change(k30, "biz_monthly")).http, (await change(k30, "biz_monthly")).error], [502, "refused"]);
assert.ok((await log()).some(l => l.action === "플랜 변경 실패"));
lsPatch = () => { throw new TypeError("network"); };
assert.equal((await change(k30, "biz_monthly")).error, "unavailable");
lsPatch = () => new Response("{}", { status: 200 });
// no API key: the app is told to use the subscription page
const apiKey = env.LS_API_KEY;
delete env.LS_API_KEY;
assert.deepEqual([(await change(k30, "biz_monthly")).http, (await change(k30, "biz_monthly")).error], [503, "unavailable"]);
env.LS_API_KEY = apiKey;
// per IP: 10 an hour
_forgetIpLimits();
for (let i = 0; i < 10; i++) await change(k30, "biz_yearly", "10.7.0.1");
assert.deepEqual(await change(k30, "biz_yearly", "10.7.0.1"), { http: 429, error: "busy" });
_forgetIpLimits();
// a blocked key, a cancelled subscription
await adm("/revoke", { id: kid(k30u), reason: "공유" });
assert.equal((await change(k30u, "yearly")).http, 403, "a blocked key");
assert.deepEqual([(await change(k30, "yearly")).http, (await change(k30, "yearly")).error], [409, "inactive"], "blocking ended the subscription's renewals");

// ------------------------------------------------------------ 6. a refund or a block stops the billing
ls.length = 0;
code = await cardOrder("ultra_yearly");
await hook("subscription_created", sub(40, V.ultra_yearly), { session: code });
await hook("order_refunded", { type: "orders", id: "9040", attributes: {} });
assert.deepEqual(ls.map(x => [x.method, x.sub]), [["DELETE", "40"]], "the refunded subscription is cancelled at Lemon Squeezy");
assert.ok((await log()).some(l => l.action === "구독 해지" && l.detail.includes("구독 40")));
await hook("subscription_created", sub(41, V.monthly));
await hook("subscription_payment_success", invoice(7041, 41));
ls.length = 0;
await hook("subscription_payment_refunded", { type: "subscription-invoices", id: "5", attributes: { subscription_id: 41 } });
assert.deepEqual(ls.map(x => [x.method, x.sub]), [["DELETE", "41"]]);
// Lemon Squeezy cannot cancel (or already did): the refund still goes through, and the log tells the owner
await hook("subscription_created", sub(42, V.monthly));
const k42 = kv.get("sub:42");
lsDelete = () => new Response("{}", { status: 404 });
r = await hook("subscription_payment_refunded", { type: "subscription-invoices", id: "6", attributes: { subscription_id: 42 } });
assert.equal(r.http, 200, "a failed cancellation never fails the webhook (Lemon Squeezy would retry it forever)");
assert.equal((await j("/check?id=" + kid(k42))).revoked, true, "the keys are blocked all the same");
assert.ok((await log()).some(l => l.action === "구독 해지 실패" && l.detail.includes("구독 42")));
lsDelete = () => { throw new TypeError("network"); };
await hook("subscription_created", sub(43, V.monthly));
assert.equal((await hook("subscription_payment_refunded", { type: "subscription-invoices", id: "7", attributes: { subscription_id: 43 } })).http, 200);
lsDelete = () => new Response("{}", { status: 200 });
// no API key: nothing is called, and the log says the owner has to cancel it in Lemon Squeezy
delete env.LS_API_KEY;
ls.length = 0;
await hook("subscription_created", sub(44, V.monthly));
await hook("subscription_payment_refunded", { type: "subscription-invoices", id: "8", attributes: { subscription_id: 44 } });
assert.equal(ls.length, 0);
assert.ok((await log()).some(l => l.action === "구독 해지 필요" && l.detail.includes("구독 44") && l.detail.includes("직접 해지")));
env.LS_API_KEY = apiKey;
// the dashboard's block: cancel: true cancels the key's subscription, without it nothing is cancelled
await hook("subscription_created", sub(45, V.monthly));
await hook("subscription_created", sub(46, V.monthly));
ls.length = 0;
r = await adm("/revoke", { id: kid(kv.get("sub:45")), reason: "환불", cancel: true });
assert.deepEqual([r.ok, r.cancelled], [true, { ok: true }]);
assert.deepEqual(ls.map(x => [x.method, x.sub]), [["DELETE", "45"]]);
r = await adm("/revoke", { id: kid(kv.get("sub:46")), reason: "공유" });
assert.deepEqual([r.ok, r.cancelled], [true, undefined]);
assert.equal(ls.length, 1, "a block without cancel leaves the subscription to the owner");
r = await adm("/revoke", { id: kid(manualKey), reason: "환불", cancel: true });
assert.deepEqual([r.ok, r.cancelled], [true, undefined], "a key without a subscription has nothing to cancel");
// src/support.js gets both helpers for its refund flow
assert.match(readFileSync(new URL("./src/index.js", import.meta.url), "utf8"), /const SUPPORT = \{[^}]*\brevokeSub\b[^}]*\bcancelSubscription\b/);

// ------------------------------------------------------------ 7. 기간 연장: the new period starts where the held key ends
_forgetIpLimits();
const held = (await adm("/issue", { plan: "monthly", note: "held" })).key;   // Pro, about 34 days left
const heldEnd = payload(held).expires;
let o = await manualOrder("yearly", held);
assert.equal(o.extends, heldEnd, "the order says it continues from the held key");
assert.deepEqual((await order(o.code)).extendFrom, { id: kid(held), expires: heldEnd });
let a = await adm("/orders/approve", { code: o.code });
assert.equal(a.extendedFrom, heldEnd);
assert.equal(payload(a.key).expires, new Date(Date.parse(heldEnd) + 369 * 864e5).toISOString().replace(/\.\d+Z$/, "Z"), "old end + 366 days + 3 days grace");
assert.ok((await log()).some(l => l.action === "키 발급" && l.detail.includes("부터 이어서")));
// a lower tier held, a higher tier bought: starts today (days left of the cheaper plan do not become days of the dearer one)
o = await manualOrder("ultra_yearly", held);
assert.equal(o.extends, undefined);
assert.equal((await order(o.code)).extendFrom, undefined);
// a higher tier held, a lower tier bought: starts today
const ultraKey = (await adm("/issue", { tier: "ultra", plan: "yearly", note: "held ultra" })).key;
o = await manualOrder("monthly", ultraKey);
assert.equal(o.extends, undefined);
assert.equal((await order(o.code)).extendFrom, undefined);
// not extended: no key (apps up to 0.8.11), a forged or edited key, a blocked key, a test key, an expired key, card, bundle
assert.equal((await manualOrder("yearly")).extends, undefined, "no body: as before");
assert.equal((await j("/order?plan=yearly&method=manual", { method: "POST", body: "x" })).extends, undefined);
assert.equal((await manualOrder("yearly", forged)).extends, undefined);
assert.equal((await manualOrder("yearly", held + "A")).extends, undefined);
assert.equal((await manualOrder("yearly", { key: held })).extends, undefined);
const blocked = (await adm("/issue", { plan: "monthly", note: "blocked" })).key;
await adm("/revoke", { id: kid(blocked), reason: "공유" });
assert.equal((await manualOrder("yearly", blocked)).extends, undefined);
assert.equal((await manualOrder("yearly", k2old)).extends, undefined, "an expired key: the period counts from the approval anyway");
await adm("/sales-mode", { mode: "test" });
await hook("subscription_created", sub(50, V.monthly), {}, true);
await adm("/sales-mode", { mode: "live" });
assert.equal(payload(kv.get("sub:50")).test, true);
assert.equal((await manualOrder("yearly", kv.get("sub:50"))).extends, undefined, "a test key");
const longKey = (await adm("/issue", { plan: "days", days: 3650, note: "long" })).key;
assert.ok((await manualOrder("yearly", longKey)).extends, "a key with an end date far away still counts");
assert.equal((await manualOrder("yearly_x5", held)).extends, undefined, "a bundle starts today");
const cardWithKey = await j("/order?plan=monthly&method=card", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ key: held }) });
assert.equal(cardWithKey.extends, undefined, "a card subscription starts on its payment day");
// approved without an extension: from now
o = await manualOrder("yearly");
a = await adm("/orders/approve", { code: o.code });
assert.ok(Math.abs(days(payload(a.key).expires) - 369) < 0.01);
assert.equal(a.extendedFrom, undefined);
// the held key was blocked between the order and the approval (a refund): from now
const soonBlocked = (await adm("/issue", { plan: "monthly", note: "refunded later" })).key;
o = await manualOrder("monthly", soonBlocked);
assert.ok(o.extends);
await adm("/revoke", { id: kid(soonBlocked), reason: "환불" });
a = await adm("/orders/approve", { code: o.code });
assert.ok(Math.abs(days(payload(a.key).expires) - 34) < 0.01, "a blocked key's days are not carried");
// the longest order record still fits Cloudflare's 1,024 bytes of metadata (the fake KV refuses more)
o = await manualOrder("yearly", held);
assert.equal((await paid(o.code, { payer: "가".repeat(60), msg: "나".repeat(150) })).status, "paid");
for (let i = 0; i < 5; i++) await paid(o.code, { payer: "다".repeat(39) + i, msg: "라".repeat(150) });
assert.equal((await adm("/orders/reject", { code: o.code, reason: "마".repeat(150) })).ok, true);

// ------------------------------------------------------------ 8. one payment, one key
o = await manualOrder("yearly");
const first = (await adm("/orders/approve", { code: o.code })).key;
r = await adm("/issue", { code: o.code, plan: "yearly" });
assert.equal(r.http, 409, "issuing again for an approved order is refused");
assert.match(r.error, /이미 키를 발급한 주문/);
assert.equal((await j("/claim?session=" + o.code)).key, first);
n0 = lics();
r = await adm("/issue", { code: o.code, plan: "yearly", reissue: true });
assert.equal(r.http, 200);
assert.equal(lics(), n0 + 1);
assert.equal((await j("/claim?session=" + o.code)).key, r.key, "a reissue replaces the claimable key");
assert.equal((await j("/check?id=" + kid(first))).revoked, true, "and blocks the earlier key");
assert.equal((await j("/check?id=" + kid(r.key))).revoked, false);
// a bundle reissued: every earlier key is blocked
_forgetIpLimits();
o = await manualOrder("yearly_x5");
const b5 = (await adm("/orders/approve", { code: o.code })).keys;
assert.equal(b5.length, 5);
r = await adm("/issue", { code: o.code, plan: "yearly_x5", reissue: true });
assert.equal(r.keys.length, 5);
for (const k of b5) assert.equal((await j("/check?id=" + kid(k))).revoked, true);
for (const k of r.keys) assert.equal((await j("/check?id=" + kid(k))).revoked, false);
assert.equal((await j("/claim?session=" + o.code)).keys.length, 5);

// ------------------------------------------------------------ 9. /thanks shows the key only right after paying
code = await cardOrder("monthly");
await hook("subscription_created", sub(60, V.monthly), { session: code });
const k60 = kv.get("sub:60");
let page = await (await call("/thanks?session=" + code)).text();
assert.ok(page.includes(k60), "right after the payment: the key");
assert.ok(page.includes("1시간"));
assert.ok(!page.includes("메일 주소로도 저장돼"), "the old wording that read as 'also e-mailed' is gone");
assert.ok(page.includes("메일로 보내지 않아요"));
assert.ok(page.includes("구매 확인서") && page.includes("플랜: 프로 월간") && page.includes("청약철회") && page.includes("미성년자"), "the purchase confirmation to keep");
md.set("session:" + code, { at: new Date(Date.now() - 2 * 3600e3).toISOString() });
page = await (await call("/thanks?session=" + code)).text();
assert.ok(!page.includes(k60), "two hours later: no key in a page whose address stays in the history");
assert.ok(page.includes(code) && page.includes("주문 코드로 키 받기"), "the page says how to get it");
assert.ok(page.includes("구매 확인서") && !page.includes(k60.split(".")[1]), "the confirmation stays, without the key");
md.set("session:" + code, null);
assert.ok(!(await (await call("/thanks?session=" + code)).text()).includes(k60), "a record from before (no time): no key");
assert.equal((await j("/claim?session=" + code)).key, k60, "the app still claims it");
assert.ok((await (await call("/thanks?session=SB-AAAA-AAAA")).text()).includes("확인하는 중"));
const mo9 = (await manualOrder("monthly")).code;
await adm("/orders/approve", { code: mo9 });
assert.ok((await (await call("/thanks?session=" + mo9)).text()).includes(kv.get("session:" + mo9)), "an approval sets the time too");

// nothing secret in KV
for (const [k, v] of kv) assert.ok(!String(v).includes(apiKey) && !String(v).includes(SECRET) && !String(v).includes(TOKEN), k);
console.log("payment fix tests passed");
