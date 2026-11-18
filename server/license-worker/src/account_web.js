// The /account/* routes of the Worker (index.js hands them here) and POST /keys/resend.
//  - JSON actions: POST /account/<action> (src/accounts.js). The address, country and session come from the request
//    itself, never from the body. "features" also answers GET.
//  - Pages for the links in mails: /account/verify, /account/reset, /account/link, /account/recovery/cancel. GET only
//    shows a page with a button (mail scanners open links); the form's POST does the work. No script, a nonce for the
//    one style, no referrer, the token in the form body (not the address) once the page is open. Korean or English by
//    ?lang= or Accept-Language. A token is never logged.
//  - Before accounts v2 start (ACCOUNTS_V2_FROM, src/accounts_gate.js): legacyWeb(), the /account/* of 2026-11-13.
import { accounts, cleanEmail } from "./accounts.js";
import { accountsV2Open } from "./accounts_gate.js";
import { mailOn, sendMail } from "./mail.js";
import { ipBucket } from "./support.js";

const OLD_ACTIONS = ["signup", "login", "recover", "me", "invite", "reward", "logout", "password", "delete"];
const NEW_ACTIONS = ["features", "signup_email", "verify", "login2", "forgot", "reset", "add_email", "devices", "logout_device", "logout_all",
                     "totp_start", "totp_confirm", "totp_off", "backup_new", "backup_left", "keys", "link_key", "link_key_confirm", "consent",
                     "recovery_request", "recovery_cancel"];
export const ACCOUNT_ACTIONS = [...OLD_ACTIONS, ...NEW_ACTIONS];
const PAGES = ["verify", "reset", "link", "recovery/cancel"];
export const RESEND = { perIp: 10, perAddress: 3 };   // POST /keys/resend an hour

function json(o, status = 200) {
  return new Response(JSON.stringify(o), { status, headers: { "content-type": "application/json", "cache-control": "no-store", "access-control-allow-origin": "*" } });
}
async function readJson(req) { const b = await req.json().catch(() => null); return b && typeof b === "object" && !Array.isArray(b) ? b : {}; }

// What the Worker knows about the request, set over whatever the body says.
function fromRequest(req, url, b) {
  b.ip = req.headers.get("CF-Connecting-IP") || "unknown";
  b.token = (req.headers.get("Authorization") || "").replace(/^Bearer /, "");
  const c = req.cf?.country;
  b.country = typeof c === "string" && /^[A-Z][A-Z0-9]$/.test(c) ? c : "";
  b.origin = url.origin;
  return b;
}

/** /account/*. deps (index.js): signup and reward (아이디 sign-up with referral gifts, invite reward key), and the
 *  licence key reads accounts() needs (keyRecord, readKey, emailKeyId, isSellerCode). */
export async function accountWeb(req, url, env, ctx, deps = {}) {
  if (!accountsV2Open(env)) return legacyWeb(req, url, env, deps);
  const action = url.pathname.slice("/account/".length);
  if (PAGES.includes(action)) {
    if (req.method === "GET") return page(req, url, env, ctx, deps, action, null);
    if (req.method === "POST" && /application\/x-www-form-urlencoded/i.test(req.headers.get("Content-Type") || "")) {
      const form = await req.formData().catch(() => null);
      return page(req, url, env, ctx, deps, action, form);
    }
  }
  if (!ACCOUNT_ACTIONS.includes(action)) return json({ error: "not found" }, 404);
  if (action === "features") return json({ email: mailOn(env), totp: true });
  const body = fromRequest(req, url, req.method === "POST" ? await readJson(req) : {});
  const r = action === "signup" && deps.signup ? await deps.signup(env, body)
          : action === "reward" && deps.reward ? await deps.reward(env, body)
          : await accounts(env, action, body, ctx, deps);
  const { status = 200, ...rest } = r;
  for (const k of Object.keys(rest)) if (k.startsWith("_")) delete rest[k];   // the pages' own hints
  return json(rest, status);
}

// Before accounts v2 start: /account/* as the server of 2026-11-13 (commit 776dd71) had it. Its nine actions, with only
// the address and the session taken from the request; anything else (the v2 actions, "features", the pages for mailed
// links) is its 404 {"error": "not found"}. The app reads that 404 from "features" as an older server: no v2 parts.
async function legacyWeb(req, url, env, deps) {
  const action = url.pathname.slice("/account/".length);
  if (!OLD_ACTIONS.includes(action)) return json({ error: "not found" }, 404);
  let body = {};
  if (req.method === "POST") { const b = await req.json().catch(() => null); body = b && typeof b === "object" ? b : {}; }   // as then
  body.ip = req.headers.get("CF-Connecting-IP") || "unknown";
  body.token = (req.headers.get("Authorization") || "").replace(/^Bearer /, "");
  const r = action === "signup" && deps.signup ? await deps.signup(env, body)
          : action === "reward" && deps.reward ? await deps.reward(env, body)
          : await accounts(env, action, body);
  const { status = 200, ...rest } = r;
  return json(rest, status);
}

