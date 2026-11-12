// Local test of referral codes: fake KV, a throwaway key made here, and the worker's real routes.
//   node test_referral.mjs
import assert from "node:assert/strict";
import { generateKeyPairSync, verify } from "node:crypto";
import worker from "./src/index.js";
import { fakeStats } from "./fake_stats.mjs";

const kv = new Map(), md = new Map();
const stats = fakeStats();   // the dashboard's 기록 lives in the Stats object
const { privateKey, publicKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
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
  STATS: stats.binding,
};
const call = (path, init) => worker.fetch(new Request("https://lic.example" + path, init), env);
const adm = async (path, body) => {
  const r = await call("/admin/api" + path, { method: body ? "POST" : "GET", headers: { Authorization: "Bearer " + env.ADMIN_TOKEN, "Content-Type": "application/json" }, body: body ? JSON.stringify(body) : undefined });
  return { status: r.status, ...(await r.json()) };
};
const device = n => String(n).padStart(32, "0");
const use = async (code, n) => {
  const r = await call("/referral", { method: "POST", body: JSON.stringify({ code, device: device(n) }) });
  return { status: r.status, ...(await r.json()) };
};
const payload = key => JSON.parse(Buffer.from(key.split(".")[0], "base64url"));

// the dashboard needs its password
assert.equal((await call("/admin/api/referrals")).status, 401);
assert.equal((await call("/admin/api/referrals", { method: "POST", body: "{}" })).status, 401);

// making codes
assert.match((await adm("/referrals", { code: "ab", days: 3, max: 10 })).error, /4~16/);
assert.match((await adm("/referrals", { code: "BAD CODE", days: 3, max: 10 })).error, /4~16/);
assert.match((await adm("/referrals", { code: "GOOD", days: 0, max: 10 })).error, /1~30/);
assert.match((await adm("/referrals", { code: "GOOD", days: 31, max: 10 })).error, /1~30/);
assert.match((await adm("/referrals", { code: "GOOD", days: 3, max: 0 })).error, /1~10000/);
assert.equal((await adm("/referrals", { code: "friend3", label: "친구", days: 3, max: 2 })).code, "FRIEND3");
assert.match((await adm("/referrals", { code: "FRIEND3", days: 3, max: 2 })).error, /이미/);
const auto = (await adm("/referrals", { plan: "ultra", days: 7, max: 1 })).code;
assert.match(auto, /^[A-Z0-9]{8}$/);

// using one: a real signed key, the right plan and period, counted
let r = await use(" friend3 ", 1);
assert.equal(r.status, 200);
const p = payload(r.key);
assert.equal(p.plan, "premium");
const days = (Date.parse(p.expires) - Date.now()) / 864e5;
assert.ok(days > 2.99 && days < 3.01, String(days));
const [data, sig] = r.key.split(".");
assert.ok(verify("sha256", Buffer.from(data, "base64url"), { key: publicKey, dsaEncoding: "ieee-p1363" }, Buffer.from(sig, "base64url")));
assert.equal((await adm("/referrals")).refs.find(x => x.code === "FRIEND3").used, 1);
const issued = (await adm("/keys", { q: "추천 코드 FRIEND3" })).keys;   // a search goes in the body
assert.equal((await adm("/keys?q=" + encodeURIComponent("추천 코드 FRIEND3"))).status, 400, "not in the address");
assert.equal(issued.length, 1);
assert.equal((await call("/check?id=" + issued[0].id)).status, 200);

// the same device, with this or another code
assert.equal((await use("FRIEND3", 1)).error, "already");
assert.equal((await use(auto, 1)).error, "already");
// the limit
assert.equal((await use("FRIEND3", 2)).status, 200);
assert.equal((await use("FRIEND3", 3)).error, "used up");
// an Ultra code
assert.equal(payload((await use(auto, 4)).key).plan, "ultra");

// off, on, deleted, unknown, junk
// the dashboard shows these replies at once: the list can lag a change by a minute or so
assert.equal((await adm("/referrals", { code: "ONOFF", days: 1, max: 5 })).ref.on, true);
assert.equal((await adm("/referrals/set", { code: "ONOFF", on: false })).ref.on, false);
assert.equal((await use("ONOFF", 5)).error, "unknown");
assert.equal((await adm("/referrals")).refs.find(x => x.code === "ONOFF").on, false);
await adm("/referrals/set", { code: "ONOFF", on: true });
assert.equal((await use("ONOFF", 5)).status, 200);
await adm("/referrals/delete", { code: "ONOFF" });
assert.equal((await use("ONOFF", 6)).error, "unknown");
assert.equal((await use("NOPE1", 7)).error, "unknown");
assert.equal((await use("a b", 7)).status, 400);
assert.equal((await call("/referral", { method: "POST", body: JSON.stringify({ code: "FRIEND3", device: "short" }) })).status, 400);
assert.equal((await call("/referral", { method: "POST", body: "not json" })).status, 400);
assert.equal((await adm("/referrals/set", { code: "ZZZZ", on: true })).status, 404);

// a failed try leaves nothing behind for that device
await adm("/referrals", { code: "LATER", days: 1, max: 5 });
assert.equal((await use("NOPE1", 8)).error, "unknown");
assert.equal((await use("LATER", 8)).status, 200);

// the daily ceiling holds whatever the codes allow
kv.set("count:ref:" + new Date().toISOString().slice(0, 10), "300");
assert.equal((await use("LATER", 9)).error, "busy");

// the audit log has who did what, and no device number
const log = JSON.stringify(await adm("/log"));
assert.ok(log.includes("추천 코드 사용") && log.includes("추천 코드 생성"));
assert.ok(!log.includes(device(1)));
// a code's label says who it was given to: the record keeps its first character only
const made = (await adm("/log")).log.find(l => l.action === "추천 코드 생성" && l.detail.startsWith("FRIEND3"));
assert.equal(made.detail, "FRIEND3 · 3일 · 2회 · 친**");
// the record is in the Stats object, not KV, and an app's line never goes to KV, even with the object down
assert.equal([...kv.keys()].filter(k => k.startsWith("log:")).length, 0);
kv.delete("count:ref:" + new Date().toISOString().slice(0, 10));   // the ceiling test above filled today
stats.down = true;
await adm("/referrals", { code: "DOWN1", days: 1, max: 5 });
assert.equal((await use("DOWN1", 10)).status, 200, "a gift does not need the record");
const kvLog = [...kv.keys()].filter(k => k.startsWith("log:")).map(k => JSON.parse(kv.get(k)));
assert.deepEqual(kvLog.map(l => l.action), ["추천 코드 생성"], "the seller's line goes to KV while the object is down; the app's does not");
stats.down = false;
console.log("referral tests passed");
