import { ADMIN_HTML } from "./admin_page.js";
// SystemBoost license server (Cloudflare Workers, free plan is enough).
//
//   POST /checkout?plan=monthly|yearly|lifetime&session=<id>
//                                creates a Lemon Squeezy checkout carrying the session -> {url}
//   POST /webhook/lemonsqueezy   Lemon Squeezy webhook (X-Signature HMAC-SHA256 checked)
//   GET  /claim?session=<id>     app polls this after opening checkout -> {status, key}
//   GET  /thanks?session=<id>    checkout "return to" page that shows the key
//   GET  /renew?sub=<id>         latest key of an active subscription (app renews before expiry)
//   GET  /status                 {sales, pay:{monthly,yearly,lifetime}} - the app enables "결제하기" only for plans
//                                that have a payment link (links are set in the dashboard)
//   POST /order?plan=…           app gets an order code (SB-XXXX-XXXX) to write in the payment memo;
//                                the owner confirms the payment and issues the key for that code in /admin,
//                                and the app (polling /claim?session=<code>) pops it up
//   GET  /check?id=<key id>      {revoked} - the app asks this at start (offline = keeps working)
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
// Vars (wrangler.toml [vars]): SALES_MODE ("off" | "test" | "live"), LS_STORE_ID, LS_VARIANT_MONTHLY, LS_VARIANT_YEARLY, LS_VARIANT_LIFETIME, PUBLIC_URL
// KV namespace binding: LICENSES

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

const CODE_RE = /^SB-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$/;   // no 0/O/1/I: easy to read aloud
function validSession(s) { return /^[A-Za-z0-9-]{16,64}$/.test(s) || CODE_RE.test(s); }
const PLANS = { monthly: "월간", yearly: "연간", lifetime: "평생" };
const PLAN_DAYS = { monthly: 31, yearly: 366 };            // manual subscription keys (+3 days grace)

async function payLinks(env) {
  try { const o = JSON.parse((await env.LICENSES.get("config:pay_urls")) || "{}"); 
        return Object.fromEntries(Object.keys(PLANS).filter(p => /^https:\/\/\S+$/.test(o[p] || "")).map(p => [p, o[p]])); }
  catch { return {}; }
}

// ---------------------------------------------------------------- orders
// Every purchase starts as an order with a code (SB-XXXX-XXXX):
//   requested  신청 중          app opened the payment page
//   paid       결제 확인 요청    buyer pressed "결제했어요" (manual payments: name/memo for matching)
//   approved   승인            key issued (by the seller in /admin, or automatically by a verified webhook)
//   rejected   거절            seller rejected it; the app shows the reason
// Two payment methods can be open at the same time; the buyer picks one in the app:
//   manual = seller's own payment links (bank transfer etc.), the seller approves in /admin
//   card   = Lemon Squeezy card checkout (sales mode test/live). Monthly/yearly are card subscriptions:
//            the card is saved and charged every period, renewals refresh the key automatically.
//            The verified payment webhook approves the order and issues the key.
const ORDER_TTL = 60 * 60 * 24 * 90;
const ORDER_STATES = ["requested", "paid", "approved", "rejected"];
async function getOrder(env, code) {
  const r = await env.LICENSES.getWithMetadata("ord:" + code);
  return r && r.value !== null && r.metadata ? { code, ...r.metadata } : null;
}
async function putOrder(env, o) {
  const { code, ...m } = o;
  m.updated = now();
  await env.LICENSES.put("ord:" + code, "", { expirationTtl: ORDER_TTL, metadata: m });
}
function lsVariant(env, plan) {
  const v = { monthly: env.LS_VARIANT_MONTHLY, yearly: env.LS_VARIANT_YEARLY, lifetime: env.LS_VARIANT_LIFETIME }[plan];
  return v && v !== "REPLACE" && env.LS_API_KEY ? v : null;
}
async function openPlans(env) {   // plans the app may sell right now, per method
  const mode = await salesMode(env);
  return { methods: { card: mode === "off" ? [] : Object.keys(PLANS).filter(p => lsVariant(env, p)),
                      manual: Object.keys(await payLinks(env)) } };
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
  let payUrl;
  if (method === "card") { payUrl = await lsCheckout(env, plan, code); if (!payUrl) return json({ error: "checkout failed" }, 502); }
  else payUrl = (await payLinks(env))[plan];
  await putOrder(env, { code, plan, at: now(), status: "requested", method });
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

export default {
  async fetch(req, env) {
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
      if (url.pathname === "/admin" || url.pathname.startsWith("/admin/")) return await admin(req, url, env);
      return json({ error: "not found" }, 404);
    } catch (e) {
      return json({ error: "server error" }, 500);
    }
  },
};

