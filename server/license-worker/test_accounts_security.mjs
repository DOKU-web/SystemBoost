// Security checks for member accounts (owner's spec section 6), on real Postgres (PGlite) with Supabase's roles and
// default grants recreated, behind a stand-in for PostgREST that maps each key to the role Supabase gives it.
// Made-up keys and passwords only; nothing leaves this process.
//   node test_accounts_security.mjs        (PGlite: npm i --no-save @electric-sql/pglite)
// What PGlite cannot show: PostgREST itself (how it turns a key or JWT into a role, which schemas it exposes),
// Supabase's real grants on the live project, and whether the live project runs this SQL at all.
import assert from "node:assert/strict";
import { createHash, generateKeyPairSync } from "node:crypto";
import { execFileSync } from "node:child_process";
import { existsSync, readFileSync, readdirSync } from "node:fs";
import { PGlite } from "@electric-sql/pglite";
import { pgcrypto } from "@electric-sql/pglite/contrib/pgcrypto";
import worker from "./src/index.js";

const here = new URL(".", import.meta.url);
// as the repository stores it (LF), whatever the checkout did to line ends
const sqlFile = name => readFileSync(new URL(name, here), "utf8").replace(/\r\n/g, "\n").replace("notify pgrst, 'reload schema';", "");
const ACCOUNTS_SQL = sqlFile("./supabase/accounts.sql");
const SHIPPED = "af5b464";   // main when the audit began: what runs in Supabase today, as far as the repo shows

// Supabase gives every new table and function in public to anon and authenticated; the SQL must take that back.
const SUPABASE = `
  CREATE SCHEMA extensions;
  CREATE ROLE anon NOLOGIN; CREATE ROLE authenticated NOLOGIN; CREATE ROLE service_role NOLOGIN BYPASSRLS;
  GRANT USAGE ON SCHEMA public, extensions TO anon, authenticated, service_role;
  ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO anon, authenticated, service_role;
  ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON FUNCTIONS TO anon, authenticated, service_role;
  ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON SEQUENCES TO anon, authenticated, service_role;`;
const fresh = async sql => {
  const db = new PGlite({ extensions: { pgcrypto } });
  await db.exec(SUPABASE);
  if (sql) await db.exec(sql);
  return db;
};

