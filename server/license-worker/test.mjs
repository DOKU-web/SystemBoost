// Local test: fake KV + signed webhook -> claim -> key; key verified later by .NET.
import worker from "./src/index.js";
import { createHmac } from "node:crypto";
import { readFileSync, writeFileSync } from "node:fs";
const kv = new Map(), md = new Map();
const env = {
  LICENSES: {
    get: async k => kv.get(k) ?? null,
    getWithMetadata: async k => ({ value: kv.get(k) ?? null, metadata: md.get(k) ?? null }),
    put: async (k, v, o) => { kv.set(k, v); md.set(k, o?.metadata ?? null); },
    delete: async k => { kv.delete(k); md.delete(k); },
    list: async ({ prefix, limit = 1000 }) => ({ keys: [...kv.keys()].filter(k => k.startsWith(prefix)).sort().slice(0, limit).map(name => ({ name, metadata: md.get(name) })), list_complete: true }),
  },
  LICENSE_PRIVATE_KEY: readFileSync(process.argv[2], "utf8"),
  LS_SIGNING_SECRET: "test-secret",
  SALES_MODE: "live",
};
const call = (path, init) => worker.fetch(new Request("https://lic.example" + path, init), env);
const session = "a1b2c3d4-e5f6-4711-8899-aabbccddeeff";
async function hook(body, secret = "test-secret") {
  const raw = JSON.stringify(body);
  const sig = createHmac("sha256", secret).update(raw).digest("hex");
  return call("/webhook/lemonsqueezy", { method: "POST", body: raw, headers: { "X-Signature": sig } });
}
let r = await (await call("/claim?session=" + session)).json(); console.log("before:", r.status);
let h = await hook({ meta: { event_name: "order_created", custom_data: { session, plan: "lifetime" } }, data: { id: "1001", attributes: { status: "paid", user_email: "buyer@example.com" } } }, "wrong");
console.log("forged webhook:", h.status);
h = await hook({ meta: { event_name: "order_created", custom_data: { session, plan: "lifetime" } }, data: { id: "1001", attributes: { status: "paid", user_email: "buyer@example.com" } } });
console.log("webhook:", h.status);
r = await (await call("/claim?session=" + session)).json(); console.log("after:", r.status);
writeFileSync("/tmp/lifetime.key", r.key);
const s2 = "ffffeeee-dddd-cccc-bbbb-aaaa99998888";
await hook({ meta: { event_name: "subscription_created", custom_data: { session: s2, plan: "monthly" } }, data: { id: "77", attributes: { status: "active", renews_at: "2026-12-05T07:00:00Z", user_email: "sub@example.com" } } });
r = await (await call("/claim?session=" + s2)).json(); writeFileSync("/tmp/sub.key", r.key);
r = await (await call("/renew?sub=77")).json(); console.log("renew:", r.status);
const t = await (await call("/thanks?session=" + session)).text(); console.log("thanks has key:", t.includes(r.key ? "" : "x") && t.includes("결제가 완료"));
console.log("bad session:", (await call("/claim?session=../../x")).status);
// checkout: mock the Lemon Squeezy API and check what we send
Object.assign(env, { LS_API_KEY: "k", LS_STORE_ID: "1", LS_VARIANT_MONTHLY: "11", PUBLIC_URL: "https://lic.example" });
let sent;
globalThis.fetch = async (u, init) => { sent = JSON.parse(init.body); return new Response(JSON.stringify({ data: { attributes: { url: "https://shop.example/checkout/abc" } } }), { status: 201 }); };
const co = await (await call("/checkout?plan=monthly&session=" + session, { method: "POST" })).json();
console.log("checkout url:", co.url, "| custom:", JSON.stringify(sent.data.attributes.checkout_data.custom), "| redirect:", sent.data.attributes.product_options.redirect_url);
console.log("bad plan:", (await call("/checkout?plan=free&session=" + session, { method: "POST" })).status);

// sales gate
const mk = (sess, test) => ({ meta: { event_name: "order_created", test_mode: test, custom_data: { session: sess, plan: "lifetime" } }, data: { id: "2002", attributes: { status: "paid", user_email: "g@example.com" } } });
const claimed = async sess => (await (await call("/claim?session=" + sess)).json()).status;
env.SALES_MODE = "off";
await hook(mk("off0off0-0000-0000-0000-000000000001", false));
console.log("off  : real order ->", await claimed("off0off0-0000-0000-0000-000000000001"), "| checkout ->", (await call("/checkout?plan=monthly&session=" + session, { method: "POST" })).status);
env.SALES_MODE = "live";
await hook(mk("live0000-0000-0000-0000-000000000002", true));
console.log("live : test order ->", await claimed("live0000-0000-0000-0000-000000000002"));
env.SALES_MODE = "test";
await hook(mk("test0000-0000-0000-0000-000000000003", false));
await hook(mk("test0000-0000-0000-0000-000000000004", true));
const tk = (await (await call("/claim?session=test0000-0000-0000-0000-000000000004")).json()).key;
console.log("test : real order ->", await claimed("test0000-0000-0000-0000-000000000003"), "| test order -> ready, payload:", Buffer.from(tk.split(".")[0], "base64url").toString());
delete env.SALES_MODE;
await hook(mk("unset000-0000-0000-0000-000000000005", false));
console.log("unset: real order ->", await claimed("unset000-0000-0000-0000-000000000005"));

