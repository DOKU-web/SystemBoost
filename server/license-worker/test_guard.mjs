// Server hardening (review 2026-11-12): the admin lockout and access log, the KV write budget, the subscription
// portal, the buyers' lane, card and bundle refunds, closed support, audit minimisation, status reads, truncation,
// the dashboard's CSP. Fake KV (counts reads and writes; put/get can be made to throw, lists can pretend there is
// more), the Stats Durable Object on node:sqlite, signed Lemon Squeezy webhooks, a throwaway signing key.
// Global fetch answers only a fake Lemon Squeezy and a fake Supabase.
//   node test_guard.mjs
import assert from "node:assert/strict";
import { createHash, createHmac, generateKeyPairSync } from "node:crypto";
import worker from "./src/index.js";
import { LIMITS, bucket48, ipBucket } from "./src/support.js";
import { GUARD } from "./src/stats.js";
import { fakeStats } from "./fake_stats.mjs";
import { ADMIN_HTML } from "./src/admin_page.js";

// ---- fake KV
const kv = new Map(), md = new Map();
const n = { reads: 0, writes: 0 };
const fail = { put: false, get: false }, more = new Set();   // more: prefixes whose list pretends there is always more
const env = {
  LICENSES: {
    get: async k => { n.reads++; if (fail.get) throw new Error("KV get() limit exceeded for the day."); return kv.get(k) ?? null; },
    getWithMetadata: async k => { n.reads++; if (fail.get) throw new Error("KV get() limit exceeded for the day."); return { value: kv.get(k) ?? null, metadata: md.get(k) ?? null }; },
    put: async (k, v, o) => { if (fail.put) throw new Error("KV put() limit exceeded for the day."); n.writes++; kv.set(k, String(v)); md.set(k, o?.metadata ?? null); },
    delete: async k => { if (fail.put) throw new Error("KV delete() limit exceeded for the day."); n.writes++; kv.delete(k); md.delete(k); },
    list: async ({ prefix = "", limit = 1000, cursor }) => {
      const all = [...kv.keys()].filter(k => k.startsWith(prefix)).sort();
      const start = cursor ? Number(cursor) : 0, page = all.slice(start, start + limit);
      const done = !more.has(prefix) && start + limit >= all.length;
      return { keys: page.map(name => ({ name, metadata: md.get(name) })), list_complete: done, cursor: done ? undefined : String(start + limit) };
    },
  },
  LICENSE_PRIVATE_KEY: generateKeyPairSync("ec", { namedCurve: "P-256" }).privateKey.export({ type: "pkcs8", format: "pem" }),
  ADMIN_TOKEN: "T".repeat(32),
  PUBLIC_URL: "https://lic.example",
  SALES_MODE: "off",
  LS_SIGNING_SECRET: "test-signing-secret",
  LS_API_KEY: "lsk_fake",
};
const st = fakeStats();
env.STATS = st.binding;
const fetched = [], lsCancels = [];
let lsDown = false;
globalThis.fetch = async (url, init = {}) => {
  url = String(url);
  fetched.push(url);
  if (url.startsWith("https://api.lemonsqueezy.com/v1/subscriptions/") && init.method === "DELETE") {
    if (lsDown) return new Response("down", { status: 503 });
    lsCancels.push(url.split("/").pop());
    return Response.json({ data: { attributes: { status: "cancelled" } } });
  }
  if (url.startsWith("https://api.lemonsqueezy.com/v1/subscriptions/"))
    return Response.json({ data: { attributes: { urls: { customer_portal: "https://shop.example/billing/" + url.split("/").pop() + "?signature=s1" } } } });
  throw new TypeError("no network in tests: " + url);
};

const call = (path, init = {}) => worker.fetch(new Request("https://lic.example" + path, init), env, { waitUntil() {} });
const j = async (path, init) => { const r = await call(path, init); let b = {}; try { b = await r.json(); } catch {} return { http: r.status, ...b }; };
let ipNo = 0;
const freshIp = () => "203.0.113." + (++ipNo % 250) + "-" + ipNo;
const post = (path, body, ip = freshIp()) => j(path, { method: "POST", headers: { "Content-Type": "application/json", "CF-Connecting-IP": ip }, body: JSON.stringify(body) });
const adm = (path, body, { token = env.ADMIN_TOKEN, ip = "192.0.2.1" } = {}) =>
  j("/admin/api" + path, { method: body ? "POST" : "GET", headers: { Authorization: "Bearer " + token, "Content-Type": "application/json", "CF-Connecting-IP": ip }, body: body ? JSON.stringify(body) : undefined });
