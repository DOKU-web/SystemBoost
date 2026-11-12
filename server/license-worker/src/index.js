import { ADMIN_HTML } from "./admin_page.js";
import { INVITE_DAYS, accounts } from "./accounts.js";
import { announce } from "./announce.js";
import { cleanPing } from "./stats.js";
import { helpPage, privacyPage, support, supportAdmin, supportCounts, webhookRefund } from "./support.js";
export { Stats } from "./stats.js";
// SystemBoost license server (Cloudflare Workers, free plan is enough).
//
//   POST /checkout?plan=monthly|yearly&session=<id>
//                                creates a Lemon Squeezy checkout carrying the session -> {url}
//   POST /webhook/lemonsqueezy   Lemon Squeezy webhook (X-Signature HMAC-SHA256 checked)
//   GET  /claim?session=<id>     app polls this after opening checkout -> {status, key, keys?} (keys: a bundle)
//   GET  /thanks?session=<id>    checkout "return to" page that shows the key
//   GET  /renew?sub=<id>         latest key of an active subscription (app renews before expiry)
//   GET  /status                 {sales, pay:{monthly,yearly}} - the app enables "결제하기" only for plans
//                                that have a payment link (links are set in the dashboard)
//   POST /order?plan=…           app gets an order code (SB-XXXX-XXXX) to write in the payment memo;
//                                the owner confirms the payment and issues the key for that code in /admin,
//                                and the app (polling /claim?session=<code>) pops it up
//   GET  /check?id=<key id>      {revoked} - the app asks this at start (offline = keeps working)
//   POST /ping                   usage statistics from a running app -> {next: seconds until the next ping}
//   /support/*, GET /help        customer support and refund requests (src/support.js, src/help_page.js)
//   GET  /privacy                the privacy policy (?lang=en: an English summary), src/privacy_page.js
//   GET  /admin                  owner dashboard (HTML); its API is /admin/api/* with
//                                "Authorization: Bearer <ADMIN_TOKEN>"
//
// Keys use the same format as tools/license_tool.py and are checked in the app
// with the public key in License.cs:  base64url(payload JSON) "." base64url(r||s)
// (ECDSA P-256 / SHA-256, IEEE P1363 = what WebCrypto produces and .NET expects).
//
// Secrets (wrangler secret put ...):
//   LICENSE_PRIVATE_KEY   PKCS#8 private key, base64 (the body of systemboost-license-private.pem)
//   LS_SIGNING_SECRET     Lemon Squeezy webhook signing secret
//   LS_API_KEY            Lemon Squeezy API key (for /checkout)
//   ADMIN_TOKEN           dashboard password, 6+ characters (unset = dashboard off; wrong tries lock out)
// Vars (wrangler.toml [vars]): SALES_MODE ("off" | "test" | "live"), PUBLIC_URL. The Lemon Squeezy store and product
// numbers are entered in the dashboard (the old LS_STORE_ID / LS_VARIANT_* vars are read when it has none).
// KV namespace binding: LICENSES. Durable Object binding: STATS (stats.js: usage numbers and the support feed).
// Cron: hourly download counts.

// SALES_MODE gate (default "off" = no checkout, no keys, whatever arrives):
//   off  -> /checkout 503, webhooks acknowledged but ignored
//   test -> only Lemon Squeezy *test mode* orders get keys, and those expire in 24 h
//   live -> only real (non-test) orders get keys
// The dashboard can override it (KV "config:sales_mode"); otherwise wrangler.toml decides.
const MODES = ["off", "test", "live"];
async function salesMode(env) {
  const o = await env.LICENSES.get("config:sales_mode");
  if (MODES.includes(o)) return o;
  return ["test", "live"].includes(env.SALES_MODE) ? env.SALES_MODE : "off";
}
// Card subscriptions renew on their own. The licence agreement takes that in from 2026-12-08 KST
// (clause 3.4), announced 30 days ahead as its clause 13 requires, so the app offers live card
// checkouts from then on. Test mode is open before. Tests set CARD_LIVE_FROM.
const CARD_LIVE_FROM = "2026-12-07T15:00:00Z";
const cardLiveOpen = env => Date.now() >= Date.parse(env.CARD_LIVE_FROM || CARD_LIVE_FROM);
async function cardOpen(env) {
  const mode = await salesMode(env);
  return mode === "test" || (mode === "live" && cardLiveOpen(env));
}

const CODE_RE = /^SB-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$/;   // no 0/O/1/I: easy to read aloud
function validSession(s) { return /^[A-Za-z0-9-]{16,64}$/.test(s) || CODE_RE.test(s); }
// On sale. Lifetime is no longer sold; keys and orders from before keep working.
// A plan id is [tier_]period[_xN]: the tier is what the key opens (premium, ultra, biz), the period how long it
// lasts, and _xN a bundle of N yearly keys, one per PC, sold at a discount by manual payment only.
const PLANS = { monthly: "프로 월간", yearly: "프로 연간", ultra_monthly: "울트라 월간", ultra_yearly: "울트라 연간",
                biz_monthly: "산업 월간", biz_yearly: "산업 연간",
                yearly_x5: "프로 연간 5대 묶음", yearly_x10: "프로 연간 10대 묶음",
                ultra_yearly_x5: "울트라 연간 5대 묶음", ultra_yearly_x10: "울트라 연간 10대 묶음",
                biz_yearly_x5: "산업 연간 5대 묶음", biz_yearly_x10: "산업 연간 10대 묶음" };
const PLAN_DAYS = { monthly: 31, yearly: 366 };            // manual subscription keys (+3 days grace)
const TIERS = ["premium", "ultra", "biz"];                 // a key's "plan"
const TIER_KO = { premium: "프로", ultra: "울트라", biz: "산업" };
const tierOf = plan => /^ultra_/.test(plan) ? "ultra" : /^biz_/.test(plan) ? "biz" : "premium";
const seatsOf = plan => Number((/_x(\d+)$/.exec(plan) || [0, 1])[1]);
const periodOf = plan => plan.replace(/^(ultra|biz)_/, "").replace(/_x\d+$/, "");
const SINGLE_PLANS = Object.keys(PLANS).filter(p => seatsOf(p) === 1);
// Bundles are in the licence agreement from its 2026-12-09 KST revision (clauses 2.1, 3.5, 3.9), announced
// 30 days ahead as its clause 13 requires, so the app offers them from then on. Tests set BUNDLE_FROM.
const BUNDLE_FROM = "2026-12-08T15:00:00Z";
const bundleOpen = env => Date.now() >= Date.parse(env.BUNDLE_FROM || BUNDLE_FROM);
const MAX_SEATS = 20;                                      // keys one issue may make

// A fixed-price payment link cannot take a discount, so a discount ticket has its own link for each plan.
// Bundles are discounted already and take no ticket.
const LINK_KEYS = Object.keys(PLANS).flatMap(p => seatsOf(p) > 1 ? [p] : [p, p + "_ticket"]);
const linkLabel = p => PLANS[p] || PLANS[p.replace(/_ticket$/, "")] + " 할인";

