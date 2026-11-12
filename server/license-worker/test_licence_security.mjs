// Security tests for licence keys, orders, card webhooks and the admin API (audit 2026-11-12).
// Fake KV, a throwaway signing key made here, a fake Lemon Squeezy; global fetch refuses everything else, so
// nothing here reaches the real Worker, Lemon Squeezy or Supabase. Every key, token and secret is made up.
//   node test_licence_security.mjs
import assert from "node:assert/strict";
import { createHash, createHmac, generateKeyPairSync, sign, verify } from "node:crypto";
import worker from "./src/index.js";

const kv = new Map(), md = new Map();
const { privateKey, publicKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
const forger = generateKeyPairSync("ec", { namedCurve: "P-256" });   // a key that is not the shop's
const SECRET = "made-up-webhook-signing-secret";
const TOKEN = "made-up-admin-token-0123456789";
const API_KEY = "lsk_made_up_api_key";
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
  BUNDLE_FROM: "2000-01-01T00:00:00Z",
  LS_API_KEY: API_KEY,
  LS_SIGNING_SECRET: SECRET,
};
// a fake Supabase (made-up address) that only records what the Worker asks for; sections that need it set the address
const dbCalls = [];
globalThis.fetch = async (url, init = {}) => {
  if (String(url) === "https://db.example/rest/v1/rpc/account_call") {
    const { action, b } = JSON.parse(init.body);
    dbCalls.push({ action, b });
    return new Response(JSON.stringify(action === "code" ? { member: false } : { id: b.id }), { status: 200 });
  }
  if (/^https:\/\/api\.lemonsqueezy\.com\/v1\/subscriptions\/\d+$/.test(String(url)) && init.method === "DELETE") {   // a refund cancels
    cancels.push(String(url).split("/").pop());
    return new Response("{}", { status: 200 });
  }
  assert.equal(String(url), "https://api.lemonsqueezy.com/v1/checkouts", "only the fake Lemon Squeezy may be called: " + url);
  return new Response(JSON.stringify({ data: { attributes: { url: "https://shop.example/checkout/x" } } }), { status: 201 });
};
const cancels = [];

const call = (path, init = {}, ip = "10.0.0.1") =>
  worker.fetch(new Request("https://lic.example" + path, { ...init, headers: { "CF-Connecting-IP": ip, ...(init.headers || {}) } }), env);
const j = async (path, init, ip) => { const r = await call(path, init, ip); return { http: r.status, ...(await r.json()) }; };
const adm = (path, body, token = TOKEN, ip = "10.0.0.1") =>
  j("/admin/api" + path, { method: body ? "POST" : "GET", headers: { Authorization: "Bearer " + token, "Content-Type": "application/json" }, body: body ? JSON.stringify(body) : undefined }, ip);
const clearFails = () => { for (const k of [...kv.keys()]) if (k.startsWith("fail:")) kv.delete(k); };
const payload = key => JSON.parse(Buffer.from(key.split(".")[0], "base64url"));
const signedBy = (key, pub) => { const [d, s] = key.split("."); return verify("sha256", Buffer.from(d, "base64url"), { key: pub, dsaEncoding: "ieee-p1363" }, Buffer.from(s, "base64url")); };
const kid = key => createHash("sha256").update(key.trim()).digest("hex").slice(0, 16);   // = keyId in index.js and License.KeyId
const keys = () => [...kv.keys()].filter(k => k.startsWith("lic:")).length;
const revoked = async key => (await j("/check?id=" + kid(key))).revoked;
const log = async () => (await adm("/log")).log;
const postJ = (path, b) => j(path, { method: "POST", headers: { "Content-Type": "application/json" }, body: typeof b === "string" ? b : JSON.stringify(b) });
const renew = (subId, key) => postJ("/renew", { sub: String(subId), key });

