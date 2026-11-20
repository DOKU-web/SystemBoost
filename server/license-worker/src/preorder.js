// Pre-registration (사전예약) from the official site's popup, open until PRE.until. One email per person, kept only to
// send the launch email once, all deleted at PRE.deleteAt (the site and the consent text say "2027년 2월 28일에 삭제").
//
// The rows live in the Stats Durable Object's SQLite (src/stats.js, table preorders), not in KV: KV's 1,000 writes a day
// are shared with orders and keys, and a sign-up rush must not take them. No IP address or User-Agent is stored.
//
// Limits, all counted by the Stats object in the same step as the sign-up (not in an isolate's memory, which on
// *.workers.dev is all iplimit.js has): PRE.perIpHour per address (IPv4, IPv6 /64) and PRE.perNetHour per network
// (IPv4 /24, IPv6 /48) an hour, as salted hashes the Worker makes (iplimit.js ipTags), and PRE.perDay in all a Korean day,
// the storage backstop. 20 a network: four addresses' worth, so a home, office or campus is not shut out, while one
// network can take at most 480 a day (16% of the day) and filling the day takes seven or more networks.
// A sign-up is taken only from the official site's pages (PRE.origins, or env PREORDER_ORIGINS, comma-separated) and only
// as application/json, so another site cannot make its visitors' browsers sign up (a text/plain form needs no preflight).
// A script can send any Origin; the counts above hold it.
//
//   GET  /preorder   {open, until}   — the site shows the popup only while open is true; anyone may ask (CORS *)
//   POST /preorder   {email, plan, lang, consent: true, adConsent: true} -> {ok} | {error: origin|type|body|consent|email|closed|busy}
//                    {ok} also when the address had signed up already: the answer must not tell anyone whether an
//                    address is on the list (the notice promises it is given to nobody else).
//                    adConsent: the separate consent to receive advertising information (the launch email); without it
//                    (missing or not true) 400 {error: "consent"} like consent. Its time is kept with the row (adAt, ms,
//                    like at); rows taken before it have adAt null. lang is stored as "ko" whatever is sent.
//   GET  /preorder/unsubscribe   a page (Korean and English) with one email field; POST it (a same-origin form) to
//                    delete that address's row. The answer is the same whether or not the address was on the list
//                    (sender Riye, the request's Korean date, deleted and not sent again). Counted per address and
//                    network an hour in the Stats object (PRE.unsubIpHour, PRE.unsubNetHour). The audit log gets
//                    "사전예약 수신 거부 1건" when a row went, never the address. Open until PRE.unsubUntil (410 after),
//                    not tied to the sign-up deadline or PREORDER=off.
//   admin (index.js): GET /preorders (counts and recent rows without emails), POST /preorders/seen,
//                     POST /preorders/export (every email, as CSV text; logged), POST /preorders/delete {email},
//                     POST /preorders/hook {hook}, /preorders/hook/test, /preorders/hook/clear (the Discord alert below)
// env PREORDER = "off" (any case) closes sign-ups early; env PREORDER_UNTIL (ISO time) moves the deadline.
//
// The Discord alert: the owner may give a webhook of a channel only they can see (KV PRE_HOOK, like announce.js keeps
// its addresses: never in a reply, an error or a log line). A new row wakes the Stats object's alarm PRE.noteWait later,
// and that one alarm posts every row since the last alert as one message of counts: how many, by plan, by language,
// today's and all. Never an email address, so no personal data goes to Discord (the notice names no one for it).
// A failed post is tried again PRE.noteRetry later for a day at most; a webhook Discord no longer has is not.
import { HOOK_RE, PUBLIC_CHANNEL_IDS, readHook, postText } from "./announce.js";