async function payLinks(env) {
  try { const o = JSON.parse((await env.LICENSES.get("config:pay_urls")) || "{}"); 
        return Object.fromEntries(LINK_KEYS.filter(p => /^https:\/\/\S+$/.test(o[p] || "")).map(p => [p, o[p]])); }
  catch { return {}; }
}

// ---------------------------------------------------------------- orders
// Every purchase starts as an order with a code (SB-XXXX-XXXX):
//   requested  신청 중          app opened the payment page
//   paid       결제 확인 요청    buyer pressed "결제했어요" (manual payments: name/memo for matching)
//   approved   승인            key issued (by the seller in /admin, or automatically by a verified webhook)
//   rejected   거절            seller rejected it; the app shows the reason
//   refunded   환불            money paid back: "환불 완료" on a refund request, or a card refund webhook that
//                              closed one (src/support.js); the order's keys are blocked
// Two payment methods can be open at the same time; the buyer picks one in the app:
//   manual = seller's own payment links (bank transfer etc.), the seller approves in /admin
//   card   = Lemon Squeezy card checkout (sales mode test/live). Monthly/yearly are card subscriptions:
//            the card is saved and charged every period, renewals refresh the key automatically.
//            The verified payment webhook approves the order and issues the key.
const ORDER_TTL = 60 * 60 * 24 * 90;
const ORDER_STATES = ["requested", "paid", "approved", "rejected", "refunded"];
async function getOrder(env, code) {
  const r = await env.LICENSES.getWithMetadata("ord:" + code);
  return r && r.value !== null && r.metadata ? { code, ...r.metadata } : null;
}
async function putOrder(env, o) {
  const { code, ...m } = o;
  m.updated = now();
  await env.LICENSES.put("ord:" + code, "", { expirationTtl: ORDER_TTL, metadata: m });
}
// Lemon Squeezy: the store and the four product variants are set in the dashboard (KV "config:ls"; the old
// wrangler.toml vars still count when the dashboard has none). The API key and the webhook signing secret are
// Worker secrets that only the owner types in (`wrangler secret put`).
const ID_RE = /^\d{1,12}$/;
async function lsConfig(env) {
  let o = {};
  try { o = JSON.parse((await env.LICENSES.get("config:ls")) || "{}"); } catch {}
  const id = v => ID_RE.test(String(v ?? "")) ? String(v) : "";
  return { store: id(o.store) || id(env.LS_STORE_ID),
           variants: Object.fromEntries(SINGLE_PLANS.map(p => [p, id(o.variants?.[p]) || id(env["LS_VARIANT_" + p.toUpperCase()])])) };
}
async function cardPlans(env) {   // plans a card checkout can be made for
  const c = await lsConfig(env);
  return c.store && env.LS_API_KEY ? SINGLE_PLANS.filter(p => c.variants[p]) : [];
}
async function openPlans(env) {   // plans the app may sell right now, per method
  return { methods: { card: (await cardOpen(env)) ? await cardPlans(env) : [],
                      manual: Object.keys(await payLinks(env)).filter(p => PLANS[p] && (seatsOf(p) === 1 || bundleOpen(env))) } };
}

async function order(req, url, env) {
  const plan = url.searchParams.get("plan");
  if (!PLANS[plan]) return json({ error: "bad plan" }, 400);
  const { methods } = await openPlans(env);
  let method = url.searchParams.get("method");
  if (!method) method = methods.card.includes(plan) ? "card" : "manual";
  if (!["card", "manual"].includes(method) || !methods[method].includes(plan)) return json({ error: "sales closed" }, 503);
  const n = Number((await env.LICENSES.get("count:orders:" + now().slice(0, 10))) || 0);
  if (n >= 300) return json({ error: "busy" }, 429);   // cheap cap on junk orders per day
  await env.LICENSES.put("count:orders:" + now().slice(0, 10), String(n + 1), { expirationTtl: 60 * 60 * 48 });
  const A = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789", r = crypto.getRandomValues(new Uint8Array(8));
  const c = [...r].map(b => A[b % A.length]).join("");
  const code = "SB-" + c.slice(0, 4) + "-" + c.slice(4);
  let payUrl, ticket = 0;
  if (method === "card") { payUrl = await lsCheckout(env, plan, code); if (!payUrl) return json({ error: "checkout failed" }, 502); }
  else if (url.searchParams.get("ticket") === "1") {
    payUrl = (await payLinks(env))[plan + "_ticket"];
    if (!payUrl) return json({ error: "ticket link" }, 409);
    const t = await accounts(env, "ticket", { token: (req.headers.get("Authorization") || "").replace(/^Bearer /, ""), order: code });
    if (t.error) return json({ error: "ticket" }, 409);
    ticket = t.percent;
  }
  else payUrl = (await payLinks(env))[plan];
  await putOrder(env, { code, plan, at: now(), status: "requested", method, ...(ticket ? { ticket } : {}) });
  return json({ code, url: payUrl, method });
}

// Buyer says "I paid" (manual payments). Name / memo help the seller find the payment.
async function orderPaid(req, url, env) {
  const code = (url.searchParams.get("code") || "").toUpperCase();
  if (!CODE_RE.test(code)) return json({ error: "bad code" }, 400);
  const o = await getOrder(env, code);
  if (!o) return json({ error: "no order" }, 404);
  if (o.status !== "requested" && o.status !== "paid") return json({ status: o.status });
  const b = await req.json().catch(() => ({}));
  o.status = "paid";
  o.payer = String(b.payer || "").trim().slice(0, 40);
  o.msg = String(b.msg || "").trim().slice(0, 100);
  o.paidAt = now();
  await putOrder(env, o);
  return json({ status: "paid" });
}

const SESSION_TTL = 60 * 60 * 24 * 7;   // claim within 7 days
const GRACE_DAYS = 3;                   // subscription key stays valid a bit past renewal

// What src/support.js uses from here (passed in, so the two files do not import each other).
// events: the support feed in the Stats Durable Object (stats.js), so the dashboard sees what buyers do.
const SUPPORT = { getOrder, putOrder, revoke, audit, accounts, verifySig, unb64url, allRecords, state, keyId, events: (env, path, body) => stats(env, path, body) };

