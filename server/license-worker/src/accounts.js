// Member accounts. They live in Supabase: supabase/accounts.sql holds the tables and the one function
// this file calls. Only this Worker goes there, with the secret key; the app never does.
//
// A password is mixed here with a pepper from the signing key and hashed with bcrypt in the database,
// so the hash costs a free-plan Worker no CPU (it gets 10 ms a request) and the database alone is not
// enough to try passwords. Replacing the signing key therefore ends every password, recovery code, backup code and
// 2-step secret. IP addresses leave this file as a one-way value (kept for an hour), except the raw address of a line
// in the security log ("addr"), which the database deletes after 30 days.
//
// Accounts v2 (2026-11-18): email accounts (sign-up confirmed by mail, login by email, password reset by mail), the
// 2-step login (TOTP, src/totp.js) with backup codes, logged-in devices, licence keys linked to the account, consents
// given in the app, and recovery requests the seller decides. Mail goes through src/mail.js after the reply.
// Everything an older app sends and gets back stays as it was.
import { isCommonPassword } from "./common_passwords.js";
import { mailOn, sendMail } from "./mail.js";
import { ipBucket } from "./support.js";
import { base32, newBackupCodes, newSecret, normBackup, otpUri, seal, totpStep, unseal } from "./totp.js";

const CONSENT_OLD = "2026-11-12";   // the sign-up text of apps that send no "consent" (app: AccountPage.SignUp). Only recorded, never asked again
export const CONSENT_NOW = "2026-11-18";   // the sign-up text with email accounts; the app sends it as "consent"
export const INVITE_DAYS = 3;   // Premium for whoever signs up with a member's code (아이디 sign-ups: index.js)
const LIMITS = {
  rewardDays: 3,                // Premium for the member who invited them
  rewardCap: 30,                // the most days one member can earn
  ticketPercent: 10,            // discount for whoever signs up with a code
  ticketDays: 30,               // how long that ticket lasts
  inviteMax: 50,                // sign-ups on one member's code
  sessionDays: 90, sessionsKept: 5,
  loginTries: 5, lockMin: 15,
  signupsPerIp: 5, signupsPerHour: 100, failsPerIp: 20,
  takenPerIp: 20,               // "this id is taken" replies to one address an hour (they tell which ids exist)
  // accounts v2
  signupHours: 24,              // a sign-up (or an added email) waits this long for its "verify" link
  resetMin: 30,                 // a "forgot password" link
  approvedHours: 24,            // the reset link of an approved recovery request
  pendingMin: 5, pendingTries: 5,   // a 2-step login half done
  linkHours: 24,                // a key link
  recoveryHours: 72,            // a recovery request waits this long before the seller can approve it
  recoveryKeepDays: 90,         // recovery requests (and their cancel links) are deleted after this
  logDays: 30,                  // the security log (raw IP addresses)
  devicesKept: 20, consentsKept: 50, keysKept: 20,
  mailPerIp: 10,                // forgot, recovery requests and other mails asked for from one address an hour
  verifyTries: 5,               // wrong passwords on a sign-up's "verify" page before its link ends
};
const TIMEOUT_MS = 10000;
const ID_RE = /^[a-z0-9_]{4,20}$/;
const CODE_RE = /^[A-Z0-9]{4,16}$/;
const TOKEN_RE = /^[0-9a-f]{64}$/;
const SID_RE = /^[0-9a-f]{12}$/;
const RESERVED = ["admin", "administrator", "systemboost", "dokku", "support", "root"];
const ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";   // no 0/O/1/I: easy to read aloud
const ID_CHARS = "abcdefghijklmnopqrstuvwxyz0123456789";
const ADMIN = ["admin/list", "admin/block", "admin/delete"];
// Actions that need no session. A database from before accounts v2 answers the new ones with "session" (it does not
// know them); that must not sign the app out, so it becomes "unavailable".
const PUBLIC_V2 = ["signup_email", "verify", "login2", "forgot", "reset", "reset_peek", "link_key_confirm", "recovery_request",
                   "recovery_cancel", "admin/recovery_list", "admin/recovery_approve", "admin/recovery_reject"];

