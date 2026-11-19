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
//   POST /preorder   {email, plan, lang, consent: true} -> {ok} | {error: origin|type|body|consent|email|closed|busy}
//                    {ok} also when the address had signed up already: the answer must not tell anyone whether an
//                    address is on the list (the notice promises it is given to nobody else).
//   admin (index.js): GET /preorders (counts and recent rows without emails), POST /preorders/seen,
//                     POST /preorders/export (every email, as CSV text; logged), POST /preorders/delete {email}
// env PREORDER = "off" (any case) closes sign-ups early; env PREORDER_UNTIL (ISO time) moves the deadline.
export const PRE = {
  until: "2026-11-27T14:59:59Z",      // 2026-11-27 23:59:59 KST
  deleteAt: "2027-02-27T15:00:00Z",   // 2027-02-28 00:00 KST: the Stats object deletes every row (hourly tick and every call)
  perIpHour: 5, perNetHour: 20, perDay: 3000, recent: 100,
  body: 2048,                         // bytes of a POST body read at most; the site's is under 400
  origins: ["https://doku-web.github.io"],
  plans: ["undecided", "free", "premium", "ultra", "biz"],
  langs: ["en", "ko", "ja", "zh", "es", "pt", "ru", "de", "fr"],
};
export const PRE_PLAN_KO = { undecided: "아직 모름", free: "무료", premium: "프로", ultra: "울트라", biz: "산업" };

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
  if (Number(req.headers.get("Content-Length")) > PRE.body) return null;
  if (!req.body) return {};
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
  try { const b = JSON.parse(new TextDecoder().decode(all)); return b && typeof b === "object" ? b : {}; } catch { return {}; }
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
  if (b.consent !== true) return out({ error: "consent" }, 400);
  const email = preEmail(b.email);
  if (!email) return out({ error: "email" }, 400);
  const plan = PRE.plans.includes(b.plan) ? b.plan : "undecided";
  const lang = PRE.langs.includes(b.lang) ? b.lang : "en";
  let r;
  try {
    const { ip, net } = await deps.ipTags(req, env, "preorder");
    r = await deps.stats(env, "/pre/add", { id: await preId(email), email, plan, lang, max: PRE.perDay, ip, net });
  } catch { return out({ error: "busy" }, 503); }
  if (r?.error === "full" || r?.error === "ip") return out({ error: "busy" }, 429);
  if (!r?.ok) return out({ error: "busy" }, 503);
  return out({ ok: true });   // the same whether it is new or was there (see the top)
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

// deps: { stats, audit(action, detail) }. Returns { status?, ...body } like supportAdmin.
export async function preorderAdmin(env, route, body, deps) {
  const until = preUntil(env);
  if (route === "GET /preorders") {
    const r = await deps.stats(env, "/pre/read", { recent: PRE.recent });
    return { ...r, open: preOpen(env), until, deleteAt: PRE.deleteAt, shown: preShown() };
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
    const csv = "email,plan,lang,at\r\n" + rows.map(x => [x.email, x.plan, x.lang, new Date(x.at).toISOString()].map(csvCell).join(",")).join("\r\n");
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