const timed = async fn => { const t = performance.now(); const r = await fn(); return { r, ms: performance.now() - t }; };
const hook = async (event, data, { test = true, custom = {} } = {}) => {
  const raw = JSON.stringify({ meta: { event_name: event, test_mode: test, custom_data: custom }, data });
  return j("/webhook/lemonsqueezy", { method: "POST", body: raw, headers: { "X-Signature": createHmac("sha256", env.LS_SIGNING_SECRET).update(raw).digest("hex") } });
};
const sub = (id, variant, extra = {}) => ({ type: "subscriptions", id: String(id), attributes: { status: "active", variant_id: Number(variant), order_id: 9000 + id,
  user_email: "b" + id + "@example.com", renews_at: new Date(Date.now() + 30 * 864e5).toISOString(), ...extra } });
const kid = key => createHash("sha256").update(key.trim()).digest("hex").slice(0, 16);
const revokedId = async id => (await j("/check?id=" + id)).revoked;
const clearCounters = like => st.db.prepare("DELETE FROM counters WHERE name LIKE ?").run(like);
const base = { cat: "howto", subject: "질문", text: "질문이 있어요", consent: true };
const create = (over = {}, ip) => post("/support/ticket", { ...base, ...over }, ip);
const stored = id => JSON.parse(kv.get("tk:" + id));

// ---- spellings of one signed key that verify just as well (review4 sec_poc/harness.mjs)
const N = BigInt("0xFFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551");
const A64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
function twin(key) {
  const [p, s] = key.split("."), sig = Buffer.from(s, "base64url"), sv = BigInt("0x" + sig.subarray(32).toString("hex"));
  return p + "." + Buffer.concat([sig.subarray(0, 32), Buffer.from((N - sv).toString(16).padStart(64, "0"), "hex")]).toString("base64url");
}
function strayBits(part) {
  const unused = { 2: 4, 3: 2 }[part.length % 4] || 0;
  if (!unused) return [part];
  const b = A64.indexOf(part.at(-1)) & ~((1 << unused) - 1);
  return Array.from({ length: 1 << unused }, (_, i) => part.slice(0, -1) + A64[b | i]);
}
function spellings(key) {
  const out = new Set();
  for (const k of [key, twin(key)]) { const [p, s] = k.split("."); for (const a of strayBits(p)) for (const b of strayBits(s)) out.add(a + "." + b); }
  return [...out];
}

// setup: card variants and test sales mode (subscription keys come from signed webhooks)
await adm("/card", { store: "42", variants: { monthly: "101", yearly: "102" } });
await adm("/sales-mode", { mode: "test" });

// ================================================================ 1. admin lockout: fails closed, never 500, no KV write
{
  clearCounters("adm:%");
  const w0 = n.writes;
  for (let i = 0; i < 5; i++) {
    const { r, ms } = await timed(() => adm("/summary", null, { token: "wrong" + i, ip: "10.9.0.1" }));
    assert.equal(r.http, 401);
    assert.ok(ms >= 390, "a wrong password waits 400 ms: " + ms.toFixed(0));
  }
  const sixth = await timed(() => adm("/summary", null, { token: "wrong", ip: "10.9.0.1" }));
  assert.equal(sixth.r.http, 429, "the 6th miss from one address is refused");
  assert.ok(sixth.ms >= 390, "after the same delay");
  const right = await timed(() => adm("/summary", null, { ip: "10.9.0.1" }));
  assert.equal(right.r.http, 429, "locked: the right password too");
  assert.ok(right.ms >= 390, "the right password on a locked address waits too (no timing tell)");
  assert.equal((await adm("/summary", null, { ip: "10.9.0.2" })).http, 200, "another address still works");
  assert.equal(n.writes - w0, 0, "the lockout writes no KV");
  assert.ok(![...kv.keys()].some(k => k.startsWith("fail:")));
  const names = st.rows("SELECT name FROM counters WHERE name LIKE 'adm:%'").map(r => r.name);
  assert.ok(names.every(x => /^adm:(ip:[0-9a-f]{16}|all:\d{4}-\d\d-\d\dT\d\d)$/.test(x)) && !names.some(x => x.includes("10.9")), "the address only as a salted hash");
  // IPv6: one /64 is one address
  clearCounters("adm:%");
  for (let i = 0; i < 5; i++) assert.equal((await adm("/summary", null, { token: "x", ip: "2001:db8:5:6::" + (i + 1) })).http, 401);
  assert.equal((await adm("/summary", null, { ip: "2001:db8:5:6:ffff::9" })).http, 429, "another address in the same /64");
  // tries at once: counted one by one (KV read-then-write let all 30 through)
  clearCounters("adm:%");
  const burst = await Promise.all(Array.from({ length: 30 }, (_, i) => call("/admin/api/summary", { headers: { Authorization: "Bearer wrong" + i, "CF-Connecting-IP": "10.9.0.3" } })));
  assert.deepEqual([burst.filter(r => r.status === 401).length, burst.filter(r => r.status === 429).length], [5, 25], "30 at once: 5 checked");
  // 20 misses an hour in all lock everyone
  clearCounters("adm:%");
  for (let i = 0; i < 20; i++) assert.equal((await adm("/summary", null, { token: "w", ip: "10.8.1." + i })).http, 401);
  assert.equal((await adm("/summary", null, { token: "w", ip: "10.8.2.1" })).http, 429, "the 21st miss in the hour");
  assert.equal((await adm("/summary", null, { ip: "10.8.2.2" })).http, 429, "and the right password, from anywhere");
  clearCounters("adm:%");

  // the object down and KV out of writes: still 401 / 429 after the delay, never 500
  st.down = true; fail.put = true;
  for (let i = 0; i < 5; i++) {
    const { r, ms } = await timed(() => adm("/summary", null, { token: "w" + i, ip: "10.7.0.1" }));
    assert.equal(r.http, 401, "KV put throws: still 401");
    assert.ok(ms >= 390);
  }
  assert.equal((await adm("/summary", null, { token: "w", ip: "10.7.0.1" })).http, 429, "counted in memory: the 6th is refused");
  assert.equal((await adm("/summary", null, { ip: "10.7.0.1" })).http, 429);
  assert.equal((await adm("/summary", null, { ip: "10.7.0.2" })).http, 200, "the owner still gets in from elsewhere");
  fail.get = true;
  assert.equal((await adm("/summary", null, { token: "w", ip: "10.7.0.3" })).http, 401, "KV reads fail too: still 401");
  fail.get = false; fail.put = false;
  // the object down, KV fine: the hour's count in KV ("fail:all:"), none for an address, nothing once locked
  for (let i = 0; i < 3; i++) assert.equal((await adm("/summary", null, { token: "w", ip: "10.7.1." + i })).http, 401);
  assert.deepEqual([...kv.keys()].filter(k => k.startsWith("fail:")).map(k => k.replace(/\d{4}-\d\d-\d\dT\d\d$/, "H")), ["fail:all:H"]);
  assert.equal(kv.get([...kv.keys()].find(k => k.startsWith("fail:all:"))), "3");
  for (const k of [...kv.keys()]) if (k.startsWith("fail:")) kv.delete(k);
  st.down = false;
}