// Lemon Squeezy
const V = { monthly: "101", yearly: "102", ultra_monthly: "201", ultra_yearly: "202", biz_monthly: "301", biz_yearly: "302" };
const sub = (id, variant, extra = {}) => ({ type: "subscriptions", id: String(id), attributes: { status: "active", variant_id: Number(variant), order_id: 9000 + id, user_email: `s${id}@example.com`, renews_at: "2026-12-30T10:00:00.000000Z", ...extra } });
const invoice = (invId, subId, reason = "renewal") => ({ type: "subscription-invoices", id: String(invId), attributes: { subscription_id: subId, billing_reason: reason, status: "paid", user_email: `s${subId}@example.com` } });
const rawHook = (event, data, { test = false, custom = {} } = {}) => JSON.stringify({ meta: { event_name: event, test_mode: test, custom_data: custom }, data });
const hmac = (raw, secret = SECRET) => createHmac("sha256", secret).update(raw).digest("hex");
const post = (raw, sig) => j("/webhook/lemonsqueezy", { method: "POST", body: raw, headers: sig === undefined ? {} : { "X-Signature": sig } });
const hook = (event, data, opt) => { const raw = rawHook(event, data, opt); return post(raw, hmac(raw)); };
const cardOrder = async plan => (await j(`/order?plan=${plan}&method=card`, { method: "POST" })).code;

await adm("/card", { store: "42", variants: V });
await adm("/pay-links", { yearly: "https://pay.example/y", ultra_monthly: "https://pay.example/um" });
await adm("/sales-mode", { mode: "live" });

// ------------------------------------------------------------ 1. admin API: nothing without the token
const ADMIN_ROUTES = [
  ["GET", "/summary"], ["GET", "/keys"], ["GET", "/log"], ["GET", "/stats"], ["GET", "/orders"], ["GET", "/key?id=0123456789abcdef"],
  ["GET", "/pay-links"], ["GET", "/card"], ["GET", "/members"], ["GET", "/referrals"], ["GET", "/announce"],
  ["POST", "/issue"], ["POST", "/revoke"], ["POST", "/unrevoke"], ["POST", "/orders/approve"], ["POST", "/orders/reject"],
  ["POST", "/orders/delete"], ["POST", "/pay-links"], ["POST", "/card"], ["POST", "/lookup"], ["POST", "/sales-mode"],
  ["POST", "/members/block"], ["POST", "/members/recovery"], ["POST", "/members/delete"], ["POST", "/referrals"],
  ["POST", "/referrals/set"], ["POST", "/referrals/delete"], ["POST", "/downloads"], ["POST", "/announce/send"],
];
const issueBody = JSON.stringify({ plan: "yearly", note: "attacker", email: "x@example.com", mode: "live", id: "0123456789abcdef", code: "FREE", days: 30, max: 9999 });
const before = JSON.stringify([...kv.entries()].filter(([k]) => !k.startsWith("log:")));
const BAD_AUTH = [{}, { Authorization: "Bearer " }, { Authorization: "Bearer wrong-token" }, { Authorization: "Bearer " + TOKEN + "x" },
                  { Authorization: "Bearer " + TOKEN.slice(0, -1) }, { Authorization: "Basic " + Buffer.from("admin:" + TOKEN).toString("base64") },
                  { Authorization: TOKEN }, { "X-Admin-Token": TOKEN }];
let ipN = 0;
for (const [method, path] of ADMIN_ROUTES) {
  clearFails();   // stay under the lockout so every reply is the auth check's own (each refusal waits 400 ms: one route at a time, its tries at once)
  await Promise.all(BAD_AUTH.map(async headers => {
    const r = await call("/admin/api" + path, { method, headers: { "Content-Type": "application/json", ...headers }, body: method === "POST" ? issueBody : undefined }, "10.1.0." + (ipN++ % 250));
    assert.equal(r.status, 401, `${method} ${path} with ${JSON.stringify(Object.keys(headers))} must be refused`);
    assert.equal(r.headers.get("access-control-allow-origin"), null, "the admin API is not readable cross-origin");
  }));
}
clearFails();
assert.equal((await call("/admin/api/summary?token=" + TOKEN)).status, 401, "a token in the address is not a token");
clearFails();
assert.equal(JSON.stringify([...kv.entries()].filter(([k]) => !k.startsWith("log:") && !k.startsWith("fail:"))), before, "refused admin calls changed nothing");
// dashboard off when the token is missing or too short
for (const t of [undefined, "", "12345"]) {
  env.ADMIN_TOKEN = t;
  assert.equal((await call("/admin/api/summary", { headers: { Authorization: "Bearer " + t } })).status, 404);
}
env.ADMIN_TOKEN = TOKEN;
assert.equal((await adm("/summary")).http, 200);

