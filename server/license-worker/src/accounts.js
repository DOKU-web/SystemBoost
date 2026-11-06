// Member accounts. They live in Supabase: supabase/accounts.sql holds the tables and the one function
// this file calls. Only this Worker goes there, with the secret key; the app never does.
//
// A password is mixed here with a pepper from the signing key and hashed with bcrypt in the database,
// so the hash costs a free-plan Worker no CPU (it gets 10 ms a request) and the database alone is not
// enough to try passwords. Replacing the signing key therefore ends every password and recovery code.
// IP addresses leave this file only as a one-way value, kept for an hour.
const CONSENT = "2026-11-06";   // the sign-up text the member agreed to
export const INVITE_DAYS = 3;   // Premium for whoever signs up with a member's code
const LIMITS = {
  rewardDays: 3,                // Premium for the member who invited them
  rewardCap: 30,                // the most days one member can earn
  ticketPercent: 10,            // discount for whoever signs up with a code
  ticketDays: 30,               // how long that ticket lasts
  inviteMax: 50,                // sign-ups on one member's code
  sessionDays: 90, sessionsKept: 5,
  loginTries: 5, lockMin: 15,
  signupsPerIp: 5, signupsPerHour: 100, failsPerIp: 20,
};
const TIMEOUT_MS = 10000;
const ID_RE = /^[a-z0-9_]{4,20}$/;
const CODE_RE = /^[A-Z0-9]{4,16}$/;
const TOKEN_RE = /^[0-9a-f]{64}$/;
const RESERVED = ["admin", "administrator", "systemboost", "dokku", "support", "root"];
const ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";   // no 0/O/1/I: easy to read aloud
const ADMIN = ["admin/list", "admin/block", "admin/delete"];

const enc = new TextEncoder();
const hex = bytes => [...new Uint8Array(bytes)].map(x => x.toString(16).padStart(2, "0")).join("");
const random = n => crypto.getRandomValues(new Uint8Array(n));
const sha = async text => hex(await crypto.subtle.digest("SHA-256", enc.encode(text)));
const code = n => [...random(n)].map(x => ALPHABET[x % ALPHABET.length]).join("");
const fail = (status, error) => ({ status, error });
const cleanId = v => String(v || "").trim().toLowerCase();
const goodPassword = (p, id) => p.length >= 8 && p.length <= 72 && p !== id;
const newRecovery = () => code(16).match(/.{4}/g).join("-");
const newToken = () => hex(random(32));

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
    console.log("supabase", action, res.status);
  } catch (e) {
    console.log("supabase", action, e.name);
  }
  return fail(503, "unavailable");
}

async function secrets(env) {
  const pepper = await sha("systemboost-account|" + env.LICENSE_PRIVATE_KEY);
  return {
    password: text => sha(pepper + "|password|" + text),
    proof: recovery => sha(pepper + "|recovery|" + String(recovery).replace(/[^A-Z0-9]/gi, "").toUpperCase()),
    ip: async ip => (await sha(pepper + "|ip|" + ip)).slice(0, 32),
  };
}

export async function accounts(env, action, b) {
  const s = await secrets(env);
  switch (action) {
    case "ping": return db(env, "ping", {});
    case "code": return CODE_RE.test(b.code || "") ? db(env, "code", { code: b.code }) : { member: false };
    case "signup": return signup(env, s, b);
    case "login": return login(env, s, b);
    case "recover": return recover(env, s, b);
    case "ticket/used":
    case "ticket/release": return db(env, action, { order: b.order });
    case "admin/recovery": {
      const recovery = newRecovery(), id = cleanId(b.id);
      const r = await db(env, action, { id, proof: await s.proof(recovery) });
      return r.error ? r : { recovery };
    }
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
    case "password": return changePassword(env, s, token, b);
    case "delete": return db(env, "delete", { token, pw: await s.password(String(b.password || "").slice(0, 100)) });
  }
  return fail(404, "not found");
}

async function signup(env, s, b) {
  const id = cleanId(b.id), password = String(b.password || "");
  if (!ID_RE.test(id) || RESERVED.includes(id)) return fail(400, "id");
  if (!goodPassword(password, id)) return fail(400, "password");
  if (b.agree !== true || b.age14 !== true) return fail(400, "consent");
  const token = newToken(), recovery = newRecovery();
  const r = await db(env, "signup", { id, pw: await s.password(password), proof: await s.proof(recovery), session: await sha(token),
                                      invite: String(b.invite || ""), ticket: !!b.ticket, ip: await s.ip(b.ip), consent: CONSENT + " " + new Date().toISOString() });
  return r.error ? r : { id, token, recovery };
}

async function login(env, s, b) {
  const id = cleanId(b.id), token = newToken();
  const r = await db(env, "login", { id, pw: await s.password(String(b.password || "").slice(0, 100)), ip: await s.ip(b.ip), session: await sha(token) });
  return r.error ? r : { id, token };
}

async function recover(env, s, b) {
  const id = cleanId(b.id), next = String(b.next || "");
  if (!goodPassword(next, id)) return fail(400, "password");
  const token = newToken(), recovery = newRecovery();
  const r = await db(env, "recover", { id, ip: await s.ip(b.ip), proof: await s.proof(b.recovery || ""), next: await s.password(next),
                                       nextProof: await s.proof(recovery), session: await sha(token) });
  return r.error ? r : { id, token, recovery };
}

async function changePassword(env, s, session, b) {
  const me = await db(env, "me", { token: session });
  if (me.error) return me;
  const next = String(b.next || "");
  if (!goodPassword(next, me.id)) return fail(400, "password");
  const token = newToken(), recovery = newRecovery();
  const r = await db(env, "password", { token: session, pw: await s.password(String(b.password || "").slice(0, 100)), next: await s.password(next),
                                        nextProof: await s.proof(recovery), session: await sha(token) });
  return r.error ? r : { id: me.id, token, recovery };
}