// ================================================================ 8. admin access log (one year, in the object)
{
  st.db.prepare("DELETE FROM admin_access").run();
  st.db.prepare("DELETE FROM counters WHERE name LIKE 'sess:%'").run();
  const ip = "192.0.2.50";
  const keyA = await adm("/issue", { plan: "yearly", email: "owner@example.com", note: "접속 기록 시험" }, { ip });
  assert.ok(keyA.key);
  await adm("/summary", null, { ip });
  await adm("/keys?status=all", null, { ip });
  await adm("/key?id=" + keyA.id, null, { ip });
  await adm("/keys", { q: "owner@example.com" }, { ip });
  await adm("/orders?status=all", null, { ip });
  const rows = st.rows("SELECT ip, route, action, target FROM admin_access ORDER BY seq");
  assert.deepEqual(rows.map(r => [r.route, r.action, r.target]), [
    ["POST /issue", "login", ""], ["GET /keys", "키 목록", "all"], ["GET /key", "키 원문", keyA.id], ["POST /keys", "키 검색", "검색"], ["GET /orders", "주문 목록", "all"]],
    "a login, then each call that shows personal data (not the summary, not the issue itself)");
  assert.ok(rows.every(r => /^[0-9a-f]{16}$/.test(r.ip)) && new Set(rows.map(r => r.ip)).size === 1, "the address as a salted hash");
  assert.ok(!JSON.stringify(st.rows("SELECT * FROM admin_access")).includes("owner@"), "a search's words are not kept");
  const acc = await adm("/access", null, { ip });
  assert.equal(acc.http, 200);
  assert.equal(acc.keepDays, 366);
  assert.deepEqual(acc.rows.slice(0, 2).map(r => r.action), ["주문 목록", "키 검색"], "newest first");
  assert.ok(acc.rows.every(r => /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$/.test(r.at)));
  // another address: a new login
  await adm("/members", null, { ip: "192.0.2.51" });
  assert.equal(st.rows("SELECT COUNT(*) AS n FROM admin_access WHERE action = 'login'")[0].n, 2);
  // 30 quiet minutes: a new login row
  const realNow = Date.now;
  Date.now = () => realNow() + 31 * 60e3;
  await adm("/summary", null, { ip });
  Date.now = realNow;
  assert.equal(st.rows("SELECT COUNT(*) AS n FROM admin_access WHERE action = 'login'")[0].n, 3);
  // kept a year and a day, then gone
  st.db.prepare("INSERT INTO admin_access (at, ip, route, action, target) VALUES (?, 'aaaaaaaaaaaaaaaa', 'GET /keys', '키 목록', '')").run(Date.now() - 367 * 864e5);
  await adm("/keys", null, { ip });
  assert.equal(st.rows("SELECT COUNT(*) AS n FROM admin_access WHERE ip = 'aaaaaaaaaaaaaaaa'")[0].n, 0, "older than " + GUARD.accessDays + " days: deleted");
  // the object down: the row goes to KV and the dashboard still lists it
  st.down = true;
  await adm("/support/ticket?id=T-AAAA-AAAA", null, { ip: "192.0.2.52" });
  st.down = false;
  const accKv = [...kv.keys()].filter(k => k.startsWith("acc:")).map(k => JSON.parse(kv.get(k)));
  assert.deepEqual(accKv.map(r => r.action).sort(), ["login", "문의 열람"]);
  assert.ok((await adm("/access", null, { ip })).rows.some(r => r.kv && r.action === "문의 열람" && r.target === "T-AAAA-AAAA"));
  assert.equal((await adm("/access?before=x", null, { ip })).http, 400);
}