// ------------------------------------------------------------ 2. forged and edited keys
// (the app's own check, License.Verify with the embedded public key, is tested from .NET; this is the server's view)
const real = (await adm("/issue", { plan: "monthly", note: "test key" })).key;
assert.ok(signedBy(real, publicKey));
const forgedPayload = Buffer.from(JSON.stringify({ plan: "biz", email: "", issued: "2026-11-12T00:00:00Z" }));
const forged = forgedPayload.toString("base64url") + "." + sign("sha256", forgedPayload, { key: forger.privateKey, dsaEncoding: "ieee-p1363" }).toString("base64url");
assert.equal((await adm("/lookup", { key: forged })).valid, false, "signed with another key");
const edited = Buffer.from(JSON.stringify({ ...payload(real), plan: "biz" })).toString("base64url") + "." + real.split(".")[1];
assert.equal((await adm("/lookup", { key: edited })).valid, false, "plan edited");
const longer = Buffer.from(JSON.stringify({ ...payload(real), expires: "2099-01-01T00:00:00Z" })).toString("base64url") + "." + real.split(".")[1];
assert.equal((await adm("/lookup", { key: longer })).valid, false, "expiry edited");
const unsigned = Buffer.from(JSON.stringify({ alg: "none", plan: "biz" })).toString("base64url") + ".";
assert.equal((await adm("/lookup", { key: unsigned })).valid, false, "no signature");
// ECDSA: (r, s) and (r, n - s) both verify. The server names a key by its exact text, so it only knows the
// spelling it issued; the app therefore checks both ids at start (License.KeyIds).
const N = 0xFFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551n;
const twin = key => {
  const [d, s] = key.split("."), sig = Buffer.from(s, "base64url");
  const t = Buffer.from((N - BigInt("0x" + sig.subarray(32).toString("hex"))).toString(16).padStart(64, "0"), "hex");
  return d + "." + Buffer.concat([sig.subarray(0, 32), t]).toString("base64url");
};
assert.notEqual(twin(real), real);
assert.ok(signedBy(twin(real), publicKey), "the twin signature verifies too");
assert.equal(twin(twin(real)), real);
await adm("/revoke", { id: kid(real), reason: "test" });
assert.equal(await revoked(real), true);
assert.equal(await revoked(twin(real)), false, "the server does not know the twin: the app must ask about both ids");
await adm("/unrevoke", { id: kid(real) });
assert.equal(await revoked(real), false);

// ------------------------------------------------------------ 3. webhooks: signature
const raw0 = rawHook("subscription_created", sub(1, V.biz_yearly), { custom: { session: "s".repeat(20) } });
assert.equal((await post(raw0)).http, 401, "no signature");
assert.equal((await post(raw0, "")).http, 401);
assert.equal((await post(raw0, hmac(raw0, "wrong-secret"))).http, 401, "wrong secret");
assert.equal((await post(raw0, hmac(raw0).slice(0, 63))).http, 401, "short signature");
assert.equal((await post(raw0, hmac(raw0).replace(/^./, c => c === "0" ? "1" : "0"))).http, 401, "one digit off");
assert.equal((await post(raw0, "z".repeat(64))).http, 401, "not hex");
const tampered = raw0.replace(V.biz_yearly, V.biz_monthly);
assert.equal((await post(tampered, hmac(raw0))).http, 401, "a signature for another body");
assert.equal((await post(raw0 + " ", hmac(raw0))).http, 401, "the body is checked byte for byte");
const oldSecret = env.LS_SIGNING_SECRET;
for (const s of [undefined, ""]) { env.LS_SIGNING_SECRET = s; assert.equal((await post(raw0, hmac(raw0, ""))).http, 401, "no secret set: nothing is accepted"); }
env.LS_SIGNING_SECRET = oldSecret;
assert.equal(keys(), 1, "no key from any of those");

