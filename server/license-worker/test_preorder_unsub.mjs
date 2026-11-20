// The pre-registration unsubscribe page (src/preorder.js preorderUnsubscribe, Stats /pre/unsub): one email field,
// the same answer for an address on the list and one that is not, an audit line without the address, a per-address
// limit, open past the sign-up deadline until 2027-02-28 23:59:59 KST, then closed.
// Fake KV, the real Stats class on node:sqlite; global fetch refuses everything: nothing leaves this machine.
//   node test_preorder_unsub.mjs
import assert from "node:assert/strict";
import { generateKeyPairSync } from "node:crypto";
import worker from "./src/index.js";
import { fakeStats } from "./fake_stats.mjs";
import { PRE, preId } from "./src/preorder.js";

const kv = new Map();
const st = fakeStats();
const { privateKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
const TOKEN = "T".repeat(32);
const env = {
  LICENSES: {
    get: async k => kv.get(k) ?? null,
    getWithMetadata: async k => ({ value: kv.get(k) ?? null, metadata: null }),
    put: async (k, v) => { kv.set(k, v); },
    delete: async k => { kv.delete(k); },
    list: async ({ prefix }) => ({ keys: [...kv.keys()].filter(k => k.startsWith(prefix)).map(name => ({ name })), list_complete: true }),
  },
  LICENSE_PRIVATE_KEY: privateKey.export({ type: "pkcs8", format: "pem" }),
  ADMIN_TOKEN: TOKEN,
  PUBLIC_URL: "https://lic.example",
  STATS: st.binding,
};
globalThis.fetch = async url => assert.fail("no network in this test: " + url);
// nothing this test does may print an address
const logged = [];
for (const k of ["log", "info", "warn", "error", "debug"]) { const orig = console[k]; console[k] = (...a) => { logged.push(a.join(" ")); if (k !== "debug") orig(...a); }; }

const SITE = "https://doku-web.github.io";
let ipN = 0;
const call = (path, init = {}, addr) => worker.fetch(new Request("https://lic.example" + path,
  { ...init, headers: { "CF-Connecting-IP": addr || "10.7." + (++ipN & 255) + ".1", ...(init.headers || {}) } }), env);
const signUp = async email => (await call("/preorder", { method: "POST", headers: { Origin: SITE, "Content-Type": "application/json" },
  body: JSON.stringify({ email, plan: "free", lang: "en", consent: true, adConsent: true }) })).json();
const unsub = async (body, addr, type = "application/x-www-form-urlencoded") => {
  const r = await call("/preorder/unsubscribe", { method: "POST", headers: { "Content-Type": type }, body }, addr);
  return { status: r.status, headers: r.headers, text: await r.text() };
};
const form = email => "email=" + encodeURIComponent(email);
const rows = () => st.rows("SELECT email FROM preorders ORDER BY at").map(r => r.email);
const audit = () => st.rows("SELECT action, detail FROM audit_log ORDER BY seq").map(r => r.action + " " + r.detail);
const noNonce = t => t.replace(/nonce="[^"]+"/g, 'nonce="N"');
const realNow = Date.now;
let now = Date.parse("2026-11-28T03:00:00Z");   // after the 11-27 sign-up deadline
Date.now = () => now;

// rows: two taken before the deadline
now = Date.parse("2026-11-20T03:00:00Z");
assert.equal((await signUp("fan@example.com")).ok, true);
assert.equal((await signUp("Other.Person@Example.com")).ok, true);
assert.deepEqual(rows(), ["fan@example.com", "other.person@example.com"]);
now = Date.parse("2026-11-28T03:00:00Z");
assert.equal((await (await call("/preorder")).json()).open, false, "sign-ups are closed now");

// GET: the page, Korean and English, one email input and one button, nothing else asked
let g = await call("/preorder/unsubscribe");
let html = await g.text();
assert.equal(g.status, 200);
assert.equal(g.headers.get("content-type"), "text/html; charset=utf-8");
const csp = g.headers.get("content-security-policy");
assert.ok(csp.startsWith("default-src 'none';") && csp.includes("form-action 'self'") && !csp.includes("script-src"), csp);
assert.equal(g.headers.get("x-frame-options"), "DENY");
assert.equal(g.headers.get("cache-control"), "no-store");
assert.ok(!/<script/i.test(html), "no script");
assert.equal((html.match(/<input\b/g) || []).length, 1, "one input");
assert.ok(/<input id="email" name="email" type="email"/.test(html));
assert.equal((html.match(/<button\b/g) || []).length, 1, "one button");
assert.ok(html.includes("수신 거부 / Unsubscribe"));
assert.ok(html.includes('<form method="post" action="/preorder/unsubscribe">'));
for (const w of ["사전예약 수신 거부", "보내는 사람: Riye", "광고성 메일을 보내지 않아요", "Unsubscribe from pre-registration mail", "Sender: Riye", "no launch notice or other advertising email"])
  assert.ok(html.includes(w), "the page says: " + w);
const nonce = /style-src 'nonce-([^']+)'/.exec(csp)[1];
assert.ok(html.includes('<style nonce="' + nonce + '">'));