export default {
  async fetch(req, env, ctx) {
    const url = new URL(req.url);
    try {
      if (req.method === "POST" && url.pathname === "/checkout") return await checkout(url, env);
      if (req.method === "POST" && url.pathname === "/webhook/lemonsqueezy") return await webhook(req, env);
      if (req.method === "GET" && url.pathname === "/claim") return await claim(url, env);
      if (req.method === "GET" && url.pathname === "/thanks") return await thanks(url, env);
      if (req.method === "GET" && url.pathname === "/renew") return await renew(url, env);
      if (req.method === "GET" && url.pathname === "/status") return json({ sales: await salesMode(env), ...(await openPlans(env)) });
      if (req.method === "POST" && url.pathname === "/order") return await order(req, url, env);
      if (req.method === "POST" && url.pathname === "/order/paid") return await orderPaid(req, url, env);
      if (req.method === "GET" && url.pathname === "/check") return await check(url, env);
      if (req.method === "POST" && url.pathname === "/ping") return await ping(req, env);
      if (req.method === "POST" && url.pathname === "/referral") return await referral(req, env);
      if (url.pathname.startsWith("/account/")) return await account(req, url, env);
      if (url.pathname.startsWith("/support/")) return await support(req, url, env, SUPPORT, ctx);
      if (req.method === "GET" && url.pathname === "/help") return helpPage(url);
      if (req.method === "GET" && url.pathname === "/privacy") return await privacyPage(url, env);
      if (url.pathname === "/admin" || url.pathname.startsWith("/admin/")) return await admin(req, url, env);
      return json({ error: "not found" }, 404);
    } catch (e) {
      return json({ error: "server error" }, 500);
    }
  },
  async scheduled(event, env, ctx) {
    ctx.waitUntil(stats(env, "/tick"));
    ctx.waitUntil(accounts(env, "ping", {}));
  },
};

// ---------------------------------------------------------------- usage statistics
async function stats(env, path, body) {
  const object = env.STATS.get(env.STATS.idFromName("all"));
  const res = await object.fetch("https://stats" + path, body === undefined ? undefined : { method: "POST", body: JSON.stringify(body) });
  if (!res.ok) throw new Error("stats " + res.status);
  return res.json();
}
async function ping(req, env) {
  const p = cleanPing(await req.json().catch(() => null), req.cf?.country);
  return p ? json(await stats(env, "/ping", p)) : json({ error: "bad ping" }, 400);
}

// ---------------------------------------------------------------- checkout (automatic payments)
async function checkout(url, env) {   // kept for direct use; the app goes through /order
  if (!(await cardOpen(env))) return json({ error: "sales closed" }, 503);
  const plan = url.searchParams.get("plan");
  const session = url.searchParams.get("session") || "";
  if (!PLANS[plan] || !validSession(session)) return json({ error: "bad request" }, 400);
  const u = await lsCheckout(env, plan, session);
  return u ? json({ url: u }) : json({ error: "checkout failed" }, 502);
}
async function lsCheckout(env, plan, session) {
  const c = await lsConfig(env), variant = c.variants[plan];
  if (!variant || !c.store || !env.LS_API_KEY) return null;
  let res;
  try {
    res = await fetch("https://api.lemonsqueezy.com/v1/checkouts", {
      method: "POST",
      headers: { Authorization: "Bearer " + env.LS_API_KEY, Accept: "application/vnd.api+json", "Content-Type": "application/vnd.api+json" },
      body: JSON.stringify({ data: {
        type: "checkouts",
        attributes: {
          checkout_data: { custom: { session } },   // only to find the buyer's app again; what was bought comes from the variant
          product_options: { redirect_url: `${env.PUBLIC_URL}/thanks?session=${session}` },
        },
        relationships: {
          store: { data: { type: "stores", id: c.store } },
          variant: { data: { type: "variants", id: variant } },
        },
      } }),
    });
  } catch { return null; }
  if (!res.ok) { await audit(env, "카드 결제", "결제창을 만들지 못함", PLANS[plan] + " · Lemon Squeezy " + res.status); return null; }
  const j = await res.json().catch(() => null);
  return j?.data?.attributes?.url || null;
}

// ---------------------------------------------------------------- webhook
async function webhook(req, env) {
  const raw = await req.text();
  if (!(await validSignature(raw, req.headers.get("X-Signature") || "", env.LS_SIGNING_SECRET)))
    return json({ error: "bad signature" }, 401);
  let body;
  try { body = JSON.parse(raw); } catch { return json({ error: "bad json" }, 400); }
  const event = body?.meta?.event_name;
  const testOrder = body?.meta?.test_mode === true;
  const a = body?.data?.attributes || {};
  // refunds are handled in every mode: the key of that order, or of that subscription, is revoked
  // and an open refund request naming that key is marked done (src/support.js)
  if (event === "order_refunded") {
    const id = await env.LICENSES.get("order:" + String(body?.data?.id));
    if (id) { await revoke(env, id, "환불", "webhook"); await webhookRefund(env, id, SUPPORT); }
    return json({ ok: true });
  }
  if (event === "subscription_payment_refunded") {
    const key = await env.LICENSES.get("sub:" + String(a.subscription_id));
    if (key) { const id = await keyId(key); await revoke(env, id, "환불", "webhook"); await webhookRefund(env, id, SUPPORT); }
    return json({ ok: true });
  }
  const mode = await salesMode(env);
  if (mode === "off" || (mode === "live" && testOrder) || (mode === "test" && !testOrder))
    return json({ ok: true, ignored: "sales mode " + mode });   // 200 so Lemon Squeezy does not retry
  if (!["subscription_created", "subscription_updated", "subscription_payment_success"].includes(event)) return json({ ok: true, ignored: event });

  // Subscriptions only: start, plan change and renewal each give a key that lasts the paid period.
  // What was bought is the variant, which Lemon Squeezy reports itself. Custom data rides on the checkout
  // address and a buyer can change it, so it only says which app to hand the key to.
  const custom = body?.meta?.custom_data || {};
  const session = typeof custom.session === "string" && validSession(custom.session) ? custom.session : null;
  const invoice = event === "subscription_payment_success";
  const subId = String(invoice ? a.subscription_id : body?.data?.id);
  if (!/^\d{1,20}$/.test(subId)) return json({ ok: true, ignored: "no subscription" });
  const variants = (await lsConfig(env)).variants;
  const byVariant = a.variant_id == null ? null : Object.keys(variants).find(p => variants[p] === String(a.variant_id)) || null;
  const known = await env.LICENSES.get("subplan:" + subId);
  if (event === "subscription_updated") {
    if (!["active", "on_trial"].includes(a.status)) {   // cancelled, paused, unpaid, expired: no more renewals
      await env.LICENSES.delete("sub:" + subId);
      return json({ ok: true });
    }
    // nothing the key carries has changed, or subscription_created has not arrived yet and will give the key
    if (!byVariant || !known || byVariant === known) return json({ ok: true });
  }
  if (invoice && a.billing_reason !== "renewal") return json({ ok: true, ignored: "first payment" });   // subscription_created gave that key
  if (event === "subscription_created" && !["active", "on_trial"].includes(a.status)) return json({ ok: true, ignored: "status " + a.status });
  const plan = invoice ? known : byVariant;
  if (!PLANS[plan]) {
    await audit(env, "카드 결제", "어떤 플랜인지 몰라서 키를 만들지 않음", "구독 " + subId + " · 상품 " + String(a.variant_id ?? "?"));
    return json({ ok: true, ignored: "unknown variant" });
  }
  const period = plan.endsWith("yearly") ? 366 : 31;
  let expires = !invoice && a.renews_at ? addDays(a.renews_at, GRACE_DAYS) : addDays(now(), period + GRACE_DAYS);
  if (testOrder) expires = addDays(now(), 1);   // test keys never last
  const what = { subscription_created: "카드 결제", subscription_updated: "플랜 변경", subscription_payment_success: "자동 갱신" }[event];
  const key = await makeKey(env, { plan: tierOf(plan), email: a.user_email || "", sub: subId, expires, test: testOrder,
                                   src: "subscription", note: [session && event === "subscription_created" ? session : "", PLANS[plan] + " " + what].filter(Boolean).join(" · ") });
  await env.LICENSES.put("sub:" + subId, key);
  await env.LICENSES.put("subplan:" + subId, plan);
  if (event === "subscription_created") {
    if (a.order_id != null) await env.LICENSES.put("order:" + String(a.order_id), await keyId(key));   // a refund of the first payment
    await store(env, session, key, a.user_email, plan);
  } else {
    if (a.user_email) await env.LICENSES.put("email:" + String(a.user_email).toLowerCase(), key);
    await audit(env, "카드 결제", what, "구독 " + subId + " · " + PLANS[plan] + " · " + (await keyId(key)));
  }
  return json({ ok: true });
}