const SECRET = "sb_secret_made_up_for_tests", PUBLISHABLE = "sb_publishable_made_up_for_tests", USER_JWT = "made.up.jwt";
let pg = await fresh(ACCOUNTS_SQL);
const sent = [];
// PostgREST stand-in: the secret key is service_role, the publishable key is anon, plus a signed-in user's JWT authenticated.
globalThis.fetch = async (url, init = {}) => {
  const u = new URL(String(url));
  assert.equal(u.origin, "https://project.supabase.example");
  let role = init.headers?.apikey === SECRET ? "service_role" : init.headers?.apikey === PUBLISHABLE ? "anon" : null;
  if (!role) return Response.json({ message: "Invalid API key" }, { status: 401 });
  if (role === "anon" && init.headers?.Authorization === "Bearer " + USER_JWT) role = "authenticated";
  const path = u.pathname.replace(/^\/rest\/v1\//, "");
  const method = init.method || "GET";
  if (init.body) sent.push(String(init.body));
  await pg.exec("SET ROLE " + role);
  try {
    if (path === "rpc/account_call" && method === "POST") {
      const { action, b } = JSON.parse(init.body);
      return Response.json((await pg.query("SELECT public.account_call($1, $2::jsonb) AS r", [action, JSON.stringify(b)])).rows[0].r);
    }
    const table = { accounts: "accounts", account_sessions: "account_sessions", account_hits: "account_hits" }[path];
    if (!table) return Response.json({ message: "not found" }, { status: 404 });
    const sql = { GET: `SELECT * FROM public.${table}`, DELETE: `DELETE FROM public.${table}`, PATCH: `UPDATE public.${table} SET ${table === "account_hits" ? "n = 0" : table === "accounts" ? "blocked = false" : "expires = now()"}` }[method];
    return Response.json((await pg.query(sql)).rows);
  } catch (e) {
    return Response.json({ code: e.code, message: e.message }, { status: e.code === "42501" ? (role === "anon" ? 401 : 403) : 400 });
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
  ADMIN_TOKEN: "A".repeat(32),
  SUPABASE_URL: "https://project.supabase.example/",
  SUPABASE_SECRET_KEY: SECRET,
};

// everything the Worker logs or answers, to look for secrets in at the end
const logged = [], replies = [], issued = new Set();
const log = console.log;
console.log = (...args) => { logged.push(args.map(String).join(" ")); log(...args); };

let ipN = 0;
const newIp = () => { ipN++; return `10.${(ipN >> 16) & 255}.${(ipN >> 8) & 255}.${ipN & 255}`; };
const send = async (path, body, token, ip, e = env) => {
  const headers = { "CF-Connecting-IP": ip || newIp() };
  if (token) headers.Authorization = "Bearer " + token;
  const r = await worker.fetch(new Request("https://lic.example" + path, { method: body === undefined ? "GET" : "POST", headers, body: body === undefined ? undefined : JSON.stringify(body) }), e);
  const text = await r.text();
  replies.push(text);
  const out = { status: r.status, ...JSON.parse(text) };
  for (const k of ["token", "recovery"]) if (typeof out[k] === "string") issued.add(out[k]);
  return out;
};
const adm = async (path, body, token = env.ADMIN_TOKEN, ip = newIp()) => {
  const r = await worker.fetch(new Request("https://lic.example/admin/api" + path, { method: body ? "POST" : "GET", headers: { Authorization: "Bearer " + token, "CF-Connecting-IP": ip }, body: body ? JSON.stringify(body) : undefined }), env);
  return { status: r.status, ...(await r.json()) };
};
const direct = async (path, { method = "GET", body, jwt } = {}) => {
  const headers = { apikey: PUBLISHABLE, "Content-Type": "application/json" };
  if (jwt) headers.Authorization = "Bearer " + jwt;
  const r = await fetch("https://project.supabase.example/rest/v1/" + path, { method, headers, body: body && JSON.stringify(body) });
  return { status: r.status, body: await r.json() };
};
const rows = async (query, ...args) => (await pg.query(query, args)).rows;
const count = async (query, ...args) => Number((await rows(query, ...args))[0].n);
const run = async (query, ...args) => { await pg.query(query, args); };
const sha = text => createHash("sha256").update(text).digest("hex");
const PW = "correct-horse-9";
const user = (id, extra) => ({ id, password: PW, agree: true, age14: true, ...extra });
const signup = async (id, extra) => {
  const r = await send("/account/signup", user(id, extra));
  assert.equal(r.status, 200, id + ": " + JSON.stringify(r));
  return r;
};
const unlock = id => run("UPDATE public.accounts SET fails = 0, locked_until = NULL WHERE id = $1", id);
const median = xs => [...xs].sort((a, b) => a - b)[xs.length >> 1];

const results = [];
async function check(name, fn, owner) {
  try {
    await fn();
    results.push({ name, state: "PASS" });
  } catch (e) {
    results.push({ name, state: owner ? "OPEN" : "FAIL", owner, why: String(e.message).split("\n")[0].slice(0, 160) });
  }
}

// ---------------------------------------------------------------------------------------------- 1. no session
await check("no session: every member action refuses", async () => {
  for (const action of ["me", "invite", "reward", "logout", "password", "delete"]) {
    const r = await send("/account/" + action, action === "me" ? undefined : { password: PW, next: "another-pass-1" });
    assert.deepEqual([r.status, r.error], [401, "session"], action);
  }
});

// ---------------------------------------------------------------------------------------------- 2. forged tokens
await check("forged or altered tokens are refused", async () => {
  const a = await signup("sec_forge");
  const [stored] = await rows("SELECT token FROM public.account_sessions WHERE account = 'sec_forge'");
  assert.equal(stored.token, sha(a.token));                       // the database holds only a hash of the token
  const flip = a.token.slice(0, -1) + (a.token.endsWith("0") ? "1" : "0");
  for (const t of ["f".repeat(64), flip, stored.token, a.token.toUpperCase(), a.token + "0", a.token.slice(1), "' OR '1'='1", "null"])
    assert.equal((await send("/account/me", undefined, t)).error, "session", t.slice(0, 12));
  assert.equal((await send("/account/me", { token: a.token })).error, "session");   // a token in the body is not a session
  assert.equal((await send("/account/me", undefined, a.token)).id, "sec_forge");
});

await check("tokens: 256 random bits, unique, never stored or sent in the clear", async () => {
  const seen = new Set();
  const a = await signup("sec_tokens");
  for (let i = 0; i < 12; i++) {
    const t = (await send("/account/login", { id: "sec_tokens", password: PW })).token;
    assert.match(t, /^[0-9a-f]{64}$/);
    seen.add(t);
  }
  assert.equal(seen.size, 12);
  const src = readFileSync(new URL("./src/accounts.js", here), "utf8");
  assert.ok(!/Math\.random/.test(src), "accounts.js must not use Math.random");
  assert.match(src, /crypto\.getRandomValues/);
  const dump = JSON.stringify(await rows("SELECT * FROM public.account_sessions"));
  for (const t of [...seen, a.token]) assert.ok(!dump.includes(t));
  for (const body of sent) for (const t of [...seen, a.token]) assert.ok(!body.includes(t), "a token left the Worker");
});

// ---------------------------------------------------------------------------------------------- 3. expired session
await check("expired session: refused everywhere, removed by the hourly job", async () => {
  const a = await signup("sec_expired");
  await run("UPDATE public.account_sessions SET expires = now() - interval '1 second' WHERE account = 'sec_expired'");
  for (const action of ["me", "invite", "reward", "logout", "password", "delete"]) {
    const r = await send("/account/" + action, action === "me" ? undefined : { password: PW, next: "another-pass-1" }, a.token);
    assert.deepEqual([r.status, r.error], [401, "session"], action);
  }
  assert.equal(await count("SELECT COUNT(*) AS n FROM public.accounts WHERE id = 'sec_expired'"), 1);   // delete did nothing
  const pings = [];
  await worker.scheduled({}, { ...env, STATS: { idFromName: () => "all", get: () => ({ fetch: async () => Response.json({}) }) } }, { waitUntil: p => pings.push(p) });
  await Promise.all(pings);
  assert.equal(await count("SELECT COUNT(*) AS n FROM public.account_sessions WHERE account = 'sec_expired'"), 0);
});

await check("a session lasts 90 days at most, whatever the request says", async () => {
  const tamper = { sessionDays: 36500, sessionsKept: 999, loginTries: 999999, lockMin: 0, failsPerIp: 1e9, signupsPerIp: 1e9,
                   ticketPercent: 100, ticketDays: 3650, rewardDays: 999, rewardCap: 99999, inviteMax: 99999 };
  await signup("sec_tamper", tamper);
  await send("/account/login", { id: "sec_tamper", password: PW, ...tamper });
  const [r] = await rows("SELECT MAX(expires) > now() + interval '90 days 1 minute' AS long, COUNT(*) AS n FROM public.account_sessions WHERE account = 'sec_tamper'");
  assert.equal(r.long, false);
  assert.equal(Number(r.n), 2);
  for (let i = 0; i < 5; i++) await send("/account/login", { id: "sec_tamper", password: "wrong-pass-1", ...tamper });
  assert.equal((await send("/account/login", { id: "sec_tamper", password: PW, ...tamper })).error, "locked");   // 5 tries still
  const [row] = await rows("SELECT ticket_percent FROM public.accounts WHERE id = 'sec_tamper'");
  assert.notEqual(row.ticket_percent, 100);
});

await check("a discount ticket comes only with a code (signup body {ticket:true})", async () => {
  const a = await signup("sec_ticket", { ticket: true });
  assert.equal((await send("/account/me", undefined, a.token)).ticket, null);
}, "licence: src/index.js signup() passes the request body's ticket flag through");

// ---------------------------------------------------------------------------------------------- 4. logout
await check("token after logout: refused; the member's other sessions stay", async () => {
  await signup("sec_logout");
  const t1 = (await send("/account/login", { id: "sec_logout", password: PW })).token;
  const t2 = (await send("/account/login", { id: "sec_logout", password: PW })).token;
  assert.equal((await send("/account/logout", {}, t1)).ok, true);
  assert.equal(await count("SELECT COUNT(*) AS n FROM public.account_sessions WHERE token = $1", sha(t1)), 0);
  for (const action of ["me", "logout", "invite", "password", "delete"])
    assert.equal((await send("/account/" + action, action === "me" ? undefined : { password: PW, next: "another-pass-1" }, t1)).error, "session", action);
  assert.equal((await send("/account/me", undefined, t2)).id, "sec_logout");
});

await check("five sessions per member, enforced by the database", async () => {
  const first = (await signup("sec_five")).token;
  const tokens = [];
  for (let i = 0; i < 7; i++) tokens.push((await send("/account/login", { id: "sec_five", password: PW })).token);
  assert.equal(await count("SELECT COUNT(*) AS n FROM public.account_sessions WHERE account = 'sec_five'"), 5);
  for (const t of [first, tokens[0], tokens[1]]) assert.equal((await send("/account/me", undefined, t)).error, "session");
  for (const t of tokens.slice(2)) assert.equal((await send("/account/me", undefined, t)).id, "sec_five");
});

// ---------------------------------------------------------------------------------------------- 5. someone else's id
await check("another member's id in the request changes nothing", async () => {
  const me = await signup("sec_mallory"), victim = await signup("sec_victim");
  assert.equal((await send("/account/me", { id: "sec_victim" }, me.token)).id, "sec_mallory");
  assert.equal((await send("/account/me?id=sec_victim", undefined, me.token)).id, "sec_mallory");
  assert.equal((await send("/account/invite", { id: "sec_victim" }, me.token)).status, 200);
  assert.equal((await rows("SELECT invite_code FROM public.accounts WHERE id = 'sec_victim'"))[0].invite_code, null);
  assert.equal((await send("/account/reward", { id: "sec_victim" }, me.token)).error, "nothing");
  const p = await send("/account/password", { id: "sec_victim", password: PW, next: "mallory-new-1" }, me.token);
  assert.equal(p.id, "sec_mallory");
  assert.equal((await send("/account/login", { id: "sec_victim", password: PW })).status, 200);
  assert.equal((await send("/account/me", undefined, victim.token)).id, "sec_victim");
  assert.equal((await send("/account/delete", { id: "sec_victim", password: "mallory-new-1" }, p.token)).ok, true);
  assert.equal(await count("SELECT COUNT(*) AS n FROM public.accounts WHERE id = 'sec_mallory'"), 0);
  assert.equal(await count("SELECT COUNT(*) AS n FROM public.accounts WHERE id = 'sec_victim'"), 1);
});

// ---------------------------------------------------------------------------------------------- 6. admin actions
await check("a member cannot reach admin or internal actions through the Worker", async () => {
  const m = await signup("sec_member");
  await signup("sec_target");
  for (const action of ["admin/list", "admin/block", "admin/recovery", "admin/delete", "ticket/used", "ticket/release", "ticket", "ping", "code"]) {
    const r = await send("/account/" + action, { id: "sec_target", on: true, order: "SB-AAAA-AAAA", code: "ABCD" }, m.token);
    assert.equal(r.status, 404, action);
    assert.ok(!("recovery" in r) && !("members" in r), action);
  }
  for (const path of ["/members", "/members/block", "/members/recovery", "/members/delete"]) {
    const r = await adm(path, path === "/members" ? undefined : { id: "sec_target", on: true }, m.token);
    assert.equal(r.status, 401, path);
  }
  const [t] = await rows("SELECT blocked, recovery FROM public.accounts WHERE id = 'sec_target'");
  assert.equal(t.blocked, false);
  assert.equal(await count("SELECT COUNT(*) AS n FROM public.accounts WHERE id = 'sec_target'"), 1);
  assert.equal((await adm("/members")).status, 200);   // the owner's token still works after those misses
});

await check("the publishable key (anon) and a Supabase user JWT (authenticated) reach no table and no function", async () => {
  for (const jwt of [undefined, USER_JWT]) {
    const denied = jwt ? 403 : 401;
    for (const action of ["admin/list", "admin/delete", "admin/block", "admin/recovery", "ping", "login", "me", "ticket/used"]) {
      const r = await direct("rpc/account_call", { method: "POST", body: { action, b: { id: "sec_target", on: true } }, jwt });
      assert.equal(r.status, denied, action);
      assert.equal(r.body.code, "42501", action);
    }
    for (const table of ["accounts", "account_sessions", "account_hits"])
      for (const method of ["GET", "DELETE", "PATCH"]) {
        const r = await direct(table, { method, jwt });
        assert.equal(r.status, denied, method + " " + table);
      }
  }
  assert.equal(await count("SELECT COUNT(*) AS n FROM public.accounts WHERE id = 'sec_target'"), 1);
  // every helper, called straight in SQL as each role
  const fns = await rows(`SELECT p.oid::regprocedure::text AS sig, (SELECT string_agg('NULL::' || format_type(t, NULL), ', ') FROM unnest(p.proargtypes) t) AS args
                          FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'public' AND p.proname LIKE 'account\\_%'`);
  assert.ok(fns.length >= 10);
  for (const role of ["anon", "authenticated"]) {
    for (const f of fns) {
      await pg.exec("SET ROLE " + role);
      try {
        await assert.rejects(pg.query(`SELECT ${f.sig.replace(/\(.*$/, "")}(${f.args || ""})`), /permission denied/, role + " " + f.sig);
      } finally { await pg.exec("RESET ROLE"); }
    }
  }
  // the grants as Postgres reports them, and the default grants of this stand-in really were in force
  for (const f of fns) {
    const [g] = await rows(`SELECT has_function_privilege('anon', $1, 'execute') AS a, has_function_privilege('authenticated', $1, 'execute') AS u,
                                   has_function_privilege('public', $1, 'execute') AS p, has_function_privilege('service_role', $1, 'execute') AS s`, f.sig);
    assert.deepEqual(g, { a: false, u: false, p: false, s: true }, f.sig);
  }
  for (const t of ["accounts", "account_sessions", "account_hits"]) {
    const [g] = await rows(`SELECT c.relrowsecurity AS rls, (SELECT COUNT(*) FROM pg_policies WHERE schemaname = 'public' AND tablename = $1) AS policies,
                                   has_table_privilege('anon', c.oid, 'select,insert,update,delete,truncate,references,trigger') AS a,
                                   has_table_privilege('authenticated', c.oid, 'select,insert,update,delete,truncate,references,trigger') AS u
                            FROM pg_class c WHERE c.oid = ('public.' || $1)::regclass`, t);
    assert.deepEqual([g.rls, Number(g.policies), g.a, g.u], [true, 0, false, false], t);
  }
  await run("CREATE TABLE public.probe_default (x int)");
  assert.equal((await rows("SELECT has_table_privilege('anon', 'public.probe_default', 'select') AS a"))[0].a, true);   // what Supabase does
  await run("DROP TABLE public.probe_default");
});

await check("every account function: no SECURITY DEFINER, a fixed search_path", async () => {
  const fns = await rows(`SELECT p.proname, p.prosecdef, p.proconfig FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                          WHERE n.nspname = 'public' AND p.proname LIKE 'account\\_%'`);
  for (const f of fns) {
    assert.equal(f.prosecdef, false, f.proname);
    assert.ok((f.proconfig || []).some(c => c.startsWith("search_path=")), f.proname + " has no fixed search_path");
  }
});

await check("running the SQL again takes back a grant given by mistake", async () => {
  await pg.exec("GRANT SELECT ON public.account_sessions TO anon; GRANT EXECUTE ON FUNCTION public.account_call(text, jsonb) TO anon");
  await pg.exec(ACCOUNTS_SQL);
  const [g] = await rows(`SELECT has_table_privilege('anon', 'public.account_sessions', 'select') AS t,
                                 has_function_privilege('anon', 'public.account_call(text, jsonb)', 'execute') AS f`);
  assert.deepEqual(g, { t: false, f: false });
});

await check("the SQL stops with an error when a table has a policy (e.g. one opened to anon)", async () => {
  await pg.exec("CREATE POLICY probe ON public.accounts FOR SELECT TO anon USING (true)");
  try {
    await assert.rejects(pg.exec(ACCOUNTS_SQL), /access rights on (public\.)?accounts are not as expected/);
  } finally { await pg.exec("DROP POLICY probe ON public.accounts"); }
  await pg.exec(ACCOUNTS_SQL);
});

// ---------------------------------------------------------------------------------------------- 7. guessing
await check("five wrong passwords lock the account; the right one and the recovery code wait too", async () => {
  const a = await signup("sec_lock");
  for (let i = 0; i < 5; i++) assert.equal((await send("/account/login", { id: "sec_lock", password: "wrong-pass-" + i })).error, "wrong");
  assert.equal((await send("/account/login", { id: "sec_lock", password: PW })).error, "locked");
  assert.equal((await send("/account/recover", { id: "sec_lock", recovery: a.recovery, next: "brand-new-pass" })).error, "locked");
  const [r] = await rows("SELECT locked_until > now() + interval '14 minutes' AS long FROM public.accounts WHERE id = 'sec_lock'");
  assert.equal(r.long, true);
  await run("UPDATE public.accounts SET locked_until = now() - interval '1 second' WHERE id = 'sec_lock'");
  assert.equal((await send("/account/login", { id: "sec_lock", password: PW })).status, 200);
  assert.equal((await rows("SELECT fails FROM public.accounts WHERE id = 'sec_lock'"))[0].fails, 0);
});

await check("twenty misses from one address stop that address, not others", async () => {
  await signup("sec_ipuser");
  const ip = "192.0.2.77";
  for (let i = 0; i < 20; i++) await send("/account/login", { id: "sec_nobody" + i, password: "x-wrong-pass" }, null, ip);
  assert.equal((await send("/account/login", { id: "sec_ipuser", password: PW }, null, ip)).error, "busy");
  assert.equal((await send("/account/recover", { id: "sec_ipuser", recovery: "AAAA-AAAA-AAAA-AAAA", next: "brand-new-pass" }, null, ip)).error, "busy");
  assert.equal((await send("/account/login", { id: "sec_ipuser", password: PW })).status, 200);
});

await check("wrong passwords on password change and leaving are counted and locked like logins", async () => {
  const a = await signup("sec_pwguess");
  for (let i = 0; i < 5; i++)
    assert.equal((await send("/account/password", { password: "wrong-pass-" + i, next: "another-pass-1" }, a.token)).error, "wrong");
  assert.equal((await send("/account/password", { password: PW, next: "another-pass-1" }, a.token)).error, "locked");
  assert.equal((await send("/account/delete", { password: PW }, a.token)).error, "locked");
  assert.equal(await count("SELECT COUNT(*) AS n FROM public.accounts WHERE id = 'sec_pwguess'"), 1);
  assert.equal((await send("/account/me", undefined, a.token)).id, "sec_pwguess");   // the session itself goes on
  await unlock("sec_pwguess");
  const ip = "192.0.2.88", owners = [];
  for (let k = 0; k < 4; k++) owners.push(await signup("sec_pwip" + k));
  for (const o of owners) for (let i = 0; i < 5; i++) await send("/account/delete", { password: "wrong-pass-" + i }, o.token, ip);
  assert.equal((await send("/account/password", { password: PW, next: "another-pass-1" }, a.token, ip)).error, "busy");
  assert.equal((await send("/account/password", { password: PW, next: "another-pass-1" }, a.token)).status, 200);
});

await check("an old Worker (no address sent on password change) still works with the new SQL", async () => {
  const a = await signup("sec_oldworker");
  const call = async (action, b) => {
    await pg.exec("SET ROLE service_role");
    try { return (await pg.query("SELECT public.account_call($1, $2::jsonb) AS r", [action, JSON.stringify(b)])).rows[0].r; }
    finally { await pg.exec("RESET ROLE"); }
  };
  const limits = { loginTries: 5, lockMin: 15, failsPerIp: 20, sessionDays: 90, sessionsKept: 5 };   // what the old Worker sends
  assert.deepEqual(await call("delete", { ...limits, token: sha(a.token), pw: sha("not-it") }), { status: 401, error: "wrong" });
  assert.equal((await rows("SELECT fails FROM public.accounts WHERE id = 'sec_oldworker'"))[0].fails, 1);
  assert.equal(await count("SELECT COUNT(*) AS n FROM public.account_hits WHERE k NOT LIKE '%:%' OR k LIKE 'fail:' "), 0);
});

await check("a missing id costs the same bcrypt work as a wrong password (no timing tell)", async () => {
  await signup("sec_timing");
  const took = async body => { const t = performance.now(); const r = await send("/account/login", body); assert.equal(r.error, "wrong"); return performance.now() - t; };
  const real = [], missing = [];
  for (let i = 0; i < 7; i++) {
    real.push(await took({ id: "sec_timing", password: "wrong-pass-" + i }));
    await unlock("sec_timing");
    missing.push(await took({ id: "sec_no_such_" + i, password: "wrong-pass-" + i }));
  }
  const [r, m] = [median(real), median(missing)];
  assert.ok(m > r * 0.6 && m < r * 1.6, `wrong password ${r.toFixed(0)} ms, missing id ${m.toFixed(0)} ms`);
});

await check("ids already taken: one address learns at most twenty an hour", async () => {
  const ip = "192.0.2.99";
  for (let i = 0; i < 20; i++) assert.equal((await send("/account/signup", user("sec_timing"), null, ip)).error, "taken");
  assert.equal((await send("/account/signup", user("sec_timing"), null, ip)).error, "busy");
  assert.equal((await send("/account/signup", user("sec_timing"))).error, "taken");
});

await check("a blocked member: a guess still says 'wrong'; only the right password learns 'blocked'", async () => {
  const a = await signup("sec_blocked");
  assert.equal((await adm("/members/block", { id: "sec_blocked", on: true })).ok, true);
  assert.equal((await send("/account/me", undefined, a.token)).error, "session");
  assert.equal((await send("/account/login", { id: "sec_blocked", password: "wrong-pass-1" })).error, "wrong");
  assert.equal((await send("/account/login", { id: "sec_blocked", password: PW })).error, "blocked");
  assert.equal((await send("/account/recover", { id: "sec_blocked", recovery: a.recovery, next: "brand-new-pass" })).error, "blocked");
  await adm("/members/block", { id: "sec_blocked", on: false });
  assert.equal((await send("/account/login", { id: "sec_blocked", password: PW })).status, 200);
});

// ---------------------------------------------------------------------------------------------- 8. recovery codes
await check("recovery code: one use, ends the old sessions, dies when the dashboard issues a new one", async () => {
  const a = await signup("sec_recover"), b = await signup("sec_other");
  assert.equal((await send("/account/recover", { id: "sec_recover", recovery: b.recovery, next: "brand-new-pass" })).error, "wrong");
  const r1 = await send("/account/recover", { id: "sec_recover", recovery: a.recovery, next: "brand-new-pass" });
  assert.equal(r1.status, 200);
  assert.equal((await send("/account/me", undefined, a.token)).error, "session");
  assert.equal((await send("/account/recover", { id: "sec_recover", recovery: a.recovery, next: "brand-new-pass2" })).error, "wrong");
  const issued = (await adm("/members/recovery", { id: "sec_recover" })).recovery;
  assert.equal((await send("/account/recover", { id: "sec_recover", recovery: r1.recovery, next: "brand-new-pass3" })).error, "wrong");
  assert.equal((await send("/account/recover", { id: "sec_recover", recovery: issued, next: "brand-new-pass4" })).status, 200);
  assert.equal((await send("/account/recover", { id: "sec_recover", recovery: issued, next: "brand-new-pass5" })).error, "wrong");
  assert.equal((await send("/account/login", { id: "sec_recover", password: "brand-new-pass4" })).status, 200);
});

await check("recovery codes: 16 symbols of 32 (80 bits), unique, stored only as a peppered hash", async () => {
  await signup("sec_codes");
  const codes = new Set();
  for (let i = 0; i < 25; i++) {
    const c = (await adm("/members/recovery", { id: "sec_codes" })).recovery;
    assert.match(c, /^[A-HJ-NP-Z2-9]{4}(-[A-HJ-NP-Z2-9]{4}){3}$/);
    codes.add(c);
  }
  assert.equal(codes.size, 25);
  const [row] = await rows("SELECT recovery FROM public.accounts WHERE id = 'sec_codes'");
  assert.match(row.recovery, /^[0-9a-f]{64}$/);
  for (const c of codes) {
    assert.notEqual(row.recovery, sha(c));
    assert.notEqual(row.recovery, sha(c.replaceAll("-", "")));
  }
});

// ---------------------------------------------------------------------------------------------- 9. storage, input
await check("nothing in the clear: password, recovery code, token, IP", async () => {
  const ip = "198.18.7.7";
  const r = await send("/account/signup", user("sec_store", { password: "unique-pass-77" }), null, ip);
  assert.equal(r.status, 200);
  const dump = JSON.stringify([await rows("SELECT * FROM public.accounts"), await rows("SELECT * FROM public.account_sessions"), await rows("SELECT * FROM public.account_hits")]);
  for (const secret of ["unique-pass-77", r.recovery, r.recovery.replaceAll("-", ""), r.token, ip]) assert.ok(!dump.includes(secret));
  for (const body of sent) for (const secret of ["unique-pass-77", r.recovery, r.token, ip]) assert.ok(!body.includes(secret), "left the Worker in the clear");
  const [row] = await rows("SELECT hash FROM public.accounts WHERE id = 'sec_store'");
  assert.match(row.hash, /^\$2a\$10\$[./A-Za-z0-9]{53}$/);   // bcrypt, its own salt
  const [other] = await rows("SELECT hash FROM public.accounts WHERE id = 'sec_codes'");
  assert.notEqual(row.hash.slice(7, 29), other.hash.slice(7, 29));
});

await check("SQL in ids and passwords is only data", async () => {
  const before = await count("SELECT COUNT(*) AS n FROM public.accounts");
  for (const id of ["x' OR '1'='1", "a'; DROP TABLE public.accounts; --", "sec_store' --", "%", "_"]) {
    assert.equal((await send("/account/login", { id, password: "' OR '1'='1" })).error, "wrong");
    assert.equal((await send("/account/signup", user(id))).error, "id");
  }
  assert.equal((await send("/account/login", { id: "sec_store", password: "' OR '1'='1" })).error, "wrong");
  assert.equal(await count("SELECT COUNT(*) AS n FROM public.accounts"), before);
  assert.ok((await adm("/members", { q: "' OR 1=1 --" })).members.length === 0);   // the search goes in the body
});

await check("without the signing key there is no pepper, so accounts stop instead of using a known one", async () => {
  const bare = { ...env };
  delete bare.LICENSE_PRIVATE_KEY;
  assert.deepEqual(await send("/account/signup", user("sec_nopepper"), null, undefined, bare), { status: 503, error: "unavailable" });
  assert.equal((await send("/account/login", { id: "sec_store", password: "unique-pass-77" }, null, undefined, bare)).error, "unavailable");
  assert.equal(await count("SELECT COUNT(*) AS n FROM public.accounts WHERE id = 'sec_nopepper'"), 0);
});

// ---------------------------------------------------------------------------------------------- 10. migration
await check("the migration keeps today's members, passwords, recovery codes and sessions, and ends where accounts.sql does", async () => {
  const dir = new URL("./supabase/migrations/", here);
  assert.ok(existsSync(dir), "no supabase/migrations");
  const files = readdirSync(dir).filter(f => f.endsWith(".sql")).sort();
  assert.ok(files.length > 0, "no migration file");
  let shipped;
  try {
    shipped = execFileSync("git", ["show", SHIPPED + ":server/license-worker/supabase/accounts.sql"], { cwd: here, encoding: "utf8" });
  } catch { throw new Error("git show " + SHIPPED + " failed: cannot rebuild today's database"); }
  const main = pg;
  try {
    pg = await fresh(shipped.replace("notify pgrst, 'reload schema';", ""));
    const old = await signup("legacy_one");
    const locked = await signup("legacy_locked");
    for (let i = 0; i < 5; i++) await send("/account/login", { id: "legacy_locked", password: "wrong-pass-" + i });
    const hashes = await rows("SELECT id, hash, recovery FROM public.accounts ORDER BY id");
    for (const f of files) await pg.exec(sqlFile("./supabase/migrations/" + f));
    for (const f of files) await pg.exec(sqlFile("./supabase/migrations/" + f));   // a second run is harmless
    assert.deepEqual(await rows("SELECT id, hash, recovery FROM public.accounts ORDER BY id"), hashes);   // nothing re-hashed
    assert.equal((await send("/account/me", undefined, old.token)).id, "legacy_one");
    assert.equal((await send("/account/me", undefined, locked.token)).id, "legacy_locked");
    assert.equal((await send("/account/login", { id: "legacy_locked", password: PW })).error, "locked");   // a lock in force stays
    assert.equal((await send("/account/login", { id: "legacy_one", password: PW })).status, 200);
    assert.equal((await send("/account/recover", { id: "legacy_one", recovery: old.recovery, next: "legacy-new-pass" })).status, 200);
    const shape = async () => ({
      fns: await rows(`SELECT p.oid::regprocedure::text AS sig, pg_get_functiondef(p.oid) AS def, p.proacl::text AS acl FROM pg_proc p
                       JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'public' AND p.proname LIKE 'account\\_%' ORDER BY 1`),
      tables: await rows(`SELECT relname, relrowsecurity, relacl::text AS acl FROM pg_class WHERE relnamespace = 'public'::regnamespace AND relkind = 'r' ORDER BY 1`),
    });
    const migrated = await shape();
    pg = await fresh(ACCOUNTS_SQL);
    assert.deepEqual(migrated, await shape());
  } finally {
    pg = main;
  }
});

await check("the Worker's logs and replies hold no key, token, password or recovery code", async () => {
  assert.ok(issued.size > 50);
  const pem = env.LICENSE_PRIVATE_KEY.replace(/-----[^-]+-----|\s/g, "");
  for (const line of logged) for (const s of [...issued, PW, SECRET, pem.slice(20, 60)]) assert.ok(!line.includes(s), "log: " + line.slice(0, 40));
  for (const text of replies) for (const s of [SECRET, PUBLISHABLE, pem.slice(20, 60), PW]) assert.ok(!text.includes(s), "reply leaks a secret");
});

// ---------------------------------------------------------------------------------------------- report
const width = Math.max(...results.map(r => r.name.length));
for (const r of results) console.log(r.state.padEnd(5), r.name.padEnd(width), r.state === "PASS" ? "" : "\n      " + (r.owner ? "[" + r.owner + "] " : "") + r.why);
const by = s => results.filter(r => r.state === s).length;
console.log(`\n${results.length} checks: ${by("PASS")} pass, ${by("FAIL")} fail, ${by("OPEN")} open in another agent's file`);
if (by("FAIL")) process.exit(1);
console.log("account security tests passed" + (by("OPEN") ? " (open items above are outside accounts.js / accounts.sql)" : ""));
