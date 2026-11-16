// 0.8.14: the seller's Discord alert for bank transfers (src/seller_alert.js, secret ADMIN_ALERT_WEBHOOK).
// A buyer's first "I paid" sends one message with the order code, the plan and the time left, nothing about the
// buyer; a failing Discord never fails the request; the hourly cron reminds once, DEPOSIT_WARN_HOURS (20) after the
// first "I paid", with no KV write for the bookkeeping. Global fetch is a fake: nothing leaves this machine.
//   node test_alert.mjs
import assert from "node:assert/strict";
import { generateKeyPairSync } from "node:crypto";
import worker from "./src/index.js";
import { dueForReminder } from "./src/seller_alert.js";

const kv = new Map(), md = new Map();
let puts = 0, lists = 0;
const { privateKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
// a made-up address of the right shape, built here so no webhook address is ever written out whole
const HOOK = "https://discord.com/api/webhooks/" + "4242" + "/test-only-not-a-real-token";
const env = {
  LICENSES: {
    get: async k => kv.get(k) ?? null,
    getWithMetadata: async k => ({ value: kv.get(k) ?? null, metadata: md.get(k) ?? null }),
    put: async (k, v, o) => { puts++; kv.set(k, v); md.set(k, o?.metadata ?? null); },
    delete: async k => { kv.delete(k); md.delete(k); },
    list: async ({ prefix, limit = 1000 }) => { lists++; return { keys: [...kv.keys()].filter(k => k.startsWith(prefix)).sort().slice(0, limit).map(name => ({ name, metadata: md.get(name) })), list_complete: true }; },
  },
  LICENSE_PRIVATE_KEY: privateKey.export({ type: "pkcs8", format: "pem" }),
  ADMIN_TOKEN: "T".repeat(32),
  SALES_MODE: "test",
  ADMIN_ALERT_WEBHOOK: HOOK,
};
kv.set("config:pay_urls", JSON.stringify({ yearly: "https://pay.example/yearly", ultra_monthly: "https://pay.example/um" }));

const sent = [];
let discordAnswer = () => new Response(null, { status: 204 });
globalThis.fetch = async (url, init = {}) => {
  url = String(url);
  assert.equal(url, HOOK, "only the seller's Discord is called: " + url);
  sent.push({ init, body: JSON.parse(init.body) });
  return discordAnswer();
};

// ctx like the Workers runtime's: waitUntil keeps the promise; the test awaits them
const pending = [];
const ctx = { waitUntil: p => { pending.push(Promise.resolve(p).catch(() => {})); } };
const settle = async () => { while (pending.length) await pending.shift(); };
let ipN = 0;
const call = (path, init = {}, c = ctx) => worker.fetch(new Request("https://lic.example" + path, { ...init, headers: { "CF-Connecting-IP": "10.3.0." + (++ipN % 250), ...(init.headers || {}) } }), env, c);
const j = async (path, init, c) => { const r = await call(path, init, c); return { http: r.status, ...(await r.json()) }; };
const order = async plan => (await j(`/order?plan=${plan}&method=manual`, { method: "POST" })).code;
const PAYER = "김결제", MEMO = "국민은행 49,900원", EMAIL = "buyer@example.com";
const paid = (code, payer = PAYER, c) => j("/order/paid", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ code, payer, msg: MEMO, email: EMAIL }) }, c);

// 1. the first "I paid": one message, the code, the plan and the time left; nothing about the buyer
const A = await order("yearly");
let p0 = puts;
assert.equal((await paid(A)).status, "paid");
await settle();
assert.equal(sent.length, 1);
const msg = sent[0].body;
assert.deepEqual(Object.keys(msg).sort(), ["allowed_mentions", "content"]);
assert.deepEqual(msg.allowed_mentions, { parse: [] }, "it can never ping anyone");
assert.ok(msg.content.includes(A) && msg.content.includes("프로 연간") && msg.content.includes("24시간 안에 확인"), msg.content);
for (const secret of [PAYER, MEMO, EMAIL, "10.3.0.", "@", "김"]) assert.ok(!msg.content.includes(secret), "no " + secret + ": " + msg.content);
assert.equal(sent[0].init.method, "POST");
assert.equal(puts - p0, 1, "only the order's own write: nothing written for the alert");

// a changed payer note later: no second message
assert.equal((await paid(A, "김결제2")).status, "paid");
await settle();
assert.equal(sent.length, 1);

