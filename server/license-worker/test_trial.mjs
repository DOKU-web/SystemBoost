// 0.8.14: POST /trial gives one 3-day Ultra key per PC (the app sends a 64-hex hash of the PC).
// First call: a key; the same PC again: 409 "already"; the day's public KV share or TRIAL_DAILY used up: 429 "busy";
// sales mode off or env TRIAL = "off": 503 "closed"; a bad body: 400 "bad request". Two KV writes per trial, counted
// in the Stats object; while the object is down a KV count caps the day instead.
// Fake KV, fake Stats object: nothing leaves this machine.
//   node test_trial.mjs
import assert from "node:assert/strict";
import { createHash, generateKeyPairSync, verify } from "node:crypto";
import worker from "./src/index.js";
import { fakeStats } from "./fake_stats.mjs";
import { _forgetIpLimits } from "./src/iplimit.js";

const kv = new Map(), md = new Map(), ttl = new Map();
let puts = 0;
const st = fakeStats();
const { privateKey, publicKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
const env = {
  LICENSES: {
    get: async k => kv.get(k) ?? null,
    getWithMetadata: async k => ({ value: kv.get(k) ?? null, metadata: md.get(k) ?? null }),
    put: async (k, v, o) => { puts++; kv.set(k, v); md.set(k, o?.metadata ?? null); ttl.set(k, o?.expirationTtl); },
    delete: async k => { kv.delete(k); md.delete(k); },
    list: async ({ prefix, limit = 1000 }) => ({ keys: [...kv.keys()].filter(k => k.startsWith(prefix)).sort().slice(0, limit).map(name => ({ name, metadata: md.get(name) })), list_complete: true }),
  },
  LICENSE_PRIVATE_KEY: privateKey.export({ type: "pkcs8", format: "pem" }),
  ADMIN_TOKEN: "T".repeat(32),
  SALES_MODE: "live",
  STATS: st.binding,
};

let ipN = 0;
const call = (path, init = {}, ip) => worker.fetch(new Request("https://lic.example" + path, { ...init, headers: { "CF-Connecting-IP": ip || "10.6." + (ipN >> 8 & 255) + "." + (++ipN & 255), ...(init.headers || {}) } }), env);
const j = async (path, init, ip) => { const r = await call(path, init, ip); return { http: r.status, ...(await r.json()) }; };
const trial = (device, ip) => j("/trial", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ device }) }, ip);
const dev = i => createHash("sha256").update("made-up-salt" + i).digest("hex");
const day = () => new Date().toISOString().slice(0, 10);
const n = name => Number(st.obj.peek({ names: [name] }).n[name]) || 0;
const kid = key => createHash("sha256").update(key).digest("hex").slice(0, 16);
const summary = async () => (await j("/admin/api/summary", { headers: { Authorization: "Bearer " + env.ADMIN_TOKEN } })).counts;
const pub = publicKey;
const verifies = key => {
  const [p, s] = key.split(".");
  return verify("sha256", Buffer.from(p, "base64url"), { key: pub, dsaEncoding: "ieee-p1363" }, Buffer.from(s, "base64url"));
};

// 1. the first trial of a PC: an Ultra key for about 3 days, signed like every key, 2 KV writes
let p0 = puts;
const t1 = await trial(dev(1));
assert.equal(t1.http, 200, JSON.stringify(t1));
assert.deepEqual(Object.keys(t1), ["http", "key"], "only the key comes back");
assert.ok(verifies(t1.key), "the key verifies with the server's public key");
const pl = JSON.parse(Buffer.from(t1.key.split(".")[0], "base64url"));
assert.equal(pl.plan, "ultra");
assert.equal(pl.email, "");
const daysLeft = (Date.parse(pl.expires) - Date.now()) / 864e5;
assert.ok(daysLeft > 2.99 && daysLeft <= 3, "about 3 days: " + daysLeft);
assert.equal(puts - p0, 2, "two KV writes: trialdev: and lic:");
assert.equal(kv.get("trialdev:" + dev(1)) !== undefined, true);
assert.equal(ttl.get("trialdev:" + dev(1)), 400 * 86400, "the PC hash is kept 400 days");
const meta = md.get("lic:" + kid(t1.key));
assert.equal(meta.src, "trial");
assert.equal(meta.note, "3일 체험");
assert.equal(meta.plan, "ultra");
assert.deepEqual([n("kv:pub:" + day()), n("kv:trial:" + day())], [2, 1], "counted in the public share and in today's trials");
assert.equal(kv.has("count:trial:" + day()), false, "no KV write for counting");
assert.equal((await j("/check?id=" + kid(t1.key))).revoked, false);