// ---------------- admin dashboard + manual payments
const A = { "Content-Type": "application/json" };
const adm = (path, body, tok = "T".repeat(32)) => call("/admin/api" + path, { method: body ? "POST" : "GET", headers: { ...A, Authorization: "Bearer " + tok }, body: body ? JSON.stringify(body) : undefined });
console.log("admin off (no token):", (await call("/admin")).status);
env.ADMIN_TOKEN = "T".repeat(32);
const page = await call("/admin");
const pageText = await page.text();
console.log("admin page:", page.status, page.headers.get("content-security-policy") ? "csp" : "no-csp", pageText.includes("SystemBoost 관리자"), "| real mode:", pageText.includes("const SERVED = true;"));
console.log("wrong token:", (await adm("/summary", null, "nope")).status, "| no header:", (await call("/admin/api/summary")).status);
env.SALES_MODE = "off"; await env.LICENSES.delete("config:sales_mode");
console.log("status before links:", JSON.stringify(await (await call("/status")).json()));
console.log("order w/o link:", (await call("/order?plan=lifetime", { method: "POST" })).status);
console.log("bad link:", (await adm("/pay-links", { lifetime: "http://x" })).status);
await adm("/pay-links", { lifetime: "https://pay.example/sb-lifetime", monthly: "https://pay.example/sb-month" });
console.log("status:", JSON.stringify(await (await call("/status")).json()));
const od = await (await call("/order?plan=lifetime", { method: "POST" })).json();
console.log("order:", od.code, od.url, od.method, "| yearly (no link):", (await call("/order?plan=yearly", { method: "POST" })).status);
console.log("claim before:", JSON.stringify(await (await call("/claim?session=" + od.code)).json()));
console.log("buyer says paid:", JSON.stringify(await (await call("/order/paid?code=" + od.code, { method: "POST", body: JSON.stringify({ payer: "홍길동", msg: "토스 송금" }) })).json()));
let ol = await (await adm("/orders")).json();
console.log("dashboard open:", JSON.stringify(ol.orders.map(o => [o.code === od.code, o.plan, o.status, o.payer, o.msg])), "count:", JSON.stringify(ol.count));
// reject flow
const od2 = await (await call("/order?plan=monthly", { method: "POST" })).json();
await adm("/orders/reject", { code: od2.code, reason: "입금이 확인되지 않았어요" });
console.log("rejected claim:", JSON.stringify(await (await call("/claim?session=" + od2.code)).json()));
console.log("paid after reject:", JSON.stringify(await (await call("/order/paid?code=" + od2.code, { method: "POST", body: "{}" })).json()));
// approve
const iss = await (await adm("/orders/approve", { code: od.code, email: "Buyer2@example.com" })).json();
console.log("approved id:", iss.id, "| claim:", (await (await call("/claim?session=" + od.code)).json()).status);
console.log("approve again:", (await adm("/orders/approve", { code: od.code })).status, "| reject approved:", (await adm("/orders/reject", { code: od.code })).status);
ol = await (await adm("/orders?status=approved")).json();
console.log("approved list:", JSON.stringify(ol.orders.map(o => [o.status, o.keyId === iss.id, o.email])));
console.log("issue w/o code+note:", (await (await adm("/issue", { plan: "lifetime" })).json()).error);
writeFileSync("/tmp/manual.key", iss.key);
const m30 = await (await adm("/issue", { plan: "monthly", note: "리뷰용" })).json();
console.log("monthly payload:", Buffer.from(m30.key.split(".")[0], "base64url").toString());
const planOf = k => JSON.parse(Buffer.from(k.split(".")[0], "base64url").toString()).plan;
const ul = await (await adm("/issue", { plan: "lifetime", tier: "ultra", note: "울트라 시험" })).json();
const odd = await (await adm("/issue", { plan: "lifetime", tier: "gold", note: "모르는 등급" })).json();
console.log("tiers:", planOf(m30.key), planOf(ul.key), planOf(odd.key), "| listed:", (await (await adm("/keys?q=" + encodeURIComponent("울트라 시험"))).json()).keys[0].plan);
await adm("/pay-links", { lifetime: "https://pay.example/sb-lifetime", monthly: "https://pay.example/sb-month", ultra_monthly: "https://pay.example/sb-ultra-month", ultra_lifetime: "https://pay.example/sb-ultra" });
console.log("status with ultra:", JSON.stringify((await (await call("/status")).json()).methods.manual));
const uo = await (await call("/order?plan=ultra_lifetime", { method: "POST" })).json();
const ua = await (await adm("/orders/approve", { code: uo.code, email: "ultra@example.com" })).json();
const uom = await (await call("/order?plan=ultra_monthly", { method: "POST" })).json();
const uam = await (await adm("/orders/approve", { code: uom.code, email: "ultra2@example.com" })).json();
console.log("ultra orders:", uo.url, uo.method, planOf(ua.key), "|", uom.url, planOf(uam.key), "expires:", !!JSON.parse(Buffer.from(uam.key.split(".")[0], "base64url").toString()).expires,
            "| ultra yearly (no link):", (await call("/order?plan=ultra_yearly", { method: "POST" })).status, "| bad plan:", (await call("/order?plan=ultra_days", { method: "POST" })).status);