// ================================================================ 8b. searches only in a POST body
{
  assert.deepEqual(await adm("/keys?q=owner"), { http: 400, error: "검색어는 POST 본문으로 보내요" });
  assert.deepEqual(await adm("/members?q=frie"), { http: 400, error: "검색어는 POST 본문으로 보내요" });
  assert.deepEqual(await adm("/support?q=T-"), { http: 400, error: "검색어는 POST 본문으로 보내요" });
  assert.equal((await adm("/keys", { q: "접속 기록 시험" })).keys.length, 1);
  assert.equal((await adm("/keys?status=all")).http, 200, "the list without words stays a GET (the app's and old dashboards' form)");
}

// ================================================================ 9. the audit record: in the object, cut down
{
  const k1 = await adm("/issue", { plan: "yearly", email: "buyer.one@example.com", note: "홍길동 · 국민은행 메모" });
  const log = (await adm("/log")).log;
  const line = log.find(l => l.action === "키 발급" && l.detail.startsWith(k1.id));
  assert.equal(line.detail, k1.id + " · 코드 없음 · b***@e***.com · 369일 · 홍** · 국**", "no full e-mail, payer name or memo");
  assert.ok(st.rows("SELECT COUNT(*) AS n FROM audit_log WHERE detail LIKE ?", k1.id + "%")[0].n === 1, "in the object");
  assert.ok(![...kv.keys()].some(k => k.startsWith("log:")), "not in KV");
  // an order approved with its payer and memo
  await adm("/pay-links", { yearly: "https://pay.example/y" });
  const o = await j("/order?plan=yearly&method=manual", { method: "POST" });
  await j("/order/paid?code=" + o.code, { method: "POST", body: JSON.stringify({ payer: "김철수", msg: "카톡으로 연락 주세요" }) });
  const ap = await adm("/orders/approve", { code: o.code, email: "kim@example.org" });
  const l2 = (await adm("/log")).log.find(l => l.action === "키 발급" && l.detail.startsWith(ap.id));
  assert.equal(l2.detail, ap.id + " · " + o.code + " · k***@e***.org · 369일 · 김** · 카**");
  // a referral code's label
  await adm("/referrals", { code: "LABEL1", label: "유튜버 이영희", days: 3, max: 5 });
  assert.equal((await adm("/log")).log.find(l => l.action === "추천 코드 생성").detail, "LABEL1 · 3일 · 5회 · 유**");
  // older KV lines are shown cut down the same way
  kv.set("log:0000000000000zzzz", JSON.stringify({ at: "2026-11-01T00:00:00Z", who: "admin", action: "키 발급", detail: "0123456789abcdef · 코드 없음 · old@example.com · 30일 · 박서준" }));
  assert.equal((await adm("/log")).log.find(l => l.detail.startsWith("0123456789abcdef")).detail, "0123456789abcdef · 코드 없음 · o***@e***.com · 30일 · 박**");
  kv.delete("log:0000000000000zzzz");
  // the object down: the seller's line goes to KV, cut down too
  st.down = true;
  const k2 = await adm("/issue", { plan: "monthly", email: "second@example.com", note: "이몽룡" });
  st.down = false;
  const kvLines = [...kv.keys()].filter(k => k.startsWith("log:")).map(k => JSON.parse(kv.get(k)));
  assert.ok(kvLines.some(l => l.detail === k2.id + " · 코드 없음 · s***@e***.com · 34일 · 이**"));
  assert.ok(!JSON.stringify(kvLines).includes("second@") && !JSON.stringify(st.rows("SELECT * FROM audit_log")).match(/buyer\.one@|kim@|홍길동|김철수|이영희/));
  for (const k of [...kv.keys()]) if (k.startsWith("log:")) kv.delete(k);
}