// 2. the same PC again: 409, nothing written; another PC: a key
p0 = puts;
const again = await trial(dev(1));
assert.deepEqual([again.http, again.error], [409, "already"]);
assert.equal(puts, p0);
assert.equal((await trial(dev(2))).http, 200);

// 3. a bad body
for (const d of [undefined, "", dev(3).toUpperCase(), dev(3).slice(1), dev(3) + "0", "g".repeat(64), 123, [dev(3)]]) {
  const r = await trial(d);
  assert.deepEqual([r.http, r.error], [400, "bad request"], String(d));
}
assert.deepEqual([(await j("/trial", { method: "POST", body: "not json" })).error], ["bad request"]);

// 4. the dashboard counts trials: today and in all
let c = await summary();
assert.deepEqual([c.trialsToday, c.trials], [2, 2]);

// 5. the per-address limit (10 an hour)
_forgetIpLimits();
for (let i = 0; i < 10; i++) assert.equal((await trial(dev(100 + i), "10.5.5.5")).http, 200, "call " + i);
const ip11 = await trial(dev(200), "10.5.5.5");
assert.deepEqual([ip11.http, ip11.error], [429, "busy"]);
assert.equal(kv.has("trialdev:" + dev(200)), false, "nothing written for a refused call");

// 6. the day's public KV share used up: 429, nothing written, and the PC can try again tomorrow
st.obj.take({ items: [{ name: "kv:pub:" + day(), ttl: 172800, by: 400 - n("kv:pub:" + day()) - 1 }] });
p0 = puts;
const full = await trial(dev(300));
assert.deepEqual([full.http, full.error], [429, "busy"], "one write left of 400: a trial needs 2");
assert.equal(puts, p0);
assert.equal(kv.has("trialdev:" + dev(300)), false);
st.db.exec("DELETE FROM counters WHERE name LIKE 'kv:%'");   // a new day
// TRIAL_DAILY (150) trials a day, whatever the public share
st.obj.take({ items: [{ name: "kv:trial:" + day(), ttl: 172800, by: 150 }] });
const cap = await trial(dev(301));
assert.deepEqual([cap.http, cap.error], [429, "busy"]);
st.db.exec("DELETE FROM counters WHERE name LIKE 'kv:%'");

// 7. the Stats object down: a KV count caps the day instead
st.down = true;
const d1 = await trial(dev(400));
assert.equal(d1.http, 200, JSON.stringify(d1));
assert.equal(kv.get("count:trial:" + day()), "1");
kv.set("count:trial:" + day(), "150");
assert.deepEqual([(await trial(dev(401))).error], ["busy"]);
st.down = false;
kv.delete("count:trial:" + day());

// 8. closed: sales mode off (the dashboard's or wrangler's), or TRIAL = "off"
await j("/admin/api/sales-mode", { method: "POST", headers: { Authorization: "Bearer " + env.ADMIN_TOKEN, "Content-Type": "application/json" }, body: JSON.stringify({ mode: "off" }) });
let off = await trial(dev(500));
assert.deepEqual([off.http, off.error], [503, "closed"]);
await j("/admin/api/sales-mode", { method: "POST", headers: { Authorization: "Bearer " + env.ADMIN_TOKEN, "Content-Type": "application/json" }, body: JSON.stringify({ mode: "test" }) });
assert.equal((await trial(dev(501))).http, 200, "test mode gives trials too");
env.TRIAL = "off";
off = await trial(dev(502));
assert.deepEqual([off.http, off.error], [503, "closed"]);
delete env.TRIAL;
assert.equal(kv.has("trialdev:" + dev(500)) || kv.has("trialdev:" + dev(502)), false);

c = await summary();
assert.equal(c.trials, 14, "2 + 10 + 1 (object down) + 1 (test mode)");

console.log("test_trial: all passed");
