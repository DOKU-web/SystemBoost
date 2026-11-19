import { ADMIN_HTML } from "./admin_page.js";
import { INVITE_DAYS, accounts } from "./accounts.js";
import { accountWeb, keysResend } from "./account_web.js";
import { accountsV2Open } from "./accounts_gate.js";
import { mailOn } from "./mail.js";
import { announce } from "./announce.js";
import { cleanPing } from "./stats.js";
import { budgetNames, DEPOSIT_HOURS, DEPOSIT_WARN_HOURS, helpPage, ipBucket, LIMITS as SUPPORT_LIMITS, privacyPage, sellerGaps, support, supportConfig, termsPage, supportAdmin, supportCounts, webhookRefund, webhookRefundSub } from "./support.js";
import { ipTags, overIp } from "./iplimit.js";
import { preorderAdmin, preorderPublic } from "./preorder.js";
import { purchaseNote } from "./purchase_note.js";
import { alertOn, dueForReminder, paidText, reminderText, sellerAlert } from "./seller_alert.js";
export { Stats } from "./stats.js";
// SystemBoost license server (Cloudflare Workers, free plan is enough).
//
//   POST /checkout?plan=monthly|yearly&session=<id>
//                                creates a Lemon Squeezy checkout carrying the session -> {url}
//   POST /webhook/lemonsqueezy   Lemon Squeezy webhook (X-Signature HMAC-SHA256 checked)
//   GET  /claim?session=<id>     app polls this after opening checkout -> {status, key, keys?} (keys: a bundle)
//   GET  /thanks?session=<id>    checkout "return to" page that shows the key
//   POST /renew {sub, key}       latest key of an active subscription, for whoever holds one of its keys (also an
//                                expired one); GET /renew?sub=<id> (apps up to 0.8.11) gets nothing
//   POST /subscription/change {key, plan}
//                                plan change of a card subscription (Lemon Squeezy), asked by the holder of its key
//   POST /sub/price {key, lang?}  the price increase notice for the key's card subscription, and its holder's answer
//   POST /sub/price/choose {key, id, choice}
//                                agree to the new price, or cancel the subscription ("price increase" below)
//   GET  /status                 {sales, pay:{monthly,yearly}} - the app enables "결제하기" only for plans
//                                that have a payment link (links are set in the dashboard); in live mode nothing
//                                while the seller's details are incomplete (sellerBlocked)
//   POST /order?plan=…           app gets an order code (SB-XXXX-XXXX) to write in the payment memo;
//                                the owner confirms the payment and issues the key for that code in /admin,
//                                and the app (polling /claim?session=<code>) pops it up
//   GET  /check?id=<key id>      {revoked} - the app asks this at start (offline = keeps working)
//   POST /ping                   usage statistics from a running app -> {next: seconds until the next ping}
//   /support/*, GET /help        customer support and refund requests (src/support.js, src/help_page.js)
//   GET  /privacy                the privacy policy (?lang=en: an English summary), src/privacy_page.js; before accounts v2
//                                start (ACCOUNTS_V2_FROM) the policy of then, src/privacy_prev_page.js, and ?next=1 the new one
//   GET  /terms                  the licence agreement as the app shows it (?lang=en: the reference translation), src/terms_page.js
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
// The seller's details (전자상거래법: who sells must be shown before a sale) are entered in the dashboard (고객센터 설정,
// KV config:support). In live sales mode nothing new is sold while one of support.js SELLER_REQUIRED is empty: /status
// offers no plan, and /order, /checkout and a card plan change (a new payment) answer 503 {"error":"seller info"}.
// Test mode is as before. Key checks, renewals, webhooks, refunds, support, an "I paid" note on an existing order and
// the seller's own actions never look at this.
async function sellerBlocked(env, mode) {
  if ((mode ?? (await salesMode(env))) !== "live") return false;
  return sellerGaps(await supportConfig(env)).length > 0;
}
const sellerInfo = () => json({ error: "seller info" }, 503);

async function openPlans(env) {   // plans the app may sell right now, per method
  if (await sellerBlocked(env)) return { methods: { card: [], manual: [] } };
  return { methods: { card: (await cardOpen(env)) ? await cardPlans(env) : [],
                      manual: Object.keys(await payLinks(env)).filter(p => PLANS[p] && (seatsOf(p) === 1 || bundleOpen(env))) } };
}

// A plan named by a request: own keys only, so "constructor" or "__proto__" are not plans.
const isPlan = p => typeof p === "string" && Object.hasOwn(PLANS, p);

// Calls per IP address an hour on the public routes that write KV or call Lemon Squeezy (src/iplimit.js: no KV writes
// for the counting). Approximate; the day's share of KV writes (publicKvDay) and REF_DAILY stay the backstop.
const IP_LIMIT = { order: 20, paid: 30, checkout: 20, change: 10, referral: 30, price: 20 };

// The Workers free plan allows 1,000 KV writes a day (UTC) for the whole worker; past that every put fails until
// 09:00 KST, approvals and card webhooks included. Requests anyone can make share publicKvDay(env) of them (default
// PUBLIC_KV_DAY_DEFAULT): new orders, "I paid" notes, referral / invite gifts and answers to a price notice (support
// has its own 150 + 100, support.js LIMITS). The count lives in the Stats object, so counting writes no KV. The seller's actions,
// keys of approved orders and Lemon Squeezy webhooks are never counted or refused: with the default, at least 350
// writes a day stay for them (docs/KV_BUDGET.md). While the object cannot be reached the old KV caps hold (300
// orders, REF_DAILY gifts), as before the object counted.
// env PUBLIC_KV_DAY (wrangler.toml [vars]) moves the share: a whole number from PUBLIC_KV_MIN to PUBLIC_KV_MAX
// (a paid Workers plan allows far more writes); anything else, or nothing, keeps the default.
const PUBLIC_KV_DAY_DEFAULT = 400, PUBLIC_KV_MIN = 50, PUBLIC_KV_MAX = 100000;
function publicKvDay(env) {
  const v = env?.PUBLIC_KV_DAY;
  const n = typeof v === "number" ? v : typeof v === "string" && /^\s*\d{1,6}\s*$/.test(v) ? Number(v) : NaN;
  return Number.isInteger(n) && n >= PUBLIC_KV_MIN && n <= PUBLIC_KV_MAX ? n : PUBLIC_KV_DAY_DEFAULT;
}
const pubName = () => "kv:pub:" + now().slice(0, 10);
// cap = true: "" counted, "full" today's share would be passed (nothing counted); "down": the object did not answer.
// cap = false: counted whatever the total (after the checks were made), "" or "down".
async function takePublic(env, writes, more = [], cap = true) {
  try {
    const r = await stats(env, "/take", { items: [{ name: pubName(), ttl: 2 * 86400, by: writes, ...(cap ? { max: publicKvDay(env) } : {}) }, ...more] });
    if (r?.ok === true) return "";
    if (r?.ok === false) return "full";
  } catch { /* below */ }
  return "down";
}
const PAID_EDITS = 5;   // payer / memo changes an order takes after its first "I paid"
const TIER_RANK = { premium: 1, ultra: 2, biz: 3 };

// heldKey(env, text) (next to keyId): the key as issued, still honoured and not run out; null otherwise.

// Early renewal (기간 연장): the app (from the release after 0.8.11) sends the key this PC holds with a bank-transfer order for one PC.
// A key that still runs, of the same tier as the plan bought and not a test key, is noted on the order
// as extendFrom {id, expires}; adminIssue then counts the new period from that key's end instead of from the approval.
// Anything else (no key: apps up to 0.8.11, another tier, a card or bundle order) leaves the order as before. Only the
// same tier: days left of a cheaper plan must not turn into days of a dearer one (premium yearly + ultra monthly).
async function extendable(env, key, plan) {
  const h = await heldKey(env, key);
  if (!h || h.payload.test) return null;
  const end = Date.parse(h.payload.expires || "");
  if (!(end > Date.now())) return null;   // no end date (nothing to add to) or over already (counts from now anyway)
  if ((TIERS.includes(h.payload.plan) ? h.payload.plan : "premium") !== tierOf(plan)) return null;
  return { id: h.id, expires: new Date(end).toISOString().replace(/\.\d+Z$/, "Z") };
}

async function order(req, url, env) {
  const plan = url.searchParams.get("plan");
  if (!isPlan(plan)) return json({ error: "bad plan" }, 400);
  if (await sellerBlocked(env)) return sellerInfo();
  const { methods } = await openPlans(env);
  let method = url.searchParams.get("method");
  if (!method) method = methods.card.includes(plan) ? "card" : "manual";
  if (!["card", "manual"].includes(method) || !methods[method].includes(plan)) return json({ error: "sales closed" }, 503);
  if (await overIp(req, env, "order", IP_LIMIT.order)) return json({ error: "busy" }, 429);
  const extendFrom = method === "manual" && seatsOf(plan) === 1 ? await extendable(env, (await readJson(req)).key, plan) : null;
  const pub = await takePublic(env, 1);   // the order's own write (ord:)
  if (pub === "full") return json({ error: "busy" }, 429);
  if (pub === "down") {   // the object cannot count: the old KV cap on junk orders per day
    const n = Number((await env.LICENSES.get("count:orders:" + now().slice(0, 10))) || 0);
    if (n >= 300) return json({ error: "busy" }, 429);
    await env.LICENSES.put("count:orders:" + now().slice(0, 10), String(n + 1), { expirationTtl: 60 * 60 * 48 });
  }
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
  await putOrder(env, { code, plan, at: now(), status: "requested", method, ...(ticket ? { ticket } : {}), ...(extendFrom ? { extendFrom } : {}) });
  return json({ code, url: payUrl, method, ...(extendFrom ? { extends: extendFrom.expires } : {}) });
}