// ------------------------------------------------------------ 4. webhooks: mode, product, repeats
let code = await cardOrder("monthly");
assert.equal((await hook("subscription_created", sub(10, V.monthly), { test: true, custom: { session: code } })).ignored, "sales mode live", "test events are refused in live mode");
await adm("/sales-mode", { mode: "test" });
assert.equal((await hook("subscription_created", sub(10, V.monthly), { custom: { session: code } })).ignored, "sales mode test", "real events are refused in test mode");
await adm("/sales-mode", { mode: "off" });
assert.equal((await hook("subscription_created", sub(10, V.monthly), { custom: { session: code } })).ignored, "sales mode off");
await adm("/sales-mode", { mode: "live" });
assert.equal(keys(), 1);
assert.equal((await hook("subscription_created", sub(11, "999"), { custom: { session: code } })).ignored, "unknown variant", "a product that is not ours");
assert.equal((await hook("subscription_created", sub(12, V.monthly, { status: "unpaid" }), { custom: { session: code } })).ignored, "status unpaid");
assert.equal((await j("/claim?session=" + code)).status, "pending");
assert.equal(keys(), 1);

// one subscription start, delivered three times: one key
const createdRaw = rawHook("subscription_created", sub(10, V.monthly), { custom: { session: code, plan: "biz_yearly" } });
assert.equal((await post(createdRaw, hmac(createdRaw))).http, 200);
assert.equal(keys(), 2);
const k10 = (await j("/claim?session=" + code)).key;
assert.equal(payload(k10).plan, "premium", "custom data cannot change the plan");
assert.equal(payload(k10).sub, "10");
assert.equal((await post(createdRaw, hmac(createdRaw))).ignored, "repeat");
assert.equal((await hook("subscription_created", sub(10, V.monthly))).ignored, "repeat");
assert.equal(keys(), 2, "a repeated subscription_created makes no second key");
assert.equal((await j("/claim?session=" + code)).key, k10);
// one renewal invoice, delivered twice: one key
const renewRaw = rawHook("subscription_payment_success", invoice(5001, 10));
await post(renewRaw, hmac(renewRaw));
assert.equal(keys(), 3);
const k10r = kv.get("sub:10");
assert.notEqual(k10r, k10);
assert.equal((await post(renewRaw, hmac(renewRaw))).ignored, "repeat");
assert.equal(keys(), 3, "a repeated renewal makes no second key");
assert.equal(kv.get("sub:10"), k10r);
// the first payment's invoice gives no key (subscription_created did)
assert.equal((await hook("subscription_payment_success", invoice(5000, 10, "initial"))).ignored, "first payment");
// an unknown subscription's renewal
assert.equal((await hook("subscription_payment_success", invoice(5002, 77))).ignored, "unknown variant");
assert.equal(keys(), 3);

// a buyer who switches to a cheaper product on the checkout page gets what they paid for, and the order says so
code = await cardOrder("ultra_yearly");
await hook("subscription_created", sub(13, V.monthly), { custom: { session: code } });
assert.equal(payload((await j("/claim?session=" + code)).key).plan, "premium");
let ord = (await adm("/orders?status=all")).orders.find(o => o.code === code);
assert.equal(ord.status, "approved");
assert.equal(ord.plan, "monthly", "the order records the paid plan, not the asked one");
assert.ok((await log()).some(l => l.action === "주문과 다른 상품으로 결제됨"));