const enc = new TextEncoder();
const hex = bytes => [...new Uint8Array(bytes)].map(x => x.toString(16).padStart(2, "0")).join("");
const random = n => crypto.getRandomValues(new Uint8Array(n));
const sha = async text => hex(await crypto.subtle.digest("SHA-256", enc.encode(text)));
const code = n => [...random(n)].map(x => ALPHABET[x % ALPHABET.length]).join("");
const fail = (status, error) => ({ status, error });
const cleanId = v => String(v || "").trim().toLowerCase();
const newRecovery = () => code(16).match(/.{4}/g).join("-");
const newToken = () => hex(random(32));
const langOf = b => (b.lang === "en" ? "en" : "ko");
function newMemberId() {   // "m" + 11 of [a-z0-9], every symbol equally likely
  let out = "m";
  while (out.length < 12) for (const x of random(16)) if (x < 252 && out.length < 12) out += ID_CHARS[x % 36];
  return out;
}

/** Printable text only (no control, line or direction-changing characters), at most n characters. */
export function printable(v, n) {
  return [...String(v ?? "")].filter(c => {
    const x = c.codePointAt(0);
    return x >= 32 && x !== 127 && !(x >= 0x80 && x < 0xa0) && x !== 0x2028 && x !== 0x2029 &&
           !(x >= 0x200b && x <= 0x200f) && !(x >= 0x202a && x <= 0x202e) && !(x >= 0x2066 && x <= 0x2069) && x !== 0xfeff;
  }).slice(0, n).join("").trim();
}