async function store(env, session, key, email, plan) {
  if (session) await env.LICENSES.put("session:" + session, key, { expirationTtl: SESSION_TTL });
  if (session && CODE_RE.test(session)) {   // verified payment -> order approved automatically
    const o = (await getOrder(env, session)) || { code: session, plan, at: now() };
    Object.assign(o, { status: "approved", method: "card", plan: o.plan || plan, keyId: await keyId(key), email: (email || "").slice(0, 80), approvedAt: now() });
    await putOrder(env, o);
    await audit(env, "카드 결제", "자동 승인", session + " · " + o.keyId + (email ? " · " + email : ""));
  }
  if (email) await env.LICENSES.put("email:" + email.toLowerCase(), key);   // for support / restore
}

// ---------------------------------------------------------------- app endpoints
async function claim(url, env) {
  const s = url.searchParams.get("session") || "";
  if (!validSession(s)) return json({ error: "bad session" }, 400);
  const key = await env.LICENSES.get("session:" + s);
  if (key) {   // a bundle also lists every key; the app registers the first one on this PC
    const keys = CODE_RE.test(s) ? JSON.parse((await env.LICENSES.get("bundle:" + s)) || "null") : null;
    return json({ status: "ready", key, ...(Array.isArray(keys) && keys.length > 1 ? { keys } : {}) });
  }
  const o = CODE_RE.test(s) ? await getOrder(env, s) : null;
  if (o?.status === "rejected") return json({ status: "rejected", reason: o.reason || "" });
  return json({ status: "pending", order: o?.status || "" });
}

async function renew(url, env) {
  const sub = url.searchParams.get("sub") || "";
  if (!/^[0-9]{1,20}$/.test(sub)) return json({ error: "bad sub" }, 400);
  const key = await env.LICENSES.get("sub:" + sub);
  return json(key ? { status: "ready", key } : { status: "inactive" });
}

async function thanks(url, env) {
  const s = url.searchParams.get("session") || "";
  const key = validSession(s) ? await env.LICENSES.get("session:" + s) : null;
  const body = key
    ? `<h1>결제가 완료됐어요</h1><p>SystemBoost가 켜져 있으면 키가 자동으로 등록돼요. 아니라면 아래 키를 복사해 '★ 플랜 → 라이선스 키'(0.8.9 이하는 '★ 프리미엄')에 넣으세요.</p>
       <textarea readonly onclick="this.select()">${escapeHtml(key)}</textarea><p class="m">같은 키가 결제한 메일 주소로도 저장돼 있어요. 키를 잃어버리면 문의해 주세요.</p>`
    : `<h1>결제를 확인하는 중이에요</h1><p>보통 몇 초 걸려요. 이 페이지는 자동으로 새로고침돼요.</p><script>setTimeout(()=>location.reload(),3000)</script>`;
  return new Response(`<!DOCTYPE html><html lang="ko"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>SystemBoost</title><style>body{background:#0b0b0c;color:#e8e6e3;font-family:system-ui,sans-serif;max-width:640px;margin:60px auto;padding:0 20px;line-height:1.6}
h1{color:#ff7a1a}textarea{width:100%;height:110px;background:#141416;color:#e8e6e3;border:1px solid #333;border-radius:10px;padding:12px;font-family:monospace}.m{color:#8b8a88;font-size:14px}</style>
<body>${body}</body></html>`, { headers: { "content-type": "text/html; charset=utf-8", "cache-control": "no-store" } });
}

// ---------------------------------------------------------------- key registry
// Every key the server signs is recorded as "lic:<id>" (id = first 16 hex of SHA-256 of the key text,
// the app computes the same). Metadata keeps the list cheap: one KV list call, no per-key reads.
async function keyId(key) {
  const h = new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(key.trim())));
  return [...h.slice(0, 8)].map(b => b.toString(16).padStart(2, "0")).join("");
}
function meta(p, src, note, revoked = "") {
  return { plan: p.plan, email: (p.email || "").slice(0, 120), issued: p.issued, expires: p.expires || "", test: !!p.test,
           sub: p.sub || "", order: p.order || "", src, note: note.slice(0, 200), revoked: revoked.slice(0, 200) };
}
async function register(env, key, payload, src, note) {
  const id = await keyId(key);
  await env.LICENSES.put("lic:" + id, key, { metadata: meta(payload, src, note) });
  if (payload.order) await env.LICENSES.put("order:" + payload.order, id);
  return id;
}
async function revoke(env, id, reason, who) {
  await env.LICENSES.put("revoked:" + id, reason || "revoked");
  const r = await env.LICENSES.getWithMetadata("lic:" + id);
  if (r?.value) {
    const m = r.metadata || {};
    m.revoked = (reason || "revoked").slice(0, 200);
    await env.LICENSES.put("lic:" + id, r.value, { metadata: m });
    if (m.sub) await env.LICENSES.delete("sub:" + m.sub);       // no more renewals for that subscription
  }
  await audit(env, who, "차단", id + (reason ? " · " + reason : ""));
}
async function check(url, env) {
  const id = url.searchParams.get("id") || "";
  if (!/^[0-9a-f]{16}$/.test(id)) return json({ error: "bad id" }, 400);
  return json({ revoked: (await env.LICENSES.get("revoked:" + id)) !== null });
}
async function audit(env, who, action, detail) {
  const inv = String(9999999999999 - Date.now()).padStart(13, "0");   // newest first in KV order
  await env.LICENSES.put("log:" + inv + Math.random().toString(36).slice(2, 6), JSON.stringify({ at: now(), who, action, detail: String(detail).slice(0, 300) }),
                         { expirationTtl: 60 * 60 * 24 * 180 });
}