// ---------------------------------------------------------------- POST /keys/resend
// The newest key bought with an address (the KV "email:" index the card payments write), mailed to that address.
// Always the same reply, after the same reads; limits per address and per IP in the Stats object (no KV write).
export async function keysResend(req, env, ctx, deps = {}) {
  if (!mailOn(env)) return json({ error: "mail_off" }, 503);
  const b = await readJson(req);
  const email = cleanEmail(b.email);
  if (!email) return json({ error: "email" }, 400);
  const lang = b.lang === "en" ? "en" : "ko", hour = new Date().toISOString().slice(0, 13);
  if (!(await take(env, [{ name: "keys:ip:" + (await tag(env, "ip", ipBucket(req.headers.get("CF-Connecting-IP")))) + ":" + hour, ttl: 3600, max: RESEND.perIp }])))
    return json({ error: "busy" }, 429);
  const mayMail = await take(env, [{ name: "keys:to:" + (await tag(env, "to", email)) + ":" + hour, ttl: 3600, max: RESEND.perAddress }]);
  const key = await env.LICENSES.get("email:" + email);
  const id = key && deps.keyId ? await deps.keyId(key) : "0".repeat(16);
  const rec = await env.LICENSES.getWithMetadata("lic:" + id);
  const revoked = (await env.LICENSES.get("revoked:" + id)) !== null || !!rec?.metadata?.revoked;
  if (mayMail && key && rec?.value === key && !revoked)
    sendMail(env, ctx, { to: email, kind: "key_resend", lang, data: { key, plan: rec.metadata?.plan, expires: rec.metadata?.expires } });
  return json({ sent: true });
}

const sha = async s => [...new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(s)))].map(x => x.toString(16).padStart(2, "0")).join("");
let salt = null, saltOf = null;
async function tag(env, what, value) {   // a counter name that cannot be turned back into the address without the signing key
  const k = env?.LICENSE_PRIVATE_KEY || "";
  if (saltOf !== k) { salt = k ? await sha("systemboost-keys|" + k) : ""; saltOf = k; }
  return (await sha(salt + "|" + what + "|" + value)).slice(0, 24);
}
const mem = new Map();
export function _forgetResendLimits() { mem.clear(); }   // tests
async function take(env, items) {
  try {
    const o = env.STATS.get(env.STATS.idFromName("all"));
    const r = await o.fetch("https://stats/take", { method: "POST", body: JSON.stringify({ items }) });
    if (r.ok) { const j = await r.json(); if (j?.ok === true) return true; if (j?.ok === false) return false; }
  } catch { /* memory below */ }
  const now = Date.now(), n = it => { const m = mem.get(it.name); return m && m.exp > now ? m.n : 0; };
  if (items.some(it => n(it) + 1 > it.max)) return false;
  for (const it of items) mem.set(it.name, { n: n(it) + 1, exp: now + it.ttl * 1000 });
  if (mem.size > 5000) for (const [k, v] of mem) if (v.exp <= now) mem.delete(k);
  return true;
}

// ---------------------------------------------------------------- pages for the links in mails
const esc = s => String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
function langOf(req, url, form) {
  const q = form?.get("lang") || url.searchParams.get("lang");
  if (q === "en" || q === "ko") return q;
  const al = String(req.headers.get("Accept-Language") || "").toLowerCase();
  return /^en\b/.test(al) || (!/\bko\b/.test(al) && /\ben\b/.test(al)) ? "en" : "ko";
}