// ================================================================ 12c. the dashboard page: a nonce for its one script
{
  const r1 = await call("/admin"), r2 = await call("/admin");
  const csp = r1.headers.get("content-security-policy"), nonce = /'nonce-([A-Za-z0-9+/=]+)'/.exec(csp)?.[1];
  assert.ok(nonce, csp);
  assert.ok(/script-src 'nonce-[^']+';/.test(csp) && !/script-src[^;]*unsafe-inline/.test(csp), "no inline script without the nonce");
  assert.ok(csp.includes("base-uri 'none'") && csp.includes("form-action 'none'") && csp.includes("frame-ancestors 'none'") && csp.includes("default-src 'none'"));
  assert.equal(r1.headers.get("x-content-type-options"), "nosniff");
  const html = await r1.text();
  assert.ok(!html.includes("__NONCE__"));
  assert.deepEqual([...html.matchAll(/<script\b([^>]*)>/g)].map(m => m[1].trim()), ['nonce="' + nonce + '"'], "one script, carrying this load's nonce");
  assert.ok(!/\son[a-z]+\s*=/i.test(html.replace(/<script[\s\S]*?<\/script>/g, "")), "no inline event handlers (the nonce would not cover them)");
  assert.notEqual(/'nonce-([^']+)'/.exec(r2.headers.get("content-security-policy"))[1], nonce, "a new nonce each load");
}

// ================================================================ 3. the subscription portal: a key as issued, still honoured
let card = {};
{
  await hook("subscription_created", sub(70, 101), { custom: { session: "SB-PRTA-2345" } });
  const key = (await j("/claim?session=SB-PRTA-2345")).key, id = kid(key);
  card = { key, id };
  const portal = (k, ip) => post("/support/subscription", { key: k }, ip);
  const ok = { http: 200, portal: "https://shop.example/billing/70?signature=s1" }, nf = { http: 404, error: "not found" };
  clearCounters("portal:%");
  assert.deepEqual(await portal(key), ok);
  // every other spelling: the twin is refused, stray low bits and padding come to the issued text
  const all = spellings(key), tw = twin(key), twSig = tw.split(".")[1];
  assert.ok(all.length >= 32, String(all.length));
  let okN = 0, nfN = 0;
  for (const k of all) {
    clearCounters("portal:%");
    const fromTwin = strayBits(twSig).includes(k.split(".")[1]);
    assert.deepEqual(await portal(k), fromTwin ? nf : ok, (fromTwin ? "a twin spelling" : "a stray-bit spelling") + ": " + k.slice(-6));
    fromTwin ? nfN++ : okN++;
  }
  assert.deepEqual([okN, nfN], [all.length / 2, all.length / 2], "every spelling of the issued signature works, none of the twin's");
  assert.deepEqual(await portal(tw), nf, "the twin signature: not the key as issued");
  const [p0, s0] = key.split(".");
  const stray = (strayBits(p0).find(x => x !== p0) || p0) + "." + strayBits(s0).find(x => x !== s0);
  assert.deepEqual(await portal(stray), ok, "stray low bits: the same key, written another way");
  assert.deepEqual(await portal(" " + p0 + "==." + s0 + "=\n"), ok, "padding and spaces");
  // blocked under either signature's id, expired, unregistered: the same 404
  kv.set("revoked:" + id, "공유");
  for (const k of [key, stray]) assert.deepEqual(await portal(k), nf, "revoked");
  kv.delete("revoked:" + id);
  kv.set("revoked:" + kid(tw), "공유");
  assert.deepEqual(await portal(key), nf, "revoked under the twin's id (an app that registered the twin)");
  kv.delete("revoked:" + kid(tw));
  const realNow = Date.now;
  Date.now = () => realNow() + 2 * 864e5;   // a test key lasts a day
  assert.deepEqual(await portal(key), nf, "expired");
  Date.now = realNow;
  const lic = kv.get("lic:" + id), licMd = md.get("lic:" + id);
  kv.delete("lic:" + id);
  assert.deepEqual(await portal(key), nf, "not registered");
  kv.set("lic:" + id, lic); md.set("lic:" + id, licMd);
  // a daily cap per key, and a line in the record for every link handed out
  clearCounters("portal:%");
  const before = st.rows("SELECT COUNT(*) AS n FROM audit_log WHERE action = '구독 관리 링크'")[0].n;
  for (let i = 0; i < LIMITS.portalPerKey; i++) assert.deepEqual(await portal(i % 2 ? stray : key), ok);
  const capped = await portal(key);
  assert.deepEqual([capped.http, capped.scope], [429, "day"], LIMITS.portalPerKey + " links a day for one key, however written");
  assert.equal(st.rows("SELECT COUNT(*) AS n FROM audit_log WHERE action = '구독 관리 링크'")[0].n - before, LIMITS.portalPerKey);
  assert.equal(st.rows("SELECT detail FROM audit_log WHERE action = '구독 관리 링크' ORDER BY seq DESC LIMIT 1")[0].detail, "구독 70 · " + id);
  // the object down: no cap can be counted, so no link
  clearCounters("portal:%");
  st.down = true;
  assert.deepEqual(await portal(key), { http: 503, error: "unavailable" });
  st.down = false;
}