/** A trimmed, lowercased email address, or "" when it is not one. */
export function cleanEmail(v) {
  const e = String(v ?? "").trim().toLowerCase();
  if (e.length > 254) return "";
  return /^[a-z0-9.!#$%&'*+/=?^_`{|}~-]{1,64}@(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z][a-z0-9-]{0,61}[a-z0-9]$/.test(e) &&
         !e.startsWith(".") && !e.includes("..") && !e.split("@")[0].endsWith(".") ? e : "";
}

/** The rule for every new password (sign-up, change, reset, recovery): 10..72 characters, not the id, the email or its
 *  local part, and not a common password. Passwords set before it still log in. */
export function newPasswordOk(p, id, email) {
  p = String(p ?? "");
  if (p.length < 10 || p.length > 72) return false;
  const low = p.toLowerCase(), e = String(email || "").toLowerCase();
  if ([String(id || "").toLowerCase(), e, e.split("@")[0]].some(x => x && x === low)) return false;
  return !isCommonPassword(p);
}

// The sign-up text the member agreed to: the version the app sends, or the one before versions were sent.
function consentOf(b) {
  if (b.consent === undefined || b.consent === null || b.consent === "") return CONSENT_OLD + " " + new Date().toISOString();
  const v = String(b.consent);
  return /^\d{4}-\d{2}-\d{2}$/.test(v) ? v + " " + new Date().toISOString() : "";
}

// The app's device: {name, key}; key = 64 hex (SHA-256 of an install id the app keeps). Older apps send none, and an
// 아이디 sign-up's "device" is the referral device (a string): neither is a device here.
function deviceOf(b) {
  const d = b.device && typeof b.device === "object" && !Array.isArray(b.device) ? b.device : null;
  const key = d && typeof d.key === "string" && /^[0-9a-f]{64}$/i.test(d.key) ? d.key.toLowerCase() : "";
  return { name: d ? printable(d.name, 40) : "", key };
}

// One call, one transaction. Whatever goes wrong on the way, the caller gets "unavailable": a paused
// Supabase project must not take anything but accounts down with it.
async function db(env, action, b) {
  if (!env.SUPABASE_URL || !env.SUPABASE_SECRET_KEY) return fail(503, "unavailable");
  try {
    const res = await fetch(env.SUPABASE_URL.trim().replace(/\/+$/, "") + "/rest/v1/rpc/account_call", {
      method: "POST",
      headers: { apikey: env.SUPABASE_SECRET_KEY.trim(), "Content-Type": "application/json" },
      body: JSON.stringify({ action, b: { ...LIMITS, ...b } }),
      signal: AbortSignal.timeout(TIMEOUT_MS),
    });
    if (res.ok) return await res.json();
    const e = await res.json().catch(() => ({}));
    // never details or hint, and nothing quoted from the message: they can repeat what was sent
    console.log("supabase", action, res.status, e.code, String(e.message || "").replace(/"[^"]*"/g, '"…"').slice(0, 160));
  } catch (e) {
    console.log("supabase", action, e.name);
  }
  return fail(503, "unavailable");
}

// Without the signing key the pepper would be a value anyone can work out, so accounts stop instead.
const NEEDS_PEPPER = ["signup", "login", "recover", "admin/recovery", "password", "delete",
                      "signup_email", "verify", "login2", "forgot", "reset", "add_email", "totp_start", "totp_confirm", "totp_off",
                      "backup_new", "link_key", "recovery_request", "admin/recovery_approve"];
const hasPepper = env => typeof env.LICENSE_PRIVATE_KEY === "string" && env.LICENSE_PRIVATE_KEY.trim().length >= 32;

async function secrets(env) {
  const pepper = await sha("systemboost-account|" + env.LICENSE_PRIVATE_KEY);
  return {
    pepper,   // src/totp.js seals the 2-step secret with a key made from it
    password: text => sha(pepper + "|password|" + text),
    proof: recovery => sha(pepper + "|recovery|" + String(recovery).replace(/[^A-Z0-9]/gi, "").toUpperCase()),
    // an address for the hourly counters; IPv6 by its /64, which one connection usually holds whole
    ip: async ip => (await sha(pepper + "|ip|" + ipBucket(ip))).slice(0, 32),
    // a member id that was deleted: kept in account_gone as this value only, so the id cannot be signed up again
    gone: id => sha(pepper + "|gone|" + cleanId(id)),
    // what a login names ("id:..." or "email:..."), for the lock of names nobody has (account_guard)
    ident: what => sha(pepper + "|ident|" + what),
    device: key => sha(pepper + "|device|" + key),
    backup: c => sha(pepper + "|backup|" + normBackup(c)),
  };
}

// Where the links in mails point: the Worker's public address (wrangler.toml PUBLIC_URL), else the request's.
function link(env, b, path, t, lang) {
  const base = String(env.PUBLIC_URL || b.origin || "").trim().replace(/\/+$/, "");
  return base + "/account/" + path + "?t=" + t + (lang === "en" ? "&lang=en" : "");
}

/**
 * accounts(env, action, b, ctx, deps)
 *  b: the request's fields, plus what src/account_web.js sets from the request itself (never from the body):
 *     ip (CF-Connecting-IP), country (request.cf.country), token (the Bearer session), origin.
 *  ctx: the Worker's context; mails are handed to ctx.waitUntil and go out after the reply.
 *  deps: licence key reads from index.js (keyRecord, readKey, emailKeyId) for keys and link_key.
 */
export async function accounts(env, action, b = {}, ctx, deps = {}) {
  if (NEEDS_PEPPER.includes(action) && !hasPepper(env)) return fail(503, "unavailable");
  const r = await run(env, action, b, ctx, deps);
  return PUBLIC_V2.includes(action) && r?.error === "session" ? fail(503, "unavailable") : r;
}

async function run(env, action, b, ctx, deps) {
  const s = await secrets(env);
  const where = { addr: String(b.ip || "").slice(0, 64), country: /^[A-Z][A-Z0-9]$/.test(b.country || "") ? b.country : "" };
  switch (action) {
    case "ping": return db(env, "ping", {});
    case "features": return { email: mailOn(env), totp: true };
    case "code": return CODE_RE.test(b.code || "") ? db(env, "code", { code: b.code }) : { member: false };
    case "signup": return signup(env, s, b, where);
    case "login": return login(env, s, b, ctx, where);
    case "login2": return login2(env, s, b, ctx, where);
    case "recover": return recover(env, s, b, ctx, where);
    case "signup_email": return signupEmail(env, s, b, ctx, deps);
    case "verify": return verify(env, s, b, ctx, where);
    case "forgot": return forgot(env, s, b, ctx);
    case "reset_peek": {
      if (!TOKEN_RE.test(b.t || "")) return fail(400, "expired");
      const r = await db(env, "reset_peek", { t: await sha(b.t) });
      return r.error ? r : { ok: true };
    }
    case "reset": return reset(env, s, b, ctx, where);
    case "link_key_confirm": {
      if (!TOKEN_RE.test(b.t || "")) return fail(400, "expired");
      const r = await db(env, "link_confirm", { t: await sha(b.t), ...where });
      return r.error ? r : { done: true };
    }
    case "recovery_request": return recoveryRequest(env, s, b, ctx, where);
    case "recovery_cancel": {
      if (!TOKEN_RE.test(b.t || "")) return fail(400, "expired");
      const r = await db(env, "recovery_cancel", { t: await sha(b.t), ...where });
      return r.error ? r : { done: true };
    }
    case "ticket/used":
    case "ticket/release": return db(env, action, { order: b.order });
    case "admin/recovery": {
      const recovery = newRecovery(), id = cleanId(b.id);
      const r = await db(env, action, { id, proof: await s.proof(recovery) });
      return r.error ? r : { recovery };
    }
    case "admin/recovery_list": return db(env, action, { q: b.all ? "all" : "open" });
    case "admin/recovery_reject": {
      const id = String(b.id || "");
      if (!SID_RE.test(id)) return fail(400, "요청 번호가 잘못됐어요");
      return db(env, action, { id, reason: printable(b.reason, 200) });
    }
    case "admin/recovery_approve": return approveRecovery(env, s, b, ctx);
  }
  if (action === "admin/delete") {   // the pepper is needed for "gone"; without it the id is deleted as before
    const id = cleanId(b.id);
    return db(env, action, { id, ...(hasPepper(env) ? { gone: await s.gone(id) } : {}) });
  }
  if (ADMIN.includes(action)) return db(env, action, { id: cleanId(b.id), on: !!b.on, q: b.q });

  if (!TOKEN_RE.test(b.token || "")) return fail(401, "session");
  const token = await sha(b.token);
  switch (action) {
    case "me": {
      const r = await db(env, "me", { token });
      return r.error ? r : { ...r, rewardCap: LIMITS.rewardCap, rewardEach: LIMITS.rewardDays, inviteDays: INVITE_DAYS, inviteTicket: LIMITS.ticketPercent };
    }
    case "invite":
      for (let i = 0; i < 5; i++) {
        const r = await db(env, "invite", { token, code: code(8) });
        if (r.error !== "retry") return r;
      }
      return fail(503, "unavailable");
    case "reward":
    case "logout": return db(env, action, { token });
    case "ticket": return db(env, "ticket", { token, order: b.order });
    case "password": return changePassword(env, s, token, b, ctx, where);
    case "delete": {   // the session's id first, for "gone" (the database deletes by the session, as before)
      const me = await db(env, "me", { token });
      if (me.error) return me;
      return db(env, "delete", { token, pw: await s.password(String(b.password || "").slice(0, 100)), ip: await s.ip(b.ip), gone: await s.gone(me.id) });
    }
    case "add_email": return addEmail(env, s, token, b, ctx, where);
    case "devices": return db(env, "devices", { token });
    case "logout_device": {
      const sid = String(b.device ?? "");
      if (!SID_RE.test(sid)) return early(env, token, fail(404, "device"));
      const r = await db(env, "logout_device", { token, sid });
      return r.error ? r : { done: true };
    }
    case "logout_all": {
      const r = await db(env, "logout_all", { token, keep: b.keepThis === true, ...where });
      return r.error ? r : { done: true };
    }
    case "totp_start": return totpStart(env, s, token, b);
    case "totp_confirm": return totpConfirm(env, s, token, b, where);
    case "totp_off": return totpOff(env, s, token, b, where);
    case "backup_new": {
      const codes = newBackupCodes();
      const r = await db(env, "backup_new", { token, pw: await s.password(String(b.password || "").slice(0, 100)), ip: await s.ip(b.ip),
                                              backup: await Promise.all(codes.map(s.backup)), ...where });
      return r.error ? r : { backup: codes };
    }
    case "backup_left": {
      const r = await db(env, "me", { token });
      return r.error ? r : { left: Number(r.backupLeft) || 0 };
    }
    case "keys": return keys(env, token, deps);
    case "link_key": return linkKey(env, s, token, b, ctx, deps);
    case "consent": {
      if (b.kind !== "oc") return early(env, token, fail(400, "kind"));
      const version = String(b.version ?? "");
      if (!/^[0-9A-Za-z._-]{1,32}$/.test(version)) return early(env, token, fail(400, "version"));
      const r = await db(env, "consent", { token, kind: "oc", version, gpu: printable(b.gpu, 120), app: printable(b.app, 40), withdraw: b.withdraw === true });
      return r.error ? r : { done: true };
    }
  }
  return fail(404, "not found");
}

// A refusal the Worker makes before the database has seen the session: an ended session still hears "session" first,
// so the app signs out instead of showing an input error.
async function early(env, token, r) {
  const me = await db(env, "me", { token });
  return me.error ? me : r;
}

// ---------------------------------------------------------------- 아이디 accounts (as before, plus the new password rule)
async function signup(env, s, b, where) {
  const id = cleanId(b.id), password = String(b.password || "");
  if (!ID_RE.test(id) || RESERVED.includes(id)) return fail(400, "id");
  if (!newPasswordOk(password, id)) return fail(400, "password");
  if (b.agree !== true || b.age14 !== true) return fail(400, "consent");
  const consent = consentOf(b);
  if (!consent) return fail(400, "consent");
  const dev = deviceOf(b);
  const token = newToken(), recovery = newRecovery();
  const r = await db(env, "signup", { id, pw: await s.password(password), proof: await s.proof(recovery), session: await sha(token),
                                      invite: String(b.invite || ""), ticket: !!b.ticket, ip: await s.ip(b.ip), consent,
                                      gone: await s.gone(id), country: where.country,
                                      ...(dev.name ? { dname: dev.name } : {}), ...(dev.key ? { dhash: await s.device(dev.key) } : {}) });
  return r.error ? r : { id, token, recovery };
}

// Login by 아이디 or by email (it has "@"). A 2-step account gets a pending login instead of a session.
async function login(env, s, b, ctx, where) {
  const raw = String((b.email !== undefined && b.email !== null && b.email !== "" ? b.email : b.id) ?? "");
  const byEmail = raw.includes("@");
  const id = byEmail ? undefined : cleanId(raw).slice(0, 100), email = byEmail ? raw.trim().toLowerCase().slice(0, 254) : undefined;
  const dev = deviceOf(b);
  const token = newToken(), pending = newToken();
  const r = await db(env, "login", { id, email, ident: await s.ident(byEmail ? "email:" + email : "id:" + id),
                                     pw: await s.password(String(b.password || "").slice(0, 100)), ip: await s.ip(b.ip), ...where,
                                     ...(dev.name ? { dname: dev.name } : {}), ...(dev.key ? { dhash: await s.device(dev.key) } : {}),
                                     session: await sha(token), pending: await sha(pending) });
  if (r.error) return r;
  if (r.twofa) return { twofa: true, pending };
  if (r.newDevice && r.email) sendMail(env, ctx, { to: r.email, kind: "new_device", lang: langOf(b), data: { device: dev.name, country: where.country, when: Date.now() } });
  return { id: r.id ?? id, token };
}

// The second step: a code from the authenticator app, or a backup code (single use).
async function login2(env, s, b, ctx, where) {
  if (!TOKEN_RE.test(b.pending || "")) return fail(400, "expired");
  const pending = await sha(b.pending), ip = await s.ip(b.ip);
  const g = await db(env, "login2_get", { pending, ip });
  if (g.error) return g;
  const useBackup = b.backup !== undefined && b.backup !== null && b.backup !== "";
  let verdict;
  if (useBackup) verdict = { backup: await s.backup(String(b.backup).slice(0, 64)) };
  else {
    const secret = await unseal(s.pepper, g.totp, g.id);
    verdict = { step: secret ? await totpStep(secret, String(b.code ?? "").slice(0, 16), Number(g.step) || 0) : 0 };
  }
  const token = newToken();
  const r = await db(env, "login2", { pending, ip, ...where, ...verdict, session: await sha(token) });
  if (r.error) return r;
  const lang = langOf(b);
  if (r.email && useBackup) sendMail(env, ctx, { to: r.email, kind: "backup_used", lang, data: { left: r.backupLeft } });
  if (r.email && r.newDevice) sendMail(env, ctx, { to: r.email, kind: "new_device", lang, data: { device: printable(r.device, 40), country: where.country, when: Date.now() } });
  return useBackup ? { id: r.id, token, backupLeft: Number(r.backupLeft) || 0, redo2fa: true } : { id: r.id, token };
}

// The account's email is part of the password rule, but only someone with the right recovery code may learn that a
// password matched it: the first call ("dry") checks the code like a real one and names the email without changing
// anything. A database from before accounts v2 ignores "dry" and recovers right away (it has no emails).
async function recover(env, s, b, ctx, where) {
  const id = cleanId(b.id), next = String(b.next || "");
  if (!newPasswordOk(next, id)) return fail(400, "password");
  const token = newToken(), recovery = newRecovery();
  const call = { id, ident: await s.ident("id:" + id), ip: await s.ip(b.ip), proof: await s.proof(b.recovery || ""),
                 next: await s.password(next), nextProof: await s.proof(recovery), session: await sha(token), ...where };
  let r = await db(env, "recover", { ...call, dry: true });
  if (r.error) return r;
  if (r.dry) {
    if (!newPasswordOk(next, id, r.email)) return fail(400, "password");
    r = await db(env, "recover", call);
    if (r.error) return r;
  }
  if (r.email) sendMail(env, ctx, { to: r.email, kind: "password_changed", lang: langOf(b), data: { when: Date.now() } });
  return { id, token, recovery };
}

async function changePassword(env, s, session, b, ctx, where) {
  const me = await db(env, "me", { token: session });
  if (me.error) return me;
  const next = String(b.next || "");
  if (!newPasswordOk(next, me.id, me.email)) return fail(400, "password");
  const token = newToken(), recovery = newRecovery();
  const r = await db(env, "password", { token: session, pw: await s.password(String(b.password || "").slice(0, 100)), next: await s.password(next),
                                        nextProof: await s.proof(recovery), session: await sha(token), ip: await s.ip(b.ip), ...where });
  if (r.error) return r;
  if (r.email) sendMail(env, ctx, { to: r.email, kind: "password_changed", lang: langOf(b), data: { when: Date.now() } });
  return { id: me.id, token, recovery };
}

// ---------------------------------------------------------------- email accounts
// The same reply, and the same work before it, whether the address has an account or not; the mail goes after it.
// A member's invite code is taken here (the inviter's reward and the 10% ticket come when the address is confirmed);
// the 3-day Premium gift key of an 아이디 sign-up needs the app's device, so an email sign-up does not get it. A seller's
// referral code is refused with "referral": the app redeems it on its own (POST /referral), as before.
async function signupEmail(env, s, b, ctx, deps) {
  if (!mailOn(env)) return fail(503, "mail_off");
  const email = cleanEmail(b.email);
  if (!email) return fail(400, "email");
  const password = String(b.password ?? "");
  if (!newPasswordOk(password, "", email)) return fail(400, "weak");
  if (b.agree !== true || b.age14 !== true) return fail(400, "consent");
  const consent = consentOf(b);
  if (!consent) return fail(400, "consent");
  const invite = String(b.invite ?? "").trim().toUpperCase();
  if (invite && !CODE_RE.test(invite)) return fail(404, "unknown");
  if (invite && deps.isSellerCode && (await deps.isSellerCode(env, invite))) return fail(409, "referral");
  const t = newToken(), lang = langOf(b);
  const r = await db(env, "signup_email", { email, pw: await s.password(password), t: await sha(t), ip: await s.ip(b.ip), consent, invite });
  if (r.error) return r;
  if (r.mail === "verify") sendMail(env, ctx, { to: email, kind: "verify", lang, data: { link: link(env, b, "verify", t, lang) } });
  else if (r.mail === "already") sendMail(env, ctx, { to: email, kind: "already", lang, data: {} });
  return { sent: true };
}

// The "verify" link (the Worker page posts it): a new member, or an email added to an 아이디 account.
async function verify(env, s, b, ctx, where) {
  if (!TOKEN_RE.test(b.t || "")) return fail(400, "expired");
  const t = await sha(b.t);
  // a sign-up's link needs the password chosen in the app (an added email's does not: its account asked with its password)
  const pw = typeof b.password === "string" && b.password ? await s.password(b.password.slice(0, 100)) : null;
  for (let i = 0; i < 5; i++) {   // a new member's id is drawn at random; "retry" if it is somebody's already
    const r = await db(env, "verify", { t, id: newMemberId(), proof: await s.proof(newRecovery()), ...(pw ? { pw } : {}), ...where });
    if (r.error === "retry") continue;
    if (r.error) return r;
    if (r.mail === "email_added") sendMail(env, ctx, { to: r.email, kind: "email_added", lang: langOf(b), data: { id: r.id } });
    return { done: true, _added: r.mail === "email_added" };
  }
  return fail(503, "unavailable");
}

async function forgot(env, s, b, ctx) {
  if (!mailOn(env)) return fail(503, "mail_off");
  const email = cleanEmail(b.email);
  if (!email) return fail(400, "email");
  const t = newToken(), lang = langOf(b);
  const r = await db(env, "forgot", { email, t: await sha(t), ip: await s.ip(b.ip) });
  if (r.error) return r;
  if (r.mail === "reset" && r.email) sendMail(env, ctx, { to: r.email, kind: "reset", lang, data: { link: link(env, b, "reset", t, lang) } });
  return { sent: true };
}

// The reset link (the Worker page posts it with the new password): every session ends.
async function reset(env, s, b, ctx, where) {
  if (!TOKEN_RE.test(b.t || "")) return fail(400, "expired");
  const t = await sha(b.t);
  const peek = await db(env, "reset_peek", { t });
  if (peek.error) return peek;
  const next = String(b.password ?? "");
  if (!newPasswordOk(next, peek.id, peek.email)) return fail(400, "weak");
  const r = await db(env, "reset", { t, next: await s.password(next), ...where });
  if (r.error) return r;
  if (r.email) sendMail(env, ctx, { to: r.email, kind: "password_changed", lang: langOf(b), data: { when: Date.now() } });
  return { done: true };
}

async function addEmail(env, s, token, b, ctx, where) {
  if (!mailOn(env)) return early(env, token, fail(503, "mail_off"));
  const email = cleanEmail(b.email);
  if (!email) return early(env, token, fail(400, "email"));
  const t = newToken(), lang = langOf(b);
  const r = await db(env, "add_email", { token, pw: await s.password(String(b.password || "").slice(0, 100)), ip: await s.ip(b.ip),
                                         email, t: await sha(t), ...where });
  if (r.error) return r;
  if (r.mail === "verify") sendMail(env, ctx, { to: email, kind: "verify", lang, data: { link: link(env, b, "verify", t, lang) + "&add=1", add: true, id: r.id } });
  else if (r.mail === "already") sendMail(env, ctx, { to: email, kind: "already", lang, data: {} });
  return { sent: true };
}

// ---------------------------------------------------------------- 2-step login
async function totpStart(env, s, token, b) {
  const g = await db(env, "totp_get", { token });
  if (g.error) return g;
  if (g.totp) return fail(409, "already");
  const secret = newSecret();
  const r = await db(env, "totp_start", { token, pw: await s.password(String(b.password || "").slice(0, 100)), ip: await s.ip(b.ip),
                                          pending: await seal(s.pepper, secret, g.id) });
  if (r.error) return r;
  const b32 = base32(secret);
  return { secret: b32, uri: otpUri(b32, g.email || g.id) };
}

async function totpConfirm(env, s, token, b, where) {
  const g = await db(env, "totp_get", { token });
  if (g.error) return g;
  if (g.totp) return fail(409, "already");
  const secret = g.pending ? await unseal(s.pepper, g.pending, g.id) : null;
  if (!secret) return fail(409, "nothing");
  const step = await totpStep(secret, String(b.code ?? "").slice(0, 16), 0);
  if (!step) return fail(400, "wrong");
  const codes = newBackupCodes();
  const r = await db(env, "totp_on", { token, pending: g.pending, step, backup: await Promise.all(codes.map(s.backup)), ...where });
  return r.error ? r : { backup: codes };
}

// Off with the password and a code: one from the authenticator app (6 digits) or a backup code.
async function totpOff(env, s, token, b, where) {
  const g = await db(env, "totp_get", { token });
  if (g.error) return g;
  if (!g.totp) return fail(409, "off");
  const c = String(b.code ?? "").slice(0, 64);
  let verdict;
  if (normBackup(c).length === 12) verdict = { backup: await s.backup(c) };
  else {
    const secret = await unseal(s.pepper, g.totp, g.id);
    verdict = { step: secret ? await totpStep(secret, c, Number(g.step) || 0) : 0 };
  }
  const r = await db(env, "totp_off", { token, pw: await s.password(String(b.password || "").slice(0, 100)), ip: await s.ip(b.ip), ...verdict, ...where });
  return r.error ? r : { done: true };
}

// ---------------------------------------------------------------- licence keys
// The keys linked to the account, and the newest key bought with its verified email (the KV "email:" index). Reads only.
async function keys(env, token, deps) {
  const r = await db(env, "keys", { token });
  if (r.error) return r;
  const ids = (Array.isArray(r.keys) ? r.keys : []).filter(x => /^[0-9a-f]{16}$/.test(x));
  if (r.email && deps.emailKeyId) {
    const id = await deps.emailKeyId(env, r.email);
    if (id && !ids.includes(id)) ids.push(id);
  }
  const out = [];
  for (const id of ids.slice(0, LIMITS.keysKept + 1)) {
    const k = deps.readKey ? await deps.readKey(env, id) : null;
    if (k) out.push(k);
  }
  return { keys: out };
}

// A key joins the account only through a link mailed to the address it was bought with.
async function linkKey(env, s, token, b, ctx, deps) {
  if (!mailOn(env)) return early(env, token, fail(503, "mail_off"));
  const k = deps.keyRecord ? await deps.keyRecord(env, b.key) : null;
  if (!k) return early(env, token, fail(400, "key"));
  const to = cleanEmail(k.meta?.email || k.payload?.email);
  if (!to) return early(env, token, fail(400, "no_email"));
  const t = newToken(), lang = langOf(b);
  const r = await db(env, "link_key", { token, key: k.id, t: await sha(t), ip: await s.ip(b.ip) });
  if (r.error) return r;
  sendMail(env, ctx, { to, kind: "link_key", lang, data: { link: link(env, b, "link", t, lang), keyId: k.id } });
  return { sent: true };
}

// ---------------------------------------------------------------- recovery requests
// What the member may tell about a payment, so the seller can match it to the records. Each is optional.
function factsOf(b) {
  const f = {};
  const text = (name, n) => {
    if ([...String(b[name] ?? "").trim()].length > n) return false;
    const v = printable(b[name], n);
    if (v) f[name] = v;
    return true;
  };
  if (!text("contact", 100) || !text("depositor", 40)) return null;
  if (b.order != null && b.order !== "") { const v = String(b.order).trim().toUpperCase(); if (!/^[A-Z0-9-]{1,40}$/.test(v)) return null; f.order = v; }
  if (b.paidOn != null && b.paidOn !== "") { const v = String(b.paidOn).trim(); if (!/^\d{4}-\d{2}-\d{2}$/.test(v)) return null; f.paidOn = v; }
  if (b.amount != null && b.amount !== "") { const v = String(b.amount).replace(/[,\s원]/g, ""); if (!/^\d{1,9}$/.test(v)) return null; f.amount = v; }
  if (b.card4 != null && b.card4 !== "") { const v = String(b.card4).trim(); if (!/^\d{4}$/.test(v)) return null; f.card4 = v; }
  if ([...String(b.message ?? "")].length > 1000) return null;
  const m = printable(String(b.message ?? "").replace(/\r?\n/g, " "), 1000);
  if (m) f.message = m;
  f.lang = langOf(b);
  return f;
}

async function recoveryRequest(env, s, b, ctx, where) {
  const login = String(b.login ?? "").trim().slice(0, 254);
  if (!login) return fail(400, "login");
  const facts = factsOf(b);
  if (!facts) return fail(400, "facts");
  const byEmail = login.includes("@");
  const t = newToken(), lang = langOf(b);
  const r = await db(env, "recovery_request", { ...(byEmail ? { email: login.toLowerCase() } : { id: cleanId(login) }), facts,
                                                t: await sha(t), rid: hex(random(6)), ip: await s.ip(b.ip), ...where });
  if (r.error) return r;
  if (r.mail === "recovery_requested" && r.email)
    sendMail(env, ctx, { to: r.email, kind: "recovery_requested", lang, data: { link: link(env, b, "recovery/cancel", t, lang), due: r.due } });
  return { sent: true };
}

// The seller approves after "due": 2-step off, every session ended; a reset link by mail for an account with an
// email, a new recovery code (shown once in the dashboard) for one without.
async function approveRecovery(env, s, b, ctx) {
  const id = String(b.id || "");
  if (!SID_RE.test(id)) return fail(400, "요청 번호가 잘못됐어요");
  const t = newToken(), recovery = newRecovery();
  const r = await db(env, "admin/recovery_approve", { id, t: await sha(t), proof: await s.proof(recovery), mail: mailOn(env) });
  if (r.error) return r;
  if (r.email) {
    const lang = r.lang === "en" ? "en" : "ko";
    sendMail(env, ctx, { to: r.email, kind: "recovery_approved", lang, data: { link: link(env, b, "reset", t, lang) } });
    return { ok: true, account: r.account, mailed: true };
  }
  return { ok: true, account: r.account, recovery };
}