export const PRE = {
  until: "2026-11-27T14:59:59Z",      // 2026-11-27 23:59:59 KST
  deleteAt: "2027-02-27T15:00:00Z",   // 2027-02-28 00:00 KST: the Stats object deletes every row (hourly tick and every call)
  unsubUntil: "2027-02-28T14:59:59Z", // 2027-02-28 23:59:59 KST: the unsubscribe page answers until then, 410 after
  perIpHour: 5, perNetHour: 20, perDay: 3000, recent: 100,
  unsubIpHour: 10, unsubNetHour: 30,  // unsubscribe requests an hour per address / per network (as the sign-up's)
  body: 2048,                         // bytes of a POST body read at most; the site's is under 400
  origins: ["https://doku-web.github.io"],
  plans: ["undecided", "free", "premium", "ultra", "biz"],
  langs: ["en", "ko", "ja", "zh", "es", "pt", "ru", "de", "fr"],
  noteWait: 20e3, noteRetry: 10 * 60e3, noteGiveUp: 864e5,
};
export const PRE_PLAN_KO = { undecided: "아직 모름", free: "무료", premium: "프로", ultra: "울트라", biz: "산업" };
export const PRE_LANG_KO = { en: "영어", ko: "한국어", ja: "일본어", zh: "중국어", es: "스페인어", pt: "포르투갈어", ru: "러시아어", de: "독일어", fr: "프랑스어" };
export const PRE_HOOK = "config:preorder_hook";

/** The alert for rows [{plan, lang}] that came since the last one, with the totals s ({n, today}): counts only. */
export function preNoteText(rows, s) {
  const by = (f, names) => Object.entries(rows.reduce((o, r) => (o[r[f]] = (o[r[f]] || 0) + 1, o), {}))
    .sort((a, b) => b[1] - a[1]).map(([k, n]) => (names[k] || k) + " " + n).join(" · ");
  return [`🔔 사전예약 새 신청 ${rows.length}건`, "관심 플랜: " + by("plan", PRE_PLAN_KO), "언어: " + by("lang", PRE_LANG_KO),
          `오늘(한국 시간) ${s.today}명 · 모두 ${s.n}명`, "자세한 건 관리자 패널의 사전예약 탭에서 볼 수 있어요."].join("\n");
}

export function preUntil(env) {
  const v = env?.PREORDER_UNTIL;
  return typeof v === "string" && !Number.isNaN(Date.parse(v)) ? new Date(Date.parse(v)).toISOString() : PRE.until;
}
/** The origins a sign-up may come from: env PREORDER_ORIGINS (comma-separated) when it names any, else PRE.origins. */
export function preOrigins(env) {
  const v = env?.PREORDER_ORIGINS;
  const list = typeof v === "string" ? v.split(",").map(s => s.trim().toLowerCase().replace(/\/+$/, "")).filter(Boolean) : [];
  return list.length ? list : PRE.origins;
}
const preOff = env => String(env?.PREORDER ?? "").trim().toLowerCase() === "off";
export const preOpen = (env, t = Date.now()) => !preOff(env) && t <= Date.parse(preUntil(env)) && t < Date.parse(PRE.deleteAt);
/** The dashboard shows the 사전예약 tab until the rows are deleted. */
export const preShown = (t = Date.now()) => t < Date.parse(PRE.deleteAt);

// A new address: plain ASCII, the characters a browser's type=email field takes (RFC 5322 atext before the @, a dotted
// domain of letters, digits and hyphens with a letter or xn-- top level after it), and it starts with a letter, digit or _.
// So no stored address can start with = + - @ (a spreadsheet runs such a cell as a formula when the dashboard's CSV
// is opened) or hold a quote, comma or space. Real addresses with + and dots are fine.
const EMAIL = /^[a-z0-9_][a-z0-9!#$%&'*+/=?^_`{|}~.-]*@(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+(?:[a-z]{2,63}|xn--[a-z0-9-]{1,59})$/;
// What the first version took (any characters but spaces around one @). Rows taken before EMAIL may look like this,
// so the dashboard's delete still finds them.
const EMAIL_OLD = /^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/;
const lowered = v => String(v ?? "").trim().toLowerCase();

/** The address as stored and compared: trimmed, lower case; null when it is not one. */
export function preEmail(v) {
  const e = lowered(v);
  return e.length <= 254 && EMAIL.test(e) ? e : null;
}
/** An address to delete: the same, but anything the first version may have stored is taken too. */
export function preEmailStored(v) {
  const e = lowered(v);
  return e.length <= 254 && EMAIL_OLD.test(e) ? e : null;   // EMAIL_OLD takes every address EMAIL takes
}
export async function preId(email) {
  const h = new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode("preorder|" + email)));
  return [...h].map(b => b.toString(16).padStart(2, "0")).join("").slice(0, 32);
}