// ---------------------------------------------------------------- checkout (automatic payments)
async function checkout(url, env) {   // kept for direct use; the app goes through /order
  if ((await salesMode(env)) === "off") return json({ error: "sales closed" }, 503);
  const plan = url.searchParams.get("plan");
  const session = url.searchParams.get("session") || "";
  if (!PLANS[plan] || !validSession(session)) return json({ error: "bad request" }, 400);
  const u = await lsCheckout(env, plan, session);
  return u ? json({ url: u }) : json({ error: "checkout failed" }, 502);
}
async function lsCheckout(env, plan, session) {
  const variant = { monthly: env.LS_VARIANT_MONTHLY, yearly: env.LS_VARIANT_YEARLY, lifetime: env.LS_VARIANT_LIFETIME }[plan];
  if (!variant) return null;
  const res = await fetch("https://api.lemonsqueezy.com/v1/checkouts", {
    method: "POST",
    headers: { Authorization: "Bearer " + env.LS_API_KEY, Accept: "application/vnd.api+json", "Content-Type": "application/vnd.api+json" },
    body: JSON.stringify({ data: {
      type: "checkouts",
      attributes: {
        checkout_data: { custom: { session, plan } },
        product_options: { redirect_url: `${env.PUBLIC_URL}/thanks?session=${session}` },
      },
      relationships: {
        store: { data: { type: "stores", id: String(env.LS_STORE_ID) } },
        variant: { data: { type: "variants", id: String(variant) } },
      },
    } }),
  });
  if (!res.ok) return null;
  const j = await res.json();
  return j?.data?.attributes?.url || null;
}

// ---------------------------------------------------------------- webhook
async function webhook(req, env) {
  const raw = await req.text();
  if (!(await validSignature(raw, req.headers.get("X-Signature") || "", env.LS_SIGNING_SECRET)))
    return json({ error: "bad signature" }, 401);
  const body = JSON.parse(raw);
  const event = body?.meta?.event_name;
  const testOrder = body?.meta?.test_mode === true;
  // refunds are handled in every mode: the key of that order is revoked
  if (event === "order_refunded") {
    const id = await env.LICENSES.get("order:" + String(body?.data?.id));
    if (id) await revoke(env, id, "환불", "webhook");
    return json({ ok: true });
  }
  const mode = await salesMode(env);
  if (mode === "off" || (mode === "live" && testOrder) || (mode === "test" && !testOrder))
    return json({ ok: true, ignored: "sales mode " + mode });   // 200 so Lemon Squeezy does not retry
  const custom = body?.meta?.custom_data || {};
  const a = body?.data?.attributes || {};
  const session = typeof custom.session === "string" && validSession(custom.session) ? custom.session : null;

  // one-time purchase (lifetime) -> key without expiry
  if (event === "order_created" && a.status === "paid" && custom.plan === "lifetime") {
    const key = await makeKey(env, { plan: "premium", email: a.user_email || "", order: String(body.data.id),
                                     expires: testOrder ? addDays(new Date().toISOString(), 1) : undefined, test: testOrder, src: "order" });
    await store(env, session, key, a.user_email);
    return json({ ok: true });
  }
  // subscription start / renewal -> key that expires after the paid period
  if (["subscription_created", "subscription_updated", "subscription_payment_success"].includes(event)) {
    const subId = String(event === "subscription_payment_success" ? a.subscription_id : body.data.id);
    const sub = event === "subscription_payment_success" ? null : a;
    const renewsAt = sub?.renews_at || a.renews_at || null;
    const status = sub?.status || "active";
    if (!["active", "on_trial"].includes(status) && event !== "subscription_payment_success") {
      await env.LICENSES.delete("sub:" + subId);   // cancelled/expired: stop issuing renewals
      return json({ ok: true });
    }
    let expires = renewsAt ? addDays(renewsAt, GRACE_DAYS) : addDays(new Date().toISOString(), 31 + GRACE_DAYS);
    if (testOrder) expires = addDays(new Date().toISOString(), 1);   // test keys never last
    const key = await makeKey(env, { plan: "premium", email: a.user_email || "", sub: subId, expires, test: testOrder, src: "subscription" });
    await env.LICENSES.put("sub:" + subId, key);
    await store(env, session, key, a.user_email);
    return json({ ok: true });
  }
  return json({ ok: true, ignored: event });
}

