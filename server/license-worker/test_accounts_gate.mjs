// Accounts v2 start at ACCOUNTS_V2_FROM (src/accounts_gate.js; wrangler.toml: 2027-01-01 00:00 KST). Before it the Worker
// must be, for apps, the server deployed on 2026-11-13 (commit 776dd71), running on the new SQL (that commit's
// supabase/accounts.sql plus supabase/migrations/20261118_email_accounts.sql, as it is applied before the deploy), and the
// database must get none of what accounts v2 add: no security-log line, device, lock for a name nobody has, consent,
// mailed link, "last used" time or time of a wrong password, and no device, address, country or login-name hash.
// This runs that server (its src/ from git, in a temporary folder, on its own SQL) beside today's Worker and sends both the
// same requests: the replies and what goes to the database must match. Then /privacy before and after the start, the
// switch at the start, and a missing or broken ACCOUNTS_V2_FROM. Made-up keys and passwords only; nothing leaves this process.
//   node test_accounts_gate.mjs        (PGlite: npm i --no-save @electric-sql/pglite; needs git with commit 776dd71)
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { generateKeyPairSync } from "node:crypto";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { basename, join } from "node:path";
import { pathToFileURL } from "node:url";
import { PGlite } from "@electric-sql/pglite";
import { pgcrypto } from "@electric-sql/pglite/contrib/pgcrypto";
import worker from "./src/index.js";
import { fakeStats } from "./fake_stats.mjs";
import { accountsV2Day, accountsV2From, accountsV2Open } from "./src/accounts_gate.js";
import { PRIVACY_HTML } from "./src/privacy_page.js";
import { PRIVACY_PREV_HTML } from "./src/privacy_prev_page.js";
import { DEFAULT_FAQ, PRIVACY_EMPTY } from "./src/support.js";

const LIVE = "776dd71";   // the server deployed on 2026-11-13
const here = new URL(".", import.meta.url);
const git = args => execFileSync("git", args, { cwd: here, encoding: "utf8", maxBuffer: 1 << 26 });
const atLive = path => {
  try { return git(["show", LIVE + ":server/license-worker/" + path]); }
  catch { throw new Error("git show " + LIVE + ":" + path + " failed: this test needs the repository's history"); }
};
const sqlText = text => text.replace(/\r\n/g, "\n").replace("notify pgrst, 'reload schema';", "");
let checks = 0;
const ok = (cond, msg) => { assert.ok(cond, msg); checks++; };
const eq = (a, b, msg) => { assert.deepEqual(a, b, msg); checks++; };

// ---------------------------------------------------------------- 1. ACCOUNTS_V2_FROM: strict, fails closed
const toml = readFileSync(new URL("./wrangler.toml", here), "utf8").replace(/\r\n/g, "\n");
const inToml = /^ACCOUNTS_V2_FROM = "([^"]*)"$/m.exec(toml);
ok(inToml && toml.indexOf(inToml[0]) > toml.indexOf("[vars]"), "wrangler.toml [vars] sets ACCOUNTS_V2_FROM");
const START = inToml[1];
eq(START, "2027-01-01T00:00:00+09:00");
eq(accountsV2From({ ACCOUNTS_V2_FROM: START }), Date.parse("2026-12-31T15:00:00Z"));
eq(accountsV2Day({ ACCOUNTS_V2_FROM: START }), "2027-01-01");
eq(accountsV2Open({ ACCOUNTS_V2_FROM: START }, Date.parse("2026-12-31T14:59:59.999Z")), false, "one millisecond before: not yet");
eq(accountsV2Open({ ACCOUNTS_V2_FROM: START }, Date.parse("2026-12-31T15:00:00Z")), true, "at the instant: started");
eq(accountsV2From({ ACCOUNTS_V2_FROM: "2026-12-31T15:00:00Z" }), Date.parse(START));
eq(accountsV2From({ ACCOUNTS_V2_FROM: "2026-12-31T10:00:00-05:00" }), Date.parse(START));
eq(accountsV2Day({ ACCOUNTS_V2_FROM: "2026-12-31T15:00:00Z" }), "2027-01-01", "the day in Korea");
for (const bad of [undefined, null, "", " " + START, START + " ", "2027-01-01", "2027-01-01T00:00:00", "2027-01-01 00:00:00+09:00",
                   "2027-01-01T00:00:00.000+09:00", "2027-01-01T00:00+09:00", "2027-01-01T00:00:00+0900", "2027-02-30T00:00:00+09:00",
                   "2027-13-01T00:00:00+09:00", "2027-00-10T00:00:00+09:00", "2027-01-01T24:00:00+09:00", "2027-01-01T00:60:00+09:00",
                   "2027-01-01T00:00:60+09:00", "2027-01-01T00:00:00+24:00", "0099-01-01T00:00:00Z", "２０２７-01-01T00:00:00Z", "tomorrow",
                   Date.parse(START), [START], { toString: () => START }]) {
  const env = bad === undefined ? {} : { ACCOUNTS_V2_FROM: bad };
  ok(Number.isNaN(accountsV2From(env)), "not an instant: " + String(bad));
  eq(accountsV2Open(env, Date.parse("2100-01-01T00:00:00Z")), false, "never started: " + String(bad));
  eq(accountsV2Day(env), "");
}
eq(accountsV2Open(undefined), false);

// ---------------------------------------------------------------- the two servers
const dir = mkdtempSync(join(tmpdir(), "systemboost-" + LIVE + "-"));
process.on("exit", () => rmSync(dir, { recursive: true, force: true }));
for (const f of git(["ls-tree", "--full-tree", "--name-only", LIVE + ":server/license-worker/src"]).split(/\r?\n/).filter(Boolean))
  writeFileSync(join(dir, f), atLive("src/" + f));
const liveModule = await import(pathToFileURL(join(dir, "index.js")).href);