// The body as a JSON object ({} when it is not one), read no further than PRE.body bytes, so a huge body costs nothing
// past that; null when it is larger.
async function readSmall(req) {
  const text = await readText(req);
  if (text === null) return null;
  try { const b = JSON.parse(text); return b && typeof b === "object" ? b : {}; } catch { return {}; }
}
// The body as text, read no further than PRE.body bytes; null when it is larger.
async function readText(req) {
  if (Number(req.headers.get("Content-Length")) > PRE.body) return null;
  if (!req.body) return "";
  const reader = req.body.getReader(), parts = [];
  let size = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    size += value.byteLength;
    if (size > PRE.body) { reader.cancel().catch(() => {}); return null; }
    parts.push(value);
  }
  const all = new Uint8Array(size);
  let at = 0;
  for (const p of parts) { all.set(p, at); at += p.byteLength; }
  return new TextDecoder().decode(all);
}

// deps: { stats(env, path, body), ipTags(req, env, what) -> {ip, net}, json(o, status) }
export async function preorderPublic(req, env, deps) {
  const until = preUntil(env);
  if (req.method === "GET") return deps.json({ open: preOpen(env), until });
  // The preflight and every POST answer name the allowed origin it came from (Vary: Origin), never *; another origin
  // gets no CORS header at all, so its page cannot read the answer either.
  const origin = req.headers.get("Origin") || "";
  const allowed = origin !== "" && preOrigins(env).includes(origin.toLowerCase());
  const cors = allowed ? { "access-control-allow-origin": origin, vary: "Origin" } : { vary: "Origin" };
  const out = (o, status = 200) => new Response(JSON.stringify(o), { status, headers: { "content-type": "application/json", "cache-control": "no-store", ...cors } });
  if (req.method === "OPTIONS") {
    if (!allowed) return out({ error: "origin" }, 403);
    return new Response(null, { status: 204, headers: { ...cors, "access-control-allow-methods": "GET, POST",
      "access-control-allow-headers": "content-type", "access-control-max-age": "86400" } });
  }
  if (req.method !== "POST") return deps.json({ error: "method" }, 405);
  if (!allowed) return out({ error: "origin" }, 403);
  if (!preOpen(env)) return out({ error: "closed" }, 403);
  if ((req.headers.get("Content-Type") || "").split(";")[0].trim().toLowerCase() !== "application/json") return out({ error: "type" }, 415);
  const b = await readSmall(req);
  if (!b) return out({ error: "body" }, 413);
  if (b.consent !== true || b.adConsent !== true) return out({ error: "consent" }, 400);
  const email = preEmail(b.email);
  if (!email) return out({ error: "email" }, 400);
  const plan = PRE.plans.includes(b.plan) ? b.plan : "undecided";
  const lang = "ko";   // sold in Korea only: whatever the site sends, the row says ko
  let r;
  try {
    const { ip, net } = await deps.ipTags(req, env, "preorder");
    r = await deps.stats(env, "/pre/add", { id: await preId(email), email, plan, lang, ad: true, max: PRE.perDay, ip, net });
  } catch { return out({ error: "busy" }, 503); }
  if (r?.error === "full" || r?.error === "ip") return out({ error: "busy" }, 429);
  if (!r?.ok) return out({ error: "busy" }, 503);
  return out({ ok: true });   // the same whether it is new or was there (see the top)
}

