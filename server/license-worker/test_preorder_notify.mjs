// The pre-registration Discord alert (src/preorder.js PRE_HOOK, src/stats.js preWake/alarm/preNote): one message of
// counts per batch, never an email address, to a webhook the owner gives that is not in a public channel.
// Fake KV, the real Stats class on node:sqlite with a fake alarm, a fake Discord: nothing leaves this machine.
//   node test_preorder_notify.mjs
import assert from "node:assert/strict";
import { generateKeyPairSync } from "node:crypto";
import worker from "./src/index.js";
import { fakeStats } from "./fake_stats.mjs";
import { DatabaseSync } from "node:sqlite";
import { Stats } from "./src/stats.js";
import { PRE, PRE_HOOK } from "./src/preorder.js";
import { PUBLIC_CHANNEL_IDS } from "./src/announce.js";

const kv = new Map();
const kvEnv = {
  get: async k => kv.get(k) ?? null,
  getWithMetadata: async k => ({ value: kv.get(k) ?? null, metadata: null }),
  put: async (k, v) => { kv.set(k, v); },
  delete: async k => { kv.delete(k); },
  list: async ({ prefix }) => ({ keys: [...kv.keys()].filter(k => k.startsWith(prefix)).map(name => ({ name })), list_complete: true }),
};
let alarmAt = null;   // the one alarm a Durable Object has
const storage = { getAlarm: async () => alarmAt, setAlarm: async ms => { alarmAt = ms; } };
const st = fakeStats(Stats, new DatabaseSync(":memory:"), { storage, env: { LICENSES: kvEnv } });
const { privateKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
const TOKEN = "T".repeat(32);
const env = { LICENSES: kvEnv, LICENSE_PRIVATE_KEY: privateKey.export({ type: "pkcs8", format: "pem" }), ADMIN_TOKEN: TOKEN,
              PUBLIC_URL: "https://lic.example", STATS: st.binding };

// Discord: a webhook is GET to read it (its channel), POST to post; everything else is refused
const FAKE = (id, token) => `https://discord.com/api/webhooks/${id}/${token}`;   // made up (as test_announce.mjs)
const PRIVATE = FAKE("1".repeat(19), "private-Token_1");
const PUBLIC = FAKE("2".repeat(19), "public-Token_2");
const GONE = FAKE("3".repeat(19), "gone-Token_3");
const sent = [];
let postStatus = 204;
globalThis.fetch = async (url, init = {}) => {
  url = String(url);
  const hook = [PRIVATE, PUBLIC, GONE].find(h => url.startsWith(h));
  if (!hook) assert.fail("no other network in this test: " + url);
  if (hook === GONE) return new Response(JSON.stringify({ message: "Unknown Webhook" }), { status: 404 });
  if ((init.method || "GET") === "GET")
    return new Response(JSON.stringify({ id: "1", channel_id: hook === PUBLIC ? PUBLIC_CHANNEL_IDS[0] : "9999999999999999999" }), { status: 200 });
  sent.push({ url, body: JSON.parse(init.body) });
  return new Response(postStatus === 204 ? null : "{}", { status: postStatus });
};

const SITE = "https://doku-web.github.io";
let ip = 0;
const call = (path, init = {}) => worker.fetch(new Request("https://lic.example" + path,
  { ...init, headers: { "CF-Connecting-IP": "10.8." + (++ip & 255) + ".1", ...(init.headers || {}) } }), env);
const j = async (path, init) => { const r = await call(path, init); return { http: r.status, ...(await r.json()) }; };
const pre = email => j("/preorder", { method: "POST", headers: { Origin: SITE, "Content-Type": "application/json" },
                                      body: JSON.stringify({ email, plan: "premium", lang: "ko", consent: true, adConsent: true }) });
const preAs = (email, plan, lang) => j("/preorder", { method: "POST", headers: { Origin: SITE, "Content-Type": "application/json" },
                                                     body: JSON.stringify({ email, plan, lang, consent: true, adConsent: true }) });
const adm = (path, body) => j("/admin/api" + path, { method: body ? "POST" : "GET", headers: { Authorization: "Bearer " + TOKEN, "Content-Type": "application/json" },
                                                      body: body ? JSON.stringify(body) : undefined });
const audit = () => st.rows("SELECT action, detail FROM audit_log ORDER BY rowid").map(r => r.action + " " + r.detail);
let now = Date.parse("2026-11-20T03:00:00Z");
Date.now = () => now;
const fire = async () => { assert.ok(alarmAt != null, "an alarm is set"); now = Math.max(now, alarmAt); alarmAt = null; await st.obj.alarm(); };
const meta = k => st.rows("SELECT v FROM meta WHERE k = ?", k)[0]?.v;

// no webhook yet: a sign-up still answers {ok} and sets one alarm, which posts nothing and passes those rows over
assert.deepEqual([(await pre("a@example.com")).ok], [true]);
assert.equal(alarmAt, now + PRE.noteWait);
await fire();
assert.equal(sent.length, 0);
assert.equal(Number(meta("pre_noted")), st.rows("SELECT MAX(at) AS m FROM preorders")[0].m);
let r = await adm("/preorders");
assert.deepEqual([r.http, r.hook, r.note], [200, false, null]);

// connecting: only a Discord webhook Discord has, and not one in the public server's channels; nothing is posted
for (const [hook, want] of [["https://example.com/api/webhooks/1/x", "디스코드 웹후크 주소가 아니에요"], ["", "디스코드 웹후크 주소가 아니에요"],
                            [GONE, "디스코드에 없는 웹후크예요"], [PUBLIC, /모두가 보는 채널/]]) {
  r = await adm("/preorders/hook", { hook });
  assert.equal(r.http, 400, hook);
  if (want instanceof RegExp) assert.match(r.error, want); else assert.equal(r.error, want);
}
assert.equal(kv.has(PRE_HOOK), false);
assert.equal(sent.length, 0);
now += 60e3;
await pre("b@example.com");   // came before the webhook: not sent as new once it is connected
r = await adm("/preorders/hook", { hook: "  " + PRIVATE + " " });
assert.deepEqual([r.http, r.ok, r.hook], [200, true, true]);
assert.equal(kv.get(PRE_HOOK), PRIVATE);
assert.ok(audit().includes("사전예약 디스코드 알림 연결"));
r = await adm("/preorders");
assert.equal(r.hook, true);
assert.ok(!JSON.stringify(r).includes("private-Token_1") && !JSON.stringify(r).includes("1111111111111111111"), "the page never gets the address back");
await fire();
assert.equal(sent.length, 0, "rows from before the connection are not new");

// a test post: plain text that pings nobody
r = await adm("/preorders/hook/test", {});
assert.deepEqual([r.http, r.ok], [200, true]);
assert.equal(sent.length, 1);
assert.deepEqual(sent[0].body.allowed_mentions, { parse: [] });
assert.equal(sent[0].body.username, "SystemBoost");
assert.match(sent[0].body.content, /시험 알림/);
sent.length = 0;

// three sign-ups (one address twice, one already there): one alarm, one message of counts, no address in it
now += 60e3;
await preAs("c@example.com", "ultra", "en");
const first = alarmAt;
now += 5e3;
await preAs("d@example.com", "premium", "ko");
await preAs("c@example.com", "ultra", "en");   // already on the list: no new row
await preAs("a@example.com", "free", "ja");    // the same
assert.equal(alarmAt, first, "one alarm for the batch");
await fire();
assert.equal(sent.length, 1);
const text = sent[0].body.content;
assert.match(text, /새 신청 2건/);
assert.match(text, /울트라 1/); assert.match(text, /프로 1/);
assert.match(text, /언어: 한국어 2/, "every row is stored as ko, whatever the site sent"); assert.ok(!/영어/.test(text));
assert.match(text, /모두 4명/);
assert.ok(!/@|example/.test(text), "never an email address");
assert.deepEqual(sent[0].body.allowed_mentions, { parse: [] });
await st.obj.alarm();   // nothing new: nothing sent
assert.equal(sent.length, 1);
sent.length = 0;

// Discord fails: the error is kept (no address in it), tried again 10 minutes later, and the rows go in the next post
now += 60e3;
postStatus = 500;
await preAs("e@example.com", "biz", "de");
await fire();
assert.equal(sent.length, 1);
let err = JSON.parse(meta("pre_note_err"));
assert.equal(err.gone, false);
assert.equal(alarmAt, now + PRE.noteRetry);
r = await adm("/preorders");
assert.equal(r.note.gone, false);
assert.match(r.note.error, /500/);
assert.ok(!JSON.stringify(r).includes("private-Token_1"));
postStatus = 204;
await preAs("f@example.com", "free", "fr");   // an alarm is set already: this row joins it
await fire();
assert.equal(sent.length, 2);
assert.match(sent[1].body.content, /새 신청 2건/);
assert.equal(meta("pre_note_err"), undefined, "a post that worked clears the error");
sent.length = 0;

// failing for a day: no more tries until the next sign-up
postStatus = 500;
now += 60e3;
await preAs("g@example.com", "free", "ko");
await fire();
now = JSON.parse(meta("pre_note_err")).since + PRE.noteGiveUp + 1;
alarmAt = null;
await st.obj.alarm();
assert.equal(alarmAt, null, "given up after a day");
postStatus = 204;

// a webhook Discord no longer has: "gone", no retry; connecting again starts from now and clears it
kv.set(PRE_HOOK, GONE);
await preAs("h@example.com", "free", "ko");
const before = sent.length;
await fire();
assert.equal(sent.length, before);
assert.equal(JSON.parse(meta("pre_note_err")).gone, true);
assert.equal(alarmAt, null);
r = await adm("/preorders/hook/test", {});
assert.deepEqual([r.http, r.error], [400, "디스코드에 없는 웹후크예요. 다시 연결해 주세요"]);
r = await adm("/preorders/hook", { hook: PRIVATE });
assert.equal(r.ok, true);
assert.equal(meta("pre_note_err"), undefined);
await st.obj.alarm();
assert.equal(sent.length, before, "rows from before the new connection are not sent");

// turning it off deletes the address; sign-ups then post nothing
r = await adm("/preorders/hook/clear", {});
assert.deepEqual([r.http, r.ok, r.hook], [200, true, false]);
assert.equal(kv.has(PRE_HOOK), false);
assert.ok(audit().includes("사전예약 디스코드 알림 끊음"));
r = await adm("/preorders/hook/test", {});
assert.deepEqual([r.http, r.error], [400, "연결된 웹후크가 없어요"]);
await preAs("i@example.com", "free", "ko");
await fire();
assert.equal(sent.length, before);

// no log line or audit row carries a webhook
assert.ok(!audit().some(x => x.includes("discord.com")));

// at PRE.deleteAt the purge takes the alert's state with the rows
now = Date.parse(PRE.deleteAt);
await st.obj.alarm();
assert.equal(meta("pre_noted"), undefined);
assert.equal(st.rows("SELECT COUNT(*) AS n FROM preorders")[0].n, 0);

// where storage has no alarms (the other tests' fake), a sign-up works as before
now = Date.parse("2026-11-21T00:00:00Z");
const plain = fakeStats();
assert.deepEqual(await (await plain.binding.get().fetch("https://stats/pre/add", { method: "POST", body: JSON.stringify({
  id: "0".repeat(32), email: "z@example.com", plan: "free", lang: "en", ip: "0".repeat(16), net: "1".repeat(16) }) })).json(), { ok: true });

console.log("preorder notify: all passed");