// a card payment carrying a bank-transfer order's code does not approve that order
const manual = (await j("/order?plan=yearly&method=manual", { method: "POST" })).code;
await hook("subscription_created", sub(14, V.monthly), { custom: { session: manual } });
assert.equal((await j("/claim?session=" + manual)).status, "pending");
ord = (await adm("/orders?status=all")).orders.find(o => o.code === manual);
assert.equal(ord.status, "requested");
assert.equal(ord.method, "manual");
assert.ok(kv.get("sub:14"), "the paid key is still recorded for support");
assert.ok((await log()).some(l => l.action === "카드 주문이 아니라 자동 승인하지 않음"));

// ------------------------------------------------------------ 5. refunds and cancellations
// refund after a plan change: the first key and the plan-change key are both turned off
code = await cardOrder("monthly");
await hook("subscription_created", sub(20, V.monthly), { custom: { session: code } });
const k20 = (await j("/claim?session=" + code)).key;
await hook("subscription_updated", sub(20, V.ultra_monthly));
const k20u = kv.get("sub:20");
assert.equal(payload(k20u).plan, "ultra");
assert.equal(await revoked(k20), false);
await hook("order_refunded", { type: "orders", id: "9020", attributes: {} });
assert.equal(await revoked(k20), true, "refund: the first key");
assert.equal(await revoked(k20u), true, "refund: the plan-change key too");
assert.equal(kv.get("sub:20"), undefined, "no more renewals");
// refund that arrives after a cancellation
await hook("subscription_created", sub(21, V.yearly));
const k21 = kv.get("sub:21");
await hook("subscription_updated", sub(21, V.yearly, { status: "cancelled" }));
assert.equal(kv.get("sub:21"), undefined, "cancelled: no more renewals");
assert.equal((await renew(21, k21)).status, "inactive");
assert.equal(await revoked(k21), false, "cancelled: the paid period runs out on its own");
await hook("subscription_payment_refunded", { type: "subscription-invoices", id: "1", attributes: { subscription_id: 21 } });
assert.equal(await revoked(k21), true, "refund after cancellation");
// refund of a renewal: every key of the subscription
await hook("subscription_created", sub(22, V.monthly));
const k22 = kv.get("sub:22");
await hook("subscription_payment_success", invoice(5022, 22));
const k22r = kv.get("sub:22");
await hook("subscription_payment_refunded", { type: "subscription-invoices", id: "2", attributes: { subscription_id: 22 } });
assert.equal(await revoked(k22r), true);
assert.equal(await revoked(k22), true);
assert.deepEqual(cancels, ["20", "21", "22"], "each refunded subscription is cancelled at Lemon Squeezy, so it is not charged again");
// expiry, status: every subscription key carries its end
for (const k of [k10, k10r, k20, k21, k22]) assert.ok(payload(k).expires, "subscription keys expire");
// a test-mode key lasts a day
await adm("/sales-mode", { mode: "test" });
await hook("subscription_created", sub(23, V.biz_yearly), { test: true });
const k23 = payload(kv.get("sub:23"));
assert.equal(k23.test, true);
assert.ok((Date.parse(k23.expires) - Date.now()) / 864e5 < 1.01);
await adm("/sales-mode", { mode: "live" });