// ---------------------------------------------------------------- admin dashboard
async function admin(req, url, env) {
  if (!env.ADMIN_TOKEN || env.ADMIN_TOKEN.length < 6) return json({ error: "not found" }, 404);   // off unless configured
  if (url.pathname === "/admin" && req.method === "GET")
    return new Response(ADMIN_HTML.replace("/*SB_SERVED*/false", "true"), { headers: { "content-type": "text/html; charset=utf-8", "cache-control": "no-store",
      "content-security-policy": "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self' https://api.github.com; img-src data: https:; frame-ancestors 'none'",
      "x-frame-options": "DENY", "referrer-policy": "no-referrer" } });
  // Wrong-password lockout: 5 misses per IP per 15 min, 20 misses in total per hour.
  // A short PIN is only safe because of this (20 guesses an hour vs 1,000,000 six-digit PINs).
  const ip = req.headers.get("CF-Connecting-IP") || "unknown";
  const hour = now().slice(0, 13);
  const ipFails = Number((await env.LICENSES.get("fail:ip:" + ip)) || 0);
  const allFails = Number((await env.LICENSES.get("fail:all:" + hour)) || 0);
  if (ipFails >= 5 || allFails >= 20)
    return json({ error: "비밀번호를 여러 번 틀려서 잠시 잠겼어요. 15분~1시간 뒤 다시 시도하세요." }, 429, false);
  if (!(await tokenOk(req.headers.get("Authorization") || "", env.ADMIN_TOKEN))) {
    await env.LICENSES.put("fail:ip:" + ip, String(ipFails + 1), { expirationTtl: 15 * 60 });
    await env.LICENSES.put("fail:all:" + hour, String(allFails + 1), { expirationTtl: 2 * 60 * 60 });
    await new Promise(r => setTimeout(r, 400));
    return json({ error: "unauthorized" }, 401, false);
  }
  const route = req.method + " " + url.pathname.slice("/admin/api".length);
  const body = req.method === "POST" ? await req.json().catch(() => ({})) : {};
  if (route === "GET /announce" || route.startsWith("POST /announce/")) {
    const r = await announce(env, route, body, (action, detail) => audit(env, "admin", action, detail));
    return json(r, r.status || 200, false);
  }
  if (route === "GET /support" || route.startsWith("GET /support/") || route.startsWith("POST /support/")) {
    const { status = 200, ...r } = await supportAdmin(env, route, body, url, SUPPORT);
    return json(r, status, false);
  }
  switch (route) {
    case "GET /summary": return json(await summary(env), 200, false);
    case "GET /keys": return json(await listKeys(env, url.searchParams.get("q") || "", url.searchParams.get("status") || "all"), 200, false);
    case "GET /log": return json(await listLog(env), 200, false);
    case "GET /stats": return json(await stats(env, "/read"), 200, false);
    case "POST /downloads": return json(await stats(env, "/downloads", body.releases), 200, false);
    case "POST /issue": { const { status = 200, ...r } = await adminIssue(env, body); return json(r, status, false); }
    case "GET /members": {
      const r = await accounts(env, "admin/list", { q: url.searchParams.get("q") || "" });
      if (r.error) return json({ error: r.error === "unavailable" ? "Supabase에 연결하지 못했어요" : r.error }, r.status, false);
      return json(r, 200, false);
    }
    case "POST /members/block": {
      const r = await accounts(env, "admin/block", body);
      if (r.error) return json({ error: r.error }, r.status, false);
      await audit(env, "admin", body.on ? "회원 차단" : "회원 차단 해제", String(body.id));
      return json(r, 200, false);
    }
    case "POST /members/recovery": {
      const r = await accounts(env, "admin/recovery", body);
      if (r.error) return json({ error: r.error }, r.status, false);
      await audit(env, "admin", "복구 코드 재발급", String(body.id));
      return json(r, 200, false);
    }
    case "POST /members/delete": {
      const r = await accounts(env, "admin/delete", body);
      if (r.error) return json({ error: r.error }, r.status, false);
      await audit(env, "admin", "회원 삭제", String(body.id));
      return json(r, 200, false);
    }
    case "GET /referrals": return json(await listRefs(env), 200, false);
    case "POST /referrals": return json(await addRef(env, body), 200, false);
    case "POST /referrals/set": {
      const r = await getRef(env, String(body.code || ""));
      if (!r) return json({ error: "없는 코드예요" }, 404, false);
      r.on = !!body.on;
      await putRef(env, r);
      await audit(env, "admin", r.on ? "추천 코드 켜기" : "추천 코드 끄기", r.code);
      return json({ ok: true, ref: r }, 200, false);
    }
    case "POST /referrals/delete": {
      const code = String(body.code || "");
      if (!REF_RE.test(code)) return json({ error: "bad code" }, 400, false);
      await env.LICENSES.delete("ref:" + code);
      await audit(env, "admin", "추천 코드 삭제", code);
      return json({ ok: true }, 200, false);
    }
    case "POST /revoke": {
      if (!/^[0-9a-f]{16}$/.test(body.id || "")) return json({ error: "bad id" }, 400, false);
      await revoke(env, body.id, String(body.reason || "관리자 차단"), "admin");
      return json({ ok: true }, 200, false);
    }
    case "POST /unrevoke": {
      if (!/^[0-9a-f]{16}$/.test(body.id || "")) return json({ error: "bad id" }, 400, false);
      await env.LICENSES.delete("revoked:" + body.id);
      const r = await env.LICENSES.getWithMetadata("lic:" + body.id);
      if (r?.value) { const m = r.metadata || {}; m.revoked = ""; await env.LICENSES.put("lic:" + body.id, r.value, { metadata: m }); }
      await audit(env, "admin", "차단 해제", body.id);
      return json({ ok: true }, 200, false);
    }
    case "GET /orders": {
      const want = url.searchParams.get("status") || "open";
      const all = [];
      let cursor;
      for (let i = 0; i < 10; i++) {
        const r = await env.LICENSES.list({ prefix: "ord:", cursor, limit: 1000 });
        for (const k of r.keys) all.push({ code: k.name.slice(4), ...(k.metadata || {}) });
        if (r.list_complete) break; cursor = r.cursor;
      }
      const count = Object.fromEntries(ORDER_STATES.map(st => [st, all.filter(o => o.status === st).length]));
      const list = all.filter(o => want === "all" || (want === "open" ? ["requested", "paid"].includes(o.status) : o.status === want))
                      .sort((a, b) => (b.status === "paid") - (a.status === "paid") || String(b.updated || b.at).localeCompare(String(a.updated || a.at)));
      return json({ orders: list.slice(0, 300), count }, 200, false);
    }
    case "POST /orders/approve": {
      const o = CODE_RE.test(body.code || "") ? await getOrder(env, body.code) : null;
      if (!o) return json({ error: "주문을 찾지 못했어요" }, 404, false);
      if (o.status === "approved") return json({ error: "이미 승인된 주문이에요" }, 409, false);
      if (o.status === "refunded") return json({ error: "환불한 주문이에요" }, 409, false);
      const r = await adminIssue(env, { code: o.code, plan: o.plan, email: body.email, note: body.note || [o.payer, o.msg].filter(Boolean).join(" · ") });
      if (!r.error && o.ticket) await accounts(env, "ticket/used", { order: o.code });
      return json(r, r.error ? 400 : 200, false);
    }
    case "POST /orders/reject": {
      const o = CODE_RE.test(body.code || "") ? await getOrder(env, body.code) : null;
      if (!o) return json({ error: "주문을 찾지 못했어요" }, 404, false);
      if (o.status === "approved") return json({ error: "승인된 주문은 거절할 수 없어요. 키를 차단하세요" }, 409, false);
      if (o.status === "refunded") return json({ error: "환불한 주문이에요" }, 409, false);
      o.status = "rejected"; o.reason = String(body.reason || "").trim().slice(0, 100) || "결제를 확인하지 못했어요";
      await putOrder(env, o);
      if (o.ticket) await accounts(env, "ticket/release", { order: o.code });
      await audit(env, "admin", "거절", o.code + " · " + o.reason);
      return json({ ok: true }, 200, false);
    }
    case "POST /orders/delete": {
      if (!CODE_RE.test(body.code || "")) return json({ error: "bad code" }, 400, false);
      const o = await getOrder(env, body.code);
      if (o?.ticket && o.status !== "approved") await accounts(env, "ticket/release", { order: o.code });
      await env.LICENSES.delete("ord:" + body.code);
      await audit(env, "admin", "주문 삭제", body.code);
      return json({ ok: true }, 200, false);
    }
    case "GET /key": {   // full key text, to send it again to a buyer
      const id = url.searchParams.get("id") || "";
      if (!/^[0-9a-f]{16}$/.test(id)) return json({ error: "bad id" }, 400, false);
      return json({ key: await env.LICENSES.get("lic:" + id) }, 200, false);
    }
    case "GET /pay-links": return json({ pay: await payLinks(env) }, 200, false);
    case "POST /pay-links": {
      const o = {};
      for (const p of LINK_KEYS) {
        const v = String(body[p] || "").trim();
        if (v && !/^https:\/\/\S+$/.test(v)) return json({ error: linkLabel(p) + " 링크는 https:// 로 시작해야 해요" }, 400, false);
        if (v) o[p] = v;
      }
      await env.LICENSES.put("config:pay_urls", JSON.stringify(o));
      await audit(env, "admin", "결제 링크", Object.keys(o).map(linkLabel).join(", ") || "모두 비움");
      return json({ pay: o }, 200, false);
    }
    case "GET /card": return json(await cardSetup(env), 200, false);
    case "POST /card": {
      const ids = { store: String(body.store ?? "").trim(), variants: {} };
      if (ids.store && !ID_RE.test(ids.store)) return json({ error: "상점 번호는 숫자예요" }, 400, false);
      for (const p of SINGLE_PLANS) {
        const v = String(body.variants?.[p] ?? "").trim();
        if (v && !ID_RE.test(v)) return json({ error: PLANS[p] + " 상품 번호는 숫자예요" }, 400, false);
        if (v) ids.variants[p] = v;
      }
      const used = Object.values(ids.variants);
      if (new Set(used).size !== used.length) return json({ error: "두 플랜에 같은 상품 번호를 넣었어요" }, 400, false);
      await env.LICENSES.put("config:ls", JSON.stringify(ids));
      await audit(env, "admin", "카드 결제 설정", "상점 " + (ids.store || "없음") + " · " + (Object.keys(ids.variants).map(p => PLANS[p]).join(", ") || "상품 없음"));
      return json(await cardSetup(env), 200, false);
    }
    case "POST /lookup": return json(await lookup(env, String(body.key || "")), 200, false);
    case "POST /sales-mode": {
      if (!MODES.includes(body.mode)) return json({ error: "bad mode" }, 400, false);
      await env.LICENSES.put("config:sales_mode", body.mode);
      await audit(env, "admin", "판매 모드", body.mode);
      return json({ sales: body.mode }, 200, false);
    }
  }
  return json({ error: "not found" }, 404, false);
}

