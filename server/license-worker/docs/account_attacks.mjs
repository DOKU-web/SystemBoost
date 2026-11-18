// Manual attack runs against member accounts v2, on this machine only: the Worker (src/index.js) with supabase/accounts.sql
// on PGlite, the Stats object on node:sqlite, a stand-in for Resend that records mails. Not a test (nothing asserts);
// it prints what an attacker gets, for docs/ACCOUNT_SECURITY_TEST.md. Throwaway keys and passwords; no network.
//   npm i --no-save @electric-sql/pglite && node docs/account_attacks.mjs [samples]
import { generateKeyPairSync } from "node:crypto";
import { readFileSync } from "node:fs";
import { PGlite } from "@electric-sql/pglite";
import { pgcrypto } from "@electric-sql/pglite/contrib/pgcrypto";
import worker from "../src/index.js";
import { fakeStats } from "../fake_stats.mjs";
import { hotp, stepAt } from "../src/totp.js";

const SAMPLES = Number(process.argv[2]) || 40;
const pg = new PGlite({ extensions: { pgcrypto } });
await pg.exec(`CREATE SCHEMA extensions; CREATE ROLE anon NOLOGIN; CREATE ROLE authenticated NOLOGIN; CREATE ROLE service_role NOLOGIN BYPASSRLS;`);
await pg.exec(readFileSync(new URL("../supabase/accounts.sql", import.meta.url), "utf8").replace("notify pgrst, 'reload schema';", ""));
const run = async (q, ...a) => { await pg.query(q, a); };
const mails = [];
globalThis.fetch = async (url, init) => {
  if (String(url) === "https://api.resend.com/emails") { mails.push(JSON.parse(init.body)); return Response.json({ id: "x" }); }
  const { action, b } = JSON.parse(init.body);
  await pg.exec("SET ROLE service_role");
  try { return Response.json((await pg.query("SELECT public.account_call($1, $2::jsonb) AS r", [action, JSON.stringify(b)])).rows[0].r); }
  finally { await pg.exec("RESET ROLE"); }
};
const kv = new Map(), md = new Map();
const { privateKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
const env = {
  LICENSES: { get: async k => kv.get(k) ?? null, getWithMetadata: async k => ({ value: kv.get(k) ?? null, metadata: md.get(k) ?? null }),
              put: async (k, v, o) => { kv.set(k, v); md.set(k, o?.metadata ?? null); }, delete: async k => { kv.delete(k); md.delete(k); },
              list: async () => ({ keys: [], list_complete: true }) },
  ACCOUNTS_V2_FROM: "2026-01-01T00:00:00+09:00",   // accounts v2 have started (src/accounts_gate.js); test_accounts_gate.mjs tests before
  LICENSE_PRIVATE_KEY: privateKey.export({ type: "pkcs8", format: "pem" }), ADMIN_TOKEN: "T".repeat(32),
  SUPABASE_URL: "https://project.supabase.example", SUPABASE_SECRET_KEY: "made-up", PUBLIC_URL: "https://lic.example",
  RESEND_API_KEY: "made-up-mail-key", MAIL_FROM: "SystemBoost <no-reply@mail.example.com>", STATS: fakeStats().binding,
};
const waits = [], ctx = { waitUntil: p => waits.push(p) };
let ipN = 0;
const ipNew = () => { ipN++; return `10.${(ipN >> 16) & 255}.${(ipN >> 8) & 255}.${ipN & 255}`; };
async function call(path, body, { token, ip } = {}) {
  const headers = { "CF-Connecting-IP": ip || ipNew() };
  if (token) headers.Authorization = "Bearer " + token;
  const t = performance.now();
  const r = await worker.fetch(new Request("https://lic.example" + path, { method: "POST", headers, body: JSON.stringify(body) }), env, ctx);
  const text = await r.text();
  const ms = performance.now() - t;   // until the reply; mails go after it
  while (waits.length) await waits.shift();
  return { status: r.status, body: JSON.parse(text), ms, text };
}
const tokenIn = m => (/[?&]t=([0-9a-f]{64})/.exec(m?.text || "") || [])[1];
const lastTo = to => mails.filter(m => m.to[0] === to).at(-1);
const unb32 = s => { const A = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"; let bits = 0, v = 0; const out = []; for (const c of s) { v = (v << 5) | A.indexOf(c); bits += 5; if (bits >= 8) { out.push((v >>> (bits - 8)) & 255); bits -= 8; } } return Uint8Array.from(out); };
const tally = rs => Object.entries(rs.reduce((o, r) => { const k = r.status + " " + (r.body.error || Object.keys(r.body).sort().join(",")); o[k] = (o[k] || 0) + 1; return o; }, {})).map(([k, n]) => `${k} x${n}`).join(", ");
const stats = xs => { const s = [...xs].sort((a, b) => a - b), q = p => s[Math.min(s.length - 1, Math.floor(p * s.length))]; return { median: q(0.5), p10: q(0.1), p90: q(0.9), min: s[0], max: s.at(-1) }; };
const fmt = o => `median ${o.median.toFixed(1)} ms (p10 ${o.p10.toFixed(1)}, p90 ${o.p90.toFixed(1)}, min ${o.min.toFixed(1)}, max ${o.max.toFixed(1)})`;
const out = (...a) => console.log(...a);
async function member(email, password) {
  await call("/account/signup_email", { email, password, agree: true, age14: true, consent: "2026-11-18" });
  await call("/account/verify", { t: tokenIn(lastTo(email)), password });
  return (await call("/account/login", { email, password })).body;
}
const PW = "victim-strong-pw1";
const victim = await member("victim@example.com", PW);

out("## 1. Password guessing");
let rs = [];
for (let i = 0; i < 100; i++) rs.push(await call("/account/login", { email: "victim@example.com", password: "guess-" + i }));
out("100 guesses at a real email, a new address each:", tally(rs));
out("  the right password while locked:", tally([await call("/account/login", { email: "victim@example.com", password: PW })]));
rs = [];
for (let i = 0; i < 100; i++) rs.push(await call("/account/login", { email: "nobody@example.com", password: "guess-" + i }));
out("100 guesses at an email nobody has:", tally(rs));
await run("UPDATE public.accounts SET locked_until = now() - interval '1 second'");
rs = [];
for (let i = 0; i < 10; i++) rs.push(await call("/account/login", { email: "victim@example.com", password: "guess-b" + i }));
out("after the 15 minutes (lock moved to the past), 10 more:", tally(rs));
await run("UPDATE public.accounts SET fails = 0, locked_until = NULL");
rs = [];
const ip = "203.0.113.5";
for (let i = 0; i < 30; i++) rs.push(await call("/account/login", { email: "spray" + i + "@example.com", password: "guess" }, { ip }));
out("one address spraying 30 different emails:", tally(rs));
out("  then that address with the victim's right password:", tally([await call("/account/login", { email: "victim@example.com", password: PW }, { ip })]));
out("  another address with it:", tally([await call("/account/login", { email: "victim@example.com", password: PW })]));

out("\n## 2. 2-step code guessing (the password is known)");
const tok = (await call("/account/login", { email: "victim@example.com", password: PW })).body.token;
const st = (await call("/account/totp_start", { password: PW }, { token: tok })).body;
const secret = unb32(st.secret);
const backup = (await call("/account/totp_confirm", { code: await hotp(secret, stepAt()) }, { token: tok })).body.backup;
rs = [];
let pending = (await call("/account/login", { email: "victim@example.com", password: PW })).body.pending;
for (let i = 0; i < 12; i++) {
  const r = await call("/account/login2", { pending, code: String(100000 + i) });
  rs.push(r);
  if (r.body.error === "expired") pending = (await call("/account/login", { email: "victim@example.com", password: PW })).body.pending || pending;
}
out("12 wrong codes on pending logins:", tally(rs));
out("  a new pending login now:", tally([await call("/account/login", { email: "victim@example.com", password: PW })]));
await run("UPDATE public.accounts SET fails = 0, locked_until = NULL");
pending = (await call("/account/login", { email: "victim@example.com", password: PW })).body.pending;
rs = [];
for (let i = 0; i < 6; i++) rs.push(await call("/account/login2", { pending, backup: "AAAA-BBBB-CCC" + i }));
out("6 guessed backup codes on one pending login:", tally(rs));
await run("UPDATE public.accounts SET fails = 0, locked_until = NULL");

out("\n## 3. Timing: with an account / without (" + SAMPLES + " samples each, alternating; time until the reply)");
const key = await (async () => {   // a key bought with victim@example.com, as a card purchase leaves it
  const r = await worker.fetch(new Request("https://lic.example/admin/api/issue", { method: "POST", headers: { Authorization: "Bearer " + env.ADMIN_TOKEN, "CF-Connecting-IP": "10.250.0.1" },
                                                                                 body: JSON.stringify({ plan: "yearly", email: "victim@example.com", note: "x" }) }), env, ctx);
  return (await r.json()).key;
})();
kv.set("email:victim@example.com", key);
const cases = [
  ["login (wrong password)", e => ["/account/login", { email: e, password: "wrong-guess-1" }]],
  ["forgot", e => ["/account/forgot", { email: e }]],
  ["signup_email", e => ["/account/signup_email", { email: e, password: "another-strong-pw", agree: true, age14: true }]],
  ["keys/resend", e => ["/keys/resend", { email: e }]],
  ["recovery_request", e => ["/account/recovery_request", { login: e, contact: "x" }]],
];
for (const [name, mk] of cases) {
  const real = [], none = [];
  const same = new Set();
  for (let i = 0; i < SAMPLES; i++) {
    for (const [list, email] of i % 2 ? [[real, "victim@example.com"], [none, "none" + i + "@example.com"]] : [[none, "none" + i + "@example.com"], [real, "victim@example.com"]]) {
      const r = await call(...mk(email));
      list.push(r.ms);
      same.add(r.status + " " + r.text);
    }
    await run("UPDATE public.accounts SET fails = 0, fail_at = NULL, locked_until = NULL");
    await run("DELETE FROM public.account_hits");
    await run("DELETE FROM public.account_recovery");
    await run("DELETE FROM public.account_guard");
  }
  const a = stats(real), b = stats(none);
  out(`${name}: replies seen ${[...same].map(x => x.slice(0, 40)).join(" | ")}`);
  out(`  with an account    ${fmt(a)}`);
  out(`  without an account ${fmt(b)}`);
  out(`  median difference  ${(a.median - b.median).toFixed(1)} ms`);
}

out("\n## 4. Swapping tokens and ids between accounts");
const other = await member("other@example.com", "other-strong-pw1");
await call("/account/forgot", { email: "victim@example.com" });
const resetV = tokenIn(lastTo("victim@example.com"));
await call("/account/forgot", { email: "other@example.com" });
const resetO = tokenIn(lastTo("other@example.com"));
const tries = [
  ["victim's reset link as a 2-step pending login", () => call("/account/login2", { pending: resetV, code: "123456" })],
  ["victim's reset link as a sign-up link", () => call("/account/verify", { t: resetV })],
  ["victim's reset link as a key link", () => call("/account/link_key_confirm", { t: resetV })],
  ["victim's reset link as a recovery-cancel link", () => call("/account/recovery_cancel", { t: resetV })],
  ["victim's backup code on other's pending login", async () => { await run("UPDATE public.accounts SET totp = (SELECT totp FROM public.accounts WHERE email = 'victim@example.com') WHERE email = 'other@example.com'");
                                                                   const p = (await call("/account/login", { email: "other@example.com", password: "other-strong-pw1" })).body.pending;
                                                                   return call("/account/login2", { pending: p, backup: backup[0] }); }],
  ["victim's code on other's account holding a copy of victim's sealed secret", async () => {
    const p = (await call("/account/login", { email: "other@example.com", password: "other-strong-pw1" })).body.pending;
    return call("/account/login2", { pending: p, code: await hotp(secret, stepAt() + 1) }); }],
  ["victim's device id in other's logout_device", async () => {
    const d = (await call("/account/devices", {}, { token: tok })).body.devices[0].id;
    await run("UPDATE public.accounts SET totp = NULL, fails = 0, locked_until = NULL WHERE email = 'other@example.com'");
    const ot = (await call("/account/login", { email: "other@example.com", password: "other-strong-pw1" })).body.token;
    return call("/account/logout_device", { device: d }, { token: ot }); }],
  ["victim's session token in the body instead of the header", () => call("/account/me", { token: tok })],
  ["other's reset link used for victim (the link names its account)", async () => { const r = await call("/account/reset", { t: resetO, password: "taken-over-pw-1" });
                                                                                     const v = await call("/account/login", { email: "victim@example.com", password: "taken-over-pw-1" });
                                                                                     return { ...r, body: { reset: r.body, victimLogin: v.body } }; }],
];
for (const [name, f] of tries) { const r = await f(); out(`${name}: ${r.status} ${JSON.stringify(r.body).slice(0, 120)}`); }
out("\nmails recorded:", mails.length, "(none sent)");