async function store(env, session, key, email) {
  if (session) await env.LICENSES.put("session:" + session, key, { expirationTtl: SESSION_TTL });
  if (session && CODE_RE.test(session)) {   // verified payment -> order approved automatically
    const o = (await getOrder(env, session)) || { code: session, plan: "", at: now() };
    Object.assign(o, { status: "approved", method: "card", keyId: await keyId(key), email: (email || "").slice(0, 80), approvedAt: now() });
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
  if (key) return json({ status: "ready", key });
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
    ? `<h1>결제가 완료됐어요</h1><p>SystemBoost가 켜져 있으면 키가 자동으로 등록돼요. 아니라면 아래 키를 복사해 '★ 프리미엄 → 라이선스 키'에 넣으세요.</p>
       <textarea readonly onclick="this.select()">${escapeHtml(key)}</textarea><p class="m">같은 키가 결제한 메일 주소로도 저장돼 있어요. 키를 잃어버리면 문의해 주세요.</p>`
    : `<h1>결제를 확인하는 중이에요</h1><p>보통 몇 초 걸려요. 이 페이지는 자동으로 새로고침돼요.</p><script>setTimeout(()=>location.reload(),3000)</script>`;
  return new Response(`<!DOCTYPE html><html lang="ko"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>SystemBoost 프리미엄</title><style>body{background:#0b0b0c;color:#e8e6e3;font-family:system-ui,sans-serif;max-width:640px;margin:60px auto;padding:0 20px;line-height:1.6}
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
      "content-security-policy": "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; img-src data:; frame-ancestors 'none'",
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
  switch (route) {
    case "GET /summary": return json(await summary(env), 200, false);
    case "GET /keys": return json(await listKeys(env, url.searchParams.get("q") || "", url.searchParams.get("status") || "all"), 200, false);
    case "GET /log": return json(await listLog(env), 200, false);
    case "POST /issue": return json(await adminIssue(env, body), 200, false);
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
      const r = await adminIssue(env, { code: o.code, plan: o.plan, email: body.email, note: body.note || [o.payer, o.msg].filter(Boolean).join(" · ") });
      return json(r, r.error ? 400 : 200, false);
    }
    case "POST /orders/reject": {
      const o = CODE_RE.test(body.code || "") ? await getOrder(env, body.code) : null;
      if (!o) return json({ error: "주문을 찾지 못했어요" }, 404, false);
      if (o.status === "approved") return json({ error: "승인된 주문은 거절할 수 없어요. 키를 차단하세요" }, 409, false);
      o.status = "rejected"; o.reason = String(body.reason || "").trim().slice(0, 100) || "결제를 확인하지 못했어요";
      await putOrder(env, o);
      await audit(env, "admin", "거절", o.code + " · " + o.reason);
      return json({ ok: true }, 200, false);
    }
    case "POST /orders/delete": {
      if (!CODE_RE.test(body.code || "")) return json({ error: "bad code" }, 400, false);
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
      for (const p of Object.keys(PLANS)) {
        const v = String(body[p] || "").trim();
        if (v && !/^https:\/\/\S+$/.test(v)) return json({ error: PLANS[p] + " 링크는 https:// 로 시작해야 해요" }, 400, false);
        if (v) o[p] = v;
      }
      await env.LICENSES.put("config:pay_urls", JSON.stringify(o));
      await audit(env, "admin", "결제 링크", Object.keys(o).map(p => PLANS[p]).join(", ") || "모두 비움");
      return json({ pay: o }, 200, false);
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
  return { sales: await salesMode(env), autoReady: Object.keys(PLANS).filter(p => lsVariant(env, p)), salesFromDashboard: MODES.includes(await env.LICENSES.get("config:sales_mode")), counts: c };
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
async function adminIssue(env, b) {
  const email = String(b.email || "").trim().slice(0, 120);
  const note = String(b.note || "").trim().slice(0, 200);
  const code = String(b.code || "").trim().toUpperCase();
  if (code && !CODE_RE.test(code)) return { error: "주문 코드 형식이 아니에요 (SB-XXXX-XXXX)" };
  let days = 0;
  if (b.plan === "days") days = Math.floor(Number(b.days));
  else if (PLAN_DAYS[b.plan]) days = PLAN_DAYS[b.plan] + GRACE_DAYS;
  else if (b.plan !== "lifetime") return { error: "플랜을 고르세요" };
  if (b.plan === "days" && !(days >= 1 && days <= 3650)) return { error: "기간은 1~3650일이에요" };
  if (!note && !code) return { error: "주문 코드나 메모(발급 이유) 중 하나는 적어 주세요" };
  const src = code ? "payment" : "manual";
  const key = await makeKey(env, { plan: "premium", email, expires: days ? addDays(now(), days) : undefined, src,
                                   note: (code ? code + (note ? " · " : "") : "") + note });
  const id = await keyId(key);
  if (code) {   // the buyer's app is polling /claim?session=<code> -> popup
    await env.LICENSES.put("session:" + code, key, { expirationTtl: 60 * 60 * 24 * 30 });
    const o = (await getOrder(env, code)) || { code, plan: b.plan, at: now(), method: "manual" };
    Object.assign(o, { status: "approved", keyId: id, email: email.slice(0, 80), approvedAt: now() });
    await putOrder(env, o);
  }
  await audit(env, "admin", "키 발급", id + " · " + (code || "코드 없음") + " · " + (email || "메일 없음") + " · " +
              (days ? days + "일" : "평생") + (note ? " · " + note : ""));
  return { key, id };
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