// ---- GET/POST /preorder/unsubscribe: opt-out of the pre-registration (advertising) mail, explained in Korean and English
// on one page as Korean rules on advertising mail ask. Static HTML, no script; every value put in is escaped.
const escHtml = s => String(s).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const MONTHS_EN = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
/** The Korean (KST) date of t, as Korean and English text. */
export function kstDay(t) {
  const d = new Date(t + 9 * 36e5), y = d.getUTCFullYear(), m = d.getUTCMonth(), day = d.getUTCDate();
  return { ko: `${y}년 ${m + 1}월 ${day}일`, en: `${MONTHS_EN[m]} ${day}, ${y}` };
}
export const UNSUB_SENDER = "Riye";
const UNSUB_CSS = `:root{--bg:#0b0b0c;--card:#141416;--line:#2a2a2e;--text:#e8e6e3;--muted:#8b8a88;--accent:#ff7a1a;--bad:#ff6b6b}
*{box-sizing:border-box}
body{background:var(--bg);color:var(--text);font-family:system-ui,-apple-system,"Segoe UI","Malgun Gothic",sans-serif;max-width:640px;margin:0 auto;padding:32px 16px 64px;line-height:1.6;overflow-wrap:anywhere}
h1{color:var(--accent);font-size:24px;margin:0}h2{font-size:18px;margin:0 0 8px}.sub{color:var(--muted);margin:2px 0 0}
section{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:16px 18px;margin-top:16px}
section.done{border-color:var(--accent)}p{margin:6px 0}.m{color:var(--muted);font-size:14px}.err{color:var(--bad);font-weight:600}
dl{display:grid;grid-template-columns:max-content 1fr;gap:4px 14px;margin:8px 0 0}dt{color:var(--muted)}dd{margin:0}
label{display:block;font-size:14px;color:var(--muted);margin-top:4px}
input{width:100%;margin-top:4px;background:#0f0f11;color:var(--text);border:1px solid var(--line);border-radius:8px;padding:10px;font:inherit}
button{margin-top:12px;background:var(--accent);color:#111;border:0;border-radius:8px;padding:10px 18px;font:inherit;font-weight:700;cursor:pointer}
`;
const UNSUB_TX = {
  ko: { title: "사전예약 수신 거부",
        lead: "SystemBoost 공식 사이트에서 사전예약한 메일 주소를 넣고 '수신 거부'를 누르세요. 그 주소의 사전예약 정보를 지우고, 앞으로 그 주소로 출시 알림 등 광고성 메일을 보내지 않아요.",
        from: UNSUB_SENDER + " (SystemBoost 판매자)", who: "보내는 사람: " + UNSUB_SENDER + " (SystemBoost 판매자)", ask: "메일 주소 말고는 아무것도 묻지 않아요.",
        until: "이 페이지는 2027년 2월 28일까지 열려 있어요. 남은 사전예약 정보는 그날 모두 지워요.",
        doneTitle: "수신 거부를 처리했어요", sender: "보낸 사람", date: "요청한 날짜", result: "처리 결과",
        resultText: "입력한 메일 주소의 사전예약 정보를 지웠어요(목록에 있었다면). 앞으로 이 주소로 사전예약 · 출시 알림 등 광고성 메일을 보내지 않아요.",
        kst: " (한국 시간)", bad: "메일 주소를 확인해 주세요.", busy: "요청이 많아 지금은 처리하지 못했어요. 잠시 뒤 다시 시도해 주세요.",
        down: "지금은 처리하지 못했어요. 잠시 뒤 다시 시도해 주세요.", closed: "사전예약 수신 거부 페이지는 2027년 2월 28일에 닫혔어요. 사전예약 정보는 그날 모두 지웠고, 사전예약 메일은 더 보내지 않아요." },
  en: { title: "Unsubscribe from pre-registration mail",
        lead: "Enter the email address you pre-registered with on the SystemBoost official site and press 'Unsubscribe'. Its pre-registration is deleted, and no launch notice or other advertising email will be sent to it.",
        from: UNSUB_SENDER + " (seller of SystemBoost)", who: "Sender: " + UNSUB_SENDER + " (seller of SystemBoost)", ask: "Nothing but the email address is asked.",
        until: "This page is open until February 28, 2027 (Korean time). All remaining pre-registrations are deleted that day.",
        doneTitle: "Your unsubscribe request is done", sender: "Sender", date: "Date of request", result: "Result",
        resultText: "The pre-registration of the address you entered has been deleted (if it was on the list). No pre-registration, launch or other advertising email will be sent to it again.",
        kst: " (Korean time)", bad: "Please check the email address.", busy: "Too many requests right now. Please try again in a while.",
        down: "This could not be done right now. Please try again in a while.", closed: "The pre-registration unsubscribe page closed on February 28, 2027. All pre-registrations were deleted that day, and no pre-registration mail is sent any more." },
};
function unsubResponse(status, body) {
  const nonce = btoa(String.fromCharCode(...crypto.getRandomValues(new Uint8Array(16))));
  const T = UNSUB_TX;
  const html = `<!DOCTYPE html>
<html lang="ko">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="robots" content="noindex">
<title>SystemBoost ${escHtml(T.ko.title)} · Unsubscribe</title>
<style nonce="${nonce}">
${UNSUB_CSS}</style>
</head>
<body>
<main>
<h1>SystemBoost ${escHtml(T.ko.title)}</h1>
<p class="sub" lang="en">${escHtml(T.en.title)}</p>
${body}
</main>
</body>
</html>
`;
  return new Response(html, { status, headers: {
    "content-type": "text/html; charset=utf-8", "cache-control": "no-store",
    "content-security-policy": `default-src 'none'; style-src 'nonce-${nonce}'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'`,
    "x-frame-options": "DENY", "referrer-policy": "no-referrer", "x-content-type-options": "nosniff", "x-robots-tag": "noindex" } });
}
const both = f => `<section><p>${escHtml(f(UNSUB_TX.ko))}</p></section>\n<section lang="en"><p>${escHtml(f(UNSUB_TX.en))}</p></section>`;
function unsubForm(error = "") {
  const T = UNSUB_TX;
  const err = error ? `<p class="err" role="alert">${escHtml(T.ko[error])} <span lang="en">${escHtml(T.en[error])}</span></p>\n` : "";
  const info = L => `<p>${escHtml(L.lead)}</p><p>${escHtml(L.who)}</p><p class="m">${escHtml(L.ask)} ${escHtml(L.until)}</p>`;
  return `<section>${info(T.ko)}</section>
<section lang="en">${info(T.en)}</section>
<section>
${err}<form method="post" action="/preorder/unsubscribe">
<label for="email">메일 주소 · Email address</label>
<input id="email" name="email" type="email" required maxlength="254" autocomplete="email" inputmode="email">
<button type="submit">수신 거부 / Unsubscribe</button>
</form>
</section>`;
}
/** The one answer to every accepted request, whether or not the address was on the list. */
export function unsubDoneHtml(t) {
  const day = kstDay(t);
  const block = (L, d, attr) => `<section class="done"${attr}><h2>${escHtml(L.doneTitle)}</h2><dl>` +
    `<dt>${escHtml(L.sender)}</dt><dd>${escHtml(L.from)}</dd>` +
    `<dt>${escHtml(L.date)}</dt><dd>${escHtml(d + L.kst)}</dd>` +
    `<dt>${escHtml(L.result)}</dt><dd>${escHtml(L.resultText)}</dd></dl></section>`;
  return block(UNSUB_TX.ko, day.ko, "") + "\n" + block(UNSUB_TX.en, day.en, ' lang="en"');
}