// Buyer says "I paid" (manual payments). Name / memo help the seller find the payment.
// The order code is what claims the key, so the app sends it in the body; apps up to 0.8.11 put it in the address.
// Anyone with a code can call this, so it writes KV only when something changes: requested -> paid, or a new
// payer / memo, at most PAID_EDITS times after the first. Repeating the same note is answered without a write.
// The first note (requested -> paid) also tells the seller's Discord (seller_alert.js: code, plan, time left only).
async function orderPaid(req, url, env, ctx) {
  const b = await readJson(req);
  const code = String(url.searchParams.get("code") || b.code || "").toUpperCase();
  if (!CODE_RE.test(code)) return json({ error: "bad code" }, 400);
  if (await overIp(req, env, "paid", IP_LIMIT.paid)) return json({ error: "busy" }, 429);
  const o = await getOrder(env, code);
  if (!o) return json({ error: "no order" }, 404);
  if (o.status !== "requested" && o.status !== "paid") return json({ status: o.status });
  // a card payment is confirmed by Lemon Squeezy's signed webhook, never by the buyer: an unpaid card order
  // must not show up in the dashboard as 결제 확인 요청
  if (o.method === "card" || o.method === "auto") return json({ status: o.status });
  const payer = String(b.payer || "").trim().slice(0, 40), msg = String(b.msg || "").trim().slice(0, 100);
  if (o.status === "paid") {
    if (payer === (o.payer || "") && msg === (o.msg || "")) return json({ status: "paid" });   // nothing new: no write
    if ((o.edits || 0) >= PAID_EDITS) return json({ error: "too many changes", status: "paid" }, 429);
    o.edits = (o.edits || 0) + 1;
  } else o.paidAt = now();
  if (await takePublic(env, 1) === "full") return json({ error: "busy" }, 429);   // the day's share is used up (KV free plan)
  const first = o.status === "requested";
  o.status = "paid";
  o.payer = payer;
  o.msg = msg;
  await putOrder(env, o);
  if (first) await later(ctx, sellerAlert(env, paidText(o.code, PLANS[o.plan] || o.plan, o.paidAt)));
  return json({ status: "paid" });
}
// Work the answer must not wait for (the Worker keeps running it); awaited where there is no ctx (tests).
async function later(ctx, p) {
  if (ctx?.waitUntil) { try { ctx.waitUntil(p); return; } catch { /* awaited below */ } }
  await p;
}
// The hourly cron: one Discord message for the bank transfers still waiting DEPOSIT_WARN_HOURS after their first
// "I paid" (seller_alert.js dueForReminder: the hour before the run's scheduled time, so once each, no KV write).
// Without ADMIN_ALERT_WEBHOOK it does not even list the orders. Never throws.
async function depositReminders(env, at) {
  try {
    if (!alertOn(env)) return 0;
    const paid = [];
    let cursor;
    for (let i = 0; i < 10; i++) {
      const l = await env.LICENSES.list({ prefix: "ord:", cursor, limit: 1000 });
      for (const k of l.keys) if (k.metadata?.status === "paid" && k.metadata.paidAt)
        paid.push({ code: k.name.slice(4), plan: PLANS[k.metadata.plan] || k.metadata.plan || "", paidAt: k.metadata.paidAt });
      if (l.list_complete) break;
      cursor = l.cursor;
    }
    const due = dueForReminder(paid, at).sort((a, b) => a.paidAt.localeCompare(b.paidAt));
    if (due.length) await sellerAlert(env, reminderText(due));
    return due.length;
  } catch { return 0; }
}

const SESSION_TTL = 60 * 60 * 24 * 7;   // claim within 7 days
const GRACE_DAYS = 3;                   // subscription key stays valid a bit past renewal
const HOOK_TTL = 60 * 60 * 24 * 400;    // "this webhook was handled" markers (hook:*)

// What src/support.js uses from here (passed in, so the two files do not import each other).
// events: the support feed in the Stats Durable Object (stats.js), so the dashboard sees what buyers do.
// revokeSub(env, sub, reason, who): blocks every key of a card subscription, returns the ids it blocked.
// cancelSubscription(env, sub, why): stops its billing at Lemon Squeezy (or logs that the owner must), never throws;
// {ok: true} or {ok: false, reason}. stats: the same Durable Object's counters (per-address limits, the support write
// budget, the portal cap). canonicalKey / heldKey: one spelling per key, and "a key this server issued and still
// honours" (see keyId).
// approveOrder / rejectOrder: the 판매·키 buttons, also behind a 입금 확인 ticket's buttons (POST /admin/api/support/deposit).
const SUPPORT = { getOrder, putOrder, revoke, revokeSub, cancelSubscription, audit, accounts, verifySig, unb64url, allRecords, state, keyId,
                  canonicalKey, heldKey, approveOrder, rejectOrder,
                  events: (env, path, body) => stats(env, path, body), stats: (env, path, body) => stats(env, path, body) };

