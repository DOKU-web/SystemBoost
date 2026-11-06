// Local test of member accounts: the worker's real routes and supabase/accounts.sql on real Postgres (PGlite),
// behind a stand-in for Supabase's /rest/v1/rpc route; fake KV, a throwaway key.
//   npm i --no-save @electric-sql/pglite && node test_accounts.mjs
import assert from "node:assert/strict";
import { generateKeyPairSync } from "node:crypto";
import { readFileSync } from "node:fs";
import { PGlite } from "@electric-sql/pglite";
import { pgcrypto } from "@electric-sql/pglite/contrib/pgcrypto";
import worker from "./src/index.js";

const pg = new PGlite({ extensions: { pgcrypto } });
await pg.exec(`CREATE SCHEMA extensions; CREATE ROLE anon NOLOGIN; CREATE ROLE authenticated NOLOGIN; CREATE ROLE service_role NOLOGIN BYPASSRLS;`);
await pg.exec(readFileSync(new URL("./supabase/accounts.sql", import.meta.url), "utf8").replace("notify pgrst, 'reload schema';", ""));
const rows = async (query, ...args) => (await pg.query(query, args)).rows;
const count = async (query, ...args) => Number((await rows(query, ...args))[0].n);
const run = async (query, ...args) => { await pg.query(query, args); };

const SECRET = "sb_secret_test";
let down = false;
const calls = [];
globalThis.fetch = async (url, init) => {
  assert.equal(String(url), "https://project.supabase.example/rest/v1/rpc/account_call");
  assert.equal(init.headers.apikey, SECRET);
  assert.equal(init.headers.Authorization, undefined);
  if (down) return new Response("paused", { status: 503 });
  const { action, b } = JSON.parse(init.body);
  calls.push(init.body);
  await pg.exec("SET ROLE service_role");
  try {
    return Response.json((await pg.query("SELECT public.account_call($1, $2::jsonb) AS r", [action, JSON.stringify(b)])).rows[0].r);
  } finally {
    await pg.exec("RESET ROLE");
  }
};