// ------------------------------------------------------------ 6. /renew needs a key of that subscription
assert.equal((await j("/renew?sub=10")).http, 403, "a bare subscription number gets nothing (what apps up to 0.8.11 send)");
assert.equal((await j(`/renew?sub=10&id=${kid(k10)}`)).http, 403, "a key id in the address is not a proof: ids travel in /check addresses");
assert.equal((await postJ("/renew", { sub: "10", id: kid(k10) })).http, 403, "nor in the body");
assert.equal((await renew(10, kv.get("sub:14"))).http, 403, "another subscription's key");
assert.equal((await renew(10, forged)).http, 403, "a forged key");
assert.equal((await renew(10, twin(k10))).http, 403, "the key with its twin signature is not the key that was issued");
assert.equal((await renew(10, k10.slice(0, -2))).http, 403, "a damaged key");
assert.equal((await renew(10, "x".repeat(5000))).http, 403);
let r = await renew(10, k10);
assert.equal(r.status, "ready");
assert.equal(r.key, k10r, "the holder gets the renewed key");
for (let n = 1; n <= 60; n++) assert.ok(!(await j("/renew?sub=" + n)).key, "walking subscription numbers gives no key: " + n);
assert.equal((await renew(10, "  " + k10 + "\n")).key, k10r, "blanks around the key are fine");
assert.equal((await postJ("/renew", { sub: "10" })).http, 403);
assert.equal((await postJ("/renew", { sub: 10, key: k10 })).key, k10r);
assert.equal((await postJ("/renew", "null")).http, 400);
await adm("/revoke", { id: kid(k10), reason: "공유" });
assert.equal((await renew(10, k10)).status, "inactive", "a revoked key renews nothing");

// ------------------------------------------------------------ 7. orders: the client cannot pay for itself
assert.equal((await j("/order?plan=yearly&method=manual&code=SB-AAAA-AAAA", { method: "POST" })).code === "SB-AAAA-AAAA", false, "the server picks the code");
const codes = new Set();
for (let i = 0; i < 40; i++) codes.add((await j("/order?plan=yearly&method=manual", { method: "POST" }, "10.4.0." + i)).code);   // 20 an hour per address
assert.equal(codes.size, 40);
for (const c of codes) assert.match(c, /^SB-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$/);
for (const p of ["constructor", "__proto__", "toString", "hasOwnProperty", "valueOf", ""])
  assert.equal((await j("/order?plan=" + p + "&method=manual", { method: "POST" })).http, 400, "plan " + p);
assert.equal((await j("/checkout?plan=constructor&session=" + "s".repeat(20), { method: "POST" })).http, 400);
assert.equal((await j("/order?plan=biz_yearly_x10&method=card", { method: "POST" })).http, 503, "bundles are never card checkouts");
// "I paid" is a note for the seller, not a payment
let n0 = keys();
const mine = (await j("/order?plan=yearly&method=manual", { method: "POST" })).code;
assert.equal((await j("/order/paid?code=" + mine, { method: "POST", body: JSON.stringify({ status: "approved", key: real, keyId: kid(real) }) })).status, "paid");
assert.equal((await j("/order/paid?code=" + mine.toLowerCase(), { method: "POST", body: JSON.stringify({ payer: "홍길동", msg: "<script>alert(1)</script>" }) })).status, "paid");
assert.equal((await adm("/orders?status=all")).orders.find(o => o.code === mine).keyId, undefined, "the body sets the payer note only");
// the memo is stored as text; the dashboard escapes it (admin.html esc())
assert.ok((await adm("/orders?status=all")).orders.find(o => o.code === mine).msg.includes("<script>"));
let c = await j("/claim?session=" + mine);
assert.equal(c.status, "pending");
assert.equal(c.order, "paid");
assert.equal(c.key, undefined);
assert.equal(keys(), n0, "marking an order paid issues nothing");
assert.equal((await j("/order/paid?code=" + mine, { method: "POST", body: "null" })).http, 200, "a null body is not a server error");
assert.equal((await j("/order/paid?code=SB-AAAA-AAAA", { method: "POST" })).http, 404);
assert.equal((await j("/order/paid?code=../ord:x", { method: "POST" })).http, 400);
// a card order is confirmed by Lemon Squeezy only
const cardCode = await cardOrder("monthly");
assert.equal((await j("/order/paid?code=" + cardCode, { method: "POST", body: JSON.stringify({ payer: "x" }) })).status, "requested");
assert.equal((await adm("/orders?status=all")).orders.find(o => o.code === cardCode).status, "requested", "an unpaid card order never shows as 결제 확인 요청");
// approving needs the admin token
clearFails();
assert.equal((await call("/admin/api/orders/approve", { method: "POST", body: JSON.stringify({ code: mine }) }, "10.2.0.1")).status, 401);
assert.equal((await call("/admin/api/orders/approve", { method: "POST", headers: { Authorization: "Bearer guess" }, body: JSON.stringify({ code: mine }) }, "10.2.0.2")).status, 401);
assert.equal((await j("/claim?session=" + mine)).status, "pending");
assert.equal(kv.get("session:" + mine), undefined);
clearFails();
// someone else's order: a guessed code finds nothing (40 random bits per code)
for (let i = 0; i < 50; i++) {
  const guess = "SB-" + [...Array(8)].map(() => "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"[Math.floor(Math.random() * 32)]).join("").replace(/^(.{4})/, "$1-");
  if (codes.has(guess) || guess === mine) continue;
  const g = await j("/claim?session=" + guess);
  assert.equal(g.key, undefined);
  assert.equal(g.order, "");
}
// the app's way: code in the body (POST /claim, POST /order/paid); apps up to 0.8.11 use the address
const other = (await j("/order?plan=ultra_monthly&method=manual", { method: "POST" })).code;
assert.equal((await postJ("/order/paid", { code: other, payer: "김철수", msg: "" })).status, "paid");
assert.equal((await postJ("/claim", { session: other })).order, "paid");
assert.equal((await postJ("/claim", { session: "../x" })).http, 400);
assert.equal((await postJ("/claim", "null")).http, 400);
assert.equal((await postJ("/claim", { session: { $ne: "" } })).http, 400);
// by design the order code is the claim ticket: whoever has it (the buyer's app, the payment memo) gets the key
const approved = await adm("/orders/approve", { code: mine });
assert.equal((await j("/claim?session=" + mine)).key, approved.key);
assert.equal((await postJ("/claim", { session: mine })).key, approved.key);
assert.equal((await adm("/orders/approve", { code: mine })).http, 409, "approved once");