const TEXT = {
  ko: {
    expired: "링크가 만료됐거나 이미 쓰였어요. 앱에서 다시 시도하세요.",
    unavailable: "지금은 처리할 수 없어요. 잠시 뒤 다시 시도하세요.",
    busy: "시도가 너무 많아요. 잠시 뒤 다시 시도하세요.",
    verify: { title: "메일 주소 확인", lead: "앱에서 가입할 때 정한 비밀번호를 넣고 아래 버튼을 누르면 회원 가입이 끝나요.", button: "가입 완료",
              p1: "비밀번호", wrong: "비밀번호가 맞지 않아요. 앱에서 가입할 때 정한 비밀번호를 넣으세요. 다섯 번 틀리면 이 링크는 더 쓸 수 없어요.",
              done: "가입이 끝났어요. 이제 앱의 '◈ 계정'에서 이 메일 주소와 비밀번호로 로그인하세요.",
              already: "이 메일 주소는 이미 다른 계정에 연결돼 있어요. 앱에서 그 계정으로 로그인하거나 '비밀번호 찾기'를 쓰세요." },
    add: { title: "메일 주소 연결", lead: "아래 버튼을 누르면 이 메일 주소가 메일에 적힌 아이디의 계정에 연결되고, 이 주소로 산 키가 그 계정에 보여요. 본인이 요청하지 않았다면 누르지 마세요.", button: "연결 완료",
           done: "메일 주소를 연결했어요. 이제 이 주소로도 로그인하고 비밀번호 재설정 링크를 받을 수 있어요." },
    reset: { title: "새 비밀번호 정하기", lead: "새 비밀번호를 두 번 입력하세요. 바꾸면 모든 기기에서 로그아웃돼요.",
             rule: "10~72자이고, 아이디나 메일 주소(또는 @ 앞부분)와 같지 않고, 흔한 비밀번호가 아니어야 해요.",
             p1: "새 비밀번호", p2: "새 비밀번호 확인", button: "비밀번호 바꾸기",
             mismatch: "두 비밀번호가 달라요.", weak: "비밀번호가 규칙에 맞지 않아요.",
             done: "비밀번호를 바꿨어요. 모든 기기에서 로그아웃됐으니 앱에서 새 비밀번호로 로그인하세요." },
    link: { title: "키를 계정에 연결", lead: "아래 버튼을 누르면 이 메일 주소로 산 키가 요청한 회원 계정에 연결돼요. 본인이 요청하지 않았다면 누르지 마세요.",
            button: "연결하기", done: "키를 계정에 연결했어요. 앱의 '◈ 계정'에서 볼 수 있어요." },
    cancel: { title: "계정 복구 요청 취소", lead: "아래 버튼을 누르면 이 계정의 복구 요청이 취소돼요.", button: "복구 요청 취소",
              done: "복구 요청을 취소했어요. 본인이 한 요청이 아니었다면 앱에서 비밀번호를 바꾸고 2단계 인증을 켜세요.",
              closed: "이미 처리됐거나 취소된 요청이에요." },
  },
  en: {
    expired: "This link has expired or was already used. Please try again from the app.",
    unavailable: "This cannot be done right now. Please try again in a while.",
    busy: "Too many tries. Please try again in a while.",
    verify: { title: "Confirm your email", lead: "Enter the password you chose in the app and press the button below to finish signing up.", button: "Finish sign-up",
              p1: "Password", wrong: "The password is not right. Enter the password you chose when signing up in the app. After five wrong tries this link stops working.",
              done: "You're signed up. Now log in under '◈ Account' in the app with this email address and your password.",
              already: "This email address already belongs to another account. Log in to that account in the app or use 'Forgot password'." },
    add: { title: "Link your email", lead: "Pressing the button below links this email address to the account with the ID in the email, and keys bought with this address show in that account. If you did not ask for this, do not press it.", button: "Finish",
           done: "Your email address is linked. You can now log in with it and get password reset links at it." },
    reset: { title: "Choose a new password", lead: "Enter the new password twice. Changing it logs you out on every device.",
             rule: "10 to 72 characters, not your ID or email address (or the part before @), and not a common password.",
             p1: "New password", p2: "New password again", button: "Change password",
             mismatch: "The two passwords differ.", weak: "The password does not meet the rules.",
             done: "Your password was changed. Every device was logged out; log in to the app with the new password." },
    link: { title: "Link a key to your account", lead: "Press the button below to link the key bought with this email address to the member account that asked. If you did not ask for this, do not press it.",
            button: "Link", done: "The key is linked to your account. You can see it under '◈ Account' in the app." },
    cancel: { title: "Cancel the account recovery request", lead: "Press the button below to cancel the recovery request for this account.", button: "Cancel the request",
              done: "The recovery request was cancelled. If you did not make it, change your password in the app and turn on 2-step verification.",
              closed: "This request was already decided or cancelled." },
  },
};