// What the dashboard shows for card payments. The two secrets are only reported as set or not.
async function cardSetup(env) {
  const c = await lsConfig(env);
  return { ...c, apiKey: !!env.LS_API_KEY, signing: !!env.LS_SIGNING_SECRET, ready: await cardPlans(env),
           webhook: (env.PUBLIC_URL || "") + "/webhook/lemonsqueezy", sales: await salesMode(env) };
}

async function tokenOk(header, token) {
  const given = header.startsWith("Bearer ") ? header.slice(7) : "";
  const a = new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(given)));
  const b = new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(token)));
  let d = 0; for (let i = 0; i < a.length; i++) d |= a[i] ^ b[i];   // constant time
  return d === 0 && given.length > 0;
}

async function allRecords(env) {
  const out = []; let cursor;
  for (let page = 0; page < 20; page++) {          // up to 20k keys; more than enough for an indie shop
    const r = await env.LICENSES.list({ prefix: "lic:", cursor, limit: 1000 });
    for (const k of r.keys) out.push({ id: k.name.slice(4), ...(k.metadata || {}) });
    if (r.list_complete) break;
    cursor = r.cursor;
  }
  return out;
}
function state(m, t = Date.now()) {
  if (m.revoked) return "revoked";
  if (m.expires && Date.parse(m.expires) < t) return "expired";
  return m.test ? "test" : "active";
}
async function summary(env) {
  const all = await allRecords(env), t = Date.now(), month = t - 30 * 864e5;
  const c = { total: all.length, active: 0, revoked: 0, expired: 0, test: 0, last30: 0, lifetime: 0, subscription: 0, manual: 0 };
  for (const m of all) {
    c[state(m, t)]++;
    if (Date.parse(m.issued) > month) c.last30++;
    if (m.src === "manual") c.manual++; else if (m.sub) c.subscription++; else c.lifetime++;
  }
  const op = await env.LICENSES.list({ prefix: "ord:", limit: 1000 });
  c.ordersOpen = op.keys.filter(k => ["requested", "paid"].includes(k.metadata?.status)).length;
  c.ordersPaid = op.keys.filter(k => k.metadata?.status === "paid").length;
  Object.assign(c, await supportCounts(env));   // ticketsOpen, refundsOpen
  return { sales: await salesMode(env), autoReady: await cardPlans(env), cardLiveOpen: cardLiveOpen(env),
           salesFromDashboard: MODES.includes(await env.LICENSES.get("config:sales_mode")), counts: c };
}
async function listKeys(env, q, status) {
  q = q.trim().toLowerCase();
  let all = await allRecords(env);
  for (const m of all) m.state = state(m);
  if (status !== "all") all = all.filter(m => m.state === status);
  if (q) all = all.filter(m => [m.id, m.email, m.note, m.order, m.sub].some(v => String(v || "").toLowerCase().includes(q)));
  all.sort((a, b) => String(b.issued).localeCompare(String(a.issued)));
  return { keys: all.slice(0, 500), more: all.length > 500 };
}
async function listLog(env) {
  const r = await env.LICENSES.list({ prefix: "log:", limit: 100 });
  const out = [];
  for (const k of r.keys) { const v = await env.LICENSES.get(k.name); if (v) out.push(JSON.parse(v)); }
  return { log: out };
}
// ---------------------------------------------------------------- referral codes
// Whoever enters a code gets a short key, once per device. The person who shared the code is
// rewarded by hand, from the use count on the dashboard.
const REF_RE = /^[A-Z0-9]{4,16}$/;
const REF_DEVICE_TTL = 60 * 60 * 24 * 90;
const REF_DAILY = 300;   // keys a day, whatever the codes say