// deps: { stats(env, path, body), ipTags(req, env, what), audit(action, detail), json(o, status) }
export async function preorderUnsubscribe(req, env, deps) {
  const t = Date.now();
  if (t > Date.parse(PRE.unsubUntil)) return unsubResponse(410, both(L => L.closed));
  if (req.method === "GET" || req.method === "HEAD") return unsubResponse(200, unsubForm());
  if (req.method !== "POST") return deps.json({ error: "method" }, 405);
  const text = await readText(req);
  let email = null;
  if (text !== null) { try { email = preEmailStored(new URLSearchParams(text).get("email")); } catch { email = null; } }
  if (!email) return unsubResponse(400, unsubForm("bad"));
  let r;
  try {
    const { ip, net } = await deps.ipTags(req, env, "preorder-unsub");
    r = await deps.stats(env, "/pre/unsub", { id: await preId(email), ip, net });
  } catch { return unsubResponse(503, unsubForm("down")); }
  if (r?.error === "ip") return unsubResponse(429, unsubForm("busy"));
  if (!r?.ok) return unsubResponse(503, unsubForm("down"));
  // the address is never logged: only that one row went
  if (r.deleted) { try { await deps.audit("사전예약 수신 거부", "1건"); } catch { /* the row is gone; the record is extra */ } }
  return unsubResponse(200, unsubDoneHtml(t));
}

