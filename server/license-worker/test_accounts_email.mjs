// Local test of member accounts v2: email sign-up and its "verify" page, login by email or 아이디 with a device, the
// 2-step login (TOTP and backup codes), password reset by mail, logged-in devices, an email added to a 아이디 account,
// licence keys (list, link by mail, POST /keys/resend), consents, recovery requests (request, cancel, the dashboard's
// approve and reject), leaving, and mail being off. Real Postgres (PGlite) with supabase/accounts.sql behind a stand-in
// for Supabase's RPC route, the Stats object on node:sqlite (fake_stats.mjs), fake KV, and a stand-in for Resend that
// records mails instead of sending them. Throwaway keys and passwords only; nothing leaves this process.
//   npm i --no-save @electric-sql/pglite && node test_accounts_email.mjs
import assert from "node:assert/strict";
import { generateKeyPairSync } from "node:crypto";
import { readFileSync } from "node:fs";
import { PGlite } from "@electric-sql/pglite";
import { pgcrypto } from "@electric-sql/pglite/contrib/pgcrypto";
import worker from "./src/index.js";
import { fakeStats } from "./fake_stats.mjs";
import { hotp, stepAt } from "./src/totp.js";
import { MAIL_KINDS, _forgetMailLimits, render } from "./src/mail.js";
import { _forgetResendLimits } from "./src/account_web.js";
import { CONSENT_NOW, cleanEmail, newPasswordOk } from "./src/accounts.js";

const pg = new PGlite({ extensions: { pgcrypto } });
await pg.exec(`CREATE SCHEMA extensions; CREATE ROLE anon NOLOGIN; CREATE ROLE authenticated NOLOGIN; CREATE ROLE service_role NOLOGIN BYPASSRLS;`);
await pg.exec(readFileSync(new URL("./supabase/accounts.sql", import.meta.url), "utf8").replace("notify pgrst, 'reload schema';", ""));
const rows = async (query, ...args) => (await pg.query(query, args)).rows;
const one = async (query, ...args) => (await rows(query, ...args))[0];
const count = async (query, ...args) => Number((await one(query, ...args)).n);
const run = async (query, ...args) => { await pg.query(query, args); };

const SECRET = "made-up-supabase-key", MAIL_KEY = "made-up-mail-key";
const mails = [];
globalThis.fetch = async (url, init) => {
  if (String(url) === "https://api.resend.com/emails") {
    assert.equal(init.method, "POST");
    assert.equal(init.headers.Authorization, "Bearer " + MAIL_KEY);
    const m = JSON.parse(init.body);
    assert.deepEqual(Object.keys(m).sort(), ["from", "html", "subject", "text", "to"]);   // no tracking asked for
    mails.push(m);
    return Response.json({ id: "made-up-" + mails.length });
  }
  assert.equal(String(url), "https://project.supabase.example/rest/v1/rpc/account_call");
  assert.equal(init.headers.apikey, SECRET);
  const { action, b } = JSON.parse(init.body);
  await pg.exec("SET ROLE service_role");
  try {
    return Response.json((await pg.query("SELECT public.account_call($1, $2::jsonb) AS r", [action, JSON.stringify(b)])).rows[0].r);
  } finally {
    await pg.exec("RESET ROLE");
  }
};