// ================================================================ 4. the buyers' lane: a paid key as issued, or an approved order
{
  clearCounters("sup:%"); clearCounters("rl:%");
  const today = new Date().toISOString().slice(0, 10);
  const lane = () => Object.fromEntries(st.rows("SELECT name, n FROM counters WHERE name LIKE ?", "sup:" + today + ":%").map(r => [r.name.split(":").slice(2).join(":"), r.n]));
  const ownerOf = async over => { const t = await create(over); assert.equal(t.http, 201, JSON.stringify(t)); return stored(t.id).owner; };
  // paid keys
  const paid = await adm("/issue", { code: "SB-LANE-2345", plan: "yearly" });
  assert.equal(await ownerOf({ cat: "pay", key: paid.key }), "o:SB-LANE-2345", "a paid key: its order");
  assert.equal(await ownerOf({ cat: "pay", key: card.key }), "s:70", "a card key: its subscription");
  // not purchases: a referral key, a hand-made key, the twin, another spelling, a blocked key
  await adm("/referrals", { code: "GIFT7", days: 7, max: 10 });
  const gift = (await j("/referral", { method: "POST", body: JSON.stringify({ code: "GIFT7", device: "1".repeat(32) }) })).key;
  assert.ok(gift);
  assert.equal(await ownerOf({ cat: "pay", key: gift }), undefined, "a referral key is free: anonymous");
  const manual = await adm("/issue", { plan: "yearly", note: "선물" });
  assert.equal(await ownerOf({ cat: "pay", key: manual.key }), undefined, "a key the seller made by hand");
  assert.equal(await ownerOf({ cat: "pay", key: twin(paid.key) }), undefined, "the twin");
  const [pp, ps] = paid.key.split(".");
  assert.equal(await ownerOf({ cat: "pay", key: pp + "." + strayBits(ps).find(x => x !== ps) }), undefined, "another spelling (each would be a new owner)");
  const blocked = await adm("/issue", { code: "SB-BLCK-2345", plan: "yearly" });
  await adm("/revoke", { id: blocked.id });
  assert.equal(await ownerOf({ cat: "pay", key: blocked.key }), undefined, "a blocked key");
  const l = lane();
  assert.deepEqual([l.vc, l["by:o:SB-LANE-2345"], l["by:s:70"], l.c], [2, 1, 1, 5]);

  // anonymous writes from IPv6 also count per /48: many /64s of one site cannot fill the lane
  assert.equal(bucket48(ipBucket("2001:db8:77:1:abcd::1")), "2001:db8:77::/48");
  assert.equal(bucket48(ipBucket("198.51.100.7")), "");
  clearCounters("sup:%"); clearCounters("rl:%");
  let made = 0, last = null;
  for (let i = 0; i < 10 && !last; i++)
    for (let k = 0; k < LIMITS.createPerIp; k++) {
      const t = await create({}, "2001:db8:77:" + i + "::" + (k + 1));
      if (t.http === 201) made++; else { last = t; break; }
    }
  assert.equal(made, LIMITS.anonPer48, LIMITS.anonPer48 + " anonymous writes an hour from one /48");
  assert.deepEqual([last.http, last.error, last.scope], [429, "busy", "ip"]);
  assert.equal((await create({}, "2001:db8:78:1::1")).http, 201, "the next /48 is someone else");
  assert.equal((await create({ cat: "pay", key: paid.key }, "2001:db8:77:99::1")).http, 201, "a buyer with a purchase in that /48 still gets in");
  clearCounters("sup:%"); clearCounters("rl:%");
}