const ROLES = "CREATE SCHEMA extensions; CREATE ROLE anon NOLOGIN; CREATE ROLE authenticated NOLOGIN; CREATE ROLE service_role NOLOGIN BYPASSRLS;";
const database = async (...scripts) => {
  const pg = new PGlite({ extensions: { pgcrypto } });
  await pg.exec(ROLES);
  for (const s of scripts) await pg.exec(sqlText(s));
  return pg;
};
const pgLive = await database(atLive("supabase/accounts.sql"));
const pgNew = await database(atLive("supabase/accounts.sql"), readFileSync(new URL("./supabase/migrations/20261118_email_accounts.sql", here), "utf8"));
const rowsIn = async (pg, query, ...args) => (await pg.query(query, args)).rows;
const countIn = async (pg, query, ...args) => Number((await rowsIn(pg, query, ...args))[0].n);

const DB_URL = { "https://live.supabase.example/rest/v1/rpc/account_call": "live", "https://new.supabase.example/rest/v1/rpc/account_call": "now" };
const sent = { live: [], now: [] }, mails = [];
globalThis.fetch = async (url, init = {}) => {
  if (String(url) === "https://api.resend.com/emails") { mails.push(JSON.parse(init.body)); return Response.json({ id: "made-up-" + mails.length }); }
  const which = DB_URL[String(url)];
  assert.ok(which, "nothing else is fetched: " + url);
  const { action, b } = JSON.parse(init.body);
  sent[which].push({ action, b });
  const pg = which === "live" ? pgLive : pgNew;
  await pg.exec("SET ROLE service_role");
  try {
    return Response.json((await pg.query("SELECT public.account_call($1, $2::jsonb) AS r", [action, JSON.stringify(b)])).rows[0].r);
  } finally {
    await pg.exec("RESET ROLE");
  }
};

const { privateKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
const PEM = privateKey.export({ type: "pkcs8", format: "pem" });   // one signing key: the same pepper, so the same one-way values
const waits = [];
const ctx = { waitUntil: p => { waits.push(p); } };
const settle = async () => { while (waits.length) await waits.shift(); };
const kst = t => new Date(t + 9 * 3600e3).toISOString().slice(0, 19) + "+09:00";
const BEFORE = kst(Date.now() + 7 * 86400e3), AFTER = kst(Date.now() - 86400e3);
function server(name, w, pg, extra) {
  const kv = new Map(), md = new Map();
  const LICENSES = {
    get: async k => kv.get(k) ?? null,
    getWithMetadata: async k => ({ value: kv.get(k) ?? null, metadata: md.get(k) ?? null }),
    put: async (k, v, o) => { kv.set(k, v); md.set(k, o?.metadata ?? null); },
    delete: async k => { kv.delete(k); md.delete(k); },
    list: async ({ prefix, limit = 1000 }) => ({ keys: [...kv.keys()].filter(k => k.startsWith(prefix)).sort().slice(0, limit).map(n => ({ name: n, metadata: md.get(n) })), list_complete: true }),
  };
  return { name, worker: w, pg, t: {}, r: {}, c: {},
           env: { LICENSES, LICENSE_PRIVATE_KEY: PEM, ADMIN_TOKEN: "T".repeat(32), SUPABASE_SECRET_KEY: "made-up-db-key", PUBLIC_URL: "https://lic.example", ...extra } };
}
const L = server("live", liveModule.default, pgLive, { SUPABASE_URL: "https://live.supabase.example/", STATS: fakeStats(liveModule.Stats).binding });
// mail is set up here, so it is the start and nothing else that keeps accounts v2 off
const N = server("now", worker, pgNew, { SUPABASE_URL: "https://new.supabase.example/", STATS: fakeStats().binding, ACCOUNTS_V2_FROM: BEFORE,
                                          RESEND_API_KEY: "made-up-mail-key", MAIL_FROM: "SystemBoost <no-reply@mail.example.com>" });

async function call(s, path, o = {}, env = s.env) {
  const headers = { "CF-Connecting-IP": o.ip || "10.9.9.9", ...(o.headers || {}) };
  if (o.token) headers.Authorization = "Bearer " + o.token;
  const init = { method: o.method || (o.body === undefined && !o.form ? "GET" : "POST"), headers };
  if (o.form) { headers["Content-Type"] = "application/x-www-form-urlencoded"; init.body = new URLSearchParams(o.form).toString(); }
  else if (o.body !== undefined) init.body = typeof o.body === "string" ? o.body : JSON.stringify(o.body);
  const req = new Request("https://lic.example" + path, init);
  Object.defineProperty(req, "cf", { value: { country: "KR" } });   // before the start the Worker must not pass it on
  const r = await s.worker.fetch(req, env, ctx);
  const text = await r.text();
  await settle();
  let j = null;
  try { j = JSON.parse(text); } catch { /* a page */ }
  return { status: r.status, headers: Object.fromEntries(r.headers), text, j };
}
const adm = async (s, path, body) => {
  const r = await s.worker.fetch(new Request("https://lic.example/admin/api" + path, { method: body ? "POST" : "GET",
    headers: { Authorization: "Bearer " + s.env.ADMIN_TOKEN, "CF-Connecting-IP": "10.250.0.1" }, body: body ? JSON.stringify(body) : undefined }), s.env, ctx);
  await settle();
  return { status: r.status, ...(await r.json()) };
};

// What may differ between the two runs: random tokens, recovery codes, keys, order and invite codes, and times.
const normText = t => t.replace(/[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{40,}/g, "<key>").replace(/[0-9a-f]{64}/g, "<hex>")
  .replace(/[A-Z2-9]{4}(?:-[A-Z2-9]{4}){3}/g, "<recovery>").replace(/SB-[A-Z2-9]{4}-[A-Z2-9]{4}/g, "<order>")
  .replace(/"code":"[A-Z2-9]{8}"/g, '"code":"<code>"').replace(/\b1[7-9]\d{11}\b/g, "<ms>");
const shown = r => ({ status: r.status, type: r.headers["content-type"], cache: r.headers["cache-control"] ?? null,
                      cors: r.headers["access-control-allow-origin"] ?? null, body: normText(r.text) });
// what goes to the database: the limits accounts v2 added are numbers the old actions do not read; random values by shape
const V2_LIMITS = ["signupHours", "resetMin", "approvedHours", "pendingMin", "pendingTries", "linkHours", "recoveryHours", "recoveryKeepDays",
                   "logDays", "devicesKept", "consentsKept", "keysKept", "mailPerIp", "verifyTries"];
const V2_FIELDS = ["addr", "country", "dname", "dhash", "ident", "log", "email", "pending", "dry", "device", "t", "backup", "step", "facts", "kind"];
const normB = s => ({ action, b }) => {
  const o = { ...b };
  for (const [k, v] of Object.entries(o)) for (const [name, code] of Object.entries(s.c))
    if (typeof v === "string" && v.toUpperCase() === code) o[k] = "<code " + name + ">";   // each server's own invite codes
  for (const k of V2_LIMITS) delete o[k];
  for (const k of ["session", "proof", "nextProof", "token"]) if (/^[0-9a-f]{64}$/.test(o[k] ?? "")) o[k] = "<random>";
  if (action === "invite" && /^[A-Z2-9]{8}$/.test(o.code ?? "")) o.code = "<code>";
  if (typeof o.consent === "string") o.consent = o.consent.replace(/ \d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/, " <time>");
  return { action, b: o };
};
let ipN = 0;
// The same request to both servers (build(s) gives the path and options, from each server's own tokens and codes).
async function both(label, build, o = {}) {
  const out = [];
  const ip = "10.20." + ((ipN >> 8) & 255) + "." + (ipN & 255);
  ipN++;
  for (const s of [L, N]) {
    const from = sent[s.name].length;
    const [path, opt = {}] = build(s);
    out.push({ res: await call(s, path, { ip, ...opt }), db: sent[s.name].slice(from) });
  }
  const [a, b] = out;
  eq(shown(b.res), shown(a.res), label + ": the reply");
  const db = b.db.map(normB(N));
  if (o.consent) for (const x of db) if (x.action === "signup") {
    eq(x.b.consent, o.consent + " <time>", label + ": the sign-up text the app showed is recorded");
    x.b.consent = "2026-11-12 <time>";
  }
  eq(db, a.db.map(normB(L)), label + ": what goes to the database");
  for (const x of b.db) for (const k of V2_FIELDS) ok(!(k in x.b), label + ": no " + k + " to the database (" + x.action + ")");
  return { live: a.res, now: b.res };
}
const keep = (r, name) => { for (const [s, res] of [[L, r.live], [N, r.now]]) { s.t[name] = res.j.token; s.r[name] = res.j.recovery; } };

// ---------------------------------------------------------------- 2. before the start: what an app sees of accounts v2
const V2_ROUTES = ["features", "signup_email", "verify", "login2", "forgot", "reset", "reset_peek", "add_email", "devices", "logout_device",
                   "logout_all", "totp_start", "totp_confirm", "totp_off", "totp_get", "backup_new", "backup_left", "keys", "link_key",
                   "link_key_confirm", "link_confirm", "consent", "recovery_request", "recovery_cancel", "link", "recovery/cancel", "login2_get",
                   "code", "ticket", "ping", "admin/list", "admin/recovery_list", "admin/recovery_approve", "", "login/", "me/x"];
const v2Body = { email: "member@example.com", password: "pass-word-123", t: "ab".repeat(32), pending: "cd".repeat(32), code: "123456",
                 key: "x.y", kind: "oc", version: "2026-11-18", login: "member@example.com", device: "0123456789ab" };
// session(s): the session each server sends (none, an unknown one, a member's)
async function v2Routes(label, session = () => undefined) {
  for (const a of V2_ROUTES) {
    const g = await both(label + "GET /account/" + a, s => ["/account/" + a, { token: session(s) }]);
    eq([g.now.status, g.now.j], [404, { error: "not found" }], label + "/account/" + a + " is the 404 of a route that is not there");
    await both(label + "POST /account/" + a, s => ["/account/" + a, { body: v2Body, token: session(s) }]);
  }
  for (const p of ["verify", "reset", "link", "recovery/cancel"]) {
    await both(label + "the page for a mailed link: GET /account/" + p, s => ["/account/" + p + "?t=" + "ab".repeat(32) + "&lang=en", { token: session(s) }]);
    await both(label + "its form: POST /account/" + p, () => ["/account/" + p, { form: { t: "ab".repeat(32), password: "pass-word-123", password2: "pass-word-123" } }]);
  }
  await both(label + "POST /keys/resend", () => ["/keys/resend", { body: { email: "buyer@example.com", lang: "en" } }]);
  await both(label + "GET /keys/resend", () => ["/keys/resend"]);
}
const features = await both("GET /account/features", () => ["/account/features"]);
eq(features.now.status, 404, "an app asking for the v2 parts hears 'older server' (Services/Account.cs FeaturesAsync)");
eq(features.now.text, '{"error":"not found"}');
await v2Routes("no session: ");

// ---------------------------------------------------------------- 3. before the start: the 아이디 accounts of 2026-11-13, on the new SQL
const okUser = (id, extra) => ({ id, password: "pass-word-123", agree: true, age14: true, ...extra });
const APP_DEVICE = { name: "Windows 11 PC", key: "ab".repeat(32) };   // what app 0.8.18 sends with a login
const dev = n => String(n).padStart(32, "0");

// sign-up: what it refuses, and the rule of then (8 to 72 characters, not the id; no list of common passwords)
for (const [what, body] of [["a short id", okUser("ab")], ["a space", okUser("has space")], ["a reserved id", okUser("admin")],
                            ["7 characters", { ...okUser("gooduser"), password: "short12" }], ["the id itself", { ...okUser("gooduser"), password: "gooduser" }],
                            ["73 characters", { ...okUser("gooduser"), password: "x".repeat(73) }], ["no consent", { ...okUser("gooduser"), agree: false }],
                            ["no age check", { ...okUser("gooduser"), age14: undefined }], ["a body that is a list", ["gooduser"]], ["no body", "null"]])
  await both("sign-up refused: " + what, () => ["/account/signup", { body }]);
eq((await both("sign-up: 9 characters were enough then", () => ["/account/signup", { body: { ...okUser("ninechar"), password: "nine-char" } }])).now.status, 200);
eq((await both("sign-up: a common password was not refused then", () => ["/account/signup", { body: { ...okUser("commonpw"), password: "password123" } }])).now.status, 200);
// app 0.8.18 sends its sign-up text version and its device; the request has a country
const a = await both("sign-up", () => ["/account/signup", { body: okUser("Alice_1", { consent: "2026-11-18", device: APP_DEVICE }) }], { consent: "2026-11-18" });
eq([a.now.status, a.now.j.id], [200, "alice_1"]);
keep(a, "alice");
await both("sign-up: taken", () => ["/account/signup", { body: okUser("ALICE_1") }]);
await both("sign-up: a malformed sign-up text version is not refused (it was not read then)", () => ["/account/signup", { body: okUser("oddconsent", { consent: "yes" }) }]);

// login and session
await both("login: wrong password", () => ["/account/login", { body: { id: "alice_1", password: "nope", device: APP_DEVICE } }]);
await both("login: nobody", () => ["/account/login", { body: { id: "nobody", password: "pass-word-123", device: APP_DEVICE } }]);
await both("login by email (app 0.8.18): there are no emails then", () => ["/account/login", { body: { email: "alice@example.com", password: "pass-word-123", device: APP_DEVICE } }]);
const l = await both("login", () => ["/account/login", { body: { id: "ALICE_1", password: "pass-word-123", device: APP_DEVICE } }]);
eq(Object.keys(l.now.j).sort(), ["id", "token"]);
keep(l, "l");
const me = await both("me", s => ["/account/me", { token: s.t.l }]);
eq(Object.keys(me.now.j).sort(), ["code", "created", "earnedDays", "id", "inviteDays", "inviteTicket", "invites", "rewardCap", "rewardDays", "rewardEach", "ticket"],
   "me: what it said then, nothing of accounts v2 (email, twofa, backupLeft)");
await both("me: an unknown session", () => ["/account/me", { token: "f".repeat(64) }]);
await both("me: no session", () => ["/account/me"]);
await both("logout", s => ["/account/logout", { body: {}, token: s.t.l }]);
await both("me after logout", s => ["/account/me", { token: s.t.l }]);
await both("me: the other session stays", s => ["/account/me", { token: s.t.alice }]);
await v2Routes("an unknown session: ", () => "f".repeat(64));
await v2Routes("a member's session: ", s => s.t.alice);

// five sessions at most
for (let i = 0; i < 7; i++) keep(await both("login " + (i + 1), () => ["/account/login", { body: { id: "alice_1", password: "pass-word-123", device: APP_DEVICE } }]), "s" + i);
eq(await countIn(pgNew, "SELECT COUNT(*) AS n FROM public.account_sessions WHERE account = 'alice_1'"), 5);
eq(await countIn(pgLive, "SELECT COUNT(*) AS n FROM public.account_sessions WHERE account = 'alice_1'"), 5);
await both("the oldest session went", s => ["/account/me", { token: s.t.alice }]);
for (const s of [L, N]) s.t.alice = s.t.s6;

// wrong passwords lock the account for a while, counted as then (no time of the miss: they add up until the lock)
await both("sign-up bobby", () => ["/account/signup", { body: okUser("bobby") }]);
for (let i = 0; i < 3; i++) await both("bobby: wrong password " + (i + 1), () => ["/account/login", { body: { id: "bobby", password: "wrong-pass" } }]);
const failsOf = pg => rowsIn(pg, "SELECT fails, locked_until IS NOT NULL AS locked FROM public.accounts WHERE id = 'bobby'");
eq(await failsOf(pgNew), await failsOf(pgLive));
eq(await rowsIn(pgNew, "SELECT fails, fail_at FROM public.accounts WHERE id = 'bobby'"), [{ fails: 3, fail_at: null }], "(B) no time of a wrong password");
await pgNew.query("UPDATE public.accounts SET fail_at = NULL WHERE id = 'bobby'");   // as a database from before has it
for (let i = 3; i < 5; i++) await both("bobby: wrong password " + (i + 1), () => ["/account/login", { body: { id: "bobby", password: "wrong-pass" } }]);
await both("bobby: locked", () => ["/account/login", { body: { id: "bobby", password: "pass-word-123" } }]);
eq(await failsOf(pgNew), await failsOf(pgLive));
for (const pg of [pgLive, pgNew]) await pg.query("UPDATE public.accounts SET locked_until = NULL WHERE id = 'bobby'");
await both("bobby: unlocked", () => ["/account/login", { body: { id: "bobby", password: "pass-word-123" } }]);
// a name nobody has never locks (as then): every try says "wrong", and nothing is kept for the name
for (let i = 0; i < 6; i++) await both("nobody here: try " + (i + 1), () => ["/account/login", { body: { id: "ghost_member", password: "wrong-pass-" + i } }]);
for (let i = 0; i < 6; i++) await both("nobody here (recover): try " + (i + 1), () => ["/account/recover", { body: { id: "ghost_member", recovery: "AAAA-BBBB-CCCC-DDDD", next: "new-pass-word" } }]);

// one address: twenty wrong tries stop its logins for the hour; five sign-ups an hour
for (let i = 0; i < 20; i++) await both("one address: wrong try " + (i + 1), () => ["/account/login", { body: { id: "nobody", password: "x" }, ip: "203.0.113.9" }]);
await both("one address: busy", () => ["/account/login", { body: { id: "alice_1", password: "pass-word-123" }, ip: "203.0.113.9" }]);
for (let i = 0; i < 5; i++) await both("one address: sign-up " + (i + 1), () => ["/account/signup", { body: okUser("many" + i), ip: "203.0.113.10" }]);
await both("one address: sign-ups busy", () => ["/account/signup", { body: okUser("many9"), ip: "203.0.113.10" }]);

// invite codes, rewards, seller codes
await both("me: no code yet", s => ["/account/me", { token: s.t.alice }]);
const c1 = await both("invite", s => ["/account/invite", { body: {}, token: s.t.alice }]);
const c2 = await both("invite again", s => ["/account/invite", { body: {}, token: s.t.alice }]);
L.c.old = c1.live.j.code; N.c.old = c1.now.j.code; L.c.code = c2.live.j.code; N.c.code = c2.now.j.code;
await both("an invite code that was replaced", s => ["/account/signup", { body: okUser("friend0", { invite: s.c.old, device: dev(100) }) }]);
const f1 = await both("sign-up with a member's code", s => ["/account/signup", { body: okUser("friend1", { invite: s.c.code.toLowerCase(), device: dev(101) }) }]);
eq([f1.now.j.days, typeof f1.now.j.key], [3, "string"]);
await both("me: one invite", s => ["/account/me", { token: s.t.alice }]);
await both("one device, one gift", s => ["/account/signup", { body: okUser("friend2", { invite: s.c.code, device: dev(101) }) }]);
await both("a code needs the device", s => ["/account/signup", { body: okUser("friend3", { invite: s.c.code }) }]);
await both("reward", s => ["/account/reward", { body: {}, token: s.t.alice }]);
await both("reward again", s => ["/account/reward", { body: {}, token: s.t.alice }]);
for (const s of [L, N]) { await adm(s, "/referrals", { code: "SELLER1", plan: "ultra", days: 7, max: 1 }); await adm(s, "/referrals", { code: "SELLER2", days: 3, max: 5 }); }
await both("a seller's code at sign-up", () => ["/account/signup", { body: okUser("friend7", { invite: "SELLER1", device: dev(107) }) }]);
await both("a seller's code used up", () => ["/account/signup", { body: okUser("friend8", { invite: "SELLER1", device: dev(108) }) }]);
await both("a member's code on the plan page", s => ["/referral", { body: { code: s.c.code, device: dev(109) } }]);

// a new password ends the sessions; the old one and the old recovery code stop working
keep(await both("sign-up carol", () => ["/account/signup", { body: okUser("carol") }]), "carol");
await both("password: wrong", s => ["/account/password", { body: { password: "wrong-pass", next: "newpassword1" }, token: s.t.carol }]);
await both("password: too short", s => ["/account/password", { body: { password: "pass-word-123", next: "x" }, token: s.t.carol }]);
await both("password: the id", s => ["/account/password", { body: { password: "pass-word-123", next: "carol" }, token: s.t.carol }]);
await both("password: no session", () => ["/account/password", { body: { password: "pass-word-123", next: "newpassword1" } }]);
const p1 = await both("password: 9 characters were enough then", s => ["/account/password", { body: { password: "pass-word-123", next: "ninechars" }, token: s.t.carol }]);
eq(p1.now.status, 200);
keep(p1, "carol1");
await both("the old session ended", s => ["/account/me", { token: s.t.carol }]);
await both("the new session", s => ["/account/me", { token: s.t.carol1 }]);
await both("the old password", () => ["/account/login", { body: { id: "carol", password: "pass-word-123" } }]);
await both("the old recovery code", s => ["/account/recover", { body: { id: "carol", recovery: s.r.carol, next: "newpassword2" } }]);
await both("recover: too short", s => ["/account/recover", { body: { id: "carol", recovery: s.r.carol1, next: "short" } }]);
keep(await both("recover", s => ["/account/recover", { body: { id: "carol", recovery: s.r.carol1.toLowerCase().replaceAll("-", ""), next: "newpassword2" } }]), "carol2");
keep(await both("the new password", () => ["/account/login", { body: { id: "carol", password: "newpassword2" } }]), "carol3");
await both("recover: a used code", s => ["/account/recover", { body: { id: "carol", recovery: s.r.carol1, next: "newpassword3" } }]);

// the dashboard: block, a new recovery code, delete
for (const s of [L, N]) eq((await adm(s, "/members/block", { id: "carol", on: true })).ok, true);
await both("blocked: the session ended", s => ["/account/me", { token: s.t.carol3 }]);
await both("blocked: no login", () => ["/account/login", { body: { id: "carol", password: "newpassword2" } }]);
for (const s of [L, N]) await adm(s, "/members/block", { id: "carol", on: false });
keep(await both("unblocked", () => ["/account/login", { body: { id: "carol", password: "newpassword2" } }]), "carol4");
for (const s of [L, N]) s.r.bobby = (await adm(s, "/members/recovery", { id: "bobby" })).recovery;
keep(await both("a recovery code from the dashboard", s => ["/account/recover", { body: { id: "bobby", recovery: s.r.bobby, next: "bobbyspass" } }]), "bobby");

// a discount ticket: only with a code, spent when the seller approves
const t1 = await both("sign-up with a ticket", s => ["/account/signup", { body: okUser("ticket1", { invite: s.c.code, device: dev(120) }) }]);
keep(t1, "t1");
await both("me: the ticket", s => ["/account/me", { token: s.t.t1 }]);
for (const s of [L, N]) await adm(s, "/pay-links", { monthly: "https://pay.example/m", monthly_ticket: "https://pay.example/m-sale" });
const orders = {};
for (const s of [L, N]) {
  const r = await call(s, "/order?plan=monthly&method=manual&ticket=1", { method: "POST", token: s.t.t1 });
  orders[s.name] = r.j;
  eq((await adm(s, "/orders/approve", { code: r.j.code, email: "" })).error, undefined);
}
eq([orders.now.url, orders.now.ticket], [orders.live.url, orders.live.ticket], "the ticket's payment link, as then");
await both("me: the ticket is spent", s => ["/account/me", { token: s.t.t1 }]);

// leaving
await both("delete: wrong password", s => ["/account/delete", { body: { password: "wrong-pass" }, token: s.t.alice }]);
await both("delete", s => ["/account/delete", { body: { password: "pass-word-123" }, token: s.t.alice }]);
await both("deleted: no login", () => ["/account/login", { body: { id: "alice_1", password: "pass-word-123" } }]);
await both("deleted: the id is not handed out again", () => ["/account/signup", { body: okUser("alice_1") }]);
await both("delete: an unknown session", () => ["/account/delete", { body: { password: "pass-word-123" }, token: "ef".repeat(32) }]);
for (const s of [L, N]) eq((await adm(s, "/members/delete", { id: "friend1" })).ok, true);
await both("deleted by the seller: the id is not handed out again", () => ["/account/signup", { body: okUser("friend1") }]);
// the dashboard's 복구 요청 is there, and empty
eq((await adm(N, "/recovery")).requests, []);

// the hourly job
for (const s of [L, N]) {
  await s.worker.scheduled({ scheduledTime: Date.now() }, s.env, ctx);
  await settle();
}
eq(sent.now.filter(x => x.action === "ping").map(normB(N)), sent.live.filter(x => x.action === "ping").map(normB(L)), "the hourly job's call");

// ---------------------------------------------------------------- 4. before the start: what the database holds
for (const t of ["account_security_log", "account_devices", "account_guard", "account_consents", "account_tokens", "account_backup", "account_keys", "account_recovery"])
  eq(await countIn(pgNew, `SELECT COUNT(*) AS n FROM public.${t}`), 0, t + " stays empty before the start");
eq(await countIn(pgNew, "SELECT COUNT(*) AS n FROM public.account_sessions"), await countIn(pgLive, "SELECT COUNT(*) AS n FROM public.account_sessions"), "the same sessions");
ok(await countIn(pgNew, "SELECT COUNT(*) AS n FROM public.account_sessions") >= 10);
eq(await countIn(pgNew, "SELECT COUNT(*) AS n FROM public.account_sessions WHERE device_name IS NOT NULL OR device_hash IS NOT NULL OR country IS NOT NULL"), 0,
   "no device name, device hash or country with a session");
eq(await countIn(pgNew, "SELECT COUNT(*) AS n FROM public.account_sessions WHERE last_seen <> created"), 0, "(A) no session has a 'last used' time of its own");
eq(await countIn(pgNew, "SELECT COUNT(*) AS n FROM public.accounts WHERE email IS NOT NULL OR email_at IS NOT NULL OR totp IS NOT NULL OR totp_pending IS NOT NULL OR fail_at IS NOT NULL"), 0,
   "no email, 2-step secret or time of a wrong password");
eq(await rowsIn(pgNew, "SELECT id, fails, blocked, invites, reward_days, earned_days, ticket_percent, ticket_used IS NOT NULL AS used, invited_by FROM public.accounts ORDER BY id"),
   await rowsIn(pgLive, "SELECT id, fails, blocked, invites, reward_days, earned_days, ticket_percent, ticket_used IS NOT NULL AS used, invited_by FROM public.accounts ORDER BY id"),
   "the same members, counted the same");
eq((await rowsIn(pgNew, "SELECT consent FROM public.accounts WHERE id = 'alice_1'")).length, 0);
eq((await rowsIn(pgNew, "SELECT left(consent, 10) AS v FROM public.accounts WHERE id IN ('bobby', 'oddconsent') ORDER BY id")).map(r => r.v), ["2026-11-12", "2026-11-12"]);
eq(await countIn(pgNew, "SELECT COUNT(*) AS n FROM public.account_gone"), await countIn(pgLive, "SELECT COUNT(*) AS n FROM public.account_gone"));
// (A) a session call does not keep a "last used" time, also not ten minutes later
await pgNew.query("UPDATE public.account_sessions SET last_seen = now() - interval '1 hour', created = now() - interval '1 hour' WHERE account = 'carol'");
const seen = async () => rowsIn(pgNew, "SELECT sid, last_seen FROM public.account_sessions WHERE account = 'carol' ORDER BY sid");
const seenBefore = await seen();
eq((await call(N, "/account/me", { token: N.t.carol4 })).j.id, "carol");
await call(N, "/account/invite", { body: {}, token: N.t.carol4 });
eq(await seen(), seenBefore, "(A) 'last used' is not kept before the start");
eq(mails, [], "no mail before the start");
eq(sent.now.filter(x => "log" in x.b).length, 0, "the database was never asked to log");
const kvKinds = async s => (await s.env.LICENSES.list({ prefix: "" })).keys.map(k => k.name.replace(/[:_].*$/, "")).sort();
eq(await kvKinds(N), await kvKinds(L), "the same kinds and number of records in KV (keys, orders, invite gifts)");

// ---------------------------------------------------------------- 5. /privacy before the start: the policy of 2026-11-13 and the notice
const realNow = Date.now;
const at = (iso, f) => async (...args) => { Date.now = () => Date.parse(iso); try { return await f(...args); } finally { Date.now = realNow; } };
const P = { ...N, env: { ...N.env, ACCOUNTS_V2_FROM: START } };   // the real start, read on a pinned clock
const config = { rep: "홍길동", email: "help@example.com", dbRegion: "대한민국 서울 (ap-northeast-2)", effectiveDate: "2026-11-13" };
for (const s of [L, N]) {
  const saved = (await adm(s, "/support/config")).config;
  eq((await adm(s, "/support/config", { ...saved, seller: { ...saved.seller, rep: config.rep, email: config.email },
                                       privacy: { dbRegion: config.dbRegion, effectiveDate: config.effectiveDate } })).status, 200);
}
const nonceFree = r => r.text.replace(/nonce="[^"]+"/g, 'nonce="N"');
const cspShape = r => r.headers["content-security-policy"].replace(/'nonce-[^']+'/, "'nonce-N'");
const notice = /<div class="notice"><p>(.*?)<\/p><\/div>\n/;
const NOTICE_CSS = ".notice{background:var(--card);border:1px solid var(--accent);border-radius:12px;padding:12px 16px;margin:18px 0}\n";
const before = at("2026-11-20T09:00:00+09:00", (path, env = P.env) => call(P, path, {}, env));
const after = at("2027-01-02T09:00:00+09:00", (path, env = P.env) => call(P, path, {}, env));
for (const lang of ["ko", "en"]) {
  const q = lang === "en" ? "?lang=en" : "";
  const live = await call(L, "/privacy" + q), now = await before("/privacy" + q);
  eq(now.status, 200);
  eq(Object.fromEntries(Object.entries(now.headers).filter(([k]) => k !== "content-security-policy")),
     Object.fromEntries(Object.entries(live.headers).filter(([k]) => k !== "content-security-policy")), lang + ": the same headers");
  eq(cspShape(now), cspShape(live), lang + ": the same CSP, a nonce of its own");
  const box = notice.exec(now.text);
  ok(box, lang + ": a notice at the top");
  eq(box[1], lang === "en" ? 'The privacy policy changes on January 1, 2027. <a href="/privacy?next=1&amp;lang=en">Preview the new policy</a>'
                           : '개인정보 처리방침이 2027년 1월 1일에 바뀌어요. <a href="/privacy?next=1">바뀌는 처리방침 미리 보기</a>');
  ok(now.text.indexOf(box[0]) === now.text.indexOf("<main>\n") + 7, lang + ": right at the top of the page");
  eq(nonceFree({ text: now.text.replace(box[0], "").replace(NOTICE_CSS, "") }), nonceFree(live), lang + ": otherwise the policy in force, as the live server shows it");
  ok(live.text.includes(lang === "en" ? "Effective date: November 13, 2026" : "시행일: 2026년 11월 13일"), lang + ": its blanks filled as always");
  eq([...now.text.matchAll(/<style nonce="([^"]+)">/g)].map(m => m[1]).join(), /'nonce-([^']+)'/.exec(now.headers["content-security-policy"])[1]);
  ok(!/<script|<link|<img|<iframe|\bsrc=|\son\w+=/i.test(now.text), lang + ": no script, nothing loaded");

  const next = await before("/privacy?next=1" + (lang === "en" ? "&lang=en" : ""));
  eq(next.status, 200);
  eq(cspShape(next), cspShape(live));
  const top = notice.exec(next.text);
  eq(top[1], lang === "en" ? 'This policy takes effect on January 1, 2027. Until then the current privacy policy applies. <a href="/privacy?lang=en">Read the current policy</a>'
                           : '이 처리방침은 2027년 1월 1일부터 시행해요. 그때까지는 지금의 개인정보 처리방침이 적용돼요. <a href="/privacy">지금의 처리방침 보기</a>');
  eq(next.text.split(lang === "en" ? "Effective date: January 1, 2027" : "시행일: 2027년 1월 1일").length - 1, lang === "en" ? 1 : 2,
     lang + ": the new policy, dated from the start, not from the dashboard's 시행일");
  ok(!next.text.includes("2026년 11월 13일") && !next.text.includes("November 13, 2026"));
  ok(next.text.includes(lang === "en" ? '<a href="/privacy?next=1" hreflang="ko">' : '<a href="/privacy?next=1&amp;lang=en" hreflang="en">'), lang + ": the other language stays on the new policy");
  const filled = html => html.replace(/\{\{OWNER_NAME\}\}/g, config.rep).replace(/\{\{OWNER_EMAIL\}\}/g, config.email)
    .replace(/\{\{SUPABASE_REGION\}\}/g, config.dbRegion.replace(/</g, "&lt;"));
  eq(nonceFree({ text: next.text.replace(top[0], "").replace(NOTICE_CSS, "") }).replace(lang === "en" ? "/privacy?next=1\" hreflang" : "/privacy?next=1&amp;lang=en\" hreflang", lang === "en" ? "/privacy\" hreflang" : "/privacy?lang=en\" hreflang"),
     filled(PRIVACY_HTML[lang]).replace(/\{\{EFFECTIVE_DATE\}\}/g, lang === "en" ? "January 1, 2027" : "2027년 1월 1일").replace(/__NONCE__/g, "N"), lang + ": otherwise privacy_page.js");
  eq(filled(PRIVACY_PREV_HTML[lang]).replace(/\{\{EFFECTIVE_DATE\}\}/g, lang === "en" ? "November 13, 2026" : "2026년 11월 13일").replace(/__NONCE__/g, "N"), nonceFree(live),
     lang + ": privacy_prev_page.js is the live server's page");
}
eq(PRIVACY_PREV_HTML, (await import(pathToFileURL(join(dir, "privacy_page.js")).href)).PRIVACY_HTML, "privacy_prev_page.js holds commit 776dd71's page as it was");

// ---------------------------------------------------------------- 6. a missing or broken ACCOUNTS_V2_FROM: as before the start
for (const v of [undefined, "", "2027-01-01", "2027-01-01T00:00:00+09:00 ", "2027-02-30T00:00:00+09:00"]) {
  const env = { ...N.env };
  if (v === undefined) delete env.ACCOUNTS_V2_FROM; else env.ACCOUNTS_V2_FROM = v;
  const label = "ACCOUNTS_V2_FROM " + JSON.stringify(v) + ": ";
  const f = await call(N, "/account/features", {}, env);
  eq([f.status, f.j], [404, { error: "not found" }], label + "no v2 parts");
  eq((await call(N, "/keys/resend", { body: { email: "buyer@example.com" } }, env)).status, 404, label + "no POST /keys/resend");
  eq((await call(N, "/account/verify?t=" + "ab".repeat(32), {}, env)).j, { error: "not found" }, label + "no pages for mailed links");
  const from = sent.now.length;
  eq((await call(N, "/account/login", { body: { id: "bobby", password: "bobbyspass", device: APP_DEVICE } }, env)).status, 200, label + "아이디 login as before");
  eq(sent.now.slice(from).filter(x => V2_FIELDS.some(k => k in x.b)).length, 0, label + "nothing of accounts v2 to the database");
  const pv = await after("/privacy", env), live = await call(L, "/privacy");
  eq(nonceFree(pv), nonceFree(live), label + "/privacy is the policy in force, with no date to announce");
  const nx = await after("/privacy?next=1", env);
  ok(nx.text.includes("이 처리방침은 아직 시행하지 않아요. 지금은 지금의 개인정보 처리방침이 적용돼요.") && nx.text.includes("시행일: <span class=\"blank\">" + PRIVACY_EMPTY.ko + "</span>"),
     label + "?next=1: the new policy, no date");
}
eq(await countIn(pgNew, "SELECT COUNT(*) AS n FROM public.account_security_log"), 0);
eq(await countIn(pgNew, "SELECT COUNT(*) AS n FROM public.account_devices"), 0);

// ---------------------------------------------------------------- 7. from the start: accounts v2 as they are
N.env.ACCOUNTS_V2_FROM = AFTER;
const f2 = await call(N, "/account/features");
eq([f2.status, f2.j], [200, { email: true, totp: true }], "from the start: the v2 parts");
const from = sent.now.length;
const in2 = await call(N, "/account/login", { body: { id: "bobby", password: "bobbyspass", device: APP_DEVICE }, ip: "198.51.100.7" });
eq(in2.status, 200);
const call2 = sent.now.slice(from).find(x => x.action === "login").b;
eq([call2.log, call2.addr, call2.country, call2.dname, typeof call2.dhash, typeof call2.ident], ["true", "198.51.100.7", "KR", "Windows 11 PC", "string", "string"],
   "from the start the database gets the address, country, device and login-name hash, and is asked to log");
eq(await rowsIn(pgNew, "SELECT event, ip, country, device FROM public.account_security_log WHERE account = 'bobby'"),
   [{ event: "login", ip: "198.51.100.7", country: "KR", device: "Windows 11 PC" }], "a security-log line");
eq(await countIn(pgNew, "SELECT COUNT(*) AS n FROM public.account_devices WHERE account = 'bobby'"), 1, "the device");
eq(await rowsIn(pgNew, "SELECT device_name, country FROM public.account_sessions WHERE account = 'bobby' AND device_name IS NOT NULL"),
   [{ device_name: "Windows 11 PC", country: "KR" }]);
const me2 = await call(N, "/account/me", { token: in2.j.token });
ok("twofa" in me2.j && "email" in me2.j && "backupLeft" in me2.j, "me: the v2 fields");
await call(N, "/account/login", { body: { id: "bobby", password: "wrong-pass" } });
ok((await rowsIn(pgNew, "SELECT fail_at FROM public.accounts WHERE id = 'bobby'"))[0].fail_at, "(B) the time of a wrong password from the start");
ok(await countIn(pgNew, "SELECT COUNT(*) AS n FROM public.account_security_log WHERE event = 'login_failed'") === 1);
await call(N, "/account/login", { body: { id: "ghost_member", password: "wrong" } });
eq(await countIn(pgNew, "SELECT COUNT(*) AS n FROM public.account_guard"), 1, "a name nobody has is counted from the start");
await pgNew.query("UPDATE public.account_sessions SET last_seen = now() - interval '1 hour' WHERE account = 'carol'");
await call(N, "/account/me", { token: N.t.carol4 });
eq(await rowsIn(pgNew, "SELECT count(*) FILTER (WHERE last_seen > now() - interval '1 minute')::int AS fresh, count(*)::int AS n FROM public.account_sessions WHERE account = 'carol'"),
   [{ fresh: 1, n: seenBefore.length }], "(A) from the start the session used gets its 'last used' time, the others keep theirs");
eq((await call(N, "/account/signup", { body: { ...okUser("ninechar2"), password: "nine-char" } })).j.error, "password", "the 10-character rule from the start");
eq((await call(N, "/keys/resend", { body: { email: "buyer@example.com" } })).j, { sent: true }, "POST /keys/resend from the start");
const pageNow = await call(N, "/account/verify?t=" + "ab".repeat(32));
eq([pageNow.status, pageNow.headers["content-type"]], [200, "text/html; charset=utf-8"], "the pages for mailed links from the start");

// /privacy from the start: the new policy; dated from the start, or from the dashboard's 시행일 when that is later
const pa = await after("/privacy");
ok(!notice.test(pa.text) && !pa.text.includes(NOTICE_CSS), "no notice");
eq(pa.text.split("시행일: 2027년 1월 1일").length - 1, 2, "the dashboard's 2026-11-13 is before the start: the start's day");
eq(nonceFree(pa), nonceFree(await after("/privacy?next=1")), "?next=1 is the same page from the start");
ok((await after("/privacy?lang=en")).text.includes("Effective date: January 1, 2027"));
{
  const saved = (await adm(N, "/support/config")).config;
  await adm(N, "/support/config", { ...saved, privacy: { ...saved.privacy, effectiveDate: "2027-01-15" } });
  eq((await after("/privacy")).text.split("시행일: 2027년 1월 15일").length - 1, 2, "a later 시행일 from the dashboard counts");
  ok(notice.test((await before("/privacy")).text) && (await before("/privacy?next=1")).text.includes("시행일: 2027년 1월 1일"),
     "before the start the dashboard's date does not move the announced one");
  await adm(N, "/support/config", { ...saved, privacy: { ...saved.privacy, effectiveDate: "" } });
  eq((await after("/privacy")).text.split("시행일: 2027년 1월 1일").length - 1, 2, "no 시행일 in the dashboard: the start's day");
}

// /help and /terms are as they were; the FAQ dates what starts with accounts v2
ok(DEFAULT_FAQ.ko.some(f => f.a.startsWith("2027년 1월 1일부터 메일 주소를 확인한 회원은") && f.a.includes("2027년 1월 1일부터 앱에서 계정 복구를 요청하세요")));
ok(DEFAULT_FAQ.en.some(f => f.a.startsWith("From January 1, 2027, members with a confirmed email") && f.a.includes("account recovery in the app from January 1, 2027")));

console.log("accounts gate tests passed (" + checks + " checks)");