const kv = new Map(), md = new Map();
let puts = 0;
const st = fakeStats();
const { privateKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
const env = {
  LICENSES: {
    get: async k => kv.get(k) ?? null,
    getWithMetadata: async k => ({ value: kv.get(k) ?? null, metadata: md.get(k) ?? null }),
    put: async (k, v, o) => { puts++; kv.set(k, v); md.set(k, o?.metadata ?? null); },
    delete: async k => { puts++; kv.delete(k); md.delete(k); },
    list: async ({ prefix, limit = 1000 }) => ({ keys: [...kv.keys()].filter(k => k.startsWith(prefix)).sort().slice(0, limit).map(name => ({ name, metadata: md.get(name) })), list_complete: true }),
  },
  LICENSE_PRIVATE_KEY: privateKey.export({ type: "pkcs8", format: "pem" }),
  ADMIN_TOKEN: "T".repeat(32),
  SUPABASE_URL: "https://project.supabase.example/",
  SUPABASE_SECRET_KEY: SECRET,
  PUBLIC_URL: "https://lic.example",
  RESEND_API_KEY: MAIL_KEY,
  MAIL_FROM: "SystemBoost <no-reply@mail.example.com>",
  STATS: st.binding,
};
const mailOff = { ...env, RESEND_API_KEY: "", MAIL_FROM: "" };

// everything the Worker logs, to look for secrets in at the end
const logged = [];
const log = console.log;
console.log = (...args) => { logged.push(args.map(String).join(" ")); log(...args); };

const waits = [];
const ctx = { waitUntil: p => { waits.push(p); } };
const settle = async () => { while (waits.length) await waits.shift(); };
let ipN = 0;
const nextIp = () => { ipN++; return `10.9.${(ipN >> 8) & 255}.${ipN & 255}`; };
const replies = [];
const send = async (path, body, token, opt = {}) => {
  const headers = { "CF-Connecting-IP": opt.ip || nextIp() };
  if (token) headers.Authorization = "Bearer " + token;
  const req = new Request("https://lic.example" + path, { method: body === undefined ? "GET" : "POST", headers, body: body === undefined ? undefined : JSON.stringify(body) });
  if (opt.country) Object.defineProperty(req, "cf", { value: { country: opt.country } });
  const r = await worker.fetch(req, opt.env || env, ctx);
  const text = await r.text();
  replies.push(text);
  await settle();
  return { status: r.status, ...JSON.parse(text) };
};
const page = async (path, form, opt = {}) => {
  const headers = { "CF-Connecting-IP": opt.ip || nextIp(), ...(opt.headers || {}) };
  const init = form ? { method: "POST", headers: { ...headers, "Content-Type": "application/x-www-form-urlencoded" }, body: new URLSearchParams(form).toString() } : { headers };
  const r = await worker.fetch(new Request("https://lic.example" + path, init), opt.env || env, ctx);
  const html = await r.text();
  replies.push(html);
  await settle();
  return { status: r.status, headers: r.headers, html };
};
const adm = async (path, body, e = env) => {
  const r = await worker.fetch(new Request("https://lic.example/admin/api" + path, { method: body ? "POST" : "GET", headers: { Authorization: "Bearer " + env.ADMIN_TOKEN, "CF-Connecting-IP": "10.250.0.1" }, body: body ? JSON.stringify(body) : undefined }), e, ctx);
  const out = { status: r.status, ...(await r.json()) };
  await settle();
  return out;
};
const freshLimits = () => { try { st.db.exec("DELETE FROM counters"); } catch { /* not made yet */ } _forgetMailLimits(); _forgetResendLimits(); };
const unlock = id => run("UPDATE public.accounts SET fails = 0, fail_at = NULL, locked_until = NULL WHERE id = $1", id);

// mails: the newest to an address, its kind (by subject), and the link in it
const mailsTo = to => mails.filter(m => m.to.length === 1 && m.to[0] === to);
const lastTo = to => mailsTo(to).at(-1);
const subjects = Object.fromEntries(MAIL_KINDS.map(k => [k, [render(k, "ko", {}).subject, render(k, "en", {}).subject]]));
const kindOf = m => MAIL_KINDS.find(k => subjects[k].includes(m?.subject));
const linkIn = m => {
  const x = /https:\/\/lic\.example\/account\/([a-z/]+)\?t=([0-9a-f]{64})((?:&[a-z]+=[a-z0-9]+)*)/.exec(m?.text || "");
  return x ? { path: x[1], t: x[2], query: x[3], url: x[0].slice("https://lic.example".length) } : null;
};

// TOTP in the test: base32 back to bytes, the code at a step offset, and a 6-digit code that is surely wrong now
const unb32 = s => { const A = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"; let bits = 0, v = 0; const out = []; for (const c of s) { v = (v << 5) | A.indexOf(c); bits += 5; if (bits >= 8) { out.push((v >>> (bits - 8)) & 255); bits -= 8; } } return Uint8Array.from(out); };
const codeAt = (secret, k = 0) => hotp(secret, stepAt() + k);
const wrongCode = async secret => {
  const near = await Promise.all([-2, -1, 0, 1, 2].map(k => codeAt(secret, k)));
  let c = 0;
  while (near.includes(String(c).padStart(6, "0"))) c++;
  return String(c).padStart(6, "0");
};
const K1 = "a1".repeat(32), K2 = "b2".repeat(32), K3 = "c3".repeat(32);
const signupEmail = (email, password, extra) => send("/account/signup_email", { email, password, agree: true, age14: true, lang: "ko", consent: CONSENT_NOW, ...extra });
async function emailMember(email, password) {   // sign-up, verify by the page, log in
  assert.equal((await signupEmail(email, password)).sent, true);
  const l = linkIn(lastTo(email));
  assert.equal((await page("/account/verify", { t: l.t, password })).status, 200);
  const r = await send("/account/login", { email, password, device: { name: "PC " + email, key: K1 } });
  assert.equal(r.status, 200, JSON.stringify(r));
  return r;
}
const okUser = (id, extra) => ({ id, password: "id-member-pass1", agree: true, age14: true, ...extra });

// ---------------------------------------------------------------------------------------------- the rules
assert.equal(newPasswordOk("short-9", "x"), false);
assert.equal(newPasswordOk("x".repeat(73), "x"), false);
assert.equal(newPasswordOk("password123", "x"), false);                   // common (SecLists), any case
assert.equal(newPasswordOk("PassWord123", "x"), false);
assert.equal(newPasswordOk("someone_99", "someone_99"), false);           // the id
assert.equal(newPasswordOk("longlocalpart", "", "longlocalpart@example.com"), false);   // the email's local part
assert.equal(newPasswordOk("Longlocalpart@Example.com", "", "longlocalpart@example.com"), false);
assert.equal(newPasswordOk("correct-horse-9", "x", "a@example.com"), true);
assert.equal(cleanEmail("  Alice@Example.COM "), "alice@example.com");
for (const bad of ["", "a", "a@b", "a@@b.com", "a b@c.com", "a@b..com", ".a@b.com", "a.@b.com", "a@-b.com", "x".repeat(65) + "@b.com", "a@" + "b".repeat(250) + ".com"])
  assert.equal(cleanEmail(bad), "", bad);

// ---------------------------------------------------------------------------------------------- features, mail off
assert.deepEqual(await send("/account/features"), { status: 200, email: true, totp: true });
assert.deepEqual(await send("/account/features", {}), { status: 200, email: true, totp: true });
assert.deepEqual(await send("/account/features", undefined, null, { env: mailOff }), { status: 200, email: false, totp: true });
const idMember = await send("/account/signup", okUser("off_member"), null, { env: mailOff });
assert.equal(idMember.status, 200);   // everything without mail keeps working
for (const [path, body, token] of [["/account/signup_email", { email: "x@example.com", password: "correct-horse-9", agree: true, age14: true }],
                                   ["/account/forgot", { email: "x@example.com" }], ["/keys/resend", { email: "x@example.com" }],
                                   ["/account/add_email", { email: "x@example.com", password: "id-member-pass1" }, idMember.token],
                                   ["/account/link_key", { key: "a.b" }, idMember.token]])
  assert.deepEqual(await send(path, body, token, { env: mailOff }), { status: 503, error: "mail_off" }, path);
assert.equal((await send("/account/login", { id: "off_member", password: "id-member-pass1" }, null, { env: mailOff })).status, 200);
assert.equal((await send("/account/recovery_request", { login: "off_member" }, null, { env: mailOff })).sent, true);
assert.equal(mails.length, 0);
await run("DELETE FROM public.account_recovery");

// ---------------------------------------------------------------------------------------------- email sign-up, verify
const ALICE = "alice@example.com", PWA = "alice-strong-pw1";
for (const [body, error] of [[{ email: "not-an-email" }, "email"], [{ password: "short" }, "weak"], [{ password: "password123" }, "weak"],
                             [{ email: "longlocalpart@example.com", password: "longlocalpart" }, "weak"], [{ agree: false }, "consent"],
                             [{ age14: undefined }, "consent"], [{ consent: "yesterday" }, "consent"], [{ invite: "NOPE1234" }, "unknown"]]) {
  const r = await signupEmail(body.email ?? ALICE, body.password ?? PWA, body);
  assert.equal(r.error, error, JSON.stringify(body));
}
assert.equal(mails.length, 0);
assert.deepEqual(await signupEmail(" Alice@Example.com ", PWA), { status: 200, sent: true });
let m = lastTo(ALICE);
assert.equal(kindOf(m), "verify");
assert.equal(m.from, env.MAIL_FROM);
assert.match(m.text, /본인이 아니라면/);
let l = linkIn(m);
assert.equal(l.path, "verify");
assert.equal(await count("SELECT COUNT(*) AS n FROM public.accounts WHERE email = $1", ALICE), 0);   // not before the link
const [pending] = await rows("SELECT * FROM public.account_tokens WHERE kind = 'signup' AND email = $1", ALICE);
assert.notEqual(pending.token, l.t);                     // only a one-way value of the link is kept
assert.match(pending.hash, /^\$2a\$10\$/);              // the chosen password, bcrypt
assert.ok(!JSON.stringify(pending).includes(PWA));
assert.ok(Math.abs(new Date(pending.expires) - new Date(pending.created) - 24 * 3600e3) < 5000);
// GET only shows the page (mail scanners open links); the POST of its form does the work
let p = await page(l.url);
assert.equal(p.status, 200);
assert.match(p.headers.get("content-security-policy"), /default-src 'none'.*form-action 'self'/);
assert.equal(p.headers.get("referrer-policy"), "no-referrer");
assert.equal(p.headers.get("x-frame-options"), "DENY");
assert.ok(p.html.includes(`name="t" value="${l.t}"`) && p.html.includes("가입 완료") && p.html.includes('name="password" type="password"'));
assert.ok(!/<script/i.test(p.html));
assert.equal(await count("SELECT COUNT(*) AS n FROM public.accounts WHERE email = $1", ALICE), 0);
assert.match((await page(l.url, null, { headers: { "Accept-Language": "en-US,en;q=0.9" } })).html, /Finish sign-up/);
assert.match((await page(l.url.replace("?", "?lang=en&"))).html, /Finish sign-up/);
// the link alone does not make the account: the password chosen in the app is asked for too
p = await page("/account/verify", { t: l.t, lang: "ko", password: "not-alices-pw-1" });
assert.equal(p.status, 400);
assert.match(p.html, /비밀번호가 맞지 않아요/);
assert.deepEqual(await send("/account/verify", { t: l.t }), { status: 400, error: "password" });
assert.equal(await count("SELECT COUNT(*) AS n FROM public.accounts WHERE email = $1", ALICE), 0);
assert.equal((await one("SELECT tries FROM public.account_tokens WHERE kind = 'signup' AND email = $1", ALICE)).tries, 2);
p = await page("/account/verify", { t: l.t, lang: "ko", password: PWA });
assert.equal(p.status, 200);
assert.match(p.html, /가입이 끝났어요/);
const alice = await one("SELECT * FROM public.accounts WHERE email = $1", ALICE);
assert.match(alice.id, /^m[a-z0-9]{11}$/);
assert.ok(alice.email_at && alice.consent.startsWith(CONSENT_NOW + " "));
assert.equal(await count("SELECT COUNT(*) AS n FROM public.account_tokens WHERE email = $1", ALICE), 0);
assert.equal((await page("/account/verify", { t: l.t, password: PWA })).status, 400);         // once
assert.deepEqual(await send("/account/verify", { t: l.t, password: PWA }), { status: 400, error: "expired" });
assert.equal((await page("/account/verify?t=zz")).status, 400);

// the same address again: the same reply, and an "already" mail to the owner instead of a link
assert.deepEqual(await signupEmail(ALICE, "other-strong-pw2"), { status: 200, sent: true });
assert.equal(kindOf(lastTo(ALICE)), "already");
assert.equal(linkIn(lastTo(ALICE)), null);
assert.equal(await count("SELECT COUNT(*) AS n FROM public.account_tokens WHERE kind = 'signup' AND email = $1", ALICE), 0);
// two sign-ups waiting for one address: the first link used ends the other
const BOB = "bob@example.com";
await signupEmail(BOB, "bob-strong-pw-1");
const bob1 = linkIn(lastTo(BOB));
await signupEmail(BOB, "bob-strong-pw-2");
const bob2 = linkIn(lastTo(BOB));
assert.notEqual(bob1.t, bob2.t);
// somebody else signed up the address first (bob1): its link does not take the owner's password
assert.match((await page("/account/verify", { t: bob1.t, password: "bob-strong-pw-2" })).html, /비밀번호가 맞지 않아요/);
assert.match((await page("/account/verify", { t: bob2.t, password: "bob-strong-pw-2" })).html, /가입이 끝났어요/);
assert.equal((await page("/account/verify", { t: bob1.t, password: "bob-strong-pw-1" })).status, 400);
assert.equal((await send("/account/login", { email: BOB, password: "bob-strong-pw-1" })).error, "wrong");
assert.equal((await send("/account/login", { email: BOB, password: "bob-strong-pw-2" })).status, 200);
// an email sign-up without "consent" (as an older text would) records the text before versions were sent
await send("/account/signup_email", { email: "plain@example.com", password: "plain-strong-pw1", agree: true, age14: true });
await page("/account/verify", { t: linkIn(lastTo("plain@example.com")).t, password: "plain-strong-pw1" });
assert.match((await one("SELECT consent FROM public.accounts WHERE email = 'plain@example.com'")).consent, /^2026-11-12 /);
// 아이디 sign-ups: the app's consent version too, and the old one without it
assert.equal((await send("/account/signup", okUser("con_new", { consent: CONSENT_NOW }))).status, 200);
assert.equal((await send("/account/signup", okUser("con_old"))).status, 200);
assert.equal((await send("/account/signup", okUser("con_bad", { consent: "x" }))).error, "consent");
assert.match((await one("SELECT consent FROM public.accounts WHERE id = 'con_new'")).consent, /^2026-11-18 /);
assert.match((await one("SELECT consent FROM public.accounts WHERE id = 'con_old'")).consent, /^2026-11-12 /);

// ---------------------------------------------------------------------------------------------- login, devices
let a1 = await send("/account/login", { email: ALICE, password: PWA, device: { name: "Desk PC", key: K1 } }, null, { country: "KR" });
assert.deepEqual(Object.keys(a1).sort(), ["id", "status", "token"]);
assert.equal(a1.id, alice.id);
assert.equal(mailsTo(ALICE).length, 2);                                            // the first device is not "new"
assert.equal((await send("/account/login", { id: "ALICE@example.com", password: PWA, device: { name: "Desk PC", key: K1 } })).status, 200);   // by "id" with @
assert.equal((await send("/account/login", { email: ALICE, password: "wrong-pass-1" })).error, "wrong");
assert.equal((await send("/account/login", { email: "nobody@example.com", password: PWA })).error, "wrong");
assert.equal(mailsTo(ALICE).length, 2);
const a2 = await send("/account/login", { email: ALICE, password: PWA, device: { name: "Laptop\u0007 <b>", key: K2 } }, null, { country: "JP" });
m = lastTo(ALICE);
assert.equal(kindOf(m), "new_device");
assert.ok(m.text.includes("Laptop <b>") && m.text.includes("JP") && m.html.includes("Laptop &lt;b&gt;") && !m.html.includes("Laptop <b>"));
const a3 = await send("/account/login", { email: ALICE, password: PWA });     // an older app: no device
assert.equal(mailsTo(ALICE).length, 3);
let dev = await send("/account/devices", undefined, a1.token);
assert.equal(dev.devices.length, 4);
for (const d of dev.devices) {
  assert.match(d.id, /^[0-9a-f]{12}$/);
  assert.deepEqual(Object.keys(d).sort(), ["country", "created", "current", "id", "lastSeen", "name"]);
}
assert.deepEqual(dev.devices.filter(d => d.current).map(d => d.name), ["Desk PC"]);
assert.ok(dev.devices.some(d => d.name === "SystemBoost app") && dev.devices.some(d => d.name === "Laptop <b>" && d.country === "JP"));
assert.ok(!JSON.stringify(dev).includes(a1.token) && !JSON.stringify(dev).includes(K1));
const laptop = dev.devices.find(d => d.name === "Laptop <b>");
assert.deepEqual(await send("/account/logout_device", { device: laptop.id }, a1.token), { status: 200, done: true });
assert.equal((await send("/account/me", undefined, a2.token)).error, "session");
assert.deepEqual(await send("/account/logout_device", { device: laptop.id }, a1.token), { status: 404, error: "device" });
assert.deepEqual(await send("/account/logout_device", { device: "zz" }, a1.token), { status: 404, error: "device" });
assert.deepEqual(await send("/account/logout_all", { keepThis: true }, a1.token), { status: 200, done: true });
assert.equal((await send("/account/me", undefined, a3.token)).error, "session");
assert.equal((await send("/account/me", undefined, a1.token)).id, alice.id);
assert.deepEqual(await send("/account/logout_all", {}, a1.token), { status: 200, done: true });
assert.equal((await send("/account/me", undefined, a1.token)).error, "session");
// five sessions kept, as before
const many = [];
for (let i = 0; i < 7; i++) many.push((await send("/account/login", { email: ALICE, password: PWA, device: { name: "PC", key: K1 } })).token);
assert.equal(await count("SELECT COUNT(*) AS n FROM public.account_sessions WHERE account = $1", alice.id), 5);
assert.equal((await send("/account/me", undefined, many[0])).error, "session");
let at = many.at(-1);
const me = await send("/account/me", undefined, at);
assert.equal(me.email, ALICE);
assert.equal(me.emailVerified, true);
assert.equal(me.twofa, false);
assert.equal(me.backupLeft, 0);
for (const k of ["id", "created", "code", "invites", "rewardDays", "earnedDays", "rewardCap", "rewardEach", "inviteDays", "inviteTicket"]) assert.ok(k in me, k);
// the security log: logins with the raw address, failed logins of a known account
const logRows = await rows("SELECT event, ip, country, device FROM public.account_security_log WHERE account = $1", alice.id);
assert.ok(logRows.some(r => r.event === "login" && r.country === "KR" && r.device === "Desk PC" && /^10\.9\./.test(r.ip)));
assert.ok(logRows.some(r => r.event === "login_failed") && logRows.some(r => r.event === "logout_all"));

// ---------------------------------------------------------------------------------------------- 2-step login
assert.equal((await send("/account/totp_start", { password: "wrong-pass-1" }, at)).error, "wrong");
const ts = await send("/account/totp_start", { password: PWA }, at);
assert.match(ts.secret, /^[A-Z2-7]{32}$/);
assert.equal(ts.uri, `otpauth://totp/SystemBoost%3Aalice%40example.com?secret=${ts.secret}&issuer=SystemBoost&digits=6&period=30`);
const secret = unb32(ts.secret);
assert.equal(secret.length, 20);
let row = await one("SELECT totp, totp_pending FROM public.accounts WHERE id = $1", alice.id);
assert.equal(row.totp, null);
assert.match(row.totp_pending, /^v1\.[A-Za-z0-9+/=]+$/);
assert.ok(!row.totp_pending.includes(ts.secret));
assert.deepEqual(await send("/account/totp_confirm", { code: await wrongCode(secret) }, at), { status: 400, error: "wrong" });
assert.equal((await send("/account/login", { email: ALICE, password: PWA })).twofa, undefined);   // not on before the code
const tc = await send("/account/totp_confirm", { code: await codeAt(secret) }, at);
assert.equal(tc.backup.length, 10);
for (const c of tc.backup) assert.match(c, /^[2-9A-HJKMNP-Z]{4}-[2-9A-HJKMNP-Z]{4}-[2-9A-HJKMNP-Z]{4}$/);
assert.equal(new Set(tc.backup).size, 10);
const hashes = (await rows("SELECT h FROM public.account_backup WHERE account = $1", alice.id)).map(r => r.h);
assert.equal(hashes.length, 10);
for (const c of tc.backup) assert.ok(!hashes.includes(c) && !JSON.stringify(hashes).includes(c.replaceAll("-", "")));
assert.deepEqual(await send("/account/totp_confirm", { code: await codeAt(secret) }, at), { status: 409, error: "already" });
assert.deepEqual(await send("/account/totp_start", { password: PWA }, at), { status: 409, error: "already" });
assert.equal((await send("/account/me", undefined, at)).twofa, true);
assert.deepEqual(await send("/account/backup_left", undefined, at), { status: 200, left: 10 });
// login: a pending login, no session; the code from the app (once), a backup code (once)
let lg = await send("/account/login", { email: ALICE, password: PWA, device: { name: "Desk PC", key: K1 } });
assert.deepEqual(Object.keys(lg).sort(), ["pending", "status", "twofa"]);
assert.match(lg.pending, /^[0-9a-f]{64}$/);
const tok = await one("SELECT * FROM public.account_tokens WHERE kind = 'login2' AND account = $1", alice.id);
assert.notEqual(tok.token, lg.pending);
assert.ok(Math.abs(new Date(tok.expires) - new Date(tok.created) - 5 * 60e3) < 5000);
assert.deepEqual(await send("/account/login2", { pending: lg.pending, code: await wrongCode(secret) }), { status: 401, error: "wrong" });
const confirmCode = await codeAt(secret, 0), nextCode = await codeAt(secret, 1);
assert.deepEqual(await send("/account/login2", { pending: lg.pending, code: confirmCode }), { status: 401, error: "wrong" });   // used by totp_confirm
let l2 = await send("/account/login2", { pending: lg.pending, code: nextCode });
assert.deepEqual(Object.keys(l2).sort(), ["id", "status", "token"]);
assert.equal((await send("/account/me", undefined, l2.token)).id, alice.id);
assert.deepEqual(await send("/account/login2", { pending: lg.pending, code: nextCode }), { status: 400, error: "expired" });   // spent
lg = await send("/account/login", { email: ALICE, password: PWA });
assert.deepEqual(await send("/account/login2", { pending: lg.pending, code: nextCode }), { status: 401, error: "wrong" });    // a code works once
await run("UPDATE public.accounts SET totp_step = 0 WHERE id = $1", alice.id);   // as if a minute had passed
assert.equal((await send("/account/login2", { pending: lg.pending, code: await codeAt(secret, 1) })).status, 200);
const before = mailsTo(ALICE).length;
lg = await send("/account/login", { email: ALICE, password: PWA });
l2 = await send("/account/login2", { pending: lg.pending, backup: tc.backup[0].toLowerCase().replaceAll("-", " ") });
assert.equal(l2.status, 200);
assert.equal(l2.backupLeft, 9);
assert.equal(l2.redo2fa, true);
m = lastTo(ALICE);
assert.equal(mailsTo(ALICE).length, before + 1);
assert.equal(kindOf(m), "backup_used");
assert.match(m.text, /9개/);
lg = await send("/account/login", { email: ALICE, password: PWA });
assert.deepEqual(await send("/account/login2", { pending: lg.pending, backup: tc.backup[0] }), { status: 401, error: "wrong" });   // once
await unlock(alice.id);
// five tries per pending login (each counted like a wrong password too)
lg = await send("/account/login", { email: ALICE, password: PWA });
for (let i = 0; i < 4; i++) assert.equal((await send("/account/login2", { pending: lg.pending, code: await wrongCode(secret) })).error, "wrong");
await unlock(alice.id);
assert.equal((await send("/account/login2", { pending: lg.pending, code: await wrongCode(secret) })).error, "wrong");
await unlock(alice.id);
assert.deepEqual(await send("/account/login2", { pending: lg.pending, backup: tc.backup[1] }), { status: 400, error: "expired" });
// five minutes
lg = await send("/account/login", { email: ALICE, password: PWA });
await run("UPDATE public.account_tokens SET expires = now() - interval '1 second' WHERE kind = 'login2'");
assert.deepEqual(await send("/account/login2", { pending: lg.pending, backup: tc.backup[1] }), { status: 400, error: "expired" });
assert.deepEqual(await send("/account/login2", { pending: "zz", code: "123456" }), { status: 400, error: "expired" });
// wrong 2-step codes lock the account like wrong passwords
lg = await send("/account/login", { email: ALICE, password: PWA });
for (let i = 0; i < 4; i++) await send("/account/login2", { pending: lg.pending, backup: "AAAA-AAAA-AAA" + i });
const lg2 = await send("/account/login", { email: ALICE, password: PWA });
await send("/account/login2", { pending: lg2.pending, backup: "AAAA-AAAA-AAAA" });
assert.equal((await send("/account/login", { email: ALICE, password: PWA })).error, "locked");
await unlock(alice.id);
// an older app (no device, no 2-step screen) gets the pending reply and cannot finish: documented
assert.equal((await send("/account/login", { id: ALICE, password: PWA })).twofa, true);
// new backup codes: the old ones all stop
at = l2.token;
assert.equal((await send("/account/backup_new", { password: "wrong-pass-2" }, at)).error, "wrong");
await unlock(alice.id);
const bn = await send("/account/backup_new", { password: PWA }, at);
assert.equal(bn.backup.length, 10);
lg = await send("/account/login", { email: ALICE, password: PWA });
assert.equal((await send("/account/login2", { pending: lg.pending, backup: tc.backup[2] })).error, "wrong");
await unlock(alice.id);
lg = await send("/account/login", { email: ALICE, password: PWA });
at = (await send("/account/login2", { pending: lg.pending, backup: bn.backup[0] })).token;
assert.deepEqual(await send("/account/backup_left", undefined, at), { status: 200, left: 9 });
// off: the password and a code (a backup code here; a code from the app below)
assert.equal((await send("/account/totp_off", { password: PWA, code: await wrongCode(secret) }, at)).error, "wrong");
await unlock(alice.id);
assert.equal((await send("/account/totp_off", { password: "wrong-pass-3", code: bn.backup[1] }, at)).error, "wrong");
await unlock(alice.id);
assert.deepEqual(await send("/account/totp_off", { password: PWA, code: bn.backup[1] }, at), { status: 200, done: true });
row = await one("SELECT totp, totp_pending, totp_step FROM public.accounts WHERE id = $1", alice.id);
assert.deepEqual([row.totp, row.totp_pending, Number(row.totp_step)], [null, null, 0]);
assert.equal(await count("SELECT COUNT(*) AS n FROM public.account_backup WHERE account = $1", alice.id), 0);
assert.deepEqual(await send("/account/totp_off", { password: PWA, code: "123456" }, at), { status: 409, error: "off" });
assert.deepEqual(await send("/account/backup_new", { password: PWA }, at), { status: 409, error: "off" });
assert.ok((await send("/account/login", { email: ALICE, password: PWA })).token);
// on again with a new secret, off with a code from the app
const ts2 = await send("/account/totp_start", { password: PWA }, at);
assert.notEqual(ts2.secret, ts.secret);
const secret2 = unb32(ts2.secret);
assert.equal((await send("/account/totp_confirm", { code: await codeAt(secret) }, at)).error, "wrong");   // the old secret is gone
await send("/account/totp_confirm", { code: await codeAt(secret2) }, at);
assert.deepEqual(await send("/account/totp_off", { password: PWA, code: await codeAt(secret2, 1) }, at), { status: 200, done: true });
const events = (await rows("SELECT event FROM public.account_security_log WHERE account = $1", alice.id)).map(r => r.event);
for (const e of ["2fa_on", "2fa_off", "backup_used", "backup_new", "login_failed"]) assert.ok(events.includes(e), e);

// ---------------------------------------------------------------------------------------------- forgot, reset
freshLimits();
const aliceSessions = [];
for (let i = 0; i < 3; i++) aliceSessions.push((await send("/account/login", { email: ALICE, password: PWA })).token);
assert.deepEqual(await send("/account/forgot", { email: "ALICE@example.com", lang: "en" }), { status: 200, sent: true });
m = lastTo(ALICE);
assert.equal(kindOf(m), "reset");
assert.match(m.subject, /Password reset link/);
l = linkIn(m);
assert.equal(l.path, "reset");
assert.equal(l.query, "&lang=en");
let rt = await one("SELECT * FROM public.account_tokens WHERE kind = 'reset' AND account = $1", alice.id);
assert.ok(Math.abs(new Date(rt.expires) - new Date(rt.created) - 30 * 60e3) < 5000);
const mailCount = mails.length;
assert.deepEqual(await send("/account/forgot", { email: "nobody@example.com" }), { status: 200, sent: true });   // the same reply
assert.equal(mails.length, mailCount);
assert.deepEqual(await send("/account/forgot", { email: "nope" }), { status: 400, error: "email" });
p = await page(l.url);
assert.equal(p.status, 200);
assert.ok(p.html.includes('type="password"') && p.html.includes("Choose a new password") && p.html.includes(`value="${l.t}"`));
assert.equal(await count("SELECT COUNT(*) AS n FROM public.account_tokens WHERE kind = 'reset' AND account = $1", alice.id), 1);   // GET spends nothing
p = await page("/account/reset", { t: l.t, lang: "en", password: "brand-new-pw-11", password2: "brand-new-pw-12" });
assert.equal(p.status, 400);
assert.match(p.html, /differ/);
p = await page("/account/reset", { t: l.t, lang: "en", password: "password123", password2: "password123" });
assert.equal(p.status, 400);
assert.match(p.html, /does not meet/);
p = await page("/account/reset", { t: l.t, lang: "en", password: ALICE, password2: ALICE });
assert.equal(p.status, 400);
p = await page("/account/reset", { t: l.t, lang: "en", password: "brand-new-pw-11", password2: "brand-new-pw-11" });
assert.equal(p.status, 200);
assert.match(p.html, /password was changed/);
assert.equal(kindOf(lastTo(ALICE)), "password_changed");
for (const t of aliceSessions) assert.equal((await send("/account/me", undefined, t)).error, "session");   // every session ends
assert.equal((await send("/account/login", { email: ALICE, password: PWA })).error, "wrong");
assert.equal((await send("/account/login", { email: ALICE, password: "brand-new-pw-11" })).status, 200);
assert.equal((await page("/account/reset", { t: l.t, password: "brand-new-pw-13", password2: "brand-new-pw-13" })).status, 400);   // once
assert.equal((await page(l.url)).status, 400);
// only the newest link works, and only for 30 minutes
await send("/account/forgot", { email: ALICE });
const r1 = linkIn(lastTo(ALICE));
await send("/account/forgot", { email: ALICE });
const r2 = linkIn(lastTo(ALICE));
assert.deepEqual(await send("/account/reset", { t: r1.t, password: "brand-new-pw-14" }), { status: 400, error: "expired" });
await run("UPDATE public.account_tokens SET expires = now() - interval '1 second' WHERE kind = 'reset'");
assert.deepEqual(await send("/account/reset", { t: r2.t, password: "brand-new-pw-14" }), { status: 400, error: "expired" });
await send("/account/forgot", { email: ALICE });
assert.deepEqual(await send("/account/reset", { t: linkIn(lastTo(ALICE)).t, password: "short" }), { status: 400, error: "weak" });
assert.deepEqual(await send("/account/reset", { t: linkIn(lastTo(ALICE)).t, password: "brand-new-pw-15" }), { status: 200, done: true });
const PWA2 = "brand-new-pw-15";
// a password change (the old action) mails too, and keeps its reply
freshLimits();   // five mails an hour to one address: this one has had them
at = (await send("/account/login", { email: ALICE, password: PWA2 })).token;
const ch = await send("/account/password", { password: PWA2, next: "alice" + "@example.com" }, at);
assert.equal(ch.error, "password");                        // not the email
const ch2 = await send("/account/password", { password: PWA2, next: "changed-pw-alice" }, at);
assert.deepEqual(Object.keys(ch2).sort(), ["id", "recovery", "status", "token"]);
assert.equal(kindOf(lastTo(ALICE)), "password_changed");
at = ch2.token;

// ---------------------------------------------------------------------------------------------- an email for a 아이디 account
freshLimits();
const DAVE = "dave@example.com";
const dave = await send("/account/signup", okUser("dave_id"));
assert.equal((await send("/account/add_email", { email: DAVE, password: "wrong-pass-1" }, dave.token)).error, "wrong");
assert.deepEqual(await send("/account/add_email", { email: "bad", password: "id-member-pass1" }, dave.token), { status: 400, error: "email" });
assert.deepEqual(await send("/account/add_email", { email: DAVE, password: "id-member-pass1" }, dave.token), { status: 200, sent: true });
m = lastTo(DAVE);
assert.equal(kindOf(m), "verify");
assert.match(m.text, /연결/);
assert.match(m.text, /아이디 dave_id/);                                    // the mail names the account
l = linkIn(m);
assert.equal(l.query, "&add=1");
const aliceMails = mailsTo(ALICE).length;
assert.deepEqual(await send("/account/add_email", { email: ALICE, password: "id-member-pass1" }, dave.token), { status: 200, sent: true });   // taken: same reply
assert.equal(mailsTo(ALICE).length, aliceMails + 1);
assert.equal(kindOf(lastTo(ALICE)), "already");
assert.equal((await send("/account/me", undefined, dave.token)).email, null);
assert.ok((await page(l.url)).html.includes("연결 완료"));
p = await page("/account/verify", { t: l.t, add: "1" });
assert.match(p.html, /메일 주소를 연결했어요/);
m = lastTo(DAVE);
assert.equal(kindOf(m), "email_added");
assert.match(m.text, /dave_id/);
const dme = await send("/account/me", undefined, dave.token);
assert.equal(dme.email, DAVE);
assert.equal(dme.emailVerified, true);
const dl = await send("/account/login", { email: DAVE, password: "id-member-pass1" });
assert.equal(dl.id, "dave_id");
assert.equal((await send("/account/login", { id: "dave_id", password: "id-member-pass1" })).status, 200);
assert.deepEqual(await send("/account/add_email", { email: "dave2@example.com", password: "id-member-pass1" }, dave.token), { status: 409, error: "has_email" });

// ---------------------------------------------------------------------------------------------- keys
freshLimits();
const BUYER = "buyer@example.com";
const issued = await adm("/issue", { plan: "yearly", email: BUYER, note: "test" });
assert.ok(issued.key && /^[0-9a-f]{16}$/.test(issued.id));
kv.set("email:" + BUYER, issued.key);   // what a card purchase leaves (index.js store)
const noMail = await adm("/issue", { plan: "yearly", note: "no email" });
const gone = await adm("/issue", { plan: "yearly", email: BUYER, note: "revoked one" });
kv.set("revoked:" + gone.id, "test");
const putsBefore = puts;
assert.deepEqual(await send("/account/link_key", { key: "garbage" }, dave.token), { status: 400, error: "key" });
assert.deepEqual(await send("/account/link_key", { key: noMail.key }, dave.token), { status: 400, error: "no_email" });
assert.deepEqual(await send("/account/link_key", { key: gone.key }, dave.token), { status: 400, error: "key" });
assert.deepEqual(await send("/account/link_key", { key: issued.key }), { status: 401, error: "session" });
assert.deepEqual(await send("/account/link_key", { key: " " + issued.key + "\n", lang: "en" }, dave.token), { status: 200, sent: true });
m = lastTo(BUYER);
assert.equal(kindOf(m), "link_key");
assert.ok(m.text.includes(issued.id) && !m.text.includes(issued.key));
l = linkIn(m);
assert.equal(l.path, "link");
assert.deepEqual((await send("/account/keys", undefined, dave.token)).keys, []);
assert.ok((await page(l.url)).html.includes("Link"));
assert.deepEqual((await send("/account/keys", undefined, dave.token)).keys, []);   // GET links nothing
p = await page("/account/link", { t: l.t, lang: "en" });
assert.equal(p.status, 200);
assert.match(p.html, /linked to your account/);
let ks = (await send("/account/keys", undefined, dave.token)).keys;
assert.equal(ks.length, 1);
assert.deepEqual(Object.keys(ks[0]).sort(), ["expires", "id", "issued", "key", "plan", "revoked"]);
assert.equal(ks[0].id, issued.id);
assert.equal(ks[0].key, issued.key);
assert.equal(ks[0].revoked, false);
assert.equal((await page("/account/link", { t: l.t })).status, 400);
assert.deepEqual(await send("/account/link_key_confirm", { t: l.t }), { status: 400, error: "expired" });
// a JSON confirm works the same
await send("/account/link_key", { key: issued.key }, dl.token);
assert.deepEqual(await send("/account/link_key_confirm", { t: linkIn(lastTo(BUYER)).t }), { status: 200, done: true });
// the verified email's key shows without a link; a revoked key shows as revoked
const buyer = await emailMember(BUYER, "buyer-strong-pw1");
ks = (await send("/account/keys", undefined, buyer.token)).keys;
assert.deepEqual(ks.map(k => k.id), [issued.id]);
kv.set("revoked:" + issued.id, "test");
assert.equal((await send("/account/keys", undefined, dave.token)).keys[0].revoked, true);
kv.delete("revoked:" + issued.id);
// POST /keys/resend: always the same reply; a mail only for a key bought with the address
freshLimits();
let n0 = mails.length;
assert.deepEqual(await send("/keys/resend", { email: "Buyer@Example.com", lang: "en" }), { status: 200, sent: true });
m = mails.at(-1);
assert.equal(mails.length, n0 + 1);
assert.equal(kindOf(m), "key_resend");
assert.ok(m.text.includes(issued.key) && m.to[0] === BUYER);
assert.deepEqual(await send("/keys/resend", { email: "nobody@example.com" }), { status: 200, sent: true });
assert.equal(mails.length, n0 + 1);
assert.deepEqual(await send("/keys/resend", { email: "x" }), { status: 400, error: "email" });
for (let i = 0; i < 4; i++) assert.deepEqual(await send("/keys/resend", { email: BUYER }), { status: 200, sent: true });
assert.equal(mails.length, n0 + 3);   // three an hour to one address
freshLimits();
const ip = "10.77.0.1";
for (let i = 0; i < 10; i++) assert.equal((await send("/keys/resend", { email: "n" + i + "@example.com" }, null, { ip })).status, 200);
assert.deepEqual(await send("/keys/resend", { email: BUYER }, null, { ip }), { status: 429, error: "busy" });
freshLimits();
md.set("lic:" + issued.id, { ...md.get("lic:" + issued.id), revoked: "test" });
n0 = mails.length;
await send("/keys/resend", { email: BUYER });
assert.equal(mails.length, n0);       // a revoked key is not sent
md.set("lic:" + issued.id, { ...md.get("lic:" + issued.id), revoked: "" });
assert.equal(puts, putsBefore);       // none of it wrote to KV

// ---------------------------------------------------------------------------------------------- consents
assert.deepEqual(await send("/account/consent", { kind: "oc", version: "2026-11-18", gpu: "NVIDIA GeForce RTX 4070\n", app: "0.8.16" }, dave.token), { status: 200, done: true });
assert.deepEqual(await send("/account/consent", { kind: "oc", version: "2026-11-18", withdraw: true }, dave.token), { status: 200, done: true });
assert.deepEqual(await send("/account/consent", { kind: "x", version: "1" }, dave.token), { status: 400, error: "kind" });
assert.deepEqual(await send("/account/consent", { kind: "oc", version: "no spaces allowed" }, dave.token), { status: 400, error: "version" });
assert.deepEqual(await send("/account/consent", { kind: "oc", version: "1" }), { status: 401, error: "session" });
const cs = await rows("SELECT kind, version, gpu, app, withdrawn FROM public.account_consents WHERE account = 'dave_id' ORDER BY at");
assert.deepEqual(cs.map(c => [c.kind, c.version, c.gpu, c.app, c.withdrawn]), [["oc", "2026-11-18", "NVIDIA GeForce RTX 4070", "0.8.16", false], ["oc", "2026-11-18", "", "", true]]);

// ---------------------------------------------------------------------------------------------- recovery requests
freshLimits();
const ERIN = "erin@example.com", PWE = "erin-strong-pw1";
const erin = await emailMember(ERIN, PWE);
const ets = await send("/account/totp_start", { password: PWE }, erin.token);
await send("/account/totp_confirm", { code: await codeAt(unb32(ets.secret)) }, erin.token);
const facts = { contact: "010-0000-0000", order: "sb-abcd-efgh", paidOn: "2026-11-01", amount: "9,900원", card4: "1234", depositor: "홍길동", message: "폰을 잃어버렸어요\n백업 코드도 없어요" };
for (const [k, v] of [["paidOn", "11/01"], ["card4", "12345"], ["amount", "abc"], ["order", "SB AB"], ["contact", "x".repeat(101)], ["message", "x".repeat(1001)]])
  assert.deepEqual(await send("/account/recovery_request", { login: ERIN, ...facts, [k]: v }), { status: 400, error: "facts" }, k);
assert.deepEqual(await send("/account/recovery_request", {}), { status: 400, error: "login" });
assert.deepEqual(await send("/account/recovery_request", { login: "Erin@Example.com", ...facts, lang: "ko" }), { status: 200, sent: true });
m = lastTo(ERIN);
assert.equal(kindOf(m), "recovery_requested");
l = linkIn(m);
assert.equal(l.path, "recovery/cancel");
let rq = await one("SELECT * FROM public.account_recovery WHERE account = $1", erin.id);
assert.equal(rq.status, "open");
assert.ok(Math.abs(new Date(rq.due) - new Date(rq.created) - 72 * 3600e3) < 5000);
assert.deepEqual(rq.facts, { contact: "010-0000-0000", order: "SB-ABCD-EFGH", paidOn: "2026-11-01", amount: "9900", card4: "1234", depositor: "홍길동", message: "폰을 잃어버렸어요 백업 코드도 없어요", lang: "ko" });
n0 = mails.length;
assert.deepEqual(await send("/account/recovery_request", { login: "nobody@example.com", ...facts }), { status: 200, sent: true });   // the same reply
assert.deepEqual(await send("/account/recovery_request", { login: "no_such_id", ...facts }), { status: 200, sent: true });
assert.deepEqual(await send("/account/recovery_request", { login: ERIN, ...facts }), { status: 200, sent: true });   // one open request per account
assert.equal(mails.length, n0);
assert.equal(await count("SELECT COUNT(*) AS n FROM public.account_recovery"), 1);
let list = await adm("/recovery");
assert.equal(list.status, 200);
assert.equal(list.mail, true);
assert.equal(list.requests.length, 1);
assert.equal(list.requests[0].email, ERIN);
assert.equal(list.requests[0].twofa, true);
assert.equal(list.requests[0].facts.card4, "1234");
assert.match((await adm("/recovery/approve", { id: rq.id })).error, /72시간/);   // not before the wait
assert.equal((await one("SELECT totp FROM public.accounts WHERE id = $1", erin.id)).totp !== null, true);
await run("UPDATE public.account_recovery SET due = now() - interval '1 second'");
assert.match((await adm("/recovery/approve", { id: rq.id }, mailOff)).error, /메일 발송이 꺼져/);   // an email account needs mail
assert.equal((await adm("/recovery/approve", { id: "zz" })).status, 400);
assert.equal((await adm("/recovery/approve", { id: "0".repeat(12) })).status, 404);
const ap = await adm("/recovery/approve", { id: rq.id });
assert.deepEqual(ap, { status: 200, ok: true, account: erin.id, mailed: true });
m = lastTo(ERIN);
assert.equal(kindOf(m), "recovery_approved");
l = linkIn(m);
assert.equal(l.path, "reset");
row = await one("SELECT totp, totp_pending FROM public.accounts WHERE id = $1", erin.id);
assert.deepEqual([row.totp, row.totp_pending], [null, null]);
assert.equal(await count("SELECT COUNT(*) AS n FROM public.account_backup WHERE account = $1", erin.id), 0);
assert.equal((await send("/account/me", undefined, erin.token)).error, "session");
assert.equal((await send("/account/login", { email: ERIN, password: PWE })).error, "wrong");   // the old password is gone too
await unlock(erin.id);
rt = await one("SELECT * FROM public.account_tokens WHERE kind = 'reset' AND account = $1", erin.id);
assert.ok(Math.abs(new Date(rt.expires) - new Date(rt.created) - 24 * 3600e3) < 5000);
assert.match((await page("/account/reset", { t: l.t, password: "erin-after-pw-1", password2: "erin-after-pw-1" })).html, /비밀번호를 바꿨어요/);
assert.ok((await send("/account/login", { email: ERIN, password: "erin-after-pw-1" })).token);   // no 2-step any more
assert.match((await adm("/recovery/approve", { id: rq.id })).error, /이미 처리된/);
assert.equal((await adm("/recovery")).requests.length, 0);                 // open ones
assert.equal((await adm("/recovery", { all: true })).requests[0].status, "approved");
// cancel by the mailed link
const FRANK = "frank@example.com";
await emailMember(FRANK, "frank-strong-pw1");
await send("/account/recovery_request", { login: FRANK, contact: "frank" });
l = linkIn(lastTo(FRANK));
assert.ok((await page(l.url)).html.includes("복구 요청 취소"));
assert.equal((await one("SELECT status FROM public.account_recovery WHERE facts->>'contact' = 'frank'")).status, "open");   // GET cancels nothing
assert.match((await page("/account/recovery/cancel", { t: l.t })).html, /취소했어요/);
const fr = await one("SELECT * FROM public.account_recovery WHERE facts->>'contact' = 'frank'");
assert.equal(fr.status, "cancelled");
assert.equal((await page("/account/recovery/cancel", { t: l.t })).status, 400);
await run("UPDATE public.account_recovery SET due = now() - interval '1 second'");
assert.match((await adm("/recovery/approve", { id: fr.id })).error, /이미 처리된/);
// reject; and a 아이디-only account gets a new recovery code instead of a mail
const gina = await send("/account/signup", okUser("gina_id"));
await send("/account/recovery_request", { login: "GINA_ID", contact: "gina" });
const gr = await one("SELECT * FROM public.account_recovery WHERE account = 'gina_id'");
assert.deepEqual(await adm("/recovery/reject", { id: gr.id, reason: "결제 기록이 없어요" }), { status: 200, ok: true });
assert.deepEqual(await one("SELECT status, reason FROM public.account_recovery WHERE id = $1", gr.id), { status: "rejected", reason: "결제 기록이 없어요" });
await send("/account/recovery_request", { login: "gina_id", contact: "gina again" });
const gr2 = await one("SELECT * FROM public.account_recovery WHERE account = 'gina_id' AND status = 'open'");
await run("UPDATE public.account_recovery SET due = now() - interval '1 second' WHERE id = $1", gr2.id);
n0 = mails.length;
const ga = await adm("/recovery/approve", { id: gr2.id }, mailOff);   // works with mail off: nothing to mail
assert.equal(ga.status, 200);
assert.match(ga.recovery, /^[A-HJ-NP-Z2-9]{4}(-[A-HJ-NP-Z2-9]{4}){3}$/);
assert.equal(mails.length, n0);
assert.equal((await send("/account/me", undefined, gina.token)).error, "session");
assert.equal((await send("/account/recover", { id: "gina_id", recovery: gina.recovery, next: "gina-new-pass-1" })).error, "wrong");   // the old code died
assert.equal((await send("/account/recover", { id: "gina_id", recovery: ga.recovery, next: "gina-new-pass-1" })).status, 200);
const auditLog = JSON.stringify(await adm("/log"));
assert.ok(auditLog.includes("계정 복구 승인") && auditLog.includes("계정 복구 거절"));
assert.ok(!auditLog.includes(ERIN) && !auditLog.includes("1234") && !auditLog.includes("홍길동"));
await send("/account/recovery_request", { login: "gina_id", contact: "cancel me" });
assert.ok(await count("SELECT COUNT(*) AS n FROM public.account_tokens WHERE kind = 'cancel' AND account = 'gina_id'") === 1);   // 아이디-only: no mail, the request waits for the seller

// ---------------------------------------------------------------------------------------------- leaving removes the new data
const daveTables = ["account_sessions", "account_tokens", "account_backup", "account_devices", "account_keys", "account_consents", "account_security_log", "account_recovery"];
await send("/account/recovery_request", { login: "dave_id", contact: "dave" });
await send("/account/login", { id: "dave_id", password: "id-member-pass1", device: { name: "Dave PC", key: K3 } });
await send("/account/forgot", { email: DAVE });
const dts = await send("/account/totp_start", { password: "id-member-pass1" }, dl.token);
await send("/account/totp_confirm", { code: await codeAt(unb32(dts.secret)) }, dl.token);
for (const t of daveTables) assert.ok(await count(`SELECT COUNT(*) AS n FROM public.${t} WHERE account = 'dave_id'`) > 0, t);
const kvBefore = JSON.stringify([...kv.entries()]);
assert.deepEqual(await send("/account/delete", { password: "id-member-pass1" }, dl.token), { status: 200, ok: true });
for (const t of daveTables) assert.equal(await count(`SELECT COUNT(*) AS n FROM public.${t} WHERE account = 'dave_id'`), 0, t);
assert.equal(await count("SELECT COUNT(*) AS n FROM public.account_tokens WHERE email = $1", DAVE), 0);
assert.equal(await count("SELECT COUNT(*) AS n FROM public.accounts WHERE id = 'dave_id' OR email = $1", DAVE), 0);
assert.equal(JSON.stringify([...kv.entries()]), kvBefore);                                         // keys and orders stay
assert.equal((await send("/keys/resend", { email: BUYER })).sent, true);
assert.equal((await send("/account/signup", okUser("dave_id"))).error, "taken");                // the id stays retired
assert.equal((await send("/account/login", { email: DAVE, password: "id-member-pass1" })).error, "wrong");
// the email can be used for a new account
assert.deepEqual(await signupEmail(DAVE, "dave-again-pw-1"), { status: 200, sent: true });
assert.equal(kindOf(lastTo(DAVE)), "verify");

// ---------------------------------------------------------------------------------------------- mail: templates, caps
for (const kind of MAIL_KINDS) for (const lang of ["ko", "en"]) {
  const r = render(kind, lang, { link: "https://lic.example/account/x?t=" + "0".repeat(64), key: "k.k", keyId: "0123456789abcdef", id: "someone", left: 3, due: Date.now() });
  assert.ok(r.subject.startsWith("[SystemBoost]"), kind);
  assert.ok(r.text.includes(lang === "en" ? "If this wasn't you" : "본인이 아니라면"), kind + " " + lang);
  assert.ok(r.html.includes("#FF7A1A") && r.html.includes("#0b0b0c"), kind);
}
assert.ok(!render("new_device", "ko", { device: "<script>x</script>" }).html.includes("<script>x"));
freshLimits();
const capped = { ...env, MAIL_DAY: "2" };
n0 = mails.length;
for (let i = 0; i < 4; i++) await send("/account/forgot", { email: ALICE }, null, { env: capped });
assert.equal(mails.length, n0 + 2);   // MAIL_DAY a day for the whole Worker
st.down = true;                       // the Stats object cannot be reached: this isolate's memory counts instead
freshLimits();
n0 = mails.length;
for (let i = 0; i < 7; i++) await send("/account/forgot", { email: ALICE });
assert.equal(mails.length, n0 + 5);   // five an hour to one address
st.down = false;

// ---------------------------------------------------------------------------------------------- nothing secret in logs or replies
const secrets = [PWA, PWA2, "changed-pw-alice", "id-member-pass1", "erin-after-pw-1", ts.secret, ts2.secret, ...tc.backup, ...bn.backup,
                 ALICE, DAVE, ERIN, BUYER, issued.key, MAIL_KEY, SECRET, "10.9.0."];
for (const line of logged) for (const s of secrets) assert.ok(!line.includes(s), "log: " + line.slice(0, 60));
const errorReplies = replies.filter(t => t.startsWith('{"error"'));
for (const t of errorReplies) for (const s of secrets) assert.ok(!t.includes(s), "error reply: " + t.slice(0, 60));
for (const t of mails.map(x => x.text)) for (const s of [PWA, PWA2, "id-member-pass1", ...tc.backup]) assert.ok(!t.includes(s), "a mail holds a secret");

// ---------------------------------------------------------------------------------------------- review 2026-11-18
freshLimits();
// a 아이디 account with 2-step login: its recovery code alone does not set a password or open a session
const tf = await send("/account/signup", okUser("tf_member"));
assert.equal(tf.status, 200);
assert.match(tf.recovery, /^[A-Z0-9]{4}(-[A-Z0-9]{4}){3}$/);
const tfs = await send("/account/totp_start", { password: "id-member-pass1" }, tf.token);
assert.equal((await send("/account/totp_confirm", { code: await codeAt(unb32(tfs.secret)) }, tf.token)).backup.length, 10);
assert.deepEqual(await send("/account/recover", { id: "tf_member", recovery: tf.recovery, next: "tf-new-strong-pw1" }), { status: 403, error: "twofa" });
assert.equal((await send("/account/me", undefined, tf.token)).id, "tf_member");                 // nothing changed
assert.equal((await send("/account/login", { id: "tf_member", password: "id-member-pass1" })).twofa, true);
// a reset by mail voids a recovery code handed out before (by a password change)
const RITA = "rita@example.com", PWR = "rita-strong-pw1";
const rita = await emailMember(RITA, PWR);
const rch = await send("/account/password", { password: PWR, next: "rita-strong-pw2" }, rita.token);
assert.match(rch.recovery, /-/);
await send("/account/forgot", { email: RITA });
await page("/account/reset", { t: linkIn(lastTo(RITA)).t, password: "rita-strong-pw3", password2: "rita-strong-pw3" });
assert.equal((await send("/account/login", { email: RITA, password: "rita-strong-pw3" })).status, 200);
assert.equal((await send("/account/recover", { id: rita.id, recovery: rch.recovery, next: "rita-taken-pw-9" })).error, "wrong");
assert.equal((await send("/account/login", { email: RITA, password: "rita-strong-pw3" })).status, 200);
// five wrong passwords end a sign-up's link
freshLimits();
const SAM = "sam@example.com";
await signupEmail(SAM, "sam-strong-pw-1");
const sam = linkIn(lastTo(SAM));
for (let i = 0; i < 5; i++) assert.deepEqual(await send("/account/verify", { t: sam.t, password: "sam-wrong-pw-" + i }), { status: 400, error: "password" });
assert.deepEqual(await send("/account/verify", { t: sam.t, password: "sam-strong-pw-1" }), { status: 400, error: "expired" });
assert.equal(await count("SELECT COUNT(*) AS n FROM public.accounts WHERE email = $1", SAM), 0);
// sign-up mails use at most half of the day's mails: a reset still goes out after many made-up sign-ups
freshLimits();
const day6 = { ...env, MAIL_DAY: "6" };
let n6 = mails.length;
for (let i = 0; i < 5; i++) await send("/account/signup_email", { email: `junk${i}@example.com`, password: "junk-strong-pw-1", agree: true, age14: true, consent: CONSENT_NOW }, null, { env: day6 });
assert.equal(mails.length - n6, 3);                                       // half of 6
n6 = mails.length;
await send("/account/forgot", { email: RITA }, null, { env: day6 });
assert.equal(mails.length - n6, 1);
assert.equal(kindOf(lastTo(RITA)), "reset");

console.log("account email tests passed (" + mails.length + " mails recorded, none sent)");