// ------------------------------------------------------------ 8. request tampering and injection
for (const s of ["../../x", "ord:SB-AAAA-AAAA", "SB-AAAA-AAA", "a/b/c/d/e/f/g/h/i/j", "x".repeat(65), "%00" + "a".repeat(20)])
  assert.equal((await j("/claim?session=" + encodeURIComponent(s))).http, 400, "session " + s);
for (const id of ["../../x", "ABCDEF0123456789", "0123456789abcde", "0123456789abcdef0"]) assert.equal((await j("/check?id=" + id)).http, 400);
for (const s of ["1 OR 1=1", "-1", "1e3", "١٢"]) assert.equal((await renew(s, k10)).http, 400);
for (const body of ["null", "[]", "\"x\"", "{", "{\"code\":{\"$gt\":\"\"},\"device\":[1]}"]) {
  assert.equal((await j("/referral", { method: "POST", body })).http, 400, "referral body " + body);
  assert.notEqual((await call("/account/login", { method: "POST", body })).status, 500, "account body " + body);
}
assert.equal((await adm("/orders/approve", { code: "__proto__" })).http, 404);
assert.equal((await adm("/sales-mode", { mode: "constructor" })).http, 400);
assert.match((await adm("/issue", { plan: "constructor", note: "x" })).error, /플랜/);
assert.match((await adm("/issue", { plan: "toString_x5", note: "x" })).error, /플랜/);
assert.equal((await call("/admin/api/sales-mode", { method: "POST", headers: { Authorization: "Bearer " + TOKEN }, body: "null" })).status, 400);