const kv = new Map(), md = new Map();
const { privateKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
const env = {
  LICENSES: {
    get: async k => kv.get(k) ?? null,
    getWithMetadata: async k => ({ value: kv.get(k) ?? null, metadata: md.get(k) ?? null }),
    put: async (k, v, o) => { kv.set(k, v); md.set(k, o?.metadata ?? null); },
    delete: async k => { kv.delete(k); md.delete(k); },
    list: async ({ prefix, limit = 1000 }) => ({ keys: [...kv.keys()].filter(k => k.startsWith(prefix)).sort().slice(0, limit).map(name => ({ name, metadata: md.get(name) })), list_complete: true }),
  },
  LICENSE_PRIVATE_KEY: privateKey.export({ type: "pkcs8", format: "pem" }),
  ADMIN_TOKEN: "T".repeat(32),
  SUPABASE_URL: "https://project.supabase.example/",
  SUPABASE_SECRET_KEY: SECRET,
};

const pings = [];
let ipN = 0;
const send = async (path, body, token, ip) => {
  const headers = { "CF-Connecting-IP": ip || "10.0.0." + (++ipN % 250) };
  if (token) headers.Authorization = "Bearer " + token;
  const r = await worker.fetch(new Request("https://lic.example" + path, { method: body === undefined ? "GET" : "POST", headers, body: body === undefined ? undefined : JSON.stringify(body) }), env);
  return { status: r.status, ...(await r.json()) };
};
const adm = async (path, body) => {
  const r = await worker.fetch(new Request("https://lic.example/admin/api" + path, { method: body ? "POST" : "GET", headers: { Authorization: "Bearer " + env.ADMIN_TOKEN }, body: body ? JSON.stringify(body) : undefined }), env);
  return { status: r.status, ...(await r.json()) };
};
const device = n => String(n).padStart(32, "0");
const okUser = (id, extra) => ({ id, password: "password123", agree: true, age14: true, ...extra });
const payload = key => JSON.parse(Buffer.from(key.split(".")[0], "base64url"));

// sign-up: what it refuses
assert.equal((await send("/account/signup", okUser("ab"))).error, "id");
assert.equal((await send("/account/signup", okUser("has space"))).error, "id");
assert.equal((await send("/account/signup", okUser("admin"))).error, "id");
assert.equal((await send("/account/signup", { ...okUser("gooduser"), password: "short" })).error, "password");
assert.equal((await send("/account/signup", { ...okUser("gooduser"), password: "gooduser" })).error, "password");
assert.equal((await send("/account/signup", { ...okUser("gooduser"), password: "x".repeat(73) })).error, "password");
assert.equal((await send("/account/signup", { ...okUser("gooduser"), agree: false })).error, "consent");
assert.equal((await send("/account/signup", { ...okUser("gooduser"), age14: undefined })).error, "consent");

// sign-up, and IDs are one whatever the case
const a = await send("/account/signup", okUser("Alice_1"));
assert.equal(a.status, 200);
assert.equal(a.id, "alice_1");
assert.match(a.token, /^[0-9a-f]{64}$/);
assert.match(a.recovery, /^[A-Z2-9]{4}(-[A-Z2-9]{4}){3}$/);
assert.equal((await send("/account/signup", okUser("ALICE_1"))).error, "taken");

// what is stored: no password, no recovery code, no token, no IP
const [row] = await rows("SELECT * FROM public.accounts WHERE id = 'alice_1'");
assert.ok(!JSON.stringify(row).includes("password123"));
assert.ok(!JSON.stringify(row).includes(a.recovery));
assert.equal(await count("SELECT COUNT(*) AS n FROM public.account_sessions WHERE token = $1", a.token), 0);
assert.ok(!JSON.stringify(await rows("SELECT * FROM public.account_hits")).includes("10.0.0."));
assert.match(row.hash, /^\$2[abxy]\$10\$/);   // bcrypt
for (const call of calls) assert.ok(!call.includes("password123") && !call.includes(a.recovery) && !call.includes("10.0.0."));   // what leaves the Worker
assert.match(row.consent, /^2026-11-06 /);

// login and session
assert.equal((await send("/account/login", { id: "alice_1", password: "nope" })).error, "wrong");
assert.equal((await send("/account/login", { id: "nobody", password: "password123" })).error, "wrong");
const l = await send("/account/login", { id: "ALICE_1", password: "password123" });
assert.equal(l.status, 200);
assert.equal((await send("/account/me", undefined, l.token)).id, "alice_1");
assert.equal((await send("/account/me", undefined, "f".repeat(64))).error, "session");
assert.equal((await send("/account/me")).error, "session");
assert.equal((await send("/account/logout", {}, l.token)).ok, true);
assert.equal((await send("/account/me", undefined, l.token)).error, "session");
assert.equal((await send("/account/me", undefined, a.token)).id, "alice_1");   // the other session stays

// five sessions at most
const sessions = [];
for (let i = 0; i < 7; i++) sessions.push((await send("/account/login", { id: "alice_1", password: "password123" })).token);
assert.equal(await count("SELECT COUNT(*) AS n FROM public.account_sessions WHERE account = 'alice_1'"), 5);
assert.equal((await send("/account/me", undefined, sessions[6])).id, "alice_1");
assert.equal((await send("/account/me", undefined, a.token)).error, "session");   // the oldest went
a.token = sessions[6];

// wrong passwords lock the account, the right one included, for a while
await send("/account/signup", okUser("bobby"));
for (let i = 0; i < 5; i++) assert.equal((await send("/account/login", { id: "bobby", password: "wrong-pass" })).error, "wrong");
assert.equal((await send("/account/login", { id: "bobby", password: "password123" })).error, "locked");
await run("UPDATE public.accounts SET locked_until = NULL WHERE id = 'bobby'");
assert.equal((await send("/account/login", { id: "bobby", password: "password123" })).status, 200);

// one address: wrong tries, then sign-ups
const ip = "203.0.113.9";
for (let i = 0; i < 20; i++) await send("/account/login", { id: "nobody", password: "x" }, null, ip);
assert.equal((await send("/account/login", { id: "alice_1", password: "password123" }, null, ip)).error, "busy");
const ip2 = "203.0.113.10";
for (let i = 0; i < 5; i++) assert.equal((await send("/account/signup", okUser("many" + i), null, ip2)).status, 200);
assert.equal((await send("/account/signup", okUser("many9"), null, ip2)).error, "busy");

// invite code: made, replaced, counted, rewarded up to the cap
assert.equal((await send("/account/me", undefined, a.token)).code, null);
const c1 = (await send("/account/invite", {}, a.token)).code;
const c2 = (await send("/account/invite", {}, a.token)).code;
assert.match(c2, /^[A-Z2-9]{8}$/);
assert.notEqual(c1, c2);
assert.equal((await send("/account/signup", okUser("friend0", { invite: c1, device: device(100) }))).error, "unknown");
const f = await send("/account/signup", okUser("friend1", { invite: c2.toLowerCase(), device: device(101) }));
assert.equal(f.status, 200);
assert.equal(payload(f.key).plan, "premium");
assert.equal(f.days, 3);
let me = await send("/account/me", undefined, a.token);
assert.deepEqual([me.invites, me.rewardDays, me.earnedDays], [1, 3, 3]);
assert.equal((await send("/account/signup", okUser("friend2", { invite: c2, device: device(101) }))).error, "already");   // one device, one gift
assert.equal(await count("SELECT COUNT(*) AS n FROM public.accounts WHERE id = 'friend2'"), 0);
assert.equal((await send("/account/signup", okUser("friend3", { invite: c2 }))).error, "bad request");   // no device number

// a reward is a real key, once
const got = await send("/account/reward", {}, a.token);
assert.equal(payload(got.key).plan, "premium");
assert.equal(got.days, 3);
assert.equal((await send("/account/reward", {}, a.token)).error, "nothing");
assert.equal((await send("/account/me", undefined, a.token)).rewardDays, 0);

// the cap
await run("UPDATE public.accounts SET earned_days = 29 WHERE id = 'alice_1'");
await send("/account/signup", okUser("friend4", { invite: c2, device: device(104) }));
await send("/account/signup", okUser("friend5", { invite: c2, device: device(105) }));
me = await send("/account/me", undefined, a.token);
assert.deepEqual([me.invites, me.rewardDays, me.earnedDays], [3, 1, 30]);
// the limit on one code
await run("UPDATE public.accounts SET invites = 50 WHERE id = 'alice_1'");
assert.equal((await send("/account/signup", okUser("friend6", { invite: c2, device: device(106) }))).error, "used up");
await run("UPDATE public.accounts SET invites = 3 WHERE id = 'alice_1'");

// a seller's code works at sign-up too, and a member's code is refused on the Premium page
await adm("/referrals", { code: "SELLER1", plan: "ultra", days: 7, max: 1 });
await adm("/referrals", { code: "SELLER2", days: 3, max: 5 });
const s = await send("/account/signup", okUser("friend7", { invite: "SELLER1", device: device(107) }));
assert.equal(payload(s.key).plan, "ultra");
assert.equal((await send("/account/signup", okUser("friend8", { invite: "SELLER1", device: device(108) }))).error, "used up");
assert.equal((await send("/referral", { code: c2, device: device(109) })).error, "signup");
assert.equal((await send("/referral", { code: c2, device: device(109) })).error, "signup");

// a new password ends the sessions; the old one and the old recovery code stop working
const b0 = await send("/account/signup", okUser("carol"));
assert.equal((await send("/account/password", { password: "wrong-pass", next: "newpassword1" }, b0.token)).error, "wrong");
assert.equal((await send("/account/password", { password: "password123", next: "x" }, b0.token)).error, "password");
const b1 = await send("/account/password", { password: "password123", next: "newpassword1" }, b0.token);
assert.equal(b1.status, 200);
assert.equal((await send("/account/me", undefined, b0.token)).error, "session");
assert.equal((await send("/account/me", undefined, b1.token)).id, "carol");
assert.equal((await send("/account/login", { id: "carol", password: "password123" })).error, "wrong");
assert.equal((await send("/account/recover", { id: "carol", recovery: b0.recovery, next: "newpassword2" })).error, "wrong");

// recovery
const r = await send("/account/recover", { id: "carol", recovery: b1.recovery.toLowerCase().replaceAll("-", ""), next: "newpassword2" });
assert.equal(r.status, 200);
assert.notEqual(r.recovery, b1.recovery);
assert.equal((await send("/account/login", { id: "carol", password: "newpassword2" })).status, 200);
assert.equal((await send("/account/recover", { id: "carol", recovery: b1.recovery, next: "newpassword3" })).error, "wrong");

// the dashboard needs its password, and shows no secret
assert.equal((await worker.fetch(new Request("https://lic.example/admin/api/members"), env)).status, 401);
const list = await adm("/members");
assert.ok(list.summary.total >= 10);
assert.equal(list.summary.invited >= 3, true);
const seen = JSON.stringify(list);
for (const word of ["salt", "hash", "recovery", "token", "consent"]) assert.ok(!seen.includes('"' + word + '"'), word);
assert.equal((await adm("/members?q=frie")).members.every(m => m.id.includes("frie")), true);
assert.equal((await adm("/members?q=" + c2.toLowerCase())).members[0].id, "alice_1");

// blocking: the session ends and login stops; unblocking brings login back
await adm("/members/block", { id: "carol", on: true });
assert.equal((await send("/account/me", undefined, r.token)).error, "session");
assert.equal((await send("/account/login", { id: "carol", password: "newpassword2" })).error, "blocked");
await adm("/members/block", { id: "carol", on: false });
const c3 = await send("/account/login", { id: "carol", password: "newpassword2" });
assert.equal(c3.status, 200);

// a recovery code from the dashboard
const fresh = (await adm("/members/recovery", { id: "bobby" })).recovery;
assert.equal((await send("/account/recover", { id: "bobby", recovery: fresh, next: "bobbyspass" })).status, 200);
assert.equal((await adm("/members/recovery", { id: "ghost" })).status, 404);

// a discount ticket: only with a code, on its own payment link, spent on approval, back on refusal
const order = (query, token) => worker.fetch(new Request("https://lic.example/order?" + query, { method: "POST", headers: token ? { Authorization: "Bearer " + token } : {} }), env)
  .then(async r => ({ status: r.status, ...(await r.json()) }));
const buy = "plan=monthly&method=manual";
const t1 = await send("/account/signup", okUser("ticket1", { invite: c2, device: device(120) }));
const t2 = await send("/account/signup", okUser("ticket2", { invite: "SELLER2", device: device(121) }));
const left = async token => (await send("/account/me", undefined, token)).ticket;
assert.equal((await left(t1.token)).percent, 10);
assert.ok((await left(t1.token)).expires > Date.now() + 29 * 864e5);
assert.equal((await send("/account/me", undefined, t1.token)).inviteTicket, 10);
assert.equal(await left(c3.token), null);   // no code at sign-up, no ticket
await adm("/pay-links", { monthly: "https://pay.example/m" });
assert.equal((await order(buy + "&ticket=1", t1.token)).error, "ticket link");
await adm("/pay-links", { monthly: "https://pay.example/m", monthly_ticket: "https://pay.example/m-sale" });
assert.equal((await order(buy, t1.token)).url, "https://pay.example/m");
assert.equal((await order(buy + "&ticket=1")).error, "ticket");
assert.equal((await order(buy + "&ticket=1", c3.token)).error, "ticket");
const o1 = await order(buy + "&ticket=1", t1.token);
assert.equal(o1.url, "https://pay.example/m-sale");
assert.equal((await adm("/orders?status=all")).orders.find(o => o.code === o1.code).ticket, 10);
await adm("/orders/reject", { code: o1.code, reason: "x" });
assert.equal((await left(t1.token)).percent, 10);
const o2 = await order(buy + "&ticket=1", t1.token);
assert.equal((await adm("/orders/approve", { code: o2.code, email: "" })).error, undefined);
assert.equal(await left(t1.token), null);
assert.equal((await order(buy + "&ticket=1", t1.token)).error, "ticket");
await run("UPDATE public.accounts SET ticket_expires = now() - interval '1 minute' WHERE id = 'ticket2'");
assert.equal(await left(t2.token), null);
assert.equal((await order(buy + "&ticket=1", t2.token)).error, "ticket");
assert.equal((await send("/account/ticket", { order: "SB-AAAA-AAAA" }, t1.token)).status, 404);   // not a public route

// leaving: the password is needed, the record goes, the one they invited keeps their account
assert.equal((await send("/account/delete", { password: "wrong-pass" }, a.token)).error, "wrong");
assert.equal((await send("/account/delete", { password: "password123" }, a.token)).ok, true);
assert.equal(await count("SELECT COUNT(*) AS n FROM public.accounts WHERE id = 'alice_1'"), 0);
assert.equal(await count("SELECT COUNT(*) AS n FROM public.account_sessions WHERE account = 'alice_1'"), 0);
assert.equal((await rows("SELECT invited_by FROM public.accounts WHERE id = 'friend1'"))[0].invited_by, null);
assert.equal((await send("/account/login", { id: "alice_1", password: "password123" })).error, "wrong");
assert.equal((await send("/account/signup", okUser("friend9", { invite: c2, device: device(110) }))).error, "unknown");

// the dashboard removes a member too, and keeps a log of who did what without a password or code
await adm("/members/delete", { id: "friend1" });
assert.equal(await count("SELECT COUNT(*) AS n FROM public.accounts WHERE id = 'friend1'"), 0);
const log = JSON.stringify(await adm("/log"));
assert.ok(log.includes("회원 차단") && log.includes("회원 삭제") && !log.includes(fresh));

// only the listed actions reach the object from outside
assert.equal((await send("/account/admin/list", {})).status, 404);
assert.equal((await send("/account/admin/delete", { id: "bobby" })).status, 404);
assert.equal((await send("/account/code", { code: "X" })).status, 404);

// the publishable key's roles reach nothing: no table, no function, no helper
for (const role of ["anon", "authenticated"]) {
  await pg.exec("SET ROLE " + role);
  for (const query of ["SELECT * FROM public.accounts", "SELECT * FROM public.account_sessions", "SELECT * FROM public.account_hits",
                       "SELECT public.account_call('ping', '{}')", "SELECT public.account_call('admin/list', '{}')", "SELECT public.account_ms(now())",
                       "INSERT INTO public.account_hits VALUES ('x', 1, 1)", "DELETE FROM public.accounts"]) {
    await assert.rejects(pg.query(query), /permission denied/, role + ": " + query);
  }
  await pg.exec("RESET ROLE");
}

// housekeeping removes old counters and ended sessions
await run("INSERT INTO public.account_hits VALUES ('old', 1, 1)");
await run("UPDATE public.account_sessions SET expires = now() - interval '1 day' WHERE account = 'carol'");
assert.equal((await send("/account/me", undefined, c3.token)).error, "session");   // ended, even before the cleanup
await worker.scheduled({}, { ...env, STATS: { idFromName: () => "all", get: () => ({ fetch: async () => Response.json({}) }) } }, { waitUntil: p => pings.push(p) });
await Promise.all(pings);
assert.equal(await count("SELECT COUNT(*) AS n FROM public.account_hits WHERE k = 'old'"), 0);
assert.equal(await count("SELECT COUNT(*) AS n FROM public.account_sessions WHERE account = 'carol'"), 0);

// Supabase paused or not set up: accounts say so, and nothing else stops
await adm("/referrals", { code: "STILLOK", days: 3, max: 5 });
down = true;
assert.deepEqual(await send("/account/login", { id: "bobby", password: "bobbyspass" }), { status: 503, error: "unavailable" });
assert.equal((await send("/referral", { code: "NOSUCH1", device: device(111) })).error, "unknown");
assert.equal((await send("/referral", { code: "STILLOK", device: device(111) })).status, 200);
assert.equal((await adm("/members")).status, 503);
down = false;
const saved = env.SUPABASE_URL;
delete env.SUPABASE_URL;
assert.equal((await send("/account/login", { id: "bobby", password: "bobbyspass" })).error, "unavailable");
env.SUPABASE_URL = saved;
assert.equal((await send("/account/login", { id: "bobby", password: "bobbyspass" })).status, 200);

// the SQL can run a second time and keeps the members
const before = await count("SELECT COUNT(*) AS n FROM public.accounts");
await pg.exec(readFileSync(new URL("./supabase/accounts.sql", import.meta.url), "utf8").replace("notify pgrst, 'reload schema';", ""));
assert.equal(await count("SELECT COUNT(*) AS n FROM public.accounts"), before);
console.log("account tests passed");