function html(lang, title, inner, status = 200) {
  const nonce = btoa(String.fromCharCode(...crypto.getRandomValues(new Uint8Array(16))));
  const doc = `<!DOCTYPE html><html lang="${lang}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="robots" content="noindex"><title>SystemBoost · ${esc(title)}</title>
<style nonce="${nonce}">
body{margin:0;background:#0b0b0c;color:#e8e6e3;font-family:system-ui,-apple-system,"Segoe UI","Malgun Gothic",sans-serif;line-height:1.6}
main{max-width:520px;margin:0 auto;padding:40px 20px}
.brand{color:#FF7A1A;font-weight:700;font-size:18px;margin-bottom:20px}
h1{font-size:22px;margin:0 0 12px}
p{margin:0 0 14px}.m{color:#8b8a88;font-size:14px}.err{color:#ff8a80}
label{display:block;margin:14px 0 6px;font-size:14px}
input[type=password]{width:100%;box-sizing:border-box;background:#141416;color:#e8e6e3;border:1px solid #333;border-radius:10px;padding:12px;font-size:16px}
button{margin-top:18px;background:#FF7A1A;color:#0b0b0c;border:0;border-radius:10px;padding:12px 20px;font-weight:700;font-size:16px;cursor:pointer}
</style></head><body><main><div class="brand">SystemBoost</div><h1>${esc(title)}</h1>${inner}</main></body></html>`;
  return new Response(doc, { status, headers: {
    "content-type": "text/html; charset=utf-8", "cache-control": "no-store",
    "content-security-policy": `default-src 'none'; style-src 'nonce-${nonce}'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'`,
    "x-frame-options": "DENY", "referrer-policy": "no-referrer", "x-content-type-options": "nosniff" } });
}

// GET: the page with its button (and, for a reset, the form). POST (form): the work, then what came of it.
async function page(req, url, env, ctx, deps, which, form) {
  const lang = langOf(req, url, form), T = TEXT[lang];
  const t = String((form ? form.get("t") : url.searchParams.get("t")) || "");
  const add = (form ? form.get("add") : url.searchParams.get("add")) === "1";
  const kind = which === "verify" ? (add ? "add" : "verify") : which === "recovery/cancel" ? "cancel" : which;
  const P = T[kind];
  const msg = (text, status = 200, cls = "") => html(lang, P.title, `<p${cls ? ` class="${cls}"` : ""}>${esc(text)}</p>`, status);
  const failed = r => msg(r.error === "expired" ? T.expired : r.error === "already" ? T.verify.already : r.error === "closed" ? P.closed || T.expired
                          : r.error === "busy" || r.error === "locked" ? T.busy : T.unavailable, r.status || 400, "err");
  if (!/^[0-9a-f]{64}$/.test(t)) return msg(T.expired, 400, "err");
  const action = "/account/" + which;
  const hidden = `<input type="hidden" name="t" value="${esc(t)}"><input type="hidden" name="lang" value="${lang}">${add ? '<input type="hidden" name="add" value="1">' : ""}`;
  const resetForm = (err = "") => html(lang, P.title, `<p>${esc(P.lead)}</p><p class="m">${esc(P.rule)}</p>${err ? `<p class="err">${esc(err)}</p>` : ""}
<form method="post" action="${action}">${hidden}
<label for="p1">${esc(P.p1)}</label><input id="p1" name="password" type="password" autocomplete="new-password" minlength="10" maxlength="72" required>
<label for="p2">${esc(P.p2)}</label><input id="p2" name="password2" type="password" autocomplete="new-password" minlength="10" maxlength="72" required>
<button type="submit">${esc(P.button)}</button></form>`, err ? 400 : 200);

  // a sign-up's verify page asks for the password chosen in the app
  const verifyForm = (err = "") => html(lang, P.title, `<p>${esc(P.lead)}</p>${err ? `<p class="err">${esc(err)}</p>` : ""}
<form method="post" action="${action}">${hidden}
<label for="p1">${esc(P.p1)}</label><input id="p1" name="password" type="password" autocomplete="current-password" maxlength="72" required>
<button type="submit">${esc(P.button)}</button></form>`, err ? 400 : 200);

  const body = fromRequest(req, url, { t, lang });
  if (!form) {
    if (which === "reset") {
      const r = await accounts(env, "reset_peek", body, ctx, deps);
      return r.error ? failed(r) : resetForm();
    }
    if (kind === "verify") return verifyForm();
    return html(lang, P.title, `<p>${esc(P.lead)}</p><form method="post" action="${action}">${hidden}<button type="submit">${esc(P.button)}</button></form>`);
  }
  if (which === "reset") {
    const p1 = String(form.get("password") || ""), p2 = String(form.get("password2") || "");
    if (p1 !== p2) return resetForm(P.mismatch);
    const r = await accounts(env, "reset", { ...body, password: p1 }, ctx, deps);
    if (r.error === "weak") return resetForm(P.weak);
    return r.error ? failed(r) : msg(P.done);
  }
  const act = { verify: "verify", link: "link_key_confirm", "recovery/cancel": "recovery_cancel" }[which];
  const r = await accounts(env, act, kind === "verify" ? { ...body, password: String(form.get("password") || "") } : body, ctx, deps);
  if (r.error === "password" && kind === "verify") return verifyForm(P.wrong);
  if (r.error) return failed(r);
  return msg(which === "verify" ? (r._added ? T.add.done : T.verify.done) : P.done);
}