// ------------------------------------------------------------ 9. referral gifts: what limits them
// The "device" is a random number the app makes, so a script can claim one gift per made-up device. What holds is
// the code's own limit and 300 gifts a day in all (design limit, reported).
await adm("/referrals", { code: "FRIEND7", days: 7, max: 3 });
const dev = () => createHash("md5").update(String(Math.random())).digest("hex");
for (let i = 0; i < 3; i++) assert.equal(payload((await j("/referral", { method: "POST", body: JSON.stringify({ code: "FRIEND7", device: dev() }) })).key).plan, "premium");
assert.equal((await j("/referral", { method: "POST", body: JSON.stringify({ code: "FRIEND7", device: dev() }) })).error, "used up");
await adm("/referrals", { code: "BIGCODE", days: 1, max: 10000 });
kv.set("count:ref:" + new Date().toISOString().slice(0, 10), "300");
assert.equal((await j("/referral", { method: "POST", body: JSON.stringify({ code: "BIGCODE", device: dev() }) })).error, "busy", "300 a day in all");

// ------------------------------------------------------------ 9b. sign-up: the discount ticket comes with a code only
env.SUPABASE_URL = "https://db.example"; env.SUPABASE_SECRET_KEY = "made-up-db-key";
const signup = b => j("/account/signup", { method: "POST", body: JSON.stringify({ password: "long-enough-pw", agree: true, age14: true, ...b }) });
dbCalls.length = 0;
assert.equal((await signup({ id: "plain1", ticket: true })).http, 200);
assert.equal((await signup({ id: "plain2", ticket: "yes", ticketPercent: 100, ticketDays: 3650 })).http, 200);
let ups = dbCalls.filter(x => x.action === "signup");
assert.equal(ups.length, 2);
for (const u of ups) {
  assert.equal(u.b.ticket, false, "no code, no ticket, whatever the request says");
  assert.equal(u.b.ticketPercent, 10, "the request cannot set the discount");
  assert.equal(u.b.ticketDays, 30);
}
await adm("/referrals", { code: "SHOPCODE", days: 3, max: 10 });
kv.delete("count:ref:" + new Date().toISOString().slice(0, 10));   // section 9 filled today's gifts
dbCalls.length = 0;
assert.equal((await signup({ id: "coded1", invite: "shopcode", device: dev() })).http, 200);
ups = dbCalls.filter(x => x.action === "signup");
assert.equal(ups[0].b.ticket, true, "a code still gives the ticket");
delete env.SUPABASE_URL; delete env.SUPABASE_SECRET_KEY;

// ------------------------------------------------------------ 10. admin lockout
clearFails();
for (let i = 0; i < 5; i++) assert.equal((await adm("/summary", null, "wrong" + i, "10.9.9.9")).http, 401);
assert.equal((await adm("/summary", null, TOKEN, "10.9.9.9")).http, 429, "5 misses lock that address, even for the right token");
assert.equal((await adm("/summary", null, TOKEN, "10.9.9.8")).http, 200, "another address still works");
clearFails();
for (let i = 0; i < 20; i++) await adm("/summary", null, "wrong", "10.8.0." + i);
assert.equal((await adm("/summary", null, TOKEN, "10.7.7.7")).http, 429, "20 misses an hour lock everyone (the owner too: a known trade-off)");
// Known weakness, reported: the counters are KV read-then-write, so tries sent at once are all checked.
clearFails();
const burst = await Promise.all([...Array(30)].map((_, i) => call("/admin/api/summary", { headers: { Authorization: "Bearer wrong" + i } }, "10.6.6.6")));
const checked = burst.filter(x => x.status === 401).length;
console.log(`  admin lockout: ${checked} of 30 simultaneous wrong tries from one address were checked (limit 5) - KV counters are not atomic`);
clearFails();

// ------------------------------------------------------------ 11. no secret is stored or sent back
const everything = JSON.stringify([...kv.entries()]) + JSON.stringify([...md.entries()]) + JSON.stringify(await adm("/card")) + JSON.stringify(await adm("/summary"));
for (const s of [SECRET, TOKEN, API_KEY, "PRIVATE KEY"]) assert.ok(!everything.includes(s), "secret leaked into KV or a reply");
console.log("licence security tests passed");