async function getRef(env, code) {
  const v = REF_RE.test(code) ? await env.LICENSES.get("ref:" + code) : null;
  return v ? JSON.parse(v) : null;
}
async function putRef(env, r) { await env.LICENSES.put("ref:" + r.code, JSON.stringify(r), { metadata: r }); }

const refDay = () => "count:ref:" + now().slice(0, 10);
const REF_DEVICE = /^[0-9a-f]{32}$/;

// What stops a device from taking a gift; null when nothing does.
async function refBlocked(env, device) {
  if (Number((await env.LICENSES.get(refDay())) || 0) >= REF_DAILY) return { status: 429, error: "busy" };
  if (await env.LICENSES.get("refdev:" + device)) return { status: 409, error: "already" };
  return null;
}
function refClosed(r) {
  if (!r.on) return { status: 404, error: "unknown" };
  return r.used >= r.max ? { status: 409, error: "used up" } : null;
}
async function grant(env, code, device, plan, days) {
  await env.LICENSES.put("refdev:" + device, code, { expirationTtl: REF_DEVICE_TTL });
  const day = refDay();
  await env.LICENSES.put(day, String(Number((await env.LICENSES.get(day)) || 0) + 1), { expirationTtl: 60 * 60 * 48 });
  const key = await makeKey(env, { plan, email: "", expires: addDays(now(), days), src: "referral", note: "추천 코드 " + code });
  await audit(env, "app", "추천 코드 사용", code + " · " + (TIER_KO[plan] || "프로") + " " + days + "일");
  return { key, plan, days };
}
async function useRef(env, r, device) {
  r.used += 1;
  await putRef(env, r);
  return grant(env, r.code, device, r.plan, r.days);
}

async function referral(req, env) {
  const b = await req.json().catch(() => ({}));
  const code = String(b.code || "").trim().toUpperCase(), device = String(b.device || "");
  if (!REF_RE.test(code) || !REF_DEVICE.test(device)) return json({ error: "bad request" }, 400);
  const r = await getRef(env, code);
  if (!r) return json({ error: (await accounts(env, "code", { code })).member ? "signup" : "unknown" }, 409);
  const stop = refClosed(r) || (await refBlocked(env, device));
  return stop ? json({ error: stop.error }, stop.status) : json(await useRef(env, r, device));
}

// ---------------------------------------------------------------- member accounts (src/accounts.js)
const ACCOUNT_ACTIONS = ["signup", "login", "recover", "me", "invite", "reward", "logout", "password", "delete"];

async function account(req, url, env) {
  const action = url.pathname.slice("/account/".length);
  if (!ACCOUNT_ACTIONS.includes(action)) return json({ error: "not found" }, 404);
  const body = req.method === "POST" ? await req.json().catch(() => ({})) : {};
  body.ip = req.headers.get("CF-Connecting-IP") || "unknown";
  body.token = (req.headers.get("Authorization") || "").replace(/^Bearer /, "");
  const r = action === "signup" ? await signup(env, body)
          : action === "reward" ? await reward(env, body)
          : await accounts(env, action, body);
  const { status = 200, ...rest } = r;
  return json(rest, status);
}

// A problem with the code stops the sign-up, so nothing is left half done and the user can fix it.
async function signup(env, b) {
  const invite = String(b.invite || "").trim().toUpperCase(), device = String(b.device || "");
  if (!invite) return accounts(env, "signup", { ...b, invite: "" });
  if (!REF_RE.test(invite) || !REF_DEVICE.test(device)) return { status: 400, error: "bad request" };
  const seller = await getRef(env, invite);
  const stop = (seller && refClosed(seller)) || (await refBlocked(env, device));
  if (stop) return stop;
  const r = await accounts(env, "signup", { ...b, invite: seller ? "" : invite, ticket: true });
  if (r.error) return r;
  return { ...r, ...(seller ? await useRef(env, seller, device) : await grant(env, invite, device, "premium", INVITE_DAYS)) };
}

async function reward(env, b) {
  const r = await accounts(env, "reward", b);
  if (r.error) return r;
  const key = await makeKey(env, { plan: "premium", email: "", expires: addDays(now(), r.days), src: "referral", note: "초대 보상 " + r.id });
  await audit(env, "app", "초대 보상", r.id + " · " + r.days + "일");
  return { key, days: r.days };
}

async function listRefs(env) {
  const refs = [];
  let cursor;
  for (let i = 0; i < 10; i++) {
    const l = await env.LICENSES.list({ prefix: "ref:", cursor, limit: 1000 });
    for (const k of l.keys) if (k.metadata) refs.push(k.metadata);
    if (l.list_complete) break;
    cursor = l.cursor;
  }
  refs.sort((a, b) => String(b.at).localeCompare(String(a.at)));
  return { refs };
}

async function addRef(env, b) {
  let code = String(b.code || "").trim().toUpperCase();
  if (!code) {
    const A = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789", r = crypto.getRandomValues(new Uint8Array(8));
    code = [...r].map(x => A[x % A.length]).join("");
  }
  if (!REF_RE.test(code)) return { error: "코드는 영문 대문자와 숫자 4~16자예요" };
  if (await getRef(env, code)) return { error: "이미 있는 코드예요" };
  const days = Math.floor(Number(b.days)), max = Math.floor(Number(b.max));
  if (!(days >= 1 && days <= 30)) return { error: "기간은 1~30일이에요" };
  if (!(max >= 1 && max <= 10000)) return { error: "사용 횟수는 1~10000이에요" };
  const r = { code, label: String(b.label || "").trim().slice(0, 80), plan: TIERS.includes(b.plan) ? b.plan : "premium",
              days, max, on: true, used: 0, at: now() };
  await putRef(env, r);
  await audit(env, "admin", "추천 코드 생성", code + " · " + r.days + "일 · " + r.max + "회" + (r.label ? " · " + r.label : ""));
  return { ok: true, code, ref: r };
}