// 2. Discord down, slow to fail or refusing: the buyer's "I paid" still succeeds
for (const answer of [() => { throw new Error("network down"); }, () => new Response("no", { status: 500 }), () => new Response("{}", { status: 429 })]) {
  discordAnswer = answer;
  const c = await order("ultra_monthly");
  const r = await paid(c);
  await settle();
  assert.deepEqual([r.http, r.status], [200, "paid"]);
  assert.equal(md.get("ord:" + c).status, "paid");
}
discordAnswer = () => new Response(null, { status: 204 });
// without ctx (no waitUntil) the alert is awaited and still cannot fail the request
discordAnswer = () => { throw new Error("down"); };
const noCtx = await order("yearly");
assert.deepEqual([(await paid(noCtx, PAYER, null)).status], ["paid"]);
discordAnswer = () => new Response(null, { status: 204 });

// 3. no webhook, or something that is not a Discord webhook address: nothing is sent
for (const h of [undefined, "", "https://example.com/api/webhooks/1/x", "http://discord.com/api/webhooks/" + "1/x", HOOK + "/../x"]) {
  env.ADMIN_ALERT_WEBHOOK = h;
  const n = sent.length, c = await order("yearly");
  assert.equal((await paid(c)).status, "paid");
  await settle();
  assert.equal(sent.length, n, String(h));
}
env.ADMIN_ALERT_WEBHOOK = HOOK;

// 4. the hourly cron: once, in the hour from 20 hours after the first "I paid"
for (const k of [...kv.keys()].filter(k => k.startsWith("ord:"))) { kv.delete(k); md.delete(k); }
const at = Date.parse("2026-12-10T05:07:00Z");   // a run's scheduled time (cron "7 * * * *")
const ago = h => new Date(at - h * 36e5).toISOString().replace(/\.\d+Z$/, "Z");
const put = (code, status, paidAgo, plan = "yearly") => { kv.set("ord:" + code, ""); md.set("ord:" + code, { plan, status, at: ago(paidAgo + 1), paidAt: ago(paidAgo), payer: PAYER, msg: MEMO, email: EMAIL }); };
put("SB-AAAA-AAAA", "paid", 19.9);
put("SB-BBBB-BBBB", "paid", 20);
put("SB-CCCC-CCCC", "paid", 20.95, "ultra_monthly");
put("SB-DDDD-DDDD", "paid", 21);
put("SB-EEEE-EEEE", "approved", 20.5);
put("SB-FFFF-FFFF", "rejected", 20.5);
const cron = async t => { const before = sent.length; p0 = puts; await worker.scheduled({ scheduledTime: t, cron: "7 * * * *" }, env, ctx); await settle(); assert.equal(puts, p0, "the cron writes no KV"); return sent.slice(before); };
let out = await cron(at);
assert.equal(out.length, 1, "one message for the run");
let text = out[0].body.content;
assert.ok(text.includes("SB-BBBB-BBBB") && text.includes("SB-CCCC-CCCC") && text.includes("울트라 월간"), text);
for (const c of ["SB-AAAA-AAAA", "SB-DDDD-DDDD", "SB-EEEE-EEEE", "SB-FFFF-FFFF", PAYER, MEMO, EMAIL]) assert.ok(!text.includes(c), c);
// the next run, an hour later: the order that was at 19.9 hours now, nothing again
out = await cron(at + 36e5);
assert.equal(out.length, 1);
text = out[0].body.content;
assert.ok(text.includes("SB-AAAA-AAAA") && !text.includes("SB-BBBB-BBBB") && !text.includes("SB-CCCC-CCCC"), text);
// and nothing the hour after
assert.equal((await cron(at + 2 * 36e5)).length, 0);
// every paid order is reminded in exactly one run of a day of hourly runs
const orders = [0.01, 19.99, 20, 20.5, 20.999, 21, 23.5].map((h, i) => ({ code: "X" + i, plan: "p", paidAt: ago(-h) }));
const counts = new Map();
for (let r = 0; r < 60; r++) for (const o of dueForReminder(orders, at + r * 36e5)) counts.set(o.code, (counts.get(o.code) || 0) + 1);
assert.deepEqual([...counts.values()], orders.map(() => 1), "once each");

// without a webhook the cron does not even list the orders
env.ADMIN_ALERT_WEBHOOK = "";
const l0 = lists;
assert.equal((await cron(at)).length, 0);
assert.equal(lists, l0, "no KV list without a channel");
env.ADMIN_ALERT_WEBHOOK = HOOK;
// Discord failing in the cron: no throw
discordAnswer = () => { throw new Error("down"); };
await cron(at);

console.log("test_alert: all passed");