// ================================================================ 5. 환불 완료 on a card subscription: every key off, the subscription cancelled
{
  await hook("subscription_created", sub(50, 101), { custom: { session: "SB-SUBA-2345" } });
  const kA = (await j("/claim?session=SB-SUBA-2345")).key, idA = kid(kA);
  await hook("subscription_updated", sub(50, 102));   // a plan change: a second key
  const idB = kid(kv.get("sub:50"));
  assert.notEqual(idA, idB);
  const t = await create({ cat: "refund", subject: "환불", text: "환불해 주세요", key: kA, refund: { reason: "change_mind" } });
  const view = await adm("/support/ticket?id=" + t.id);
  assert.deepEqual(view.keys.map(k => [k.id, k.owned, k.sub, !!k.shown]).sort(), [[idA, true, "50", true], [idB, true, "50", false]].sort(),
                   "both keys of the subscription, the buyer's own shown");
  // the subscription is cancelled at Lemon Squeezy too (index.js cancelSubscription, here against the fake API)
  const dn = await adm("/support/refund", { id: t.id, action: "done" });
  const cancels = lsCancels.splice(0);
  assert.equal(dn.http, 200, JSON.stringify(dn));
  assert.deepEqual([...dn.revoked].sort(), [idA, idB].sort());
  assert.deepEqual([dn.subs, dn.cancelled, dn.cancelFailed, dn.cancelAvailable], [["50"], ["50"], [], true]);
  assert.deepEqual(cancels, ["50"]);
  assert.equal(await revokedId(idA), true);
  assert.equal(await revokedId(idB), true, "the key from the plan change too");
  assert.equal(kv.has("sub:50"), false, "the subscription's current key is dropped (a renewal Lemon Squeezy still charged would make a new one)");
  assert.ok((await adm("/log")).log.some(l => l.action === "환불 완료" && l.detail.includes(t.id) && l.detail.includes("구독 해지 50")));
  // without a Lemon Squeezy API key: the keys still go off, the cancel is reported as not done and the log says to do it by hand
  await hook("subscription_created", sub(51, 101), { custom: { session: "SB-SUBB-2345" } });
  const kC = (await j("/claim?session=SB-SUBB-2345")).key;
  const t2 = await create({ cat: "refund", subject: "환불", text: "환불", key: kC, refund: { reason: "other" } });
  const savedKey = env.LS_API_KEY;
  delete env.LS_API_KEY;
  let dn2;
  try { dn2 = await adm("/support/refund", { id: t2.id, action: "done" }); } finally { env.LS_API_KEY = savedKey; }
  assert.deepEqual([dn2.http, dn2.revoked, dn2.cancelled, dn2.cancelFailed, dn2.cancelAvailable], [200, [kid(kC)], [], ["51"], true]);
  assert.equal(lsCancels.length, 0);
  assert.ok((await adm("/log")).log.some(l => l.action === "구독 해지 필요"), "the seller is told to cancel by hand");
  // a cancel that fails is reported, the refund is still recorded
  await hook("subscription_created", sub(52, 101), { custom: { session: "SB-SUBC-2345" } });
  const kD = (await j("/claim?session=SB-SUBC-2345")).key;
  const t3 = await create({ cat: "refund", subject: "환불", text: "환불", key: kD, refund: { reason: "other" } });
  lsDown = true;
  let dn3;
  try { dn3 = await adm("/support/refund", { id: t3.id, action: "done" }); } finally { lsDown = false; }
  assert.deepEqual([dn3.http, dn3.cancelFailed, stored(t3.id).refund.status], [200, ["52"], "done"]);
}

// ================================================================ 5b. card refunds the webhook finishes: after a cancellation, after a plan change
{
  // cancelled first (sub:<id> is gone), then refunded: the request still closes
  await hook("subscription_created", sub(60, 101), { custom: { session: "SB-CNCL-2345" } });
  const k = (await j("/claim?session=SB-CNCL-2345")).key;
  const t = await create({ cat: "refund", subject: "해지 후 환불", text: "환불해 주세요", key: k, refund: { reason: "change_mind" } });
  await hook("subscription_updated", sub(60, 101, { status: "cancelled" }));
  assert.equal(kv.has("sub:60"), false);
  await hook("subscription_payment_refunded", { type: "subscription-invoices", id: "6001", attributes: { subscription_id: 60 } });
  assert.equal(await revokedId(kid(k)), true);
  assert.deepEqual([stored(t.id).refund.status, stored(t.id).status], ["done", "answered"], "the buyer is told");
  // the first payment refunded after a plan change: the request names the newer key
  await hook("subscription_created", sub(61, 101), { custom: { session: "SB-PLAN-2345" } });
  const k1 = (await j("/claim?session=SB-PLAN-2345")).key;
  await hook("subscription_updated", sub(61, 102));
  const k2 = kv.get("sub:61");
  const t2 = await create({ cat: "refund", subject: "환불", text: "환불해 주세요", key: k2, refund: { reason: "change_mind" } });
  await hook("order_refunded", { type: "orders", id: "9061", attributes: {} });
  assert.equal(await revokedId(kid(k1)), true);
  assert.equal(await revokedId(kid(k2)), true);
  assert.equal(stored(t2.id).refund.status, "done", "the request about the newer key closes too");
  assert.deepEqual([...stored(t2.id).refund.revoked].sort(), [kid(k1), kid(k2)].sort());
}