// POST: an address on the list is deleted; the answer names Riye, the KST date and the result, in both languages
now = Date.parse("2026-11-28T16:30:00Z");   // 2026-11-29 01:30 KST: the Korean date is the 29th
const known = await unsub(form("  FAN@example.com "));
assert.equal(known.status, 200);
assert.deepEqual(rows(), ["other.person@example.com"], "the row is gone");
for (const w of ["수신 거부를 처리했어요", "보낸 사람", "Riye (SystemBoost 판매자)", "요청한 날짜", "2026년 11월 29일 (한국 시간)", "처리 결과",
                 "사전예약 정보를 지웠어요", "앞으로 이 주소로 사전예약 · 출시 알림 등 광고성 메일을 보내지 않아요",
                 "Your unsubscribe request is done", "Sender", "Riye (seller of SystemBoost)", "Date of request", "November 29, 2026 (Korean time)",
                 "has been deleted", "will be sent to it again"])
  assert.ok(known.text.includes(w), "the answer says: " + w);
assert.ok(!known.text.includes("fan@example.com") && !known.text.includes("FAN@"), "the address is not echoed");
assert.ok(!/<input\b/.test(known.text), "no form in the answer");

// an address that is not on the list (and the same one again): the very same answer, nothing else changes
const unknown = await unsub(form("nobody@example.com"));
const again = await unsub(form("fan@example.com"));
assert.equal(unknown.status, known.status);
assert.equal(noNonce(unknown.text), noNonce(known.text), "the same page for an address that is not on the list");
assert.equal(noNonce(again.text), noNonce(known.text), "and for one already gone");
assert.deepEqual([...unknown.headers].filter(([k]) => k !== "content-security-policy"), [...known.headers].filter(([k]) => k !== "content-security-policy"), "the same headers");
assert.deepEqual(rows(), ["other.person@example.com"]);

// the audit log: one line for the one row that went, never the address (nor any part of it)
assert.deepEqual(audit().filter(a => a.startsWith("사전예약 수신 거부")), ["사전예약 수신 거부 1건"]);
const auditAll = JSON.stringify(st.rows("SELECT * FROM audit_log"));
assert.ok(!/@|fan|nobody|example/i.test(auditAll), "no address in the audit log");
const adminLog = await (await call("/admin/api/log", { headers: { Authorization: "Bearer " + TOKEN } })).json();
assert.ok(adminLog.log.some(l => l.action === "사전예약 수신 거부" && l.detail === "1건"), "the dashboard's 기록 shows it");
assert.ok(!logged.some(l => /@/.test(l)), "no address in console output");
// the counters hold salted hashes, never an address
const names = st.rows("SELECT name FROM counters WHERE name LIKE 'pre:unsub:%'").map(r => r.name);
assert.ok(names.length > 0 && names.every(n => /^pre:unsub:(ip|net):[0-9a-f]{16}:\d{4}-\d\d-\d\dT\d\d:\d\d$/.test(n)), JSON.stringify(names));