async function adminIssue(env, b) {
  const email = String(b.email || "").trim().slice(0, 120);
  const note = String(b.note || "").trim().slice(0, 200);
  const code = String(b.code || "").trim().toUpperCase();
  if (code && !CODE_RE.test(code)) return { error: "주문 코드 형식이 아니에요 (SB-XXXX-XXXX)" };
  const plan = String(b.plan || "");
  const period = periodOf(plan);   // orders say ultra_monthly, biz_yearly_x5 etc.; the form says monthly / yearly / days
  let days = 0;
  if (period === "days") days = Math.floor(Number(b.days));
  else if (PLAN_DAYS[period]) days = PLAN_DAYS[period] + GRACE_DAYS;
  else if (period !== "lifetime" || !code) return { error: "플랜을 고르세요" };   // an order placed while it was on sale can still be approved
  if (period === "days" && !(days >= 1 && days <= 3650)) return { error: "기간은 1~3650일이에요" };
  if (!note && !code) return { error: "주문 코드나 메모(발급 이유) 중 하나는 적어 주세요" };
  const seats = b.seats == null || b.seats === "" ? seatsOf(plan) : Math.floor(Number(b.seats));
  if (!(seats >= 1 && seats <= MAX_SEATS)) return { error: "키 개수는 1~" + MAX_SEATS + "개예요" };
  // a refunded order stays refunded: issuing for its code again would set it back to approved and let /claim hand out
  // a key for money that was paid back (status: the HTTP status for POST /issue)
  if (code && (await getOrder(env, code))?.status === "refunded")
    return { status: 409, error: "환불한 주문이에요. 이 주문 코드로는 키를 다시 발급하지 않아요. 다시 팔았다면 새 주문 코드로 발급하거나, 주문 코드 없이 메모를 적어 발급하세요" };
  const src = code ? "payment" : "manual";
  // the key's "plan": ultra also opens frame generation x6 and x8, biz adds the work and streaming features
  const tier = tierOf(plan) !== "premium" ? tierOf(plan) : TIERS.includes(b.tier) ? b.tier : "premium";
  const keys = [], ids = [];
  for (let i = 1; i <= seats; i++) {   // a bundle is that many separate keys: one per PC, each can be turned off alone
    const k = await makeKey(env, { plan: tier, email, expires: days ? addDays(now(), days) : undefined, src,
                                   note: [code, seats > 1 ? seats + "대 묶음 " + i + "/" + seats : "", note].filter(Boolean).join(" · ") });
    keys.push(k); ids.push(await keyId(k));
  }
  const key = keys[0], id = ids[0];
  if (code) {   // the buyer's app is polling /claim?session=<code> -> popup
    const ttl = { expirationTtl: 60 * 60 * 24 * 30 };
    await env.LICENSES.put("session:" + code, key, ttl);
    if (seats > 1) await env.LICENSES.put("bundle:" + code, JSON.stringify(keys), ttl);
    const o = (await getOrder(env, code)) || { code, plan, at: now(), method: "manual" };
    Object.assign(o, { status: "approved", keyId: id, email: email.slice(0, 80), approvedAt: now(), ...(seats > 1 ? { seats } : {}) });
    await putOrder(env, o);
  }
  // a bundle's key ids go last: the log keeps 300 characters
  await audit(env, "admin", "키 발급", (seats > 1 ? seats + "대 묶음" : id) + " · " + (code || "코드 없음") + " · " + (email || "메일 없음") + " · " +
              (days ? days + "일" : "평생") + (tier !== "premium" ? " · " + TIER_KO[tier] : "") + (note ? " · " + note : "") +
              (seats > 1 ? " · " + ids.join(", ") : ""));
  return { key, id, keys, ids };
}
async function lookup(env, key) {
  key = key.trim();
  const parts = key.split(".");
  if (parts.length !== 2) return { error: "키 형식이 아니에요" };
  let payload;
  try { payload = JSON.parse(new TextDecoder().decode(unb64url(parts[0]))); } catch { return { error: "키 형식이 아니에요" }; }
  const id = await keyId(key);
  const valid = await verifySig(env, parts[0], parts[1]);
  const r = await env.LICENSES.getWithMetadata("lic:" + id);
  const revoked = await env.LICENSES.get("revoked:" + id);
  return { id, valid, payload, registered: !!r?.value, revoked };
}

// ---------------------------------------------------------------- crypto
async function validSignature(raw, sigHex, secret) {
  if (!secret || !/^[0-9a-f]{64}$/i.test(sigHex)) return false;
  const k = await crypto.subtle.importKey("raw", new TextEncoder().encode(secret), { name: "HMAC", hash: "SHA-256" }, false, ["verify"]);
  const sig = new Uint8Array(sigHex.match(/../g).map(h => parseInt(h, 16)));
  return crypto.subtle.verify("HMAC", k, sig, new TextEncoder().encode(raw));   // constant-time compare
}

let signingKey, verifyKey;
async function makeKey(env, fields) {
  if (!signingKey) {
    const pkcs8 = Uint8Array.from(atob(env.LICENSE_PRIVATE_KEY.replace(/-----[^-]+-----|\s/g, "")), c => c.charCodeAt(0));
    signingKey = await crypto.subtle.importKey("pkcs8", pkcs8, { name: "ECDSA", namedCurve: "P-256" }, false, ["sign"]);
  }
  const payload = { plan: fields.plan, email: fields.email, issued: new Date().toISOString().replace(/\.\d+Z$/, "Z") };
  if (fields.sub) payload.sub = fields.sub;
  if (fields.order) payload.order = fields.order;
  if (fields.expires) payload.expires = fields.expires;
  if (fields.test) payload.test = true;
  const data = new TextEncoder().encode(JSON.stringify(payload));
  const sig = new Uint8Array(await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, signingKey, data));
  const key = b64url(data) + "." + b64url(sig);
  await register(env, key, payload, fields.src || "manual", fields.note || "");
  return key;
}

async function verifySig(env, p, s) {
  try {
    if (!verifyKey) {   // public half of the signing key (drop "d" from the JWK)
      const pkcs8 = Uint8Array.from(atob(env.LICENSE_PRIVATE_KEY.replace(/-----[^-]+-----|\s/g, "")), c => c.charCodeAt(0));
      const priv = await crypto.subtle.importKey("pkcs8", pkcs8, { name: "ECDSA", namedCurve: "P-256" }, true, ["sign"]);
      const { kty, crv, x, y } = await crypto.subtle.exportKey("jwk", priv);
      verifyKey = await crypto.subtle.importKey("jwk", { kty, crv, x, y }, { name: "ECDSA", namedCurve: "P-256" }, false, ["verify"]);
    }
    return await crypto.subtle.verify({ name: "ECDSA", hash: "SHA-256" }, verifyKey, unb64url(s), unb64url(p));
  } catch { return false; }
}
function unb64url(s) {
  s = s.replace(/-/g, "+").replace(/_/g, "/");
  return Uint8Array.from(atob(s + "===".slice((s.length + 3) % 4)), c => c.charCodeAt(0));
}
function now() { return new Date().toISOString().replace(/\.\d+Z$/, "Z"); }
function b64url(bytes) {
  let s = "";
  for (const b of bytes) s += String.fromCharCode(b);
  return btoa(s).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}
function addDays(iso, d) { return new Date(new Date(iso).getTime() + d * 864e5).toISOString().replace(/\.\d+Z$/, "Z"); }
function json(o, status = 200, cors = true) {
  const headers = { "content-type": "application/json", "cache-control": "no-store" };
  if (cors) headers["access-control-allow-origin"] = "*";   // public endpoints only; the admin API stays same-origin
  return new Response(JSON.stringify(o), { status, headers });
}
function escapeHtml(s) { return s.replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c])); }