// ================================================================ 6. part of a bundle refunded
{
  const b = await adm("/issue", { code: "SB-PRTL-2345", plan: "yearly_x5", seats: 3 });
  assert.equal(b.ids.length, 3);
  const t = await create({ cat: "refund", subject: "한 대만 환불", text: "한 대만 환불해 주세요", key: b.keys[0], refund: { reason: "change_mind" } });
  const v = await adm("/support/ticket?id=" + t.id);
  const byId = (a, c) => a[0].localeCompare(c[0]);
  assert.deepEqual(v.keys.map(k => [k.id, k.owned, !!k.shown]).sort(byId), b.ids.map((id, i) => [id, true, i === 0]).sort(byId), "every key of the order; the one the buyer pasted is marked");
  assert.equal((await adm("/support/refund", { id: t.id, action: "done", keys: [] })).http, 400);
  assert.equal((await adm("/support/refund", { id: t.id, action: "done", keys: ["0123456789abcdef"] })).http, 400, "only keys of this request");
  assert.equal((await adm("/support/refund", { id: t.id, action: "done", keys: "all" })).http, 400);
  const dn = await adm("/support/refund", { id: t.id, action: "done", keys: [b.ids[0]], amount: 9900 });
  const rest = [b.ids[1], b.ids[2]].sort();
  assert.deepEqual([dn.http, dn.revoked, [...dn.kept].sort()], [200, [b.ids[0]], rest]);
  assert.deepEqual(await Promise.all(b.ids.map(revokedId)), [true, false, false]);
  assert.equal((await adm("/orders?status=all")).orders.find(o => o.code === "SB-PRTL-2345").status, "approved", "part of it: the order stays approved");
  assert.ok(kv.has("session:SB-PRTL-2345") && kv.has("bundle:SB-PRTL-2345"), "and its keys claimable");
  assert.deepEqual([...stored(t.id).refund.kept].sort(), rest);
  assert.ok((await adm("/log")).log.some(l => l.action === "환불 완료" && l.detail.includes("남긴 키 2개")));
  // the default is still every key
  const b2 = await adm("/issue", { code: "SB-WHLE-2345", plan: "yearly_x5", seats: 2 });
  const t2 = await create({ cat: "refund", subject: "환불", text: "환불", key: b2.keys[1], refund: { reason: "change_mind" } });
  const dn2 = await adm("/support/refund", { id: t2.id, action: "done" });
  assert.deepEqual([[...dn2.revoked].sort(), dn2.kept], [[...b2.ids].sort(), undefined]);
  assert.equal((await adm("/orders?status=all")).orders.find(o => o.code === "SB-WHLE-2345").status, "refunded");
}

// ================================================================ 7. closed support: a refund request still goes in
{
  const cfg = (await adm("/support/config")).config;
  await adm("/support/config", { ...cfg, open: false });
  const info = await j("/support/info");
  assert.deepEqual([info.open, info.refunds], [false, true]);
  assert.deepEqual(await create(), { http: 503, error: "closed" });
  assert.equal((await create({ cat: "refund", order: "SB-CLSD-2345", refund: { reason: "other" } })).http, 201);
  await adm("/support/config", { ...cfg, open: true });
}

// ================================================================ 12a. status polling reads at most a few misses
{
  const t = await create({ subject: "폴링" });
  const misses = Array.from({ length: LIMITS.statusBatch - 1 }, (_, i) => ({ id: "T-AAAA-" + "ABCDEFGHJKLMNPQRSTUVWXYZ"[i % 24].repeat(4), token: "0".repeat(64) }));
  let r0 = n.reads;
  const r = await post("/support/tickets/status", { tickets: [{ id: t.id, token: t.token }, ...misses] });
  assert.equal(r.http, 200);
  assert.equal(n.reads - r0, 1 + LIMITS.statusMisses, "the found ticket, then " + LIMITS.statusMisses + " misses, then no more reads");
  assert.equal(r.tickets[t.id].status, "open");
  assert.equal(Object.keys(r.tickets).length, 1 + LIMITS.statusMisses, "the rest are left out (the app keeps what it showed)");
  assert.ok(Object.entries(r.tickets).every(([id, v]) => id === t.id || v === null));
}

// ================================================================ 12b. more than the dashboard reads: said, not silently dropped
{
  more.add("tk:");
  const L = await adm("/support?status=all");
  assert.equal(L.truncated, true);
  assert.equal((await adm("/summary")).counts.ticketsMore, true);
  more.delete("tk:");
  assert.equal((await adm("/support?status=all")).truncated, undefined);
  more.add("lic:");
  assert.equal((await adm("/keys?status=all")).truncated, true);
  assert.equal((await adm("/summary")).counts.keysMore, true);
  more.delete("lic:");
  more.add("ord:");
  assert.equal((await adm("/orders?status=all")).truncated, true);
  more.delete("ord:");
}

// ================================================================ 2. the write budget, in numbers
{
  // support's lanes (KV writes a day): anonymous + shown purchases; everything else that writes per request either
  // lives in the Stats object (lockout, audit, access log, per-address limits, portal cap) or belongs to orders/keys
  assert.ok(LIMITS.writesPerDay + LIMITS.vWritesPerDay <= 250, "support: at most 250 KV writes a day");
  assert.ok(LIMITS.createsPerDay <= LIMITS.writesPerDay && LIMITS.vCreatesPerDay <= LIMITS.vWritesPerDay);
}

assert.ok(fetched.every(u => u.startsWith("https://api.lemonsqueezy.com/v1/subscriptions/")), "no other network call: " + fetched.join(", "));
console.log("guard tests passed");