// a bad address: the form again with a bilingual error (no lookup, nothing counted); old-style stored addresses are taken
for (const bad of ["", "not-mail", "a b@example.com"]) {
  const r = await unsub(form(bad));
  assert.equal(r.status, 400, bad);
  assert.ok(r.text.includes("메일 주소를 확인해 주세요.") && r.text.includes("Please check the email address.") && /<input\b/.test(r.text), bad);
}
assert.equal((await unsub("")).status, 400, "no body");
assert.equal((await unsub("x".repeat(PRE.body + 10))).status, 400, "a body over the limit is not read");
st.db.prepare("INSERT INTO preorders (id, email, plan, lang, at) VALUES (?, ?, ?, ?, ?)").run(await preId("o'old,one@example.com"), "o'old,one@example.com", "free", "en", now);
assert.equal((await unsub(form("O'Old,One@Example.com"))).status, 200);
assert.ok(!rows().includes("o'old,one@example.com"), "a row the first version took is found too");
// other methods
assert.equal((await call("/preorder/unsubscribe", { method: "PUT", body: "x" })).status, 405);

// the limit: PRE.unsubIpHour an hour from one address, PRE.unsubNetHour from one network, the same for known and unknown
const IP = "10.66.1.1";
for (let i = 0; i < PRE.unsubIpHour; i++) assert.equal((await unsub(form(`n${i}@example.com`), IP)).status, 200, "try " + i);
const over = await unsub(form("other.person@example.com"), IP);
assert.equal(over.status, 429);
assert.ok(over.text.includes("잠시 뒤 다시 시도해 주세요") && over.text.includes("try again"), "a bilingual 'try later'");
assert.deepEqual(rows(), ["other.person@example.com"], "nothing deleted over the limit");
assert.equal(noNonce((await unsub(form("nobody2@example.com"), IP)).text), noNonce(over.text), "over the limit: the same for an unknown address");
for (let h = 2; h <= PRE.unsubNetHour - PRE.unsubIpHour + 1; h++) assert.equal((await unsub(form("x@example.com"), "10.66.1." + h)).status, 200, "host " + h);
assert.equal((await unsub(form("x@example.com"), "10.66.1.200")).status, 429, "the /24 has had its share");
assert.equal((await unsub(form("x@example.com"), "10.66.2.1")).status, 200, "another network");
// a new hour window (minute 7) opens again
now += 3600e3;
assert.equal((await unsub(form("x@example.com"), IP)).status, 200, "the next hour");

// the Stats object down: a 503 page, never a 500, and no audit line
st.down = true;
const down = await unsub(form("other.person@example.com"));
assert.equal(down.status, 503);
assert.ok(down.text.includes("지금은 처리하지 못했어요") && down.text.includes("could not be done right now"));
st.down = false;
assert.equal(audit().filter(a => a.startsWith("사전예약 수신 거부")).length, 2, "two rows went in all (fan, the old one)");

// the stored rows are deleted on 2027-02-28 00:00 KST; the page still answers that day (the same "done" answer)
now = Date.parse("2027-02-28T10:00:00Z");   // 2027-02-28 19:00 KST
assert.equal((await call("/preorder/unsubscribe")).status, 200);
const late = await unsub(form("other.person@example.com"));
assert.equal(late.status, 200);
assert.ok(late.text.includes("2027년 2월 28일 (한국 시간)") && late.text.includes("February 28, 2027 (Korean time)"));
assert.equal(rows().length, 0, "purged at PRE.deleteAt anyway");
// up to 23:59:59 KST, then closed (410, bilingual, no form), GET and POST alike
now = Date.parse(PRE.unsubUntil);
assert.equal((await call("/preorder/unsubscribe")).status, 200, "the last second");
now = Date.parse(PRE.unsubUntil) + 1000;
for (const r of [await call("/preorder/unsubscribe"), await call("/preorder/unsubscribe", { method: "POST", headers: { "Content-Type": "application/x-www-form-urlencoded" }, body: form("a@example.com") })]) {
  const t = await r.text();
  assert.equal(r.status, 410);
  assert.ok(t.includes("2027년 2월 28일에 닫혔어요") && t.includes("closed on February 28, 2027") && !/<input\b|<form\b/.test(t));
}
assert.equal(PRE.unsubUntil, "2027-02-28T14:59:59Z");

// PREORDER=off (sign-ups closed early) does not close it either
now = Date.parse("2026-12-01T03:00:00Z");
env.PREORDER = "off";
assert.equal((await call("/preorder/unsubscribe")).status, 200);
assert.equal((await unsub(form("z@example.com"))).status, 200);
delete env.PREORDER;

Date.now = realNow;
assert.ok(!logged.some(l => /@/.test(l)), "no address in console output");
console.log("test_preorder_unsub: all passed");