const sm = await (await adm("/summary")).json(); console.log("summary:", JSON.stringify(sm.counts), sm.sales);
const ks = await (await adm("/keys?q=buyer2")).json(); console.log("search buyer2:", ks.keys.length, ks.keys[0].state, ks.keys[0].src);
console.log("check before:", JSON.stringify(await (await call("/check?id=" + iss.id)).json()));
await adm("/revoke", { id: iss.id, reason: "환불" });
console.log("check after revoke:", JSON.stringify(await (await call("/check?id=" + iss.id)).json()), "| list state:", (await (await adm("/keys?status=revoked")).json()).keys.map(k => k.id === iss.id).join());
await adm("/unrevoke", { id: iss.id });
console.log("check after unrevoke:", JSON.stringify(await (await call("/check?id=" + iss.id)).json()));
const lk = await (await adm("/lookup", { key: iss.key })).json(); console.log("lookup valid:", lk.valid, lk.registered);
const forged = Buffer.from('{"plan":"premium","email":"x"}').toString("base64url") + "." + iss.key.split(".")[1];
console.log("lookup forged:", (await (await adm("/lookup", { key: forged })).json()).valid);
console.log("key copy:", (await (await adm("/key?id=" + iss.id)).json()).key === iss.key);
// refund webhook revokes the order key (registry from the earlier lifetime order 1001)
await hook({ meta: { event_name: "order_refunded" }, data: { id: "1001", attributes: {} } });
const id1001 = kv.get("order:1001");
console.log("refund -> revoked:", JSON.stringify(await (await call("/check?id=" + id1001)).json()));
console.log("log:", (await (await adm("/log")).json()).log.map(l => l.action).join(", "));
await adm("/sales-mode", { mode: "test" }); console.log("mode via dashboard:", (await (await call("/status")).json()).sales);
// automatic payments: sales mode test + LS variants -> /order makes a checkout, verified webhook approves
Object.assign(env, { LS_VARIANT_LIFETIME: "33" });
await adm("/sales-mode", { mode: "test" });
globalThis.fetch = async (u, init) => { sent = JSON.parse(init.body); return new Response(JSON.stringify({ data: { attributes: { url: "https://shop.example/checkout/auto" } } }), { status: 201 }); };
console.log("auto status:", JSON.stringify(await (await call("/status")).json()));
const ao = await (await call("/order?plan=lifetime", { method: "POST" })).json();
console.log("both open:", JSON.stringify((await (await call("/status")).json()).methods), "| manual still:", (await (await call("/order?plan=lifetime&method=manual", { method: "POST" })).json()).method);
console.log("auto order:", ao.method, ao.url, "| LS custom session = code:", sent.data.attributes.checkout_data.custom.session === ao.code);
await hook({ meta: { event_name: "order_created", test_mode: true, custom_data: { session: ao.code, plan: "lifetime" } }, data: { id: "3003", attributes: { status: "paid", user_email: "auto@example.com" } } });
console.log("auto claim:", (await (await call("/claim?session=" + ao.code)).json()).status,
            "| order:", JSON.stringify((await (await adm("/orders?status=approved")).json()).orders.filter(o => o.code === ao.code).map(o => [o.status, o.method, o.email])));
console.log("card w/o variant:", (await call("/order?plan=yearly&method=card", { method: "POST" })).status);
// lockout: 5 wrong PINs from one IP -> even the right one is refused for a while
const pin = (tok, ip) => call("/admin/api/summary", { headers: { Authorization: "Bearer " + tok, "CF-Connecting-IP": ip } });
for (let i = 0; i < 5; i++) await pin("000000", "9.9.9.9");
console.log("after 5 misses, right PIN from same IP:", (await pin("T".repeat(32), "9.9.9.9")).status, "| other IP:", (await pin("T".repeat(32), "8.8.8.8")).status);
console.log("admin api CORS header:", (await adm("/summary")).headers.get("access-control-allow-origin"));