// Quotes alone do not stop Excel or Sheets from running a cell that starts with = + - @ (or a tab or CR) as a formula,
// so such a cell gets a ' in front as well (OWASP's CSV injection advice). preEmail refuses such addresses now; this
// covers rows taken before it did.
const csvCell = v => {
  let s = String(v ?? "");
  const formula = /^[=+\-@\t\r]/.test(s);
  if (formula) s = "'" + s;
  return formula || /[",\r\n]/.test(s) ? '"' + s.replace(/"/g, '""') + '"' : s;
};

const TEST_NOTE = "🔔 시험 알림이에요. 사전예약 새 신청이 오면 이 채널에 신청 수, 관심 플랜, 언어를 알려 드려요. 메일 주소는 보내지 않아요.";

// The webhook for the alert: a Discord webhook Discord has, in a channel that is not one of the public server's.
async function saveHook(env, body, deps) {
  const hook = String(body?.hook ?? "").trim();
  if (!HOOK_RE.test(hook)) return { status: 400, error: "디스코드 웹후크 주소가 아니에요" };
  const r = await readHook(hook);   // reads the webhook; nothing is posted
  if (r.status === 404) return { status: 400, error: "디스코드에 없는 웹후크예요" };
  if (r.error) return { status: 502, error: "웹후크를 확인하지 못했어요: " + r.error };
  if (PUBLIC_CHANNEL_IDS.includes(String(r.data?.channel_id))) return { status: 400, error: "모두가 보는 채널의 웹후크예요. 사장님만 보는 채널의 웹후크를 넣어 주세요" };
  // from now on: the rows already there are not sent as new (first, so a failure below leaves nothing half done)
  await deps.stats(env, "/pre/noted", {});
  try { await env.LICENSES.put(PRE_HOOK, hook); } catch { return { status: 503, error: "저장하지 못했어요. 잠시 뒤 다시 시도해 주세요" }; }
  await deps.audit("사전예약 디스코드 알림", "연결");
  return { ok: true, hook: true };
}

// deps: { stats, audit(action, detail) }. Returns { status?, ...body } like supportAdmin.
export async function preorderAdmin(env, route, body, deps) {
  const until = preUntil(env);
  if (route === "GET /preorders") {
    const r = await deps.stats(env, "/pre/read", { recent: PRE.recent });
    let hook = null;   // null: KV did not answer
    try { hook = !!(await env.LICENSES.get(PRE_HOOK)); } catch { /* the rest still shows */ }
    return { ...r, hook, open: preOpen(env), until, deleteAt: PRE.deleteAt, shown: preShown() };
  }
  if (route === "POST /preorders/hook") return await saveHook(env, body, deps);
  if (route === "POST /preorders/hook/test") {
    const hook = await env.LICENSES.get(PRE_HOOK);
    if (!hook) return { status: 400, error: "연결된 웹후크가 없어요" };
    const r = await postText(hook, TEST_NOTE);
    if (r.error) return { status: r.status === 404 ? 400 : 502, error: r.status === 404 ? "디스코드에 없는 웹후크예요. 다시 연결해 주세요" : r.error };
    return { ok: true };
  }
  if (route === "POST /preorders/hook/clear") {
    await env.LICENSES.delete(PRE_HOOK);
    await deps.audit("사전예약 디스코드 알림", "끊음");
    return { ok: true, hook: false };
  }
  if (route === "POST /preorders/seen") {
    // {upTo}: the newest sign-up the dashboard showed (ms or ISO time); without it, everything there is now
    const u = body?.upTo, upTo = typeof u === "number" ? u : typeof u === "string" ? Date.parse(u) : NaN;
    return await deps.stats(env, "/pre/seen", Number.isFinite(upTo) && upTo >= 0 ? { upTo } : {});
  }
  if (route === "POST /preorders/export") {
    const r = await deps.stats(env, "/pre/export", {});
    const rows = r.rows || [];
    await deps.audit("사전예약 내보내기", rows.length + "건");
    // adAt last (a Desktop script reads the columns before it by position); empty for a row taken before adConsent
    const iso = v => v == null || v === "" ? "" : new Date(Number(v)).toISOString();
    const csv = "email,plan,lang,at,adAt\r\n" + rows.map(x => [x.email, x.plan, x.lang, iso(x.at), iso(x.adAt)].map(csvCell).join(",")).join("\r\n");
    return { count: rows.length, csv };
  }
  if (route === "POST /preorders/delete") {
    const email = preEmailStored(body?.email);
    if (!email) return { status: 400, error: "메일 주소를 확인해 주세요" };
    const r = await deps.stats(env, "/pre/delete", { id: await preId(email) });
    if (r.deleted) await deps.audit("사전예약 삭제", "1건");
    return { ok: true, deleted: !!r.deleted };
  }
  return { status: 404, error: "not found" };
}