export default {
  async fetch(req, env, ctx) {
    const url = new URL(req.url);
    try {
      if (req.method === "POST" && url.pathname === "/checkout") return await checkout(req, url, env);
      if (req.method === "POST" && url.pathname === "/webhook/lemonsqueezy") return await webhook(req, env);
      // The order code and the key are secrets: the app sends them in a POST body, where addresses (and so logs)
      // do not carry them. The GET forms are what apps up to 0.8.11 use; drop them once no such app is in use
      // (GET /renew gives those apps nothing any more: it carries no key).
      if (req.method === "POST" && url.pathname === "/claim") return await claim(String((await readJson(req)).session || ""), env);
      if (req.method === "GET" && url.pathname === "/claim") return await claim(url.searchParams.get("session") || "", env);
      if (req.method === "GET" && url.pathname === "/thanks") return await thanks(url, env);
      if (req.method === "POST" && url.pathname === "/renew") { const b = await readJson(req); return await renew(String(b.sub || ""), String(b.key || ""), env); }
      if (req.method === "GET" && url.pathname === "/renew") return await renew(url.searchParams.get("sub") || "", "", env);
      if (req.method === "POST" && url.pathname === "/subscription/change") return await subscriptionChange(req, env);
      if (req.method === "POST" && url.pathname === "/sub/price") return await subPrice(req, env);
      if (req.method === "POST" && url.pathname === "/sub/price/choose") return await subPriceChoose(req, env);
      if (url.pathname === "/preorder") return await preorderPublic(req, env, { stats, ipTags, json });
      if (req.method === "GET" && url.pathname === "/status") return json({ sales: await salesMode(env), ...(await openPlans(env)) });
      if (req.method === "POST" && url.pathname === "/order") return await order(req, url, env);
      if (req.method === "POST" && url.pathname === "/order/paid") return await orderPaid(req, url, env, ctx);
      if (req.method === "GET" && url.pathname === "/check") return await check(url, env);
      if (req.method === "POST" && url.pathname === "/ping") return await ping(req, env);
      if (req.method === "POST" && url.pathname === "/referral") return await referral(req, env);
      if (url.pathname.startsWith("/account/")) return await accountWeb(req, url, env, ctx, ACCOUNT_DEPS);
      // accounts v2 (POST /keys/resend among them) start at ACCOUNTS_V2_FROM; before it this route is not there (404 below)
      if (req.method === "POST" && url.pathname === "/keys/resend" && accountsV2Open(env)) return await keysResend(req, env, ctx, ACCOUNT_DEPS);
      if (url.pathname.startsWith("/support/")) return await support(req, url, env, SUPPORT, ctx);
      if (req.method === "GET" && url.pathname === "/help") return helpPage(url);
      if (req.method === "GET" && url.pathname === "/privacy") return await privacyPage(url, env);
      if (req.method === "GET" && url.pathname === "/terms") return termsPage(url);
      if (url.pathname === "/admin" || url.pathname.startsWith("/admin/")) return await admin(req, url, env, ctx);
      return json({ error: "not found" }, 404);
    } catch (e) {
      return json({ error: "server error" }, 500);
    }
  },
  async scheduled(event, env, ctx) {
    ctx.waitUntil(stats(env, "/tick"));
    ctx.waitUntil(accounts(env, "ping", {}));
    ctx.waitUntil(depositReminders(env, Number(event?.scheduledTime) || Date.now()));
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
async function checkout(req, url, env) {   // kept for direct use; the app goes through /order
  if (await sellerBlocked(env)) return sellerInfo();
  if (!(await cardOpen(env))) return json({ error: "sales closed" }, 503);
  const plan = url.searchParams.get("plan");
  const session = url.searchParams.get("session") || "";
  if (!isPlan(plan) || !validSession(session)) return json({ error: "bad request" }, 400);
  if (await overIp(req, env, "checkout", IP_LIMIT.checkout)) return json({ error: "busy" }, 429);
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
  // refunds are handled in every mode: the key of that order, and every key of its subscription, is revoked.
  // A subscription can hold more than one key that is still valid (a plan change, a repeated webhook, a
  // refund that comes after a cancellation), and a refund must not leave any of them on. An open refund request
  // naming the refunded key (the order's key, or the subscription's current key) is marked done (src/support.js).
  // A refund also cancels the subscription at Lemon Squeezy (cancelSubscription), so the card is not charged again for
  // keys that are blocked; a cancellation that fails is logged for the owner and never fails the webhook.
  if (event === "order_refunded") {
    const id = await env.LICENSES.get("order:" + String(body?.data?.id));
    if (id) {
      const m = (await env.LICENSES.getWithMetadata("lic:" + id))?.metadata;
      if ((await env.LICENSES.get("revoked:" + id)) === null) await revoke(env, id, "환불", "webhook");
      if (m?.sub) {
        await revokeSub(env, String(m.sub), "환불");
        await cancelSubscription(env, String(m.sub), "환불");
      }
      await webhookRefund(env, id, SUPPORT);
    }
    return json({ ok: true });
  }
  if (event === "subscription_payment_refunded") {
    const key = await env.LICENSES.get("sub:" + String(a.subscription_id));   // read before revokeSub drops it
    await revokeSub(env, String(a.subscription_id), "환불");
    await cancelSubscription(env, String(a.subscription_id), "환불");
    if (key) await webhookRefund(env, await keyId(key), SUPPORT);
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
  if (!isPlan(plan)) {
    await audit(env, "카드 결제", "어떤 플랜인지 몰라서 키를 만들지 않음", "구독 " + subId + " · 상품 " + String(a.variant_id ?? "?"));
    return json({ ok: true, ignored: "unknown variant" });
  }
  // Lemon Squeezy sends an event again when it is not sure it arrived, and again after a reply that was not 2xx (the
  // owner can resend one too). The start of a subscription, a plan change and each paid invoice give ONE key, however
  // often they arrive, and a retry after a failure part way still delivers it. The event's marker:
  //   (none)              not seen yet: sign a key
  //   "pending:<key id>"  a key was signed and noted, then something failed: deliver that same key (signing again would
  //                       make a second key for one payment). A noted key that never got registered is signed anew.
  //   "1"                 handled: written LAST, after the key, the subscription record and the order are stored
  // Every write in between overwrites, so doing them again on a retry is safe. Markers written by the worker before this
  // one hold "1". A plan change without updated_at has no marker (as before): its subplan is written last instead.
  const once = event === "subscription_created" ? "hook:created:" + subId
             : invoice ? "hook:invoice:" + String(body?.data?.id)
             : a.updated_at ? "hook:plan:" + subId + ":" + plan + ":" + String(a.updated_at).slice(0, 40) : null;
  const mark = once ? await env.LICENSES.get(once) : null;
  if (mark !== null && !String(mark).startsWith("pending:")) return json({ ok: true, ignored: "repeat" });
  const period = plan.endsWith("yearly") ? 366 : 31;
  let expires = !invoice && a.renews_at ? addDays(a.renews_at, GRACE_DAYS) : addDays(now(), period + GRACE_DAYS);
  if (testOrder) expires = addDays(now(), 1);   // test keys never last
  const what = { subscription_created: "카드 결제", subscription_updated: "플랜 변경", subscription_payment_success: "자동 갱신" }[event];
  const fields = { plan: tierOf(plan), email: a.user_email || "", sub: subId, expires, test: testOrder,
                   src: "subscription", note: [session && event === "subscription_created" ? session : "", PLANS[plan] + " " + what].filter(Boolean).join(" · ") };
  let key = null;
  if (mark) {   // signed on an earlier try
    const id = String(mark).slice("pending:".length);
    const r = /^[0-9a-f]{16}$/.test(id) ? await env.LICENSES.getWithMetadata("lic:" + id) : null;
    if (r?.value && String(r.metadata?.sub || "") === subId) key = r.value;
  }
  if (!key) {
    const signed = await signKey(env, fields);
    if (once) await env.LICENSES.put(once, "pending:" + (await keyId(signed.key)), { expirationTtl: HOOK_TTL });
    await register(env, signed.key, signed.payload, fields.src, fields.note);
    key = signed.key;
  }
  const id = await keyId(key);
  await env.LICENSES.put("sub:" + subId, key);
  await addSubKey(env, subId, id);
  if (event === "subscription_created") {
    if (a.order_id != null) await env.LICENSES.put("order:" + String(a.order_id), id);   // a refund of the first payment
    await store(env, session, key, a.user_email, plan);
  } else {
    if (a.user_email) await env.LICENSES.put("email:" + String(a.user_email).toLowerCase(), key);
    await audit(env, "카드 결제", what, "구독 " + subId + " · " + PLANS[plan] + " · " + id);
  }
  await env.LICENSES.put("subplan:" + subId, plan);   // after the key is out: a plan change is "new" until here
  if (once) await env.LICENSES.put(once, "1", { expirationTtl: HOOK_TTL });
  return json({ ok: true });
}

// Stops a card subscription's billing: Lemon Squeezy's DELETE /v1/subscriptions/<id> cancels it (it runs to the end of
// the paid period and is not charged again). Without the API key the owner has to cancel it in Lemon Squeezy, and the
// log says so. Never throws, so a refund or a block goes through even when Lemon Squeezy cannot be reached; every
// outcome is a log line. Returns { ok: true } or { ok: false, reason }. Also passed to src/support.js (SUPPORT).
async function cancelSubscription(env, sub, why = "") {
  sub = String(sub ?? "");
  if (!/^\d{1,20}$/.test(sub)) return { ok: false, reason: "bad sub" };
  const tell = (action, detail) => audit(env, "카드 결제", action, "구독 " + sub + (why ? " · " + why : "") + " · " + detail).catch(() => {});
  if (!env.LS_API_KEY) {
    await tell("구독 해지 필요", "Lemon Squeezy API 키가 없어서 자동으로 해지하지 못했어요. Lemon Squeezy에서 이 구독을 직접 해지하세요");
    return { ok: false, reason: "no api key" };
  }
  let res;
  try {
    res = await fetch("https://api.lemonsqueezy.com/v1/subscriptions/" + sub, {
      method: "DELETE",
      headers: { Authorization: "Bearer " + env.LS_API_KEY, Accept: "application/vnd.api+json", "Content-Type": "application/vnd.api+json" },
      signal: AbortSignal.timeout(10000),
    });
  } catch {
    await tell("구독 해지 실패", "Lemon Squeezy에 연결하지 못했어요. Lemon Squeezy에서 이 구독을 직접 해지하세요");
    return { ok: false, reason: "network" };
  }
  if (!res.ok) {
    await tell("구독 해지 실패", "Lemon Squeezy " + res.status + ". 이미 해지된 구독이 아니라면 Lemon Squeezy에서 직접 해지하세요");
    return { ok: false, reason: "lemonsqueezy " + res.status };
  }
  await tell("구독 해지", "Lemon Squeezy에서 해지했어요. 더 결제되지 않아요");
  return { ok: true };
}

// ---------------------------------------------------------------- plan change of a card subscription
// The app sends the key it holds and the plan it wants. Lemon Squeezy switches the subscription to that plan's product
// (PATCH /v1/subscriptions/<id> with its variant_id) and bills the difference under the store's own settings; its
// subscription_updated webhook then gives the new key (above), which the app fetches through /renew.
// A new checkout instead would start a second subscription and leave this one charging. Reads only, plus a log line.
async function subscriptionChange(req, env) {
  if (await overIp(req, env, "change", IP_LIMIT.change)) return json({ error: "busy" }, 429);
  if (await sellerBlocked(env)) return sellerInfo();   // a plan change bills the difference: a new payment
  const b = await readJson(req);
  const plan = String(b.plan ?? "");
  if (!isPlan(plan) || seatsOf(plan) !== 1) return json({ error: "bad plan" }, 400);
  const h = await heldKey(env, b.key);
  const sub = String(h?.payload?.sub ?? "");
  if (!h || !/^\d{1,20}$/.test(sub) || String(h.meta.sub || "") !== sub) return json({ error: "key" }, 403);
  if ((await env.LICENSES.get("sub:" + sub)) === null) return json({ error: "inactive" }, 409);   // cancelled, refunded or blocked
  const c = await lsConfig(env);
  if (!env.LS_API_KEY || !c.store) return json({ error: "unavailable" }, 503);
  if (!c.variants[plan]) return json({ error: "plan closed" }, 409);
  const from = await env.LICENSES.get("subplan:" + sub);
  if (from === plan) return json({ error: "same plan" }, 409);
  const res = await lsSetVariant(env, sub, c.variants[plan]);
  if (res.status === 0) return json({ error: "unavailable" }, 503);
  if (!res.ok) {
    await audit(env, "카드 결제", "플랜 변경 실패", "구독 " + sub + " · " + (PLANS[from] || "?") + " → " + PLANS[plan] + " · Lemon Squeezy " + res.status);
    return json({ error: "refused" }, 502);
  }
  await audit(env, "카드 결제", "플랜 변경 요청", "구독 " + sub + " · " + (PLANS[from] || "?") + " → " + PLANS[plan] + " · " + h.id);
  return json({ ok: true, plan });
}

// Moves a card subscription to another product (variant): PATCH /v1/subscriptions/<id>. extra: more attributes, such
// as disable_prorations (the new price starts at the next renewal, nothing is charged now). Needs env.LS_API_KEY.
// -> { ok: true, status } | { ok: false, status } (status 0: no answer).
async function lsSetVariant(env, sub, variant, extra = {}) {
  try {
    const res = await fetch("https://api.lemonsqueezy.com/v1/subscriptions/" + sub, {
      method: "PATCH",
      headers: { Authorization: "Bearer " + env.LS_API_KEY, Accept: "application/vnd.api+json", "Content-Type": "application/vnd.api+json" },
      body: JSON.stringify({ data: { type: "subscriptions", id: sub, attributes: { variant_id: Number(variant), ...extra } } }),
      signal: AbortSignal.timeout(15000),
    });
    return { ok: res.ok, status: res.status };
  } catch { return { ok: false, status: 0 }; }
}

// ---------------------------------------------------------------- price increase of a card subscription (0.8.14)
// A card plan may cost more only for subscribers who agreed, told at least PRICE_NOTICE_DAYS ahead. The seller writes
// a notice in the dashboard (POST /admin/api/price): the plan, its old and new price (원), the Lemon Squeezy product
// (variant) at the new price and the day (KST) the price changes, at least PRICE_NOTICE_DAYS after today (KST).
// Nothing is sent: the dashboard gets the app's text and an e-mail draft the seller sends by hand.
//   POST /sub/price {key, lang?} -> {notice: {id, plan, oldPrice, newPrice, effective, text} | null, choice: "" | "agree" | "cancel"}
//     the notice for the plan the key's subscription is on, until the day before it takes effect (a subscription
//     that ended gets null unless it answered). 403 {"error":"key"} for anything but a held card subscription key.
//     Reads only. text is Korean, or English with lang "en".
//   POST /sub/price/choose {key, id, choice: "agree" | "cancel"} -> {ok: true, choice, cancelled?}
//     one KV write (pricechoice:<id>:<sub>), counted in the day's public share. "cancel" also stops the subscription
//     at Lemon Squeezy (cancelSubscription: it runs to the end of the paid period; cancelled false = the seller has
//     to cancel it by hand, the log says so). Agreeing can be changed to cancelling until the day it takes effect; a
//     cancel is final. The same answer again writes nothing.
//     Errors: 400 "bad request", 403 "key", 404 "notice" (no such notice for this subscription's plan), 409 "closed"
//     (the day has come), 409 "already" (cancelled before), 409 "inactive" (the subscription ended), 429 "busy".
// No answer is no consent: only subscriptions that agreed can be moved to the new product (POST /admin/api/price/move,
// on or after the day, without proration). Lemon Squeezy stops taking payments on 2027-03-31, so this stays small.
const PRICE_NOTICE_DAYS = 30;
const PRICE_MAX = 20;                              // notices kept in config:price_notices
const PRICE_MAX_WON = 10000000;
const PRICE_CHOICE_TTL = 60 * 60 * 24 * 365 * 3;  // the answers, kept three years
const PRICE_ID = /^[0-9a-f]{8}$/;
const PLAN_EN = p => ({ premium: "Pro", ultra: "Ultra", biz: "Business" }[tierOf(p)]) + " " + periodOf(p);
async function priceNotices(env) {
  try { const a = JSON.parse((await env.LICENSES.get("config:price_notices")) || "[]"); return Array.isArray(a) ? a : []; }
  catch { return []; }
}
const won = n => String(n).replace(/\B(?=(\d{3})+(?!\d))/g, ",");
const dayBefore = d => new Date(Date.parse(d + "T00:00:00Z") - 864e5).toISOString().slice(0, 10);
const koDate = d => { const [y, m, x] = d.split("-").map(Number); return `${y}년 ${m}월 ${x}일`; };
const enDate = d => new Date(d + "T00:00:00Z").toLocaleDateString("en-US", { year: "numeric", month: "long", day: "numeric", timeZone: "UTC" });
// The words the app shows: the change, both answers and what each does, and that silence is not consent.
function priceText(n, lang) {
  const last = dayBefore(n.effective);
  return lang === "en"
    ? `The ${PLAN_EN(n.plan)} subscription price changes from ₩${won(n.oldPrice)} to ₩${won(n.newPrice)} for automatic payments on or after ${enDate(n.effective)}. ` +
      `If you agree, you keep using it at the new price. If you do not, you can cancel and keep using what you already paid for until it ends. ` +
      `No answer is not taken as agreement, and nothing is charged at the new price without it. You can choose until ${enDate(last)}.`
    : `${PLANS[n.plan]} 구독 가격이 ${koDate(n.effective)} 이후 자동 결제부터 ${won(n.oldPrice)}원에서 ${won(n.newPrice)}원으로 바뀌어요. ` +
      `동의하면 새 가격으로 계속 쓸 수 있어요. 동의하지 않으면 해지할 수 있고, 이미 결제한 기간은 끝날 때까지 써요. ` +
      `답하지 않으면 동의한 것으로 보지 않고, 새 가격으로 결제하지 않아요. ${koDate(last)}까지 고를 수 있어요.`;
}
function priceEmail(n) {
  const last = dayBefore(n.effective);
  return {
    ko: { subject: `[SystemBoost] ${PLANS[n.plan]} 구독 가격 변경 안내 (${koDate(n.effective)}부터)`,
          body: `안녕하세요, SystemBoost예요.\n\n${PLANS[n.plan]} 구독의 가격이 바뀌어요.\n- 지금 가격: ${won(n.oldPrice)}원\n` +
                `- 새 가격: ${won(n.newPrice)}원 (${koDate(n.effective)} 이후 자동 결제부터)\n\n` +
                `새 가격으로 결제하려면 먼저 동의를 받아야 해요. SystemBoost 앱에서 동의하거나 해지할 수 있어요. 동의하면 새 가격으로 계속 쓸 수 있고, ` +
                `해지하면 이미 결제한 기간이 끝날 때까지 쓸 수 있어요. 답하지 않으면 동의한 것으로 보지 않고, 새 가격으로 결제하지 않아요.\n\n` +
                `${koDate(last)}까지 골라 주세요. 궁금한 점은 앱의 '고객센터'나 도움말 페이지(/help)로 문의해 주세요.` },
    en: { subject: `[SystemBoost] ${PLAN_EN(n.plan)} subscription price change (from ${enDate(n.effective)})`,
          body: `Hello from SystemBoost.\n\nThe price of the ${PLAN_EN(n.plan)} subscription changes.\n- Current price: ₩${won(n.oldPrice)}\n` +
                `- New price: ₩${won(n.newPrice)} (automatic payments on or after ${enDate(n.effective)})\n\n` +
                `We need your agreement before charging the new price. You can agree or cancel in the SystemBoost app. If you agree, you keep using it at ` +
                `the new price; if you cancel, you keep using what you already paid for until it ends. No answer is not taken as agreement, and nothing ` +
                `is charged at the new price without it.\n\nPlease choose by ${enDate(last)}. For questions, use 'Support' in the app or the help page (/help).` },
  };
}
// The card subscription a held key belongs to -> { h, sub } or null (the same check as a plan change).
async function priceSub(env, key) {
  const h = await heldKey(env, key);
  const sub = String(h?.payload?.sub ?? "");
  return h && /^\d{1,20}$/.test(sub) && String(h.meta.sub || "") === sub ? { h, sub } : null;
}
async function subPrice(req, env) {
  const b = await readJson(req);
  const s = await priceSub(env, b.key);
  if (!s) return json({ error: "key" }, 403);
  const none = { notice: null, choice: "" };
  const plan = await env.LICENSES.get("subplan:" + s.sub), today = kstDay(Date.now());
  const n = (await priceNotices(env)).find(x => x.plan === plan && today < x.effective);
  if (!n) return json(none);
  const was = await env.LICENSES.get("pricechoice:" + n.id + ":" + s.sub);
  const choice = was === "agree" || was === "cancel" ? was : "";
  if (!choice && (await env.LICENSES.get("sub:" + s.sub)) === null) return json(none);   // ended: nothing to agree to
  return json({ notice: { id: n.id, plan: n.plan, oldPrice: n.oldPrice, newPrice: n.newPrice, effective: n.effective,
                          text: priceText(n, b.lang === "en" ? "en" : "ko") }, choice });
}
async function subPriceChoose(req, env) {
  if (await overIp(req, env, "price", IP_LIMIT.price)) return json({ error: "busy" }, 429);
  const b = await readJson(req);
  const id = String(b.id ?? ""), choice = String(b.choice ?? "");
  if (!PRICE_ID.test(id) || !["agree", "cancel"].includes(choice)) return json({ error: "bad request" }, 400);
  const s = await priceSub(env, b.key);
  if (!s) return json({ error: "key" }, 403);
  const plan = await env.LICENSES.get("subplan:" + s.sub);
  const n = (await priceNotices(env)).find(x => x.id === id);
  if (!n || n.plan !== plan) return json({ error: "notice" }, 404);
  if (kstDay(Date.now()) >= n.effective) return json({ error: "closed" }, 409);
  const name = "pricechoice:" + id + ":" + s.sub, was = await env.LICENSES.get(name);
  if (was === choice) return json({ ok: true, choice });   // the same answer again: nothing to write
  if (was === "cancel") return json({ error: "already" }, 409);
  if ((await env.LICENSES.get("sub:" + s.sub)) === null) return json({ error: "inactive" }, 409);
  if ((await takePublic(env, 1)) === "full") return json({ error: "busy" }, 429);   // "down": the address limit holds
  const cancelled = choice === "cancel" ? (await cancelSubscription(env, s.sub, "가격 변경 안내 " + id)).ok : undefined;
  const meta = { choice, at: now(), key: s.h.id, ...(choice === "cancel" ? { cancelled } : {}) };
  await env.LICENSES.put(name, choice, { expirationTtl: PRICE_CHOICE_TTL, metadata: meta });
  await audit(env, "app", choice === "agree" ? "가격 변경 동의" : "가격 변경 해지", "구독 " + s.sub + " · " + PLANS[plan] + " · " + id);
  return json({ ok: true, choice, ...(choice === "cancel" ? { cancelled } : {}) });
}
// The dashboard: the notices with every answer (subscription number, answer, time, key id; who has not answered is not
// listed: that would read every subscription), new notices, deleting one, and moving an agreed subscription.
async function priceList(env) {
  const today = kstDay(Date.now()), notices = [];
  for (const n of await priceNotices(env)) {
    const pre = "pricechoice:" + n.id + ":";
    const l = await env.LICENSES.list({ prefix: pre, limit: 1000 });
    const choices = l.keys.map(k => ({ sub: k.name.slice(pre.length), ...(k.metadata || {}) })).sort((a, b) => String(b.at).localeCompare(String(a.at)));
    notices.push({ ...n, open: today < n.effective, agree: choices.filter(c => c.choice === "agree").length,
                   cancel: choices.filter(c => c.choice === "cancel").length, choices,
                   text: { ko: priceText(n, "ko"), en: priceText(n, "en") }, email: priceEmail(n) });
  }
  return { notices, today, minEffective: kstDay(Date.now() + PRICE_NOTICE_DAYS * 864e5), noticeDays: PRICE_NOTICE_DAYS };
}
const validDay = d => /^\d{4}-\d{2}-\d{2}$/.test(d) && !isNaN(Date.parse(d + "T00:00:00Z")) && new Date(d + "T00:00:00Z").toISOString().slice(0, 10) === d;
async function priceCreate(env, b) {
  const plan = String(b.plan ?? ""), variant = String(b.variant ?? "").trim(), effective = String(b.effective ?? "").trim();
  const oldPrice = Number(b.oldPrice), newPrice = Number(b.newPrice);
  if (!SINGLE_PLANS.includes(plan)) return { status: 400, error: "카드 구독 플랜을 고르세요" };
  if (![oldPrice, newPrice].every(x => Number.isInteger(x) && x >= 1 && x <= PRICE_MAX_WON)) return { status: 400, error: "가격은 1~10,000,000원 사이의 정수예요" };
  if (newPrice <= oldPrice) return { status: 400, error: "새 가격이 지금 가격보다 높을 때만 동의를 받아요" };
  if (!ID_RE.test(variant)) return { status: 400, error: "새 상품 번호는 숫자예요" };
  const c = await lsConfig(env);
  if (variant === c.variants[plan]) return { status: 400, error: "새 상품 번호가 지금 상품 번호와 같아요. Lemon Squeezy에서 새 가격의 상품을 따로 만드세요" };
  if (Object.values(c.variants).includes(variant)) return { status: 400, error: "다른 플랜의 상품 번호예요" };
  const today = kstDay(Date.now()), min = kstDay(Date.now() + PRICE_NOTICE_DAYS * 864e5);
  if (!validDay(effective)) return { status: 400, error: "시행일은 YYYY-MM-DD 날짜예요" };
  if (effective < min) return { status: 400, error: `시행일은 오늘(한국 시간)부터 ${PRICE_NOTICE_DAYS}일 뒤인 ${min}이나 그 뒤여야 해요` };
  if (effective > kstDay(Date.now() + 366 * 864e5)) return { status: 400, error: "시행일은 1년 안이어야 해요" };
  const list = await priceNotices(env);
  if (list.some(x => x.plan === plan && today < x.effective)) return { status: 409, error: "이 플랜에 아직 시행 전인 안내가 있어요. 그 안내를 지우고 다시 만드세요" };
  if (list.length >= PRICE_MAX) return { status: 409, error: "안내는 " + PRICE_MAX + "개까지예요. 지난 안내를 지우세요" };
  const id = [...crypto.getRandomValues(new Uint8Array(4))].map(x => x.toString(16).padStart(2, "0")).join("");
  const n = { id, plan, oldPrice, newPrice, variant, effective, created: now() };
  await env.LICENSES.put("config:price_notices", JSON.stringify([n, ...list]));
  await audit(env, "admin", "가격 변경 안내", PLANS[plan] + " · " + oldPrice + "원 → " + newPrice + "원 · " + effective + " 시행 · " + id);
  return { notice: n, text: { ko: priceText(n, "ko"), en: priceText(n, "en") }, email: priceEmail(n) };
}
async function priceDelete(env, b) {
  const list = await priceNotices(env), n = list.find(x => x.id === String(b.id ?? ""));
  if (!n) return { status: 404, error: "안내를 찾지 못했어요" };
  await env.LICENSES.put("config:price_notices", JSON.stringify(list.filter(x => x !== n)));
  await audit(env, "admin", "가격 변경 안내 삭제", PLANS[n.plan] + " · " + n.effective + " 시행 · " + n.id);
  return { ok: true };
}
async function priceMove(env, b) {
  const n = (await priceNotices(env)).find(x => x.id === String(b.id ?? "")), sub = String(b.sub ?? "");
  if (!n || !/^\d{1,20}$/.test(sub)) return { status: 404, error: "안내를 찾지 못했어요" };
  if (kstDay(Date.now()) < n.effective) return { status: 409, error: `시행일(${n.effective}, 한국 시간)부터 옮길 수 있어요` };
  const name = "pricechoice:" + n.id + ":" + sub, r = await env.LICENSES.getWithMetadata(name);
  if (r?.value !== "agree") return { status: 409, error: "새 가격에 동의한 구독만 옮길 수 있어요" };
  if (r.metadata?.moved) return { status: 409, error: "이미 옮겼어요" };
  if ((await env.LICENSES.get("sub:" + sub)) === null) return { status: 409, error: "끝난 구독이에요" };
  if ((await env.LICENSES.get("subplan:" + sub)) !== n.plan) return { status: 409, error: "이 구독은 지금 다른 플랜이에요" };
  if (!env.LS_API_KEY) return { status: 503, error: "Lemon Squeezy API 키가 없어요" };
  const res = await lsSetVariant(env, sub, n.variant, { disable_prorations: true });
  if (!res.ok) {
    await audit(env, "카드 결제", "새 가격으로 옮기기 실패", "구독 " + sub + " · " + PLANS[n.plan] + " · Lemon Squeezy " + res.status);
    return { status: 502, error: res.status ? "Lemon Squeezy가 받지 않았어요 (" + res.status + ")" : "Lemon Squeezy에 연결하지 못했어요" };
  }
  const moved = now();
  await env.LICENSES.put(name, "agree", { expirationTtl: PRICE_CHOICE_TTL, metadata: { ...(r.metadata || {}), moved } });
  await audit(env, "admin", "새 가격으로 옮김", "구독 " + sub + " · " + PLANS[n.plan] + " · " + won(n.newPrice) + "원 · " + n.id);
  return { ok: true, moved };
}

async function store(env, session, key, email, plan) {
  const o = session && CODE_RE.test(session) ? await getOrder(env, session) : null;
  // The session rides on the checkout address, so a buyer can put any order code there. A card payment never
  // approves a bank-transfer order (the seller checks those by hand); the key stays findable by e-mail.
  if (o && o.method !== "card" && o.method !== "auto") {
    await audit(env, "카드 결제", "카드 주문이 아니라 자동 승인하지 않음", session + " · " + (await keyId(key)) + (email ? " · " + email : ""));
    session = null;
  }
  if (session) await env.LICENSES.put("session:" + session, key, { expirationTtl: SESSION_TTL, metadata: { at: now() } });   // at: /thanks
  if (session && CODE_RE.test(session)) {   // verified payment -> order approved automatically
    const ord = o || { code: session, plan, at: now() };
    // the order says what the app asked for; the key is what was paid for (the variant), and so is the record
    if (ord.plan && ord.plan !== plan) await audit(env, "카드 결제", "주문과 다른 상품으로 결제됨", session + " · " + (PLANS[ord.plan] || ord.plan) + " → " + PLANS[plan]);
    Object.assign(ord, { status: "approved", method: "card", plan, keyId: await keyId(key), email: (email || "").slice(0, 80), approvedAt: now() });
    await putOrder(env, ord);
    await audit(env, "카드 결제", "자동 승인", session + " · " + ord.keyId + (email ? " · " + email : ""));
  }
  if (email) await env.LICENSES.put("email:" + email.toLowerCase(), key);   // for support / restore
}

// ---------------------------------------------------------------- app endpoints
async function claim(s, env) {
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

// Only whoever holds a key of that subscription gets its next key: subscription numbers count up, so a bare
// number would hand out every subscriber's key and e-mail address. The app sends the key it has, exactly as
// it was issued (not its id: ids travel in /check addresses); apps up to 0.8.11 send none and renew by hand.
// The key may have run out: an app that was closed (or asleep) past the end of a paid period, or a payment that Lemon
// Squeezy retried for days, must still get the next key. What it may not be is blocked, or another subscription's.
// Three KV reads, no writes.
async function renew(sub, held, env) {
  if (!/^[0-9]{1,20}$/.test(sub)) return json({ error: "bad sub" }, 400);
  held = held.trim();
  const id = held && held.length <= 4096 ? await keyId(held) : "";
  const r = id ? await env.LICENSES.getWithMetadata("lic:" + id) : null;
  const m = r?.value != null && r.value === held ? r.metadata : null;
  if (!m || String(m.sub || "") !== sub) return json({ error: "key" }, 403);
  if (m.revoked || (await env.LICENSES.get("revoked:" + id)) !== null) return json({ status: "inactive" });
  const key = await env.LICENSES.get("sub:" + sub);
  return json(key ? { status: "ready", key } : { status: "inactive" });
}

// The checkout's "return to" page. Its address (with the order code) stays in the browser's history, so the full key
// shows only for THANKS_SHOW after it was issued; later the page says where the key is instead. (The order code itself
// still claims the key in the app for SESSION_TTL; GET /claim, which apps up to 0.8.11 use, answers the same way.)
const THANKS_SHOW = 60 * 60 * 1000;
async function thanks(url, env) {
  const s = url.searchParams.get("session") || "";
  const r = validSession(s) ? await env.LICENSES.getWithMetadata("session:" + s) : null;
  const key = r?.value ?? null, fresh = key !== null && Date.parse(r?.metadata?.at || "") > Date.now() - THANKS_SHOW;
  const code = CODE_RE.test(s) ? s : "";
  const keep = `키는 메일로 보내지 않아요. 잃어버리면 앱의 '고객센터'나 <a href="/help" style="color:#ff7a1a">도움말 페이지</a>에서 ${code ? "주문 코드 " + code + "를 적어 " : ""}문의해 주세요. 판매자는 결제한 메일 주소로도 키를 찾을 수 있어요.`;
  const body = fresh
    ? `<h1>결제가 완료됐어요</h1><p>SystemBoost가 켜져 있으면 키가 자동으로 등록돼요. 아니라면 아래 키를 복사해 '★ 플랜 → 라이선스 키'(0.8.9 이하는 '★ 프리미엄')에 넣으세요.</p>
       <textarea readonly onclick="this.select()">${escapeHtml(key)}</textarea><p class="m">이 화면은 결제 뒤 1시간 동안만 키를 보여 줘요. ${keep}</p>${await purchaseNote(env, key)}`
    : key !== null
    ? `<h1>결제가 완료됐어요</h1><p>키는 SystemBoost 앱에 자동으로 등록돼요. 이 화면은 결제 뒤 1시간이 지나면 키를 보여 주지 않아요.</p>
       ${code ? `<p>앱이 키를 받지 못했다면 '★ 플랜 → 주문 코드로 키 받기'에 주문 코드 <b>${code}</b>를 넣으세요.</p>` : ""}<p class="m">${keep}</p>${await purchaseNote(env, key)}`
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
// One signed key can be written many ways that all verify: base64 with padding, spaces or other values in a part's
// unused low bits, and the ECDSA twin (r, n - s) of its signature. Each way has its own id, so a route that names a
// key by the text it was given must first bring the text to one spelling, as the app does (License.Canonical):
// canonical base64url, no padding, no spaces. The twin stays a different text; heldKey() checks revocation for both.
// "" when the text cannot be a key at all.
function canonicalKey(text) {
  text = String(text ?? "").replace(/\s+/g, "");
  if (!text || text.length > 4096) return "";
  const parts = text.split(".");
  if (parts.length !== 2 || !parts.every(p => /^[A-Za-z0-9_\-+/]+={0,2}$/.test(p))) return "";
  try { return parts.map(p => b64url(unb64url(p.replace(/=+$/, "")))).join("."); } catch { return ""; }
}
const P256_N = 0xffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551n;
function twinKey(key) {   // the same key with the other valid signature (r, n - s); "" when the signature is not 64 bytes
  const [p, s] = key.split(".");
  let sig;
  try { sig = unb64url(s); } catch { return ""; }
  if (sig.length !== 64) return "";
  let v = 0n;
  for (const b of sig.subarray(32)) v = (v << 8n) | BigInt(b);
  if (v === 0n || v >= P256_N) return "";
  let t = P256_N - v;
  const out = new Uint8Array(64);
  out.set(sig.subarray(0, 32));
  for (let i = 63; i >= 32; i--) { out[i] = Number(t & 0xffn); t >>= 8n; }
  return p + "." + b64url(out);
}
// A key this server issued and still honours, checked the way /renew checks one: its canonical spelling is the text
// registered under its id (so the twin and every other spelling of it are refused), it is not revoked under either
// signature's id, and it has not run out. -> { key, id, payload, meta } or null.
async function heldKey(env, text) {
  const key = canonicalKey(text);
  if (!key) return null;
  const [p, s] = key.split(".");
  if (!(await verifySig(env, p, s))) return null;
  const id = await keyId(key);
  const r = await env.LICENSES.getWithMetadata("lic:" + id);
  if (r?.value !== key || !r.metadata || r.metadata.revoked) return null;
  let payload;
  try { payload = JSON.parse(new TextDecoder().decode(unb64url(p))); } catch { return null; }
  if (!payload || typeof payload !== "object") return null;
  const twin = twinKey(key);
  for (const x of [id, twin && (await keyId(twin))]) if (x && (await env.LICENSES.get("revoked:" + x)) !== null) return null;
  const ends = payload.expires || r.metadata.expires;
  if (ends && !(Date.parse(ends) > Date.now())) return null;
  return { key, id, payload, meta: r.metadata };
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
// Every key a subscription was given, so a refund can turn all of them off. Kept 400 days after the subscription's
// last key (each new key writes it again), longer than any key of it runs; revokeSub also finds older keys in lic:.
const SUBKEYS_TTL = 60 * 60 * 24 * 400;
async function addSubKey(env, sub, id) {
  let ids = [];
  try { ids = JSON.parse((await env.LICENSES.get("subkeys:" + sub)) || "[]"); } catch {}
  if (!Array.isArray(ids)) ids = [];
  if (!ids.includes(id)) ids.push(id);
  await env.LICENSES.put("subkeys:" + sub, JSON.stringify(ids.slice(-200)), { expirationTtl: SUBKEYS_TTL });
}
// who: "webhook" (Lemon Squeezy's refund events) or "admin" (the seller's 환불 완료 in support.js). A webhook refund also
// closes the open refund request that names any key of the subscription (support.js webhookRefundSub): the refund
// webhook finds the request by the current key only while sub:<id> exists, and a cancellation deletes it first.
async function revokeSub(env, sub, reason, who = "webhook") {
  if (!/^\d{1,20}$/.test(sub)) return [];
  const ids = new Set();
  try { for (const id of JSON.parse((await env.LICENSES.get("subkeys:" + sub)) || "[]")) if (/^[0-9a-f]{16}$/.test(id)) ids.add(id); } catch {}
  const current = await env.LICENSES.get("sub:" + sub);
  if (current) ids.add(await keyId(current));
  for (const m of await allRecords(env)) if (String(m.sub || "") === sub) ids.add(m.id);   // keys from before "subkeys:" was kept
  const done = [];
  for (const id of ids) {
    const m = (await env.LICENSES.getWithMetadata("lic:" + id))?.metadata || {};
    if (m.revoked || (await env.LICENSES.get("revoked:" + id)) !== null) continue;
    if (m.expires && Date.parse(m.expires) < Date.now()) continue;   // ran out already
    await revoke(env, id, reason, who);
    done.push(id);
  }
  await env.LICENSES.delete("sub:" + sub);   // no more renewals for that subscription
  if (who === "webhook" && reason === "환불") await webhookRefundSub(env, sub, SUPPORT, [...ids]);
  return done;
}
async function check(url, env) {
  const id = url.searchParams.get("id") || "";
  if (!/^[0-9a-f]{16}$/.test(id)) return json({ error: "bad id" }, 400);
  return json({ revoked: (await env.LICENSES.get("revoked:" + id)) !== null });
}
// The dashboard's 기록: what was done, kept 180 days in the Stats Durable Object (stats.js audit_log), not KV, which
// allows 1,000 writes a day. Only when the object cannot be reached does a line go to KV ("log:", as before); a line
// from the app's own requests (referral and invite gifts: anyone can cause those) is then left out instead, so it
// cannot spend KV writes. A failing record never fails what it records.
// The detail keeps what tells what was done (key ids, order codes, ticket numbers, member ids, days, amounts), not
// who paid: e-mail addresses are masked, and the free text that can hold a payer's name or payment memo is cut to its
// first character (minimise).
async function audit(env, who, action, detail) {
  const row = { who, action, detail: minimise(action, String(detail)).slice(0, 300) };
  try { await stats(env, "/audit", row); return; } catch { /* the object is down: KV below */ }
  if (who === "app") return;
  const inv = String(9999999999999 - Date.now()).padStart(13, "0");   // newest first in KV order
  try { await env.LICENSES.put("log:" + inv + Math.random().toString(36).slice(2, 6), JSON.stringify({ at: now(), ...row }), { expirationTtl: 60 * 60 * 24 * 180 }); }
  catch { /* KV cannot write either */ }
}
const EMAIL_RE = /[^\s@·,;:<>()"']+@[^\s@·,;:<>()"']+\.[A-Za-z]{2,}/g;
function maskEmail(e) {
  const [local, domain] = e.split("@"), dot = domain.lastIndexOf(".");
  return [...local][0] + "***@" + [...domain][0] + "***" + domain.slice(dot);
}
const maskText = s => { const c = [...s.trim()]; return c.length ? c[0] + "**" : ""; };
// Pieces of a detail that are never personal: key ids and lists of them, order and ticket codes, counts and dates.
const SAFE_PIECE = /^(?:[0-9a-f]{16}(?:, [0-9a-f]{16})*|SB-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}|T-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}|\d+대 묶음(?: \d+\/\d+)?|\d+일|\d+회|평생|코드 없음|메일 없음|프로|울트라|산업|[\d,]+원)$/;
// also never personal in a key's issue line: a subscription number, the date an extension continues from, a plan name
const SAFE_ISSUE = /^(?:구독 \d{1,20}|\d{4}-\d{2}-\d{2}부터 이어서)$/;
const PLAN_NAMES = new Set(Object.values(PLANS));
function minimise(action, detail) {
  detail = detail.replace(EMAIL_RE, maskEmail);
  // a key's note (the payer's name and payment memo when the seller approves an order) and a referral code's label
  // (who it was given to) are free text: those pieces are masked. A referral code's first piece is the code itself.
  if (action === "키 발급")
    detail = detail.split(" · ").map(p => SAFE_PIECE.test(p.trim()) || SAFE_ISSUE.test(p.trim()) || PLAN_NAMES.has(p.trim()) || /\*\*\*@/.test(p) ? p : maskText(p)).join(" · ");
  if (action === "추천 코드 생성")
    detail = detail.split(" · ").map((p, i) => i < 3 ? p : maskText(p)).join(" · ");
  return detail;
}

// ---------------------------------------------------------------- admin dashboard
// The page: one <script>, allowed by a nonce made for each load (no inline handlers in admin.html). Styles stay
// 'unsafe-inline' (the page builds rows with style attributes, which a nonce cannot cover). img-src stays https:
// because the Discord announcement preview shows the image address the seller types, from any https host.
function adminPage() {
  const nonce = btoa(String.fromCharCode(...crypto.getRandomValues(new Uint8Array(16))));
  return new Response(ADMIN_HTML.replace("/*SB_SERVED*/false", "true").replaceAll("__NONCE__", nonce), { headers: {
    "content-type": "text/html; charset=utf-8", "cache-control": "no-store",
    "content-security-policy": `default-src 'none'; script-src 'nonce-${nonce}'; style-src 'unsafe-inline'; connect-src 'self' https://api.github.com; img-src data: https:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'`,
    "x-frame-options": "DENY", "referrer-policy": "no-referrer", "x-content-type-options": "nosniff" } });
}

// Wrong-password lockout: 5 misses per address per 15 minutes, 20 misses in all per hour. A short PIN is only safe
// because of this (20 guesses an hour vs 1,000,000 six-digit PINs). The counts live in the Stats Durable Object, one
// atomic step per try (stats.js gate), so tries sent at once are counted one by one and no KV write is spent. When the
// object cannot be reached, an address's misses are counted in this isolate's memory and the hour's misses in KV
// ("fail:all:<hour>", one write a miss and none once locked: at most 21 an hour), in memory too when KV cannot write
// (its daily writes used up): whatever fails, a wrong password gets 401 or 429 after the delay, never a 500.
// The address is kept only as a salted hash (IPv6 per /64, like the support limits).
const LOCK = { ip: 5, all: 20, delay: 400 };
const lockMem = new Map(), sessMem = new Map();   // name -> end time (ms) / count; the fallbacks only
const lockedReply = () => json({ error: "비밀번호를 여러 번 틀려서 잠시 잠겼어요. 15분~1시간 뒤 다시 시도하세요." }, 429, false);
const pause = ms => new Promise(r => setTimeout(r, ms));
const sha256hex = async s => [...new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(s)))].map(b => b.toString(16).padStart(2, "0")).join("");
let admSalt = null, admSaltOf = null;
async function adminTag(env, ip) {
  const k = env.LICENSE_PRIVATE_KEY || "";
  if (admSaltOf !== k) { admSalt = k ? await sha256hex("systemboost-admin-ip|" + k) : ""; admSaltOf = k; }
  return (await sha256hex(admSalt + "|" + ipBucket(ip) + "|sb-admin")).slice(0, 16);
}
function memCount(name) { const m = lockMem.get(name); return m && m.exp > Date.now() ? m.n : 0; }
function memBump(name, seconds) {   // synchronous, so tries at once in this isolate are counted one by one
  const t = Date.now(), n = memCount(name) + 1;
  if (lockMem.size > 5000) for (const [k, m] of lockMem) if (m.exp <= t) lockMem.delete(k);
  lockMem.set(name, { n, exp: t + seconds * 1000 });
  return n;
}
async function kvCount(env, name) { try { return Number((await env.LICENSES.get(name)) || 0) || 0; } catch { return 0; } }
async function fallbackFails(env, tag, hour) {
  const ipName = "fail:ip:" + tag, allName = "fail:all:" + hour, kvAll = await kvCount(env, allName);
  return { ipName, allName, kvAll, ip: memCount(ipName), all: kvAll + memCount(allName) };
}
// A wrong password: count it, -> the counts after it.
async function lockFail(env, tag, hour) {
  try {
    const r = await stats(env, "/gate", { ip: tag, hour, ipMax: LOCK.ip, allMax: LOCK.all, fail: true });
    if (Number.isInteger(r.ip) && Number.isInteger(r.all)) return r;
  } catch { /* KV and memory below */ }
  const f = await fallbackFails(env, tag, hour);
  const ip = memBump(f.ipName, 15 * 60), all = f.kvAll + memCount(f.allName) + 1;
  if (ip <= LOCK.ip && all <= LOCK.all) {   // once locked, nothing more is written
    try { await env.LICENSES.put(f.allName, String(f.kvAll + 1), { expirationTtl: 2 * 3600 }); }
    catch { memBump(f.allName, 2 * 3600); }
  }
  return { ip, all };
}
// The right password: refused while either count is at its limit; else the access is recorded (see adminAccess).
async function lockPass(env, tag, hour, route, view) {
  try {
    const r = await stats(env, "/gate", { ip: tag, hour, ipMax: LOCK.ip, allMax: LOCK.all, route, action: view?.action || "", target: view?.target || "" });
    if (r.locked) return false;
    if (r.ok) return true;
  } catch { /* KV and memory below */ }
  const f = await fallbackFails(env, tag, hour);
  if (f.ip >= LOCK.ip || f.all >= LOCK.all) return false;
  await accessKv(env, tag, route, view);
  return true;
}

// Admin access log (privacy: who saw personal data): every login (the first successful call from an address after
// 30 quiet minutes) and every call that shows personal data, with the time, the salted address hash, the route, what
// it showed and which item (a key or ticket id, a list's filter; never a search's words). Kept one year in the Stats
// Durable Object (stats.js admin_access), written in the same call as the lockout check. Only while the object is down
// does a row go to KV ("acc:", one write a row, kept as long).
const ADMIN_VIEWS = {
  "GET /keys": "키 목록", "POST /keys": "키 검색", "GET /key": "키 원문", "POST /lookup": "키 확인", "GET /orders": "주문 목록",
  "GET /members": "회원 목록", "POST /members": "회원 검색", "GET /support": "문의 목록", "POST /support": "문의 검색",
  "GET /support/ticket": "문의 열람", "GET /log": "작업 기록", "GET /price": "가격 변경 동의",
  "GET /recovery": "복구 요청 목록", "POST /recovery": "복구 요청 목록",
  "POST /preorders/export": "사전예약 메일 내보내기",
};
function viewOf(route, url, body) {
  const action = ADMIN_VIEWS[route];
  if (!action) return null;
  const p = url.searchParams;
  const target = route.startsWith("POST ") && route !== "POST /lookup" ? (String(body.q || "").trim() ? "검색" : String(body.status || ""))
               : p.get("id") || p.get("status") || "";
  return { action, target: String(target).slice(0, 40) };
}
async function accessKv(env, tag, route, view) {
  const t = Date.now(), login = !((sessMem.get(tag) || 0) > t);
  sessMem.set(tag, t + 30 * 60e3);
  if (sessMem.size > 1000) for (const [k, e] of sessMem) if (e <= t) sessMem.delete(k);
  const rows = [...(login ? [{ action: "login", target: "" }] : []), ...(view ? [view] : [])];
  for (const r of rows) {
    const inv = String(9999999999999 - t).padStart(13, "0");
    try { await env.LICENSES.put("acc:" + inv + Math.random().toString(36).slice(2, 6), JSON.stringify({ at: t, ip: tag, route, ...r }), { expirationTtl: 60 * 60 * 24 * 366 }); }
    catch { /* nowhere to write it */ }
  }
}
async function listAccess(env, before) {
  let r = { rows: [], more: false, keepDays: 366 }, down = false;
  try { r = await stats(env, "/access/read", before ? { before } : {}); } catch { down = true; }
  const rows = (r.rows || []).map(x => ({ ...x, at: new Date(x.at).toISOString().replace(/\.\d+Z$/, "Z") }));
  if (!before) {   // the KV rows (written while the object was down) come with the first page
    const k = await env.LICENSES.list({ prefix: "acc:", limit: 100 });
    for (const x of k.keys) {
      let v = null;
      try { v = JSON.parse((await env.LICENSES.get(x.name)) || "null"); } catch {}
      if (v) rows.push({ at: new Date(v.at).toISOString().replace(/\.\d+Z$/, "Z"), ip: v.ip, route: v.route, action: v.action, target: v.target || "", kv: true });
    }
    rows.sort((a, b) => String(b.at).localeCompare(String(a.at)));
  }
  return { rows, more: !!r.more, keepDays: r.keepDays || 366, ...(down ? { down: true } : {}) };
}

async function admin(req, url, env, ctx) {
  if (!env.ADMIN_TOKEN || env.ADMIN_TOKEN.length < 6) return json({ error: "not found" }, 404);   // off unless configured
  if (url.pathname === "/admin" && req.method === "GET") return adminPage();
  const tag = await adminTag(env, req.headers.get("CF-Connecting-IP") || "unknown");
  const hour = now().slice(0, 13);
  if (!(await tokenOk(req.headers.get("Authorization") || "", env.ADMIN_TOKEN))) {
    await pause(LOCK.delay);
    const n = await lockFail(env, tag, hour);
    return n.ip > LOCK.ip || n.all > LOCK.all ? lockedReply() : json({ error: "unauthorized" }, 401, false);
  }
  const route = req.method + " " + url.pathname.slice("/admin/api".length);
  const body = req.method === "POST" ? await readJson(req) : {};
  if (!(await lockPass(env, tag, hour, route, viewOf(route, url, body)))) { await pause(LOCK.delay); return lockedReply(); }
  // searches go in a POST body: words in an address end up in logs and browser history
  if (req.method === "GET" && url.searchParams.get("q") && ["GET /keys", "GET /members", "GET /support"].includes(route))
    return json({ error: "검색어는 POST 본문으로 보내요" }, 400, false);
  if (route === "GET /preorders" || route.startsWith("POST /preorders/")) {
    let res;   // the rows are only in the Stats object: when it cannot be reached, say so (not the generic 500)
    try { res = await preorderAdmin(env, route, body, { stats, audit: (action, detail) => audit(env, "admin", action, detail) }); }
    catch { return json({ error: "통계 저장소에 연결하지 못했어요. 잠시 뒤 다시 시도하세요", error_en: "Could not reach the stats store. Try again in a moment." }, 503, false); }
    const { status = 200, ...r } = res;
    return json(r, status, false);
  }
  if (route === "GET /announce" || route.startsWith("POST /announce/")) {
    const r = await announce(env, route, body, (action, detail) => audit(env, "admin", action, detail));
    return json(r, r.status || 200, false);
  }
  if (route === "GET /support" || route === "POST /support" || route.startsWith("GET /support/") || route.startsWith("POST /support/")) {
    const { status = 200, ...r } = await supportAdmin(env, route, body, url, SUPPORT);
    return json(r, status, false);
  }
  switch (route) {
    case "GET /summary": return json(await summary(env), 200, false);
    case "GET /keys": return json(await listKeys(env, "", url.searchParams.get("status") || "all"), 200, false);
    case "POST /keys": return json(await listKeys(env, String(body.q || ""), String(body.status || "all")), 200, false);
    case "GET /log": return json(await listLog(env), 200, false);
    case "GET /access": {
      const b = url.searchParams.get("before");
      if (b !== null && !/^\d{1,15}$/.test(b)) return json({ error: "before는 숫자예요" }, 400, false);
      return json(await listAccess(env, b === null ? 0 : Number(b)), 200, false);
    }
    case "GET /stats": return json(await stats(env, "/read"), 200, false);
    case "POST /downloads": return json(await stats(env, "/downloads", body.releases), 200, false);
    case "POST /issue": { const { status = 200, ...r } = await adminIssue(env, body); return json(r, status, false); }
    case "GET /members":
    case "POST /members": {
      const r = await accounts(env, "admin/list", { q: route === "POST /members" ? String(body.q || "") : "" });
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
    // 복구 요청 (accounts v2): members who lost their 2-step device and backup codes. Approve only after the 72-hour wait.
    case "GET /recovery":
    case "POST /recovery": {
      const r = await accounts(env, "admin/recovery_list", { all: route === "POST /recovery" && body.all === true });
      if (r.error) return json({ error: r.error === "unavailable" ? "Supabase에 연결하지 못했어요" : r.error }, r.status, false);
      return json({ ...r, mail: mailOn(env) }, 200, false);
    }
    case "POST /recovery/approve": {
      const r = await accounts(env, "admin/recovery_approve", { id: String(body.id || ""), origin: url.origin }, ctx);
      if (r.error) return json({ error: r.error === "unavailable" ? "Supabase에 연결하지 못했어요" : r.error }, r.status, false);
      await audit(env, "admin", "계정 복구 승인", String(body.id) + " · " + r.account + (r.mailed ? " · 재설정 링크 메일" : " · 복구 코드 재발급"));
      return json(r, 200, false);
    }
    case "POST /recovery/reject": {
      const r = await accounts(env, "admin/recovery_reject", { id: String(body.id || ""), reason: body.reason });
      if (r.error) return json({ error: r.error === "unavailable" ? "Supabase에 연결하지 못했어요" : r.error }, r.status, false);
      await audit(env, "admin", "계정 복구 거절", String(body.id) + " · " + r.account);
      return json({ ok: true }, 200, false);
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
      // cancel: true also stops the billing of the key's card subscription (cancelSubscription, payments section)
      const sub = body.cancel === true ? String((await env.LICENSES.getWithMetadata("lic:" + body.id))?.metadata?.sub || "") : "";
      return json({ ok: true, ...(sub ? { cancelled: await cancelSubscription(env, sub, String(body.reason || "관리자 차단")) } : {}) }, 200, false);
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
      let cursor, truncated = true;
      for (let i = 0; i < 10; i++) {
        const r = await env.LICENSES.list({ prefix: "ord:", cursor, limit: 1000 });
        for (const k of r.keys) all.push({ code: k.name.slice(4), ...(k.metadata || {}) });
        if (r.list_complete) { truncated = false; break; } cursor = r.cursor;
      }
      const count = Object.fromEntries(ORDER_STATES.map(st => [st, all.filter(o => o.status === st).length]));
      const list = all.filter(o => want === "all" || (want === "open" ? ["requested", "paid"].includes(o.status) : o.status === want))
                      .sort((a, b) => (b.status === "paid") - (a.status === "paid") || String(b.updated || b.at).localeCompare(String(a.updated || a.at)));
      return json({ orders: list.slice(0, 300), count, ...(list.length > 300 ? { more: true } : {}), ...(truncated ? { truncated: true } : {}) }, 200, false);
    }
    case "POST /orders/approve": { const a = await approveOrder(env, body); return json(a.body, a.http, false); }
    case "POST /orders/reject": { const a = await rejectOrder(env, body); return json(a.body, a.http, false); }
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
    case "GET /price": return json(await priceList(env), 200, false);
    case "POST /price": case "POST /price/delete": case "POST /price/move": {
      const f = { "POST /price": priceCreate, "POST /price/delete": priceDelete, "POST /price/move": priceMove }[route];
      const { status = 200, ...r } = await f(env, body);
      return json(r, status, false);
    }
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
  out.truncated = true;   // said on the dashboard when there are more than 20k keys
  for (let page = 0; page < 20; page++) {          // up to 20k keys; more than enough for an indie shop
    const r = await env.LICENSES.list({ prefix: "lic:", cursor, limit: 1000 });
    for (const k of r.keys) out.push({ id: k.name.slice(4), ...(k.metadata || {}) });
    if (r.list_complete) { out.truncated = false; break; }
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
  if (!op.list_complete) c.ordersMore = true;     // counted from the first 1,000 orders only
  if (all.truncated) c.keysMore = true;           // counted from the first 20,000 keys only
  const { depositTickets = {}, ...sc } = await supportCounts(env);
  Object.assign(c, sc);   // ticketsOpen, refundsOpen (ticketsMore: from the first 1,000)
  const sales = await salesMode(env), sellerMissing = sellerGaps(await supportConfig(env));
  return { sales, autoReady: await cardPlans(env), cardLiveOpen: cardLiveOpen(env),
           salesFromDashboard: MODES.includes(await env.LICENSES.get("config:sales_mode")), counts: c, budget: await kvBudget(env),
           // the dashboard's 할 일: which of the seller's details are empty, and whether that stops sales now (live mode)
           sellerMissing, sellerBlocked: sales === "live" && sellerMissing.length > 0,
           deposits: depositsDue(op.keys, depositTickets, t) };
}
// The dashboard's 할 일 for bank transfers: every order whose buyer said "I paid" and that the seller has not decided
// yet, oldest first, with the 24 hours (DEPOSIT_HOURS) counted from its first "I paid" (paidAt). state: "ok", "warn"
// from DEPOSIT_WARN_HOURS, "late" from DEPOSIT_HOURS. ticket: its open 입금 확인 ticket, if any. From the summary's
// own order list (no extra KV call); at most 50.
function depositsDue(keys, tickets, t = Date.now()) {
  const out = [];
  for (const k of keys) {
    const m = k.metadata;
    if (m?.status !== "paid") continue;
    const code = k.name.slice(4), from = Date.parse(m.paidAt || m.updated || m.at || "");
    if (!Number.isFinite(from)) continue;
    const hours = (t - from) / 36e5;
    out.push({ code, plan: m.plan || "", paidAt: new Date(from).toISOString().replace(/\.\d+Z$/, "Z"),
               due: new Date(from + DEPOSIT_HOURS * 36e5).toISOString().replace(/\.\d+Z$/, "Z"),
               hoursLeft: Math.round((DEPOSIT_HOURS - hours) * 10) / 10,
               state: hours >= DEPOSIT_HOURS ? "late" : hours >= DEPOSIT_WARN_HOURS ? "warn" : "ok", ...(tickets[code] ? { ticket: tickets[code] } : {}) });
  }
  return out.sort((a, b) => a.paidAt.localeCompare(b.paidAt)).slice(0, 50);
}
// Today's share of KV writes for requests anyone can make (Workers free plan), as [count, cap] for the dashboard's
// home: orders, "I paid" notes and gifts (publicKvDay), gifts (REF_DAILY) and support (both lanes). One read of the
// Stats object, no KV; null when it does not answer (the counts are then in KV, and the caps still hold).
async function kvBudget(env) {
  const s = budgetNames(), names = [pubName(), refName(), s.w, s.vw];
  try {
    const r = await stats(env, "/peek", { names });
    if (!r?.n) return null;
    const v = k => Number(r.n[k]) || 0;
    return { pub: [v(names[0]), publicKvDay(env)], gifts: [v(names[1]), REF_DAILY],
             support: [v(s.w) + v(s.vw), SUPPORT_LIMITS.writesPerDay + SUPPORT_LIMITS.vWritesPerDay] };
  } catch { return null; }
}
async function listKeys(env, q, status) {
  q = q.trim().toLowerCase();
  let all = await allRecords(env);
  const truncated = all.truncated;
  for (const m of all) m.state = state(m);
  if (status !== "all") all = all.filter(m => m.state === status);
  if (q) all = all.filter(m => [m.id, m.email, m.note, m.order, m.sub].some(v => String(v || "").toLowerCase().includes(q)));
  all.sort((a, b) => String(b.issued).localeCompare(String(a.issued)));
  return { keys: all.slice(0, 500), more: all.length > 500, ...(truncated ? { truncated: true } : {}) };
}
// The Durable Object's newest 100 lines, and the KV lines (from before the move, or written while the object was
// down), newest first; KV lines are shown cut down the same way (they were written before minimise() existed).
async function listLog(env) {
  let rows = [], down = false;
  try { rows = (await stats(env, "/audit/read")).log || []; } catch { down = true; }
  const r = await env.LICENSES.list({ prefix: "log:", limit: 100 });
  for (const k of r.keys) {
    let v = null;
    try { v = JSON.parse((await env.LICENSES.get(k.name)) || "null"); } catch {}
    if (v) rows.push({ ...v, detail: minimise(String(v.action || ""), String(v.detail ?? "")) });
  }
  rows.sort((a, b) => String(b.at).localeCompare(String(a.at)));
  return { log: rows.slice(0, 100), ...(down ? { down: true } : {}) };
}
// ---------------------------------------------------------------- referral codes
// Whoever enters a code gets a short key, once per device. The person who shared the code is
// rewarded by hand, from the use count on the dashboard.
const REF_RE = /^[A-Z0-9]{4,16}$/;
const REF_DEVICE_TTL = 60 * 60 * 24 * 90;
const REF_DAILY = 60;    // gifts a day (referral codes and member invites together), whatever the codes say
const GIFT_WRITES = 3;  // KV writes of one gift: refdev:, lic: and the code's ref: (an invite writes 2; counted as 3)

async function getRef(env, code) {
  const v = REF_RE.test(code) ? await env.LICENSES.get("ref:" + code) : null;
  return v ? JSON.parse(v) : null;
}
async function putRef(env, r) { await env.LICENSES.put("ref:" + r.code, JSON.stringify(r), { metadata: r }); }

const refDay = () => "count:ref:" + now().slice(0, 10);   // KV: only while the Stats object cannot be reached
const refName = () => "kv:ref:" + now().slice(0, 10);     // the Stats object's count of today's gifts
const REF_DEVICE = /^[0-9a-f]{32}$/;

// What stops a device from taking a gift; null when nothing does.
async function refBlocked(env, device) {
  let full = false;
  try {
    const r = await stats(env, "/peek", { names: [refName(), pubName()] });
    full = (Number(r?.n?.[refName()]) || 0) >= REF_DAILY || (Number(r?.n?.[pubName()]) || 0) + GIFT_WRITES > publicKvDay(env);
  } catch { /* the KV count below */ }
  // the KV count is read too: it holds the gifts of a day the object could not count (and of the day of the update)
  if (full || Number((await env.LICENSES.get(refDay())) || 0) >= REF_DAILY) return { status: 429, error: "busy" };
  if (await env.LICENSES.get("refdev:" + device)) return { status: 409, error: "already" };
  return null;
}
function refClosed(r) {
  if (!r.on) return { status: 404, error: "unknown" };
  return r.used >= r.max ? { status: 409, error: "used up" } : null;
}
async function grant(env, code, device, plan, days) {
  await env.LICENSES.put("refdev:" + device, code, { expirationTtl: REF_DEVICE_TTL });
  // refBlocked checked the caps; this only counts (a race may pass them by one or two)
  if (await takePublic(env, GIFT_WRITES, [{ name: refName(), ttl: 2 * 86400 }], false) === "down") {
    const day = refDay();
    await env.LICENSES.put(day, String(Number((await env.LICENSES.get(day)) || 0) + 1), { expirationTtl: 60 * 60 * 48 });
  }
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
  if (await overIp(req, env, "referral", IP_LIMIT.referral)) return json({ error: "busy" }, 429);   // a gift is 3 KV writes
  const b = await readJson(req);
  const code = String(b.code || "").trim().toUpperCase(), device = String(b.device || "");
  if (!REF_RE.test(code) || !REF_DEVICE.test(device)) return json({ error: "bad request" }, 400);
  const r = await getRef(env, code);
  if (!r) return json({ error: (await accounts(env, "code", { code })).member ? "signup" : "unknown" }, 409);
  const stop = refClosed(r) || (await refBlocked(env, device));
  return stop ? json({ error: stop.error }, stop.status) : json(await useRef(env, r, device));
}

// ---------------------------------------------------------------- member accounts (src/accounts.js, src/account_web.js)
// What the account routes use from here: the 아이디 sign-up with its referral gifts, the invite reward key, and reads of
// the key registry (KV reads only) for the keys of a member and the link-a-key mail.
const ACCOUNT_DEPS = {
  signup: (env, b) => signup(env, b),
  reward: (env, b) => reward(env, b),
  isSellerCode: async (env, code) => REF_RE.test(code) && !!(await getRef(env, code)),
  keyRecord: (env, text) => linkableKey(env, text),
  readKey: (env, id) => memberKey(env, id),
  emailKeyId: async (env, email) => { const k = await env.LICENSES.get("email:" + email); return k ? keyId(k) : null; },
  keyId,
};
// A key a member may link to the account: issued here, registered in this spelling, not revoked under either signature.
// Unlike heldKey() a key that has run out may be linked (the member still sees it in the list).
async function linkableKey(env, text) {
  const key = canonicalKey(text);
  if (!key) return null;
  const [p, s] = key.split(".");
  if (!(await verifySig(env, p, s))) return null;
  const id = await keyId(key);
  const r = await env.LICENSES.getWithMetadata("lic:" + id);
  if (r?.value !== key || !r.metadata || r.metadata.revoked) return null;
  const twin = twinKey(key);
  for (const x of [id, twin && (await keyId(twin))]) if (x && (await env.LICENSES.get("revoked:" + x)) !== null) return null;
  let payload = {};
  try { payload = JSON.parse(new TextDecoder().decode(unb64url(p))) || {}; } catch { /* the record's email is enough */ }
  return { id, payload, meta: r.metadata };
}
async function memberKey(env, id) {
  const r = await env.LICENSES.getWithMetadata("lic:" + id);
  if (!r?.value) return null;
  const m = r.metadata || {};
  const revoked = !!m.revoked || (await env.LICENSES.get("revoked:" + id)) !== null;
  return { id, plan: m.plan || "", key: r.value, issued: m.issued || "", expires: m.expires || "", revoked };
}

// A problem with the code stops the sign-up, so nothing is left half done and the user can fix it.
async function signup(env, b) {
  const invite = String(b.invite || "").trim().toUpperCase(), device = String(b.device || "");
  if (!invite) return accounts(env, "signup", { ...b, invite: "", ticket: false });   // the discount ticket comes with a code only, never from the request
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

// The seller approves or rejects an order: 판매·키's buttons (POST /orders/approve | reject) and a 입금 확인 ticket's
// (support.js POST /support/deposit) do the same thing. {http, body}: the HTTP status and what those routes answer.
async function approveOrder(env, b) {
  const o = CODE_RE.test(b.code || "") ? await getOrder(env, b.code) : null;
  if (!o) return { http: 404, body: { error: "주문을 찾지 못했어요" } };
  if (o.status === "approved") return { http: 409, body: { error: "이미 승인된 주문이에요" } };
  if (o.status === "refunded") return { http: 409, body: { error: "환불한 주문이에요" } };
  const r = await adminIssue(env, { code: o.code, plan: o.plan, email: b.email, note: b.note || [o.payer, o.msg].filter(Boolean).join(" · ") });
  if (!r.error && o.ticket) await accounts(env, "ticket/used", { order: o.code });
  return { http: r.error ? 400 : 200, body: r };
}
async function rejectOrder(env, b) {
  const o = CODE_RE.test(b.code || "") ? await getOrder(env, b.code) : null;
  if (!o) return { http: 404, body: { error: "주문을 찾지 못했어요" } };
  if (o.status === "approved") return { http: 409, body: { error: "승인된 주문은 거절할 수 없어요. 키를 차단하세요" } };
  if (o.status === "refunded") return { http: 409, body: { error: "환불한 주문이에요" } };
  o.status = "rejected"; o.reason = String(b.reason || "").trim().slice(0, 100) || "결제를 확인하지 못했어요";
  await putOrder(env, o);
  if (o.ticket) await accounts(env, "ticket/release", { order: o.code });
  await audit(env, "admin", "거절", o.code + " · " + o.reason);
  return { http: 200, body: { ok: true } };
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
  else if (Object.hasOwn(PLAN_DAYS, period)) days = PLAN_DAYS[period] + GRACE_DAYS;
  else if (period !== "lifetime" || !code) return { error: "플랜을 고르세요" };   // an order placed while it was on sale can still be approved
  if (period === "days" && !(days >= 1 && days <= 3650)) return { error: "기간은 1~3650일이에요" };
  if (!note && !code) return { error: "주문 코드나 메모(발급 이유) 중 하나는 적어 주세요" };
  const seats = b.seats == null || b.seats === "" ? seatsOf(plan) : Math.floor(Number(b.seats));
  if (!(seats >= 1 && seats <= MAX_SEATS)) return { error: "키 개수는 1~" + MAX_SEATS + "개예요" };
  const prev = code ? await getOrder(env, code) : null;
  // a refunded order stays refunded: issuing for its code again would set it back to approved and let /claim hand out
  // a key for money that was paid back (status: the HTTP status for POST /issue)
  if (prev?.status === "refunded")
    return { status: 409, error: "환불한 주문이에요. 이 주문 코드로는 키를 다시 발급하지 않아요. 다시 팔았다면 새 주문 코드로 발급하거나, 주문 코드 없이 메모를 적어 발급하세요" };
  // one payment, one key: a second issue for an approved code (two tabs, a retry, the form) would leave two valid keys.
  // reissue: true replaces them on purpose, and the order's earlier keys are blocked.
  if (prev?.status === "approved" && b.reissue !== true)
    return { status: 409, error: "이미 키를 발급한 주문이에요. 키를 다시 보내려면 키 목록의 '키 복사'를 쓰세요. 같은 주문으로 새 키를 만들려면 재발급을 고르세요(이전 키는 막아요)" };
  const before = prev?.status === "approved" ? [...new Set([prev.keyId, ...(await bundleIds(env, code))])].filter(x => /^[0-9a-f]{16}$/.test(x || "")) : [];
  const src = code ? "payment" : "manual";
  // the key's "plan": ultra also opens frame generation x6 and x8, biz adds the work and streaming features
  const tier = tierOf(plan) !== "premium" ? tierOf(plan) : TIERS.includes(b.tier) ? b.tier : "premium";
  // 기간 연장: the order names a key the buyer still holds (order(), extendable()). The new period then runs from that
  // key's end, unless it was blocked since (a refund, sharing). One PC only; a bundle starts today.
  let from = now(), extended = "";
  const ext = prev?.extendFrom;
  if (days && seats === 1 && ext && /^[0-9a-f]{16}$/.test(ext.id || "") && Date.parse(ext.expires) > Date.now()) {
    if ((await env.LICENSES.get("revoked:" + ext.id)) === null) { from = addDays(ext.expires, 0); extended = ext.id; }
  }
  const keys = [], ids = [];
  for (let i = 1; i <= seats; i++) {   // a bundle is that many separate keys: one per PC, each can be turned off alone
    const k = await makeKey(env, { plan: tier, email, expires: days ? addDays(from, days) : undefined, src,
                                   note: [code, seats > 1 ? seats + "대 묶음 " + i + "/" + seats : "", extended ? "기간 연장 " + extended : "", note].filter(Boolean).join(" · ") });
    keys.push(k); ids.push(await keyId(k));
  }
  const key = keys[0], id = ids[0];
  if (code) {   // the buyer's app is polling /claim?session=<code> -> popup
    const ttl = { expirationTtl: 60 * 60 * 24 * 30 };
    await env.LICENSES.put("session:" + code, key, { ...ttl, metadata: { at: now() } });
    if (seats > 1) await env.LICENSES.put("bundle:" + code, JSON.stringify(keys), ttl);
    else if (prev?.seats > 1) await env.LICENSES.delete("bundle:" + code);
    const o = prev || { code, plan, at: now(), method: "manual" };
    Object.assign(o, { status: "approved", keyId: id, email: email.slice(0, 80), approvedAt: now(), ...(seats > 1 ? { seats } : {}) });
    if (seats === 1) delete o.seats;
    await putOrder(env, o);
    for (const old of before) await revoke(env, old, "재발급", "admin");   // a reissue: the earlier keys of this order
  }
  // a bundle's key ids go last: the log keeps 300 characters
  await audit(env, "admin", "키 발급", (seats > 1 ? seats + "대 묶음" : id) + " · " + (code || "코드 없음") + " · " + (email || "메일 없음") + " · " +
              (days ? days + "일" : "평생") + (extended ? " · " + from.slice(0, 10) + "부터 이어서" : "") + (tier !== "premium" ? " · " + TIER_KO[tier] : "") +
              (note ? " · " + note : "") + (seats > 1 ? " · " + ids.join(", ") : ""));
  return { key, id, keys, ids, ...(extended ? { extendedFrom: from } : {}) };
}
async function bundleIds(env, code) {
  try {
    const keys = JSON.parse((await env.LICENSES.get("bundle:" + code)) || "[]");
    return Array.isArray(keys) ? await Promise.all(keys.filter(k => typeof k === "string").map(keyId)) : [];
  } catch { return []; }
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
  const { key, payload } = await signKey(env, fields);
  await register(env, key, payload, fields.src || "manual", fields.note || "");
  return key;
}
// Signs a key without storing anything (the webhook notes its id before registering it).
async function signKey(env, fields) {
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
  return { key: b64url(data) + "." + b64url(sig), payload };
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
async function readJson(req) { const b = await req.json().catch(() => null); return b && typeof b === "object" ? b : {}; }   // "null" is JSON too
function now() { return new Date().toISOString().replace(/\.\d+Z$/, "Z"); }
const kstDay = ms => new Date(ms + 9 * 36e5).toISOString().slice(0, 10);   // the day in Korea (UTC+9), "2026-12-09"
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
