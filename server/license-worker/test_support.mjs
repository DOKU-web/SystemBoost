// Local test of customer support and refund requests: fake KV (it also keeps each write's TTL and counts writes and
// lists), a fake Cache API, the Stats Durable Object on node:sqlite (the dashboard's feed), a throwaway signing key,
// signed Lemon Squeezy webhooks, and the worker's real routes. Global fetch is replaced first and answers only as a
// fake Lemon Squeezy and a fake Supabase, so nothing leaves this machine.
//   node test_support.mjs
import assert from "node:assert/strict";
import { createHash, createHmac, generateKeyPairSync } from "node:crypto";
import worker from "./src/index.js";
import { DatabaseSync } from "node:sqlite";
import { readFileSync } from "node:fs";
import { ARTICLE_9, DEFAULT_FAQ, LIMITS, PRIVACY_EMPTY, _forgetMemory, ipBucket, listRow } from "./src/support.js";
import { FEED, FEED_KINDS, Stats } from "./src/stats.js";
import { build as buildPrivacy, markdownToHtml } from "./build_privacy.mjs";

const kv = new Map(), md = new Map(), ttl = new Map(), bigMeta = [];
let writes = 0, reads = 0, lists = 0;
const env = {
  LICENSES: {
    get: async k => { reads++; return kv.get(k) ?? null; },
    getWithMetadata: async k => { reads++; return { value: kv.get(k) ?? null, metadata: md.get(k) ?? null }; },
    put: async (k, v, o) => {
      writes++;
      if (o?.metadata && Buffer.byteLength(JSON.stringify(o.metadata)) >= 1024) bigMeta.push(k);
      kv.set(k, v); md.set(k, o?.metadata ?? null); ttl.set(k, o?.expirationTtl ?? null);
    },
    delete: async k => { writes++; kv.delete(k); md.delete(k); ttl.delete(k); },
    list: async ({ prefix, limit = 1000 }) => (lists++, { keys: [...kv.keys()].filter(k => k.startsWith(prefix)).sort().slice(0, limit).map(name => ({ name, metadata: md.get(name) })), list_complete: true }),
  },
  LICENSE_PRIVATE_KEY: generateKeyPairSync("ec", { namedCurve: "P-256" }).privateKey.export({ type: "pkcs8", format: "pem" }),
  ADMIN_TOKEN: "T".repeat(32),
  PUBLIC_URL: "https://lic.example",
  SALES_MODE: "off",
  SUPABASE_URL: "https://project.supabase.example", SUPABASE_SECRET_KEY: "sb_secret_test",
};
const SECRET = "test-signing-secret";
env.LS_SIGNING_SECRET = SECRET;
// ---- the Stats Durable Object (src/stats.js) on node:sqlite: the dashboard's feed. statsDown makes every call fail.
const feedDb = new DatabaseSync(":memory:");
const feedObj = new Stats({ storage: { sql: { exec(query, ...args) {
  if (!args.length && query.includes(";")) { feedDb.exec(query); return { toArray: () => [], [Symbol.iterator]: () => [][Symbol.iterator]() }; }
  const st = feedDb.prepare(query), rows = /^\s*SELECT/i.test(query) ? st.all(...args) : (st.run(...args), []);
  return { toArray: () => rows, [Symbol.iterator]: () => rows[Symbol.iterator]() };
} } } });
let statsDown = false;
env.STATS = { idFromName: name => name, get: () => ({ fetch: async (u, init) => {
  if (statsDown) throw new Error("Durable Object down");
  return feedObj.fetch(new Request(u, init));
} }) };
const feedRows = () => feedDb.prepare("SELECT seq, at, kind, ticket, cat FROM support_events ORDER BY seq").all();
const DAY = 86400;
const sha = s => createHash("sha256").update(s).digest("hex");
const keyIdOf = key => sha(key.trim()).slice(0, 16);

// ---- fake Lemon Squeezy (subscriptions, read only) and fake Supabase (account "me")
const MEMBER = "ab".repeat(32);
const fetched = [], unexpected = [];
let lsAnswer = sub => Response.json({ data: { attributes: { urls: { customer_portal: "https://shop.example/billing/" + sub + "?signature=s1" } } } });
let supabaseUp = true;
globalThis.fetch = async (url, init = {}) => {
  url = String(url);
  fetched.push(url);
  if (url.startsWith("https://api.lemonsqueezy.com/v1/subscriptions/")) {
    assert.ok(!init.method || init.method === "GET", "portal lookup is read only");
    assert.equal(init.headers.Authorization, "Bearer " + env.LS_API_KEY);
    return lsAnswer(url.slice(url.lastIndexOf("/") + 1));
  }
  if (url === "https://project.supabase.example/rest/v1/rpc/account_call") {
    if (!supabaseUp) return new Response("paused", { status: 503 });
    const { action, b } = JSON.parse(init.body);
    if (action === "me" && b.token === sha(MEMBER)) return Response.json({ id: "member01" });
    return Response.json({ error: "session" });
  }
  unexpected.push(url);
  throw new TypeError("no network in tests");
};

// ---- fake Cache API (caches.default). cacheOn = false is *.workers.dev, where Cloudflare does not promise it works.
const edge = new Map(), cachePuts = [];
let cacheOn = true;
globalThis.caches = { default: {
  match: async u => { const e = cacheOn && edge.get(String(u)); return e && e.exp > Date.now() ? new Response(e.body) : undefined; },
  put: async (u, res) => {
    if (!cacheOn) return;
    const age = Number(/max-age=(\d+)/.exec(res.headers.get("Cache-Control") || "")?.[1] || 0);
    cachePuts.push({ u: String(u), age });
    edge.set(String(u), { body: await res.text(), exp: Date.now() + age * 1000 });
  },
} };
// The day's support budget: counters in the Stats object (sup:<date>:w, ...), and while it is down one KV counter.
const today = () => new Date().toISOString().slice(0, 10);
const kvDayKey = () => "count:support:" + today();
const freshDay = () => {   // a new day for the support budget
  feedDb.prepare("DELETE FROM counters WHERE name LIKE 'sup:%' OR name LIKE 'rl:w48:%'").run();
  kv.delete(kvDayKey()); md.delete(kvDayKey());
};
const dayCount = () => {   // the same shape the KV counter had: {w, c, r} and, once the buyers' lane is used, {vw, vc, vr, by}
  const pre = "sup:" + today() + ":";
  const rows = feedDb.prepare("SELECT name, n FROM counters WHERE name LIKE ? AND exp > ?").all(pre + "%", Date.now());
  if (!rows.length) return {};
  const v = Object.fromEntries(rows.map(r => [r.name.slice(pre.length), r.n]));
  const o = { w: v.w || 0, c: v.c || 0, r: v.r || 0 };
  if (v.vw || v.vc || v.vr) Object.assign(o, { vw: v.vw || 0, vc: v.vc || 0, vr: v.vr || 0,
    by: Object.fromEntries(Object.entries(v).filter(([k]) => k.startsWith("by:")).map(([k, n]) => [k.slice(3), n])) });
  return o;
};
const setDay = o => {   // set some of today's counts
  const pre = "sup:" + today() + ":", exp = Date.now() + 2 * DAY * 1000, put = feedDb.prepare("INSERT OR REPLACE INTO counters (name, n, exp) VALUES (?, ?, ?)");
  for (const [k, n] of Object.entries(o)) {
    if (k === "by") for (const [who, m] of Object.entries(n)) put.run(pre + "by:" + who, m, exp);
    else put.run(pre + k, n, exp);
  }
};
const counterNames = like => feedDb.prepare("SELECT name, exp FROM counters WHERE name LIKE ?").all(like);

const call = (path, init) => worker.fetch(new Request("https://lic.example" + path, init), env);
const j = async (path, init) => { const r = await call(path, init); return { http: r.status, ...(await r.json()) }; };
let ipNo = 1;
const freshIp = () => ({ "CF-Connecting-IP": "203.0.113." + (ipNo++ % 250) + "-" + ipNo });
const post = (path, body, headers = freshIp()) => j(path, { method: "POST", headers: { "Content-Type": "application/json", ...headers }, body: typeof body === "string" ? body : JSON.stringify(body) });
const adm = (path, body) => j("/admin/api" + path, { method: body ? "POST" : "GET", headers: { Authorization: "Bearer " + env.ADMIN_TOKEN, "Content-Type": "application/json" }, body: body ? JSON.stringify(body) : undefined });
const base = { cat: "howto", subject: "프레임 생성 질문", text: "프레임 생성이 켜지지 않아요", consent: true };
const create = (over = {}, headers) => post("/support/ticket", { ...base, ...over }, headers);
// the ticket token goes in a header (GET) or the JSON body (POST), never in the URL
const view = (id, token) => j("/support/ticket?id=" + encodeURIComponent(id), { headers: { "X-Ticket-Token": token } });
const reply = (id, token, text, headers) => post("/support/ticket/reply", { id, token, text }, headers);
const closeT = (id, token) => post("/support/ticket/close", { id, token });
const BUSY_IP = { http: 429, error: "busy", scope: "ip" }, BUSY_DAY = { http: 429, error: "busy", scope: "day" };
const stored = id => JSON.parse(kv.get("tk:" + id));
const hook = async (event, data, { test = true, custom = {} } = {}) => {
  const raw = JSON.stringify({ meta: { event_name: event, test_mode: test, custom_data: custom }, data });
  const r = await call("/webhook/lemonsqueezy", { method: "POST", body: raw, headers: { "X-Signature": createHmac("sha256", SECRET).update(raw).digest("hex") } });
  return { http: r.status, ...(await r.json()) };
};
const counted = async fn => { const w = writes; const r = await fn(); return { r, w: writes - w }; };
let r429;
const ID_RE = /^T-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$/;
const ago = days => new Date(Date.now() - days * 864e5).toISOString().replace(/\.\d+Z$/, "Z");
const logHas = async action => (await adm("/log")).log.some(l => l.action === action);

// ---------------------------------------------------------------- info, default FAQ
let info = await j("/support/info");
assert.equal(info.http, 200);
assert.equal((await call("/support/info")).headers.get("access-control-allow-origin"), "*");
assert.equal(info.open, true);
assert.equal(info.refundDays, 7);
assert.deepEqual(info.faq, DEFAULT_FAQ.ko, "no FAQ in the dashboard: the built-in one");
assert.equal(info.faqDefault, true);
assert.deepEqual(info.categories.map(c => c.id), ["pay", "refund", "bug", "howto", "idea", "other"]);
assert.deepEqual(info.refundReasons.map(c => c.id), ["change_mind", "not_working", "double", "other"]);
assert.ok(info.categories.every(c => c.ko && c.en) && info.refundReasons.every(c => c.ko && c.en));
assert.deepEqual(Object.values(info.seller), ["", "", "", "", "", "", "", ""], "no business registration yet: empty seller fields");
assert.match(info.policy.title, /제9조/);
assert.equal(info.policy.items.length, 5);
// ARTICLE_9 is Article 9 of the licence agreement word for word: the heading, and each numbered item with its
// continuation lines joined by a space ("! " only marks a bold line in the app)
for (const [lang, file] of [["ko", "eula.txt"], ["en", "eula.en.txt"]]) {
  const lines = readFileSync(new URL("../../app/SystemBoost.App/Assets/" + file, import.meta.url), "utf8").split(/\r?\n/);
  const at = lines.findIndex(l => /^## (제9조|Article 9 )/.test(l)), items = [];
  assert.ok(at > 0, file + ": Article 9");
  for (let i = at + 1; i < lines.length && !lines[i].startsWith("## "); i++) {
    const l = lines[i].replace(/^! /, "").trim();
    if (!l) continue;
    if (/^\d+\. /.test(l)) items.push(l); else items[items.length - 1] += " " + l;
  }
  assert.equal(ARTICLE_9[lang].title, lines[at].slice(3), file + ": the title");
  assert.deepEqual(ARTICLE_9[lang].items, items, file + ": ARTICLE_9 matches Article 9 word for word");
  assert.ok(!/시험 키|trial key/.test(items.join(" ")), file + ": no trial key the seller does not offer");
}
assert.deepEqual((await j("/support/info?lang=en")).policy, ARTICLE_9.en);
assert.deepEqual((await j("/support/info?lang=en")).faq, DEFAULT_FAQ.en);
assert.equal(DEFAULT_FAQ.ko.length, DEFAULT_FAQ.en.length);
for (const f of [...DEFAULT_FAQ.ko, ...DEFAULT_FAQ.en]) assert.ok(f.q.length <= LIMITS.faqQ && f.a.length <= LIMITS.faqA && DEFAULT_FAQ.ko.length <= LIMITS.faq);
assert.equal((await j("/support/nothing")).http, 404);

// ---------------------------------------------------------------- create: validation, consent
const bads = [
  [{ consent: false }, "consent"], [{ consent: "yes" }, "consent"], [{ consent: undefined }, "consent"],
  [{ cat: "spam" }, "cat"], [{ cat: undefined }, "cat"],
  [{ subject: "" }, "subject"], [{ subject: "   " }, "subject"], [{ subject: "가".repeat(81) }, "subject"], [{ subject: 5 }, "subject"],
  [{ text: "" }, "text"], [{ text: "x".repeat(2001) }, "text"],
  [{ contact: "c".repeat(121) }, "contact"],
  [{ order: "SB-0000-0000" }, "order"], [{ order: "ORDER" }, "order"], [{ order: 12 }, "order"],
  [{ keyId: "xyz" }, "keyId"], [{ keyId: "0123456789abcdef0" }, "keyId"], [{ keyId: "0".repeat(65) }, "keyId"],
  [{ key: "not a key" }, "bad key"], [{ key: 5 }, "bad key"], [{ key: "a".repeat(LIMITS.key) + ".b" }, "bad key"], [{ key: "a.b.c" }, "bad key"],
  [{ subject: "가" + " ".repeat(LIMITS.subject * 4 + 16) }, "subject"], [{ text: "x" + "\n".repeat(LIMITS.text * 4 + 16) }, "text"],
  [{ contact: " ".repeat(LIMITS.contact * 4 + 17) }, "contact"], [{ order: " ".repeat(60) + "SB-ABCD-EFGH" }, "order"],
  [{ lang: "fr" }, "lang"],
  [{ cat: "refund", refund: { reason: "change_mind" } }, "order"],
  [{ cat: "refund", order: "SB-ABCD-EFGH", refund: { reason: "because" } }, "reason"],
  [{ cat: "refund", order: "SB-ABCD-EFGH" }, "reason"],
];
for (const [over, field] of bads) {
  const { r, w } = await counted(() => create(over));
  assert.deepEqual([r.http, r.error], [400, field], JSON.stringify(over));
  assert.equal(w, 0, "a refused request writes nothing");
}
assert.deepEqual(await post("/support/ticket", "not json"), { http: 400, error: "body" });
assert.deepEqual(await post("/support/ticket", "[1]"), { http: 400, error: "body" });

// L2: a huge body is refused after LIMITS.body bytes, before any parsing or cleaning (it took ~85 ms of CPU before)
const hugeReq = (path, extra = {}) => new Request("https://lic.example" + path, { method: "POST", headers: { "Content-Type": "application/json", ...freshIp(), ...extra },
                                                                                body: JSON.stringify({ ...base, subject: "a".repeat(8 * 1024 * 1024) }) });
for (const path of ["/support/ticket", "/support/ticket/reply", "/support/ticket/close", "/support/tickets/status", "/support/subscription"]) {
  const rq8 = hugeReq(path), w8 = writes, t8 = performance.now();
  const r8 = await worker.fetch(rq8, env);
  const ms8 = performance.now() - t8;
  assert.deepEqual([r8.status, (await r8.json()).error, writes - w8], [413, "too large", 0], path);
  assert.ok(ms8 < 25, path + ": 8 MB body refused in " + ms8.toFixed(1) + " ms");
}
let r8 = await worker.fetch(new Request("https://lic.example/support/ticket", { method: "POST", headers: { "Content-Type": "application/json", "Content-Length": String(LIMITS.body + 1), ...freshIp() }, body: "{}" }), env);
assert.equal(r8.status, 413, "a Content-Length over the limit is refused unread");
// the app's JSON escapes every non-ASCII character (\uXXXX): 2,000 emoji, the longest message, still fits
const escAll = s => s.replace(/[^\x20-\x7e]/g, c => "\\u" + c.charCodeAt(0).toString(16).padStart(4, "0"));
const longest = escAll(JSON.stringify({ ...base, subject: "😀".repeat(LIMITS.subject), text: "😀".repeat(LIMITS.text), contact: "😀".repeat(LIMITS.contact) }));
assert.ok(longest.length > 25000 && longest.length < LIMITS.body, "the longest escaped request: " + longest.length + " bytes");
assert.equal((await post("/support/ticket", longest)).http, 201);
freshDay();

// ---------------------------------------------------------------- create: stored once, token only as its hash
let { r: t1, w } = await counted(() => create({ subject: "a\u0000b‮c", text: "  첫 줄\r\n둘째 줄 <script>alert(1)</script>  ", contact: "me@example.com", lang: "en", order: "sb-abcd-efgh" }));
assert.equal(t1.http, 201);
assert.match(t1.id, ID_RE);
assert.match(t1.token, /^[0-9a-f]{64}$/);
assert.equal(w, 1, "a ticket costs one KV write: the ticket (the day's budget is counted in the Stats object)");
assert.deepEqual(dayCount(), { w: 1, c: 1, r: 0 }, "the budget counts KV writes only");
assert.ok(Math.abs(counterNames("sup:" + today() + ":w")[0].exp - (Date.now() + 2 * DAY * 1000)) < 5000, "kept two days");
assert.equal(kv.has(kvDayKey()), false, "no KV counter while the object is up");
let s1 = stored(t1.id);
assert.equal(s1.tokenHash, sha(t1.token));
for (const [k, v] of kv) assert.ok(!String(v).includes(t1.token) && !k.includes(t1.token), "the token is never stored: " + k);
assert.equal(s1.subject, "a bc", "control characters out, bidi overrides out");
assert.equal(s1.messages[0].text, "첫 줄\n둘째 줄 <script>alert(1)</script>", "text is kept as data, newlines normalised");
assert.equal(s1.order, "SB-ABCD-EFGH");
assert.equal(s1.lang, "en");
assert.equal(s1.status, "open");
assert.ok(s1.consentAt && s1.created === s1.updated);
assert.equal(s1.account, undefined);
assert.equal(ttl.get("tk:" + t1.id), 365 * DAY, "how-to questions: 1 year");
assert.deepEqual(md.get("tk:" + t1.id), { cat: "howto", status: "open", subj: "a bc", created: s1.created, updated: s1.updated, last: "user", n: 1, rs: "", order: "SB-ABCD-EFGH", acct: "", key: "" });

// limits of length are inclusive
assert.equal((await create({ subject: "가".repeat(80), text: "x".repeat(2000), contact: "c".repeat(120) })).http, 201);

// member id from the account token, best effort
const tm = await create({}, { ...freshIp(), Authorization: "Bearer " + MEMBER });
assert.equal(stored(tm.id).account, "member01");
assert.equal(md.get("tk:" + tm.id).acct, "member01");
assert.equal((await create({}, { ...freshIp(), Authorization: "Bearer " + "cd".repeat(32) })).http, 201, "a stale account token does not stop a request");
supabaseUp = false;
const tm2 = await create({}, { ...freshIp(), Authorization: "Bearer " + MEMBER });
assert.equal(tm2.http, 201, "Supabase paused: the request still goes in");
assert.equal(stored(tm2.id).account, undefined);
supabaseUp = true;

// app info only when sent, cut to 80 characters, unknown fields dropped
const td = await create({ cat: "bug", diag: { app: "0.8.11", os: "Windows 11 Pro 10.0.26200", gpu: "G".repeat(200), cpu: "secret" } });
assert.deepEqual(stored(td.id).diag, { app: "0.8.11", os: "Windows 11 Pro 10.0.26200", gpu: "G".repeat(80) });
assert.equal(stored((await create({ diag: "x" })).id).diag, undefined);

// ---------------------------------------------------------------- public view, the same 404 for an unknown id and a bad token
let v1 = await view(t1.id, t1.token);
assert.equal(v1.http, 200);
assert.equal(v1.ticket.id, t1.id);
assert.equal(v1.ticket.tokenHash, undefined);
assert.equal(v1.ticket.contact, "me@example.com");
assert.equal((await view(t1.id.toLowerCase(), t1.token)).http, 200, "a typed id may be lower case");
const vm = await view(tm.id, tm.token);
assert.equal(vm.ticket.account, undefined, "the member id stays with the seller");
assert.equal((await view(td.id, td.token)).ticket.diag, undefined, "app info stays with the seller");
const nf = { http: 404, error: "not found" };
assert.deepEqual(await view(t1.id, "0".repeat(64)), nf, "wrong token");
assert.deepEqual(await view(t1.id, t1.token.slice(0, 63)), nf, "short token");
assert.deepEqual(await view(t1.id, t1.token.toUpperCase()), nf, "token is exact");
assert.deepEqual(await view(t1.id, ""), nf);
assert.deepEqual(await view("T-AAAA-AAAA", t1.token), nf, "unknown ticket, same answer");
assert.deepEqual(await view("../tk:x", t1.token), nf);
assert.deepEqual(await view(tm.id, t1.token), nf, "one ticket's token does not open another");
assert.deepEqual(await reply(t1.id, "0".repeat(64), "hi"), nf);
assert.deepEqual(await closeT("T-AAAA-AAAA", t1.token), nf);
let r0 = await counted(() => view(t1.id, t1.token));
assert.equal(r0.w, 0, "reading without news writes nothing");
// M2: the token never travels in a URL. Any ?token= is refused, so a client that still puts it there shows up at once.
const inUrl = { http: 400, error: "token in url" };
r0 = await counted(async () => [
  await j("/support/ticket?id=" + t1.id + "&token=" + t1.token),
  await j("/support/ticket?id=" + t1.id + "&token=" + t1.token, { headers: { "X-Ticket-Token": t1.token } }),
  await post("/support/ticket/reply?id=" + t1.id + "&token=" + t1.token, { id: t1.id, token: t1.token, text: "주소에 코드" }),
  await post("/support/ticket/close?token=" + t1.token, { id: t1.id, token: t1.token }),
  await post("/support/tickets/status?token=x", { tickets: [{ id: t1.id, token: t1.token }] }),
  await post("/support/ticket?token=", base)]);
assert.deepEqual(r0.r, Array(6).fill(inUrl));
assert.equal(r0.w, 0);
assert.equal(stored(t1.id).messages.length, 1, "nothing was posted");
assert.deepEqual(await j("/support/ticket?id=" + t1.id), nf, "no header: the same 404");
assert.deepEqual(await j("/support/ticket?id=" + t1.id, { headers: { "X-Ticket-Token": "0".repeat(64) } }), nf, "wrong header token: the same 404");
assert.deepEqual(await post("/support/ticket/reply", { id: t1.id, text: "코드 없이" }), nf, "no token in the body: the same 404");
assert.deepEqual(await post("/support/ticket/close", { id: t1.id }), nf);
assert.equal((await post("/support/ticket/reply", { id: t1.id.toLowerCase(), token: t1.token, text: "본문의 코드" })).http, 200, "the body carries id and token");

// ---------------------------------------------------------------- per-IP limits: the Stats object; the Cache API and memory while it is down; never KV
freshDay();
const ipA = { "CF-Connecting-IP": "198.51.100.7" };
for (let i = 0; i < LIMITS.createPerIp; i++) assert.equal((await create({}, ipA)).http, 201);
let rl = await counted(() => create({}, ipA));
assert.deepEqual(rl.r, BUSY_IP, "6th request in an hour from one IP");
assert.equal(rl.w, 0, "a refused request writes nothing");
r429 = await call("/support/ticket", { method: "POST", headers: { "Content-Type": "application/json", ...ipA }, body: JSON.stringify(base) });
const toHour = 3600 - Math.floor(Date.now() / 1000) % 3600, raIp = Number(r429.headers.get("Retry-After"));
assert.deepEqual([r429.status, (await r429.json()).scope], [429, "ip"]);
assert.ok(raIp >= 1 && raIp <= 3600 && Math.abs(raIp - toHour) <= 2, "ip: Retry-After = seconds to the end of this hour's count, " + raIp);
assert.equal((await create({}, { "CF-Connecting-IP": "198.51.100.8" })).http, 201, "other IPs are not held up");
_forgetMemory(); cacheOn = false;
assert.deepEqual(await create({}, ipA), BUSY_IP, "another isolate, no cache: the object still counts");
cacheOn = true;
const rlRows = counterNames("rl:new:%");
assert.ok(rlRows.length && rlRows.every(r => /^rl:new:[0-9a-f]{16}:\d{4}-\d\d-\d\dT\d\d$/.test(r.name) && r.exp <= Date.now() + 3600e3), "IP only as a 16-hex hash, kept an hour");
// tries at once from one address are counted one by one (the object counts in one step)
const ipE = { "CF-Connecting-IP": "198.51.100.12" };
const burstE = await Promise.all(Array.from({ length: 12 }, () => create({}, ipE)));
assert.equal(burstE.filter(x => x.http === 201).length, LIMITS.createPerIp, "12 at once: " + LIMITS.createPerIp + " get in");
// while the object cannot be reached: the Cache API and this isolate's memory (approximate), and the KV day counter
statsDown = true;
const madeWhileDown = [];   // no feed row for these (the feed is in the object too); deleted below
const ipD = { "CF-Connecting-IP": "198.51.100.11" };
for (let i = 0; i < LIMITS.createPerIp; i++) { const t = await create({}, ipD); assert.equal(t.http, 201); madeWhileDown.push(t.id); }
assert.deepEqual(await create({}, ipD), BUSY_IP, "object down: the fallback counts");
_forgetMemory();
assert.deepEqual(await create({}, ipD), BUSY_IP, "another isolate in the same place: the Cache API still knows");
cacheOn = false;   // *.workers.dev: the Cache API may keep nothing, so this isolate's memory counts alone
const ipC = { "CF-Connecting-IP": "198.51.100.10" };
for (let i = 0; i < LIMITS.createPerIp; i++) { const t = await create({}, ipC); assert.equal(t.http, 201); madeWhileDown.push(t.id); }
assert.deepEqual(await create({}, ipC), BUSY_IP, "no cache: the memory path holds");
cacheOn = true;
assert.deepEqual(JSON.parse(kv.get(kvDayKey())), { w: 2 * LIMITS.createPerIp * 2, c: 2 * LIMITS.createPerIp, r: 0 }, "the KV counter took over the budget, its own writes counted");
statsDown = false;
assert.ok(![...kv.keys()].some(k => k.includes("rl:") || /^count:support:(new|reply|status|sub|day)/.test(k)), "no per-IP counter in KV");
assert.deepEqual([...kv.keys()].filter(k => k.startsWith("count:support:")), [kvDayKey()], "one KV counter for support, only while the object was down");
for (const id of madeWhileDown) assert.equal((await adm("/support/delete", { id })).ok, true);
const rlPuts = cachePuts.filter(p => p.u.includes("rl%3Anew%3A"));
assert.ok(rlPuts.length && rlPuts.every(p => /^https:\/\/lic\.example\/__support-cache\/rl%3Anew%3A[0-9a-f]{16}%3A\d{4}-\d\d-\d\dT\d\d$/.test(p.u) && p.age === 3600),
          "IP only as a 16-hex hash, kept an hour");
for (const [k, v] of kv) assert.ok(!k.includes("198.51.100") && !String(v).includes("198.51.100") && !k.includes("203.0.113") && !String(v).includes("203.0.113"), "no IP address stored: " + k);
for (const p of cachePuts) assert.ok(!p.u.includes("198.51.100") && !p.u.includes("203.0.113"), "no IP address in the cache either");

// IPv6 by its /64: one home or phone network is one bucket, however many addresses it uses
assert.equal(ipBucket("2001:db8:1:2:aaaa::1"), "2001:db8:1:2::/64");
assert.equal(ipBucket("2001:0DB8:0001:0002:ffff:0:0:9"), "2001:db8:1:2::/64");
assert.equal(ipBucket("2001:db8::1"), "2001:db8:0:0::/64");
assert.equal(ipBucket("fe80::1%eth0"), "fe80:0:0:0::/64");
assert.equal(ipBucket("::ffff:198.51.100.7"), "198.51.100.7", "an IPv4-mapped address is that IPv4");
assert.equal(ipBucket("198.51.100.7"), "198.51.100.7");
assert.equal(ipBucket(null), "unknown");
for (let i = 0; i < LIMITS.createPerIp; i++) assert.equal((await create({}, { "CF-Connecting-IP": "2001:db8:aa:bb:" + i + "::" + (i + 1) })).http, 201);
assert.deepEqual(await create({}, { "CF-Connecting-IP": "2001:db8:aa:bb:ffff:ffff:ffff:ffff" }), BUSY_IP, "a new address in the same /64");
assert.equal((await create({}, { "CF-Connecting-IP": "2001:db8:aa:bc::1" })).http, 201, "the next /64 is someone else");
assert.deepEqual(await create({}, { "CF-Connecting-IP": "::ffff:198.51.100.7" }), BUSY_IP, "ipA again, written as IPv6");

// the day's caps: requests and replies each have their own, below the write budget
freshDay();
const tr = await create();
setDay({ w: 10, c: LIMITS.createsPerDay, r: 0 });
assert.deepEqual(await create(), BUSY_DAY, LIMITS.createsPerDay + " new requests a day");
assert.equal((await reply(tr.id, tr.token, "답장은 돼요")).http, 200, "replies have their own cap");
setDay({ w: 10, c: 0, r: LIMITS.repliesPerDay });
rl = await counted(() => reply(tr.id, tr.token, "하루 답장 끝"));
assert.deepEqual([rl.r, rl.w], [BUSY_DAY, 0], LIMITS.repliesPerDay + " replies a day");
assert.equal((await create()).http, 201, "requests have their own cap");
// neither the object nor the KV counter can count (KV's daily writes used up): nothing is written, try again later
statsDown = true;
const realPut = env.LICENSES.put;
env.LICENSES.put = async (k, ...rest) => { if (k.startsWith("count:support:")) throw new Error("KV PUT failed: 429 Too Many Requests"); return realPut(k, ...rest); };
rl = await counted(() => create());
assert.deepEqual([rl.r, rl.w], [BUSY_IP, 0], "the count could not be written: nothing else is either, try again in a moment");
env.LICENSES.put = realPut;
statsDown = false;
freshDay();

// replies: 20 per IP an hour, 30 messages a ticket
const ipB = { "CF-Connecting-IP": "198.51.100.9" };
let rr = await counted(() => reply(tr.id, tr.token, "추가 1", ipB));
assert.equal(rr.r.http, 200);
assert.equal(rr.w, 1, "a reply costs one KV write: the ticket");
assert.deepEqual(dayCount(), { w: 1, c: 0, r: 1 });
for (let i = 2; i <= LIMITS.repliesPerIp; i++) assert.equal((await reply(tr.id, tr.token, "추가 " + i, ipB)).http, 200);
assert.deepEqual(await reply(tr.id, tr.token, "하나 더", ipB), BUSY_IP);
assert.ok(counterNames("rl:reply:%").some(r => /^rl:reply:[0-9a-f]{16}:/.test(r.name)));
while (stored(tr.id).messages.length < LIMITS.messages) assert.equal((await reply(tr.id, tr.token, "더", freshIp())).http, 200);
assert.deepEqual(await reply(tr.id, tr.token, "31번째", freshIp()), { http: 409, error: "full" });
assert.deepEqual(await reply(tr.id, tr.token, "", freshIp()), { http: 400, error: "text" });
assert.deepEqual(await reply(t1.id, t1.token, "y".repeat(2001)), { http: 400, error: "text" });
assert.equal(md.get("tk:" + tr.id).n, 30);

// ---------------------------------------------------------------- reply, close, reopen window
freshDay();
let a = await adm("/support/reply", { id: t1.id, text: "설정에서 다시 켜 보세요" });
assert.equal(a.http, 200);
assert.equal(a.ticket.status, "answered");
assert.equal(a.ticket.unreadUser, true);
assert.equal(a.ticket.tokenHash, undefined, "the dashboard never sees the token hash either");
assert.equal(md.get("tk:" + t1.id).last, "admin");
r0 = await counted(() => view(t1.id, t1.token));
assert.equal(r0.r.ticket.unreadUser, true, "the reply that clears the flag still says it was unread");
assert.equal(r0.w, 1, "clearing the flag is one write");
assert.equal(stored(t1.id).unreadUser, false);
assert.deepEqual(dayCount(), {}, "the seller's reply and the cleared flag are outside the budget");
r0 = await counted(() => view(t1.id, t1.token));
assert.deepEqual([r0.r.ticket.unreadUser, r0.w], [false, 0]);
assert.equal(r0.r.ticket.messages.at(-1).from, "admin");

let u = await reply(t1.id, t1.token, "됐어요, 고마워요");
assert.equal(u.ticket.status, "open", "the buyer's reply opens it again");
u = await closeT(t1.id, t1.token);
assert.equal(u.ticket.status, "closed");
assert.ok(u.ticket.closedAt);
assert.equal((await counted(() => closeT(t1.id, t1.token))).w, 0, "closing a closed ticket writes nothing");
u = await reply(t1.id, t1.token, "다시 질문이 있어요");
assert.equal(u.ticket.status, "open", "reopened within 30 days");
assert.equal(u.ticket.closedAt, undefined);
await closeT(t1.id, t1.token);
let raw = stored(t1.id);
raw.closedAt = ago(31);
kv.set("tk:" + t1.id, JSON.stringify(raw));
assert.deepEqual(await reply(t1.id, t1.token, "한 달 뒤"), { http: 409, error: "closed" });
raw.closedAt = ago(29);
kv.set("tk:" + t1.id, JSON.stringify(raw));
assert.equal((await reply(t1.id, t1.token, "29일 뒤")).ticket.status, "open");

// ---------------------------------------------------------------- POST under /support takes JSON only: a page on another site cannot send it
const bodyAll = JSON.stringify({ ...base, tickets: [{ id: t1.id, token: t1.token }], key: "x" });
const supportPosts = ["/support/ticket", "/support/ticket/reply", "/support/ticket/close",
                      "/support/tickets/status", "/support/subscription", "/support/nothing"];
for (const path of supportPosts)
  for (const type of [null, "text/plain", "text/plain;charset=UTF-8", "application/x-www-form-urlencoded", "multipart/form-data; boundary=x",
                      "application/jsonp", "application/json-patch+json", "text/plain; application/json", "application/javascript"]) {
    const init = { method: "POST", headers: { ...freshIp(), ...(type ? { "Content-Type": type } : {}) }, body: type ? bodyAll : new TextEncoder().encode(bodyAll) };
    const { r, w } = await counted(() => j(path, init));
    assert.deepEqual([r.http, r.error, w], [415, "content-type", 0], path + " " + type);
  }
for (const type of ["application/json", "application/json; charset=utf-8", "APPLICATION/JSON", "Application/Json;Charset=UTF-8", "application/json ; charset=utf-8"])
  assert.equal((await j("/support/tickets/status", { method: "POST", headers: { ...freshIp(), "Content-Type": type }, body: JSON.stringify({ tickets: [{ id: t1.id, token: t1.token }] }) })).http, 200, type);
for (const path of supportPosts) {
  const pre = await call(path, { method: "OPTIONS", headers: { Origin: "https://evil.example", "Access-Control-Request-Method": "POST", "Access-Control-Request-Headers": "content-type" } });
  assert.ok(pre.status >= 400, "no preflight answer for " + path);
  assert.equal(pre.headers.get("access-control-allow-headers"), null);
  assert.equal(pre.headers.get("access-control-allow-methods"), null);
}
assert.equal((await call("/order?plan=bogus", { method: "POST", headers: { "Content-Type": "text/plain" }, body: "x" })).status, 400, "other routes are as before");
assert.equal((await call("/referral", { method: "POST", headers: { "Content-Type": "text/plain" }, body: "{}" })).status, 400, "it reads the body as before");

// ---------------------------------------------------------------- POST /support/tickets/status: the app's polling, read only
freshDay();
const st = (tickets, headers) => post("/support/tickets/status", { tickets }, headers);
const tS = await create({ subject: "상태 확인" });
await adm("/support/reply", { id: tS.id, text: "확인했어요" });
for (const b of ["not json", "[]", "null", {}, { tickets: [] }, { tickets: "x" }, { tickets: {} }, { tickets: Array(LIMITS.statusBatch + 1).fill({ id: tS.id, token: tS.token }) },
                 { tickets: [null] }, { tickets: ["T-AAAA-AAAA"] }, { tickets: [{ id: tS.id }] }, { tickets: [{ id: 5, token: tS.token }] },
                 { tickets: [{ id: "x".repeat(65), token: tS.token }] }, { tickets: [{ id: tS.id, token: "f".repeat(129) }] }])
  assert.deepEqual(await post("/support/tickets/status", b), { http: 400, error: "tickets" }, JSON.stringify(b).slice(0, 60));
const t1s = stored(t1.id), tSs = stored(tS.id);
let sres = await counted(() => st([{ id: tS.id, token: tS.token }, { id: t1.id, token: t1.token }, { id: tm.id, token: t1.token }, { id: "T-AAAA-AAAA", token: t1.token },
                                   { id: "../tk:x", token: "" }, { id: "__proto__", token: tS.token }, { id: tS.id, token: "0".repeat(64) }]));
assert.equal(sres.r.http, 200);
assert.equal(sres.w, 0, "polling writes nothing");
assert.deepEqual(sres.r.tickets, {
  [tS.id]: { status: "answered", updated: tSs.updated, n: 2, unread: true },
  [t1.id]: { status: t1s.status, updated: t1s.updated, n: t1s.messages.length, unread: false },
  [tm.id]: null, "T-AAAA-AAAA": null, "../tk:x": null, ["__proto__"]: null }, "null: unknown id or wrong token, the same answer");
assert.equal(stored(tS.id).unreadUser, true, "polling leaves the unread flag to the ticket view");
let rd = reads;
assert.equal((await st(Array.from({ length: LIMITS.statusBatch }, () => ({ id: tS.id, token: tS.token })))).http, 200, LIMITS.statusBatch + " at once");
assert.equal(reads - rd, 1, "the same ticket twenty times is read once");
rd = reads;
assert.deepEqual((await st([tS, t1, tm, td].map(t => ({ id: t.id, token: "nope" })))).tickets, { [tS.id]: null, [t1.id]: null, [tm.id]: null, [td.id]: null });
assert.equal(reads - rd, 0, "no KV read for what cannot be a token");
assert.equal((await view(tS.id, tS.token)).ticket.unreadUser, true);
assert.equal((await st([{ id: tS.id, token: tS.token }])).tickets[tS.id].unread, false, "read after the view");
const ipS = { "CF-Connecting-IP": "198.51.100.20" };
sres = await counted(async () => { for (let i = 0; i < LIMITS.statusPerIp; i++) assert.equal((await st([{ id: tS.id, token: tS.token }], ipS)).http, 200); });
assert.equal(sres.w, 0, LIMITS.statusPerIp + " polls, no KV write");
assert.deepEqual(await st([{ id: tS.id, token: tS.token }], ipS), BUSY_IP, "121st poll in an hour from one IP");
assert.equal((await st([{ id: tS.id, token: tS.token }])).http, 200, "other IPs are not held up");
assert.deepEqual(dayCount(), { w: 1, c: 1, r: 0 }, "polling spends nothing of the day's budget");

// ---------------------------------------------------------------- admin: list, filters, read, status, delete
assert.equal((await j("/admin/api/support")).http, 401, "admin API needs the password");
assert.equal((await call("/admin/api/support", { headers: { Authorization: "Bearer " + env.ADMIN_TOKEN } })).headers.get("access-control-allow-origin"), null, "no CORS on the admin API");
const tA = await create({ subject: "답변할 문의" });
await adm("/support/reply", { id: tA.id, text: "답변" });
const tC = await create({ subject: "닫을 문의" });
assert.equal((await adm("/support/status", { id: tC.id, status: "closed" })).ticket.status, "closed");
assert.deepEqual([(await adm("/support/status", { id: tC.id, status: "answered" })).http], [400]);
assert.equal((await adm("/support/status", { id: "T-AAAA-AAAA", status: "open" })).http, 404);
assert.equal((await adm("/support/reply", { id: tA.id, text: "" })).http, 400);
assert.equal((await adm("/support/reply", { id: "T-AAAA-AAAA", text: "x" })).http, 404);
assert.equal((await adm("/support/reply", { id: tr.id, text: "x" })).http, 409, "30 messages: full for the seller too");
const tX = await create({ subject: "답하고 닫기" });
const ax = await adm("/support/reply", { id: tX.id, text: "답하고 닫아요", close: true });
assert.deepEqual([ax.ticket.status, !!ax.ticket.closedAt, ax.ticket.unreadUser], ["closed", true, true]);

let L = await adm("/support");
assert.ok(L.tickets.length && L.tickets.every(x => x.status === "open"), "default: open");
assert.ok(L.tickets.every(x => ID_RE.test(x.id) && x.tokenHash === undefined));
for (let i = 1; i < L.tickets.length; i++) assert.ok(L.tickets[i - 1].updated >= L.tickets[i].updated, "newest first");
const allRows = [...md].filter(([k]) => k.startsWith("tk:")).map(([, m]) => m);
assert.deepEqual(L.count, { open: allRows.filter(m => m.status === "open").length, answered: allRows.filter(m => m.status === "answered").length,
                            closed: allRows.filter(m => m.status === "closed").length, refund: 0 });
assert.ok((await adm("/support?status=answered")).tickets.some(x => x.id === tA.id));
assert.ok((await adm("/support?status=answered")).tickets.every(x => x.status === "answered"));
assert.ok((await adm("/support?status=closed")).tickets.some(x => x.id === tC.id));
const everything = (await adm("/support?status=all")).tickets;
assert.equal(everything.length, allRows.length);
const firstNotOpen = everything.findIndex(x => x.status !== "open");
assert.ok(everything.slice(firstNotOpen).every(x => x.status !== "open"), "open ones first");
// a search goes in a POST body: its words (order codes, member ids) stay out of addresses and logs
const findT = q => adm("/support", { status: "all", q });
assert.deepEqual((await findT("닫을")).tickets.map(x => x.id), [tC.id], "search the subject");
assert.deepEqual((await findT("sb-abcd")).tickets.map(x => x.id), [t1.id], "search the order code");
assert.deepEqual((await findT(tA.id.toLowerCase())).tickets.map(x => x.id), [tA.id], "search the id");
assert.deepEqual((await findT("member01")).tickets.map(x => x.id), [tm.id], "search the member");
assert.deepEqual(await adm("/support?status=all&q=member01"), { http: 400, error: "검색어는 POST 본문으로 보내요" }, "not in the address");
assert.equal((await adm("/support", { status: "open" })).tickets.every(x => x.status === "open"), true, "POST without words: the list");
assert.equal((await adm("/support?status=bogus")).tickets.every(x => x.status === "open"), true);
let full = await adm("/support/ticket?id=" + td.id);
assert.deepEqual(full.ticket.diag.app, "0.8.11");
assert.equal(full.ticket.tokenHash, undefined);
assert.deepEqual(full.keys, []);
assert.equal((await adm("/support/ticket?id=T-AAAA-AAAA")).http, 404);
const tDel = await create({ subject: "지울 문의" });
assert.equal((await adm("/support/delete", { id: tDel.id })).ok, true);
assert.equal(kv.has("tk:" + tDel.id), false);
assert.deepEqual(await view(tDel.id, tDel.token), nf);
assert.equal((await adm("/support/delete", { id: "nope" })).http, 400);
assert.ok(await logHas("문의 삭제"));

// ---------------------------------------------------------------- refund: manual payment, approve / reject / done
freshDay();
await adm("/pay-links", { yearly: "https://pay.example/y", monthly: "https://pay.example/m" });
const mo = await j("/order?plan=yearly&method=manual", { method: "POST" });   // no Content-Type: /order is as before
assert.match(mo.code, /^SB-/);
await j("/order/paid?code=" + mo.code, { method: "POST", body: JSON.stringify({ payer: "홍길동" }) });
const mk = await adm("/orders/approve", { code: mo.code });
const mkId = keyIdOf(mk.key);
assert.equal(mk.id, mkId, "the test computes key ids like the app");
md.get("lic:" + mkId).issued = ago(10);   // the key came 10 days ago
let s0 = await adm("/summary");
let rq = await counted(() => create({ cat: "refund", subject: "환불 요청", text: "쓰지 않게 됐어요", order: mo.code, refund: { reason: "change_mind" } }));
assert.equal(rq.r.http, 201);
assert.equal(rq.w, 2, "a refund request adds one write: the key index");
assert.deepEqual(dayCount(), { w: 0, c: 0, r: 0, vw: 2, vc: 1, vr: 0, by: { ["o:" + mo.code]: 1 } }, "the code of an approved order: the buyers' lane");
const t9 = rq.r;
let s9 = stored(t9.id);
assert.deepEqual([s9.orderMatched, s9.owner, s9.keyId, s9.keyVerified], [true, "o:" + mo.code, undefined, undefined]);
assert.deepEqual(s9.refund.facts, { plan: "yearly", issued: md.get("lic:" + mkId).issued, expires: md.get("lic:" + mkId).expires, src: "payment",
                                    order: mo.code, payer: "홍길동", orderStatus: "approved", daysSinceKey: 10, keyId: mkId });
assert.deepEqual([s9.refund.status, s9.refund.reason, s9.refund.method], ["requested", "change_mind", "manual"]);
assert.equal(ttl.get("tk:" + t9.id), 1095 * DAY, "refund requests: 3 years");
assert.equal(kv.get("tkkey:" + mkId), t9.id);
assert.equal(ttl.get("tkkey:" + mkId), 1095 * DAY);
assert.equal(md.get("tk:" + t9.id).rs, "requested");
let pv = (await view(t9.id, t9.token)).ticket.refund;
assert.deepEqual(pv.facts, { issued: s9.refund.facts.issued, plan: "yearly", daysSinceKey: 10 }, "the buyer sees only issued, plan, days");
assert.equal(pv.revoked, undefined);
let s1b = await adm("/summary");
assert.equal(s1b.counts.ticketsOpen, s0.counts.ticketsOpen + 1);
assert.equal(s1b.counts.refundsOpen, s0.counts.refundsOpen + 1);
assert.equal((await adm("/support?status=refund")).tickets[0].id, t9.id);
assert.equal((await adm("/support")).count.refund, 1);
assert.deepEqual((await adm("/support/ticket?id=" + t9.id)).keys.map(k => [k.id, k.state]), [[mkId, "active"]]);

assert.equal((await adm("/support/refund", { id: t1.id, action: "approve" })).http, 400, "not a refund request");
assert.equal((await adm("/support/refund", { id: t9.id, action: "maybe" })).http, 400);
assert.equal((await adm("/support/refund", { id: t9.id, action: "approve", amount: -5 })).http, 400);
assert.equal((await adm("/support/refund", { id: t9.id, action: "approve", amount: "lots" })).http, 400);
let ap = await adm("/support/refund", { id: t9.id, action: "approve", amount: 9900 });
assert.deepEqual([ap.ticket.refund.status, ap.ticket.refund.amount, ap.ticket.status, ap.ticket.unreadUser], ["approved", 9900, "answered", true]);
assert.equal(ap.ticket.messages.at(-1).text, "환불 요청을 승인했어요. 환불을 마치면 여기로 알려 드릴게요.", "a default message when the seller writes none");
assert.ok(await logHas("환불 승인"));
assert.equal((await adm("/support?status=refund")).tickets.some(x => x.id === t9.id), true, "approved still counts as an open refund");
assert.equal((await adm("/support/refund", { id: t9.id, action: "reject" })).http, 400, "rejecting needs a reason");
let rj = await adm("/support/refund", { id: t9.id, action: "reject", reason: "키를 이미 등록해 쓰셨어요" });
assert.deepEqual([rj.ticket.refund.status, rj.ticket.refund.rejectReason], ["rejected", "키를 이미 등록해 쓰셨어요"]);
assert.equal(rj.ticket.messages.at(-1).text, "환불 요청을 받아들이지 못했어요. 사유: 키를 이미 등록해 쓰셨어요\n궁금한 점은 이 문의에 답장해 주세요.");
assert.ok(await logHas("환불 거절"));
assert.equal((await view(t9.id, t9.token)).ticket.refund.rejectReason, "키를 이미 등록해 쓰셨어요", "the buyer sees the reason");
assert.equal((await adm("/support?status=refund")).tickets.some(x => x.id === t9.id), false);
let dn = await adm("/support/refund", { id: t9.id, action: "done", message: "계좌로 돌려 드렸어요" });
assert.equal(dn.http, 200);
assert.deepEqual(dn.revoked, [mkId]);
assert.deepEqual([dn.ticket.refund.status, dn.ticket.refund.revoked, dn.ticket.refund.rejectReason], ["done", [mkId], undefined]);
assert.equal(dn.ticket.messages.at(-1).text, "계좌로 돌려 드렸어요");
assert.equal((await j("/check?id=" + mkId)).revoked, true, "done blocks the key");
assert.deepEqual(await j("/claim?session=" + mo.code), { http: 200, status: "pending", order: "refunded" }, "the app can no longer claim the key");
assert.equal(kv.has("session:" + mo.code), false);
assert.equal(md.get("lic:" + mkId).revoked, "환불");
let ords = await adm("/orders?status=refunded");
assert.deepEqual(ords.orders.map(o => [o.code, o.status]), [[mo.code, "refunded"]]);
assert.equal(ords.count.refunded, 1, "refunded is an order state");
assert.equal((await adm("/orders")).orders.some(o => o.code === mo.code), false, "a refunded order is not open");
assert.equal((await adm("/orders/approve", { code: mo.code })).http, 409, "a refunded order is not approved again");
// nor issued again through the key form with its code (that would set it back to approved and make /claim hand out a key)
let wi = writes;
let reissue = await adm("/issue", { code: mo.code, plan: "yearly", note: "다시" });
assert.equal(reissue.http, 409);
assert.match(reissue.error, /환불한 주문이에요/);
assert.deepEqual([reissue.key, reissue.status, writes - wi], [undefined, undefined, 0], "no key made, nothing written, no status field in the body");
assert.equal((await adm("/orders?status=refunded")).orders.find(o => o.code === mo.code).status, "refunded");
assert.equal(kv.has("session:" + mo.code), false, "nothing to claim");
reissue = await adm("/issue", { code: mo.code.toLowerCase(), plan: "yearly" });
assert.equal(reissue.http, 409, "the code as typed in lower case too");
assert.ok((await adm("/issue", { plan: "yearly", note: "환불 뒤 새로 판매" })).key, "a key without that code is still issued");
assert.equal((await adm("/support/refund", { id: t9.id, action: "done" })).http, 409, "done once");
assert.equal((await adm("/support/refund", { id: t9.id, action: "approve" })).http, 409);
assert.ok(await logHas("환불 완료"));

// a 2-key bundle bought by direct payment: the request names one PC's key, done blocks the whole order
const bundle = await adm("/issue", { code: "SB-BNDL-2345", plan: "yearly_x5", seats: 2 });
assert.equal(bundle.ids.length, 2);
const other = await adm("/issue", { plan: "yearly", note: "선물" });
const tb = await create({ cat: "refund", subject: "묶음 환불", text: "두 대 모두 환불해 주세요", key: bundle.keys[0], refund: { reason: "not_working" }, lang: "en" });
let sb = stored(tb.id);
assert.deepEqual([sb.refund.method, sb.refund.facts.order, sb.refund.facts.plan], ["manual", "SB-BNDL-2345", "yearly_x5"], "the order is found from the key's note");
assert.deepEqual([sb.keyId, sb.keyVerified], [bundle.ids[0], true], "the app sends the full key: checked, kept as its key id");
let bk = await adm("/support/ticket?id=" + tb.id);
assert.deepEqual(bk.keys.map(k => k.id).sort(), [...bundle.ids].sort(), "every key of the order");
assert.ok(bk.keys.every(k => k.note.startsWith("SB-BNDL-2345") && k.state === "active" && k.plan === "premium"));
assert.ok(bk.keys.every(k => k.owned), "a signed key shows its order: every key of it is the buyer's");
dn = await adm("/support/refund", { id: tb.id, action: "done", amount: "19,800" });
assert.deepEqual([...dn.revoked].sort(), [...bundle.ids].sort());
assert.equal(dn.ticket.refund.amount, 19800);
assert.match(dn.ticket.messages.at(-1).text, /refund is complete.*blocked/, "the default message in the buyer's language");
for (const id of bundle.ids) assert.equal((await j("/check?id=" + id)).revoked, true);
assert.equal((await j("/check?id=" + other.id)).revoked, false, "keys of other orders stay");
assert.equal((await adm("/orders?status=refunded")).count.refunded, 2);
assert.ok(!kv.has("session:SB-BNDL-2345") && !kv.has("bundle:SB-BNDL-2345"), "neither the first key nor the bundle list can be claimed");
assert.deepEqual(await j("/claim?session=SB-BNDL-2345"), { http: 200, status: "pending", order: "refunded" });

// The full key, as the app sends it: checked by its signature, kept only as its key id, facts shown to the buyer.
const mo3 = await j("/order?plan=yearly&method=manual", { method: "POST" });
const mk3 = await adm("/orders/approve", { code: mo3.code });
md.get("lic:" + mk3.id).issued = ago(2);
const tv3 = await counted(() => create({ cat: "refund", subject: "환불", text: "환불해 주세요", key: "  " + mk3.key + "\n", refund: { reason: "change_mind" } }));
assert.deepEqual([tv3.r.http, Object.keys(tv3.r).sort(), tv3.w], [201, ["http", "id", "token"], 2], "the answer is only the id and the token");
const sv3 = stored(tv3.r.id);
assert.deepEqual([sv3.keyId, sv3.keyVerified, sv3.owner, sv3.orderMatched], [mk3.id, true, "o:" + mo3.code, undefined], "a paid key's purchase is its order");
for (const [k, v] of kv) if (/^(tk|count:support)/.test(k)) assert.ok(!String(v).includes(mk3.key) && !JSON.stringify(md.get(k) || "").includes(mk3.key), "the key text is not kept: " + k);
assert.equal(kv.get("tkkey:" + mk3.id), tv3.r.id, "a shown key gets the webhook index");
const pv3 = (await view(tv3.r.id, tv3.r.token)).ticket;
assert.deepEqual([pv3.keyId, pv3.keyVerified, pv3.refund.facts], [mk3.id, true, { issued: md.get("lic:" + mk3.id).issued, plan: "yearly", daysSinceKey: 2 }], "verified: the facts are shown");
assert.ok(!JSON.stringify(pv3).includes(mk3.key.split(".")[1]), "the key never comes back");
assert.deepEqual((await adm("/support/ticket?id=" + tv3.r.id)).keys.map(k => [k.id, k.owned]), [[mk3.id, true]]);
dn = await adm("/support/refund", { id: tv3.r.id, action: "done" });
assert.deepEqual([dn.http, dn.revoked], [200, [mk3.id]], "a key the buyer showed is blocked without a second question");
// a key that does not check out, and a key with another key id
const [p3, s3] = mk3.key.split(".");
const forged3 = Buffer.from(JSON.stringify({ ...JSON.parse(Buffer.from(p3, "base64url")), plan: "biz" })).toString("base64url") + "." + s3;
let bk3 = await counted(() => create({ cat: "refund", key: forged3, refund: { reason: "other" } }));
assert.deepEqual([bk3.r.http, bk3.r.error, bk3.w], [400, "bad key", 0], "a key the server did not sign");
bk3 = await counted(() => create({ key: mk3.key, keyId: "0123456789abcdef" }));
assert.deepEqual([bk3.r.http, bk3.r.error, bk3.w], [400, "keyId", 0], "key and key id of different keys");
assert.equal(stored((await create({ key: mk3.key, keyId: mk3.id.toUpperCase() })).id).keyVerified, true, "key and its own key id: fine");

// M1: a key id typed alone proves nothing. Somebody names another buyer's key id: the buyer view shows no facts, the
// webhook index is not taken, and '환불 완료' blocks that key only on the seller's second, explicit yes.
const victim = await adm("/issue", { code: "SB-VCTM-2345", plan: "yearly" });
const tv = await create({ cat: "refund", subject: "환불", text: "환불해 주세요", keyId: victim.id, refund: { reason: "change_mind" } });
const sv = stored(tv.id);
assert.deepEqual([sv.keyId, sv.keyVerified, sv.owner, sv.orderMatched], [victim.id, false, undefined, undefined]);
assert.equal(sv.refund.facts.keyId, victim.id, "the seller still sees what the server knows");
assert.equal(kv.has("tkkey:" + victim.id), false, "no webhook index for a key nobody showed");
const pvv = (await view(tv.id, tv.token)).ticket;
assert.deepEqual([pvv.keyVerified, pvv.refund.facts], [false, {}], "no plan, date or days for a key id typed alone");
assert.deepEqual((await adm("/support/ticket?id=" + tv.id)).keys.map(k => [k.id, k.owned]), [[victim.id, false]]);
dn = await counted(() => adm("/support/refund", { id: tv.id, action: "done" }));
assert.deepEqual([dn.r.http, dn.r.unverified, dn.w], [409, [victim.id], 0]);
assert.match(dn.r.error, /^키 소유 확인 안 됨: 구매자가 키 식별 번호만 적어서/);
assert.equal((await adm("/support/refund", { id: tv.id, action: "done", confirmUnverified: "yes" })).http, 409, "only true counts");
assert.equal((await j("/check?id=" + victim.id)).revoked, false, "not blocked without the second yes");
assert.equal(stored(tv.id).refund.status, "requested");
dn = await adm("/support/refund", { id: tv.id, action: "done", confirmUnverified: true });
assert.deepEqual([dn.http, dn.revoked], [200, [victim.id]], "blocked after the seller confirmed");
assert.ok((await adm("/log")).log.some(l => l.action === "환불 완료" && l.detail.includes(tv.id) && l.detail.includes("소유 확인 안 된 키를 판매자가 확인함")));

// done without blocking (the seller keeps the key on purpose)
const mo2 = await j("/order?plan=monthly&method=manual", { method: "POST" });
const mk2 = await adm("/orders/approve", { code: mo2.code });
const tk2 = await create({ cat: "refund", subject: "중복 결제", text: "두 번 결제했어요", order: mo2.code, refund: { reason: "double" } });
dn = await adm("/support/refund", { id: tk2.id, action: "done", revoke: false, amount: 0 });
assert.deepEqual([dn.revoked, dn.ticket.refund.revoked, dn.ticket.refund.status, dn.ticket.refund.amount], [[], undefined, "done", 0]);
assert.equal(dn.ticket.messages.at(-1).text, "환불을 마쳤어요.");
assert.equal((await j("/check?id=" + mk2.id)).revoked, false);

// a refund request for something the server never issued: taken, no facts, no index entry
let { r: tz, w: wz } = await counted(() => create({ cat: "refund", subject: "환불", text: "환불", keyId: "0123456789abcdef", refund: { reason: "other" } }));
assert.equal(tz.http, 201);
assert.equal(wz, 1, "no key index for a key the server does not know");
assert.deepEqual(stored(tz.id).refund.facts, {});
assert.equal(stored(tz.id).refund.method, "unknown");
assert.equal(kv.has("tkkey:0123456789abcdef"), false);
dn = await counted(() => adm("/support/refund", { id: tz.id, action: "done" }));
assert.equal(dn.r.http, 409, "nothing to block and no paid order: done is refused");
assert.match(dn.r.error, /막을 키가 없고, 승인된\(결제가 확인된\) 주문도 아니에요/);
assert.equal(dn.w, 0);
assert.equal(stored(tz.id).refund.status, "requested");

// ---------------------------------------------------------------- order and key of different purchases
freshDay();
const moA = await j("/order?plan=yearly&method=manual", { method: "POST" });
const kA = await adm("/orders/approve", { code: moA.code });
const moB = await j("/order?plan=monthly&method=manual", { method: "POST" });   // not paid: no key yet
await j("/order/paid?code=" + moB.code, { method: "POST", body: JSON.stringify({ payer: "김구매" }) });
const tmB = await create({ cat: "refund", subject: "환불", text: "환불해 주세요", order: moB.code, keyId: kA.id, refund: { reason: "change_mind" } });
let sm = stored(tmB.id).refund;
assert.deepEqual(sm.facts, { plan: "monthly", order: moB.code, payer: "김구매", orderStatus: "paid", mismatch: true, otherKeyId: kA.id, otherOrder: moA.code },
                 "the order stays the subject; the key of the other purchase is set aside");
assert.equal(sm.method, "manual");
assert.equal(kv.has("tkkey:" + kA.id), false, "the other purchase's key is not indexed to this request");
let mb = await adm("/support/ticket?id=" + tmB.id);
assert.deepEqual(mb.keys, [], "order B has no key yet");
assert.deepEqual(mb.otherKeys.map(k => [k.id, k.state, k.note.startsWith(moA.code)]), [[kA.id, "active", true]], "the dashboard shows the other purchase");
dn = await counted(() => adm("/support/refund", { id: tmB.id, action: "done" }));
assert.equal(dn.r.http, 409);
assert.match(dn.r.error, /다른 구매의 키예요/);
assert.equal(dn.w, 0);
assert.equal((await j("/check?id=" + kA.id)).revoked, false, "A's key is not blocked by a request about B");
assert.equal((await adm("/orders?status=all")).orders.find(o => o.code === moB.code).status, "paid", "B is not marked refunded");
assert.equal((await adm("/support/ticket?id=" + t1.id)).otherKeys, undefined, "no warning without a mismatch");

const moC = await j("/order?plan=yearly&method=manual", { method: "POST" });
const kC = await adm("/orders/approve", { code: moC.code });
md.get("lic:" + kC.id).issued = ago(3);
md.get("lic:" + kA.id).issued = ago(20);
const tmC = await create({ cat: "refund", subject: "환불", text: "환불해 주세요", order: moC.code, keyId: kA.id, refund: { reason: "not_working" } });
sm = stored(tmC.id).refund;
assert.deepEqual([sm.facts.keyId, sm.facts.daysSinceKey, sm.facts.otherKeyId, sm.facts.otherOrder, sm.facts.orderStatus], [kC.id, 3, kA.id, moA.code, "approved"],
                 "days counted from C's own key");
assert.equal(kv.get("tkkey:" + kC.id), tmC.id);
mb = await adm("/support/ticket?id=" + tmC.id);
assert.deepEqual(mb.keys.map(k => k.id), [kC.id]);
assert.deepEqual(mb.otherKeys.map(k => k.id), [kA.id]);
dn = await adm("/support/refund", { id: tmC.id, action: "done" });
assert.deepEqual(dn.revoked, [kC.id], "only C's key");
assert.equal((await j("/check?id=" + kC.id)).revoked, true);
assert.equal((await j("/check?id=" + kA.id)).revoked, false);
assert.equal((await adm("/orders?status=refunded")).orders.some(o => o.code === moC.code), true);
assert.equal(kv.has("session:" + moC.code), false);
assert.equal(kv.has("session:" + moA.code), true, "A can still claim its key");
assert.equal(stored(tmC.id).keyId, kA.id, "what the buyer wrote stays as written");

// a key alone, after its order record expired (90 days): the code comes from the key's note
const old = await adm("/issue", { code: "SB-EXPD-2345", plan: "yearly_x5", seats: 2 });
kv.delete("ord:SB-EXPD-2345"); md.delete("ord:SB-EXPD-2345");
const tOld = await create({ cat: "refund", subject: "환불", text: "환불", key: old.keys[1], refund: { reason: "other" } });
sm = stored(tOld.id).refund;
assert.deepEqual([sm.method, sm.facts.order, sm.facts.orderStatus, sm.facts.mismatch], ["manual", "SB-EXPD-2345", undefined, undefined]);
dn = await adm("/support/refund", { id: tOld.id, action: "done" });
assert.deepEqual([...dn.revoked].sort(), [...old.ids].sort(), "every key of that order, though the record is gone");
assert.ok(!kv.has("session:SB-EXPD-2345") && !kv.has("bundle:SB-EXPD-2345"));
assert.equal(kv.has("ord:SB-EXPD-2345"), false, "no order record is made up");

// neither side known: nothing to compare, no warning
const tNone = await create({ cat: "refund", subject: "환불", text: "환불", order: "SB-WXYZ-2345", keyId: "abcdef0123456789", refund: { reason: "other" } });
assert.deepEqual(stored(tNone.id).refund.facts, { order: "SB-WXYZ-2345" });

// ---------------------------------------------------------------- card refunds: the webhook closes the request
await adm("/card", { store: "42", variants: { monthly: "101", yearly: "102" } });
await adm("/sales-mode", { mode: "test" });
const sub = (id, variant) => ({ type: "subscriptions", id: String(id), attributes: { status: "active", variant_id: Number(variant), order_id: 9000 + id, user_email: "b" + id + "@example.com", renews_at: "2026-12-30T10:00:00.000000Z" } });
assert.equal((await hook("subscription_created", sub(30, 101), { custom: { session: "SB-CARD-2345" } })).http, 200);
const ck = (await j("/claim?session=SB-CARD-2345")).key;
const ckId = keyIdOf(ck);
const tcard = await create({ cat: "refund", subject: "카드 환불", text: "작동하지 않아요", key: ck, refund: { reason: "not_working" } });
let sc = stored(tcard.id);
assert.deepEqual([sc.refund.method, sc.refund.facts.sub, sc.refund.facts.order, sc.refund.facts.orderStatus], ["card", "30", "SB-CARD-2345", "approved"]);
assert.equal(kv.get("tkkey:" + ckId), tcard.id);
await adm("/sales-mode", { mode: "off" });   // refunds are handled in every mode
assert.equal((await hook("order_refunded", { type: "orders", id: "9030", attributes: {} })).http, 200);
assert.equal((await j("/check?id=" + ckId)).revoked, true, "the existing webhook blocks the key");
sc = stored(tcard.id);
assert.deepEqual([sc.refund.status, sc.refund.revoked, sc.status, sc.unreadUser], ["done", [ckId], "answered", true]);
assert.match(sc.messages.at(-1).text, /Lemon Squeezy/);
assert.equal(sc.messages.at(-1).from, "admin");
assert.equal((await adm("/orders?status=refunded")).orders.some(o => o.code === "SB-CARD-2345"), true);
assert.equal(kv.has("session:SB-CARD-2345"), false, "the refunded key can no longer be claimed");
assert.ok((await adm("/log")).log.some(l => l.who === "webhook" && l.action === "환불 완료" && l.detail.includes(tcard.id)));
await hook("order_refunded", { type: "orders", id: "9030", attributes: {} });
assert.equal(stored(tcard.id).messages.length, sc.messages.length, "a repeated webhook does not post twice");

// renewal refund, with a request that named only the order code
await adm("/sales-mode", { mode: "test" });
await hook("subscription_created", sub(31, 102), { custom: { session: "SB-CRDB-2345" } });
const ck31 = keyIdOf((await j("/claim?session=SB-CRDB-2345")).key);
const tcard2 = await create({ cat: "refund", subject: "구독 환불", text: "환불해 주세요", order: "SB-CRDB-2345", refund: { reason: "change_mind" } });
assert.equal(stored(tcard2.id).keyId, undefined, "the buyer gave no key");
assert.equal(stored(tcard2.id).refund.facts.keyId, ck31);
assert.equal(md.get("tk:" + tcard2.id).key, ck31, "the dashboard can search the key the order points to");
assert.deepEqual((await adm("/support/ticket?id=" + tcard2.id)).keys.map(k => k.id), [ck31]);
await hook("subscription_payment_refunded", { type: "subscription-invoices", id: "1", attributes: { subscription_id: 31 } });
assert.equal(stored(tcard2.id).refund.status, "done");
assert.equal((await j("/check?id=" + ck31)).revoked, true);

// a card refund with no request: one read, nothing else changes
await hook("subscription_created", sub(32, 101), { custom: { session: "SB-CRDC-2345" } });
const ck32 = keyIdOf((await j("/claim?session=SB-CRDC-2345")).key);
const before = new Map([...kv].filter(([k]) => k.startsWith("tk:")));
await hook("order_refunded", { type: "orders", id: "9032", attributes: {} });
assert.equal((await j("/check?id=" + ck32)).revoked, true);
assert.deepEqual(new Map([...kv].filter(([k]) => k.startsWith("tk:"))), before);
// a stale index entry (the request was deleted) is ignored
await env.LICENSES.put("tkkey:" + ck32, "T-AAAA-AAAA", { expirationTtl: 1095 * DAY });
await hook("order_refunded", { type: "orders", id: "9032", attributes: {} });
assert.deepEqual(new Map([...kv].filter(([k]) => k.startsWith("tk:"))), before);

// a request whose key is of another purchase: that key's refund does not close it, even if the index pointed there
await hook("subscription_created", sub(34, 101), { custom: { session: "SB-CRDF-2345" } });
const ck34 = keyIdOf((await j("/claim?session=SB-CRDF-2345")).key);
const moD = await j("/order?plan=yearly&method=manual", { method: "POST" });
await adm("/orders/approve", { code: moD.code });
const tmD = await create({ cat: "refund", subject: "환불", text: "환불", order: moD.code, keyId: ck34, refund: { reason: "change_mind" } });
assert.deepEqual([stored(tmD.id).refund.facts.otherKeyId, stored(tmD.id).refund.facts.otherOrder, stored(tmD.id).refund.method], [ck34, "SB-CRDF-2345", "manual"]);
await env.LICENSES.put("tkkey:" + ck34, tmD.id, { expirationTtl: 1095 * DAY });
const msgsD = stored(tmD.id).messages.length;
await hook("order_refunded", { type: "orders", id: "9034", attributes: {} });
assert.equal((await j("/check?id=" + ck34)).revoked, true, "the card key is blocked as before");
assert.deepEqual([stored(tmD.id).refund.status, stored(tmD.id).messages.length], ["requested", msgsD], "the request about order D stays open");
assert.equal((await adm("/orders?status=all")).orders.find(o => o.code === moD.code).status, "approved");

// ---------------------------------------------------------------- subscription portal: the key of a running subscription only
await hook("subscription_created", sub(33, 101), { custom: { session: "SB-CRDE-2345" } });
const ck33 = (await j("/claim?session=SB-CRDE-2345")).key, ck33Id = keyIdOf(ck33);
const portal = (key, headers) => post("/support/subscription", { key }, headers);
assert.deepEqual(await portal(ck33), { http: 503, error: "unavailable" }, "no LS_API_KEY: nothing to ask");
env.LS_API_KEY = "lsk_fake";
const n0 = fetched.length;
const link33 = { http: 200, portal: "https://shop.example/billing/33?signature=s1" };
let pw = await counted(() => portal(ck33));
assert.deepEqual([pw.r, pw.w], [link33, 0], "read only: no KV write");
assert.equal(fetched.at(-1), "https://api.lemonsqueezy.com/v1/subscriptions/33");
assert.deepEqual(await portal(ck33), link33, "pressed again: the kept link");
_forgetMemory();
assert.deepEqual(await portal(ck33), link33, "another isolate: the Cache API's copy");
assert.equal(fetched.length, n0 + 1, "Lemon Squeezy is asked once in 30 minutes");
assert.ok(cachePuts.some(p => p.u === "https://lic.example/__support-cache/portal%3A33" && p.age === LIMITS.portalCache));
assert.deepEqual(await portal(ck), nf, "a refunded (blocked) key");
kv.set("revoked:" + ck33Id, "공유");   // blocked by hand while the subscription still runs
assert.deepEqual(await portal(ck33), nf, "a blocked key, its subscription still running");
kv.delete("revoked:" + ck33Id);
const sub33 = kv.get("sub:33");
kv.delete("sub:33");   // cancelled or expired: subscription_updated took it away
assert.deepEqual(await portal(ck33), nf, "no current subscription");
kv.set("sub:33", sub33);
const [p64, s64] = ck33.split(".");
const forged = Buffer.from(JSON.stringify({ ...JSON.parse(Buffer.from(p64, "base64url")), sub: "31" })).toString("base64url") + "." + s64;
assert.deepEqual(await portal(forged), nf, "a key that is not signed by the server");
assert.deepEqual(await portal(mk.key), nf, "a key without a subscription");
assert.deepEqual(await portal("garbage"), nf);
assert.deepEqual(await portal(""), nf);
assert.equal(fetched.length, n0 + 1, "refused keys never reach Lemon Squeezy");
const realNow = Date.now;
Date.now = () => realNow() + (LIMITS.portalCache + 1) * 1000;   // 30 minutes later
assert.deepEqual(await portal(ck33), link33);
Date.now = realNow;
assert.equal(fetched.length, n0 + 2, "after 30 minutes Lemon Squeezy is asked again");
const ipP = { "CF-Connecting-IP": "198.51.100.30" };   // every call counts, refused keys too (each key also has a daily cap)
for (let i = 0; i < LIMITS.subscriptionPerIp; i++) assert.equal((await portal("garbage." + i, ipP)).http, 404);
assert.deepEqual(await portal(ck33, ipP), BUSY_IP, "21st call in an hour from one IP");
edge.clear(); _forgetMemory();   // nothing kept: each answer below comes from Lemon Squeezy
lsAnswer = () => new Response("{}", { status: 404 });
assert.deepEqual(await portal(ck33), nf);
lsAnswer = () => new Response("{}", { status: 500 });
assert.deepEqual(await portal(ck33), { http: 503, error: "unavailable" });
lsAnswer = () => Response.json({ data: { attributes: { urls: { customer_portal: "javascript:alert(1)" } } } });
assert.deepEqual(await portal(ck33), { http: 503, error: "unavailable" }, "only an https link goes back");
lsAnswer = () => { throw new TypeError("network"); };
assert.deepEqual(await portal(ck33), { http: 503, error: "unavailable" });
assert.ok(![...edge.keys()].some(u => u.includes("portal%3A")), "errors are not kept");

// ---------------------------------------------------------------- the day's write budget holds against spam from many IPs
freshDay();
const w0 = writes;
let made = 0, refused = 0, lastT = null;
for (let i = 0; i < 100; i++) {   // a new IP for every call: no per-IP limit stops it, the budget has to
  const t = await create({ subject: "스팸 " + i });
  if (t.http !== 201) { assert.deepEqual(t, BUSY_DAY); refused++; continue; }
  made++; lastT = t;
  for (const r of [await reply(t.id, t.token, "스팸", freshIp()), await closeT(t.id, t.token), await reply(t.id, t.token, "또", freshIp())])
    if (r.http === 429) { assert.equal(r.error, "busy"); refused++; } else assert.equal(r.http, 200);
}
const spent = writes - w0;
assert.ok(spent <= LIMITS.writesPerDay && spent >= LIMITS.writesPerDay - 2, "support wrote " + spent + " times in a day");
assert.equal(dayCount().w, spent, "the counter counts every KV write support made");
assert.ok(made < 40 && refused > 60, made + " made, " + refused + " refused");
assert.deepEqual(await create(), BUSY_DAY);
assert.deepEqual(await reply(lastT.id, lastT.token, "더"), BUSY_DAY);
assert.deepEqual(await closeT(lastT.id, lastT.token), BUSY_DAY);
// over budget: support's own writes wait for tomorrow; orders, keys, webhooks and the dashboard go on
const wOver = writes;
const mo9 = await j("/order?plan=yearly&method=manual", { method: "POST" });
assert.equal(mo9.http, 200);
assert.match(mo9.code, /^SB-/);
assert.equal((await j("/order/paid?code=" + mo9.code, { method: "POST", body: JSON.stringify({ payer: "예산 뒤" }) })).http, 200);
const k9 = await adm("/orders/approve", { code: mo9.code });
assert.equal((await j("/claim?session=" + mo9.code)).key, k9.key, "the key is claimed as usual");
assert.equal((await hook("subscription_created", sub(35, 101), { custom: { session: "SB-CRDG-2345" } })).http, 200);
assert.equal((await j("/claim?session=SB-CRDG-2345")).status, "ready", "a card payment still gives its key");
assert.equal((await adm("/support/reply", { id: lastT.id, text: "내일 다시 받아요" })).http, 200, "the seller can still answer");
assert.equal((await st([{ id: lastT.id, token: lastT.token }])).tickets[lastT.id].unread, true, "polling works and shows the new reply");
const ov = await counted(() => view(lastT.id, lastT.token));
assert.deepEqual([ov.r.http, ov.r.ticket.unreadUser, ov.w], [200, true, 0], "reading works; clearing the unread flag in KV waits");
assert.equal(stored(lastT.id).unreadUser, true);
// the mark the buyer has now seen is not reported as new again, though KV keeps it until tomorrow (no KV write)
const ov2 = await counted(async () => [(await st([{ id: lastT.id, token: lastT.token }])).tickets[lastT.id].unread, (await view(lastT.id, lastT.token)).ticket.unreadUser]);
assert.deepEqual([ov2.r, ov2.w], [[false, false], 0], "seen: not new on the next poll or view");
assert.equal(stored(lastT.id).unreadUser, true);
assert.ok(cachePuts.some(p => p.u.endsWith("/__support-cache/seen%3A" + lastT.id)), "the note is in the Cache API, not KV");
_forgetMemory();
assert.equal((await st([{ id: lastT.id, token: lastT.token }])).tickets[lastT.id].unread, false, "another isolate: the Cache API's note");
assert.equal(dayCount().w, spent, "none of that touched the support budget");
assert.ok(writes - wOver >= 5, "and all of it wrote as usual");

// 429 says why: the day's budget (until 00:00 UTC) or this address (until the hour ends)
r429 = await call("/support/ticket", { method: "POST", headers: { "Content-Type": "application/json", ...freshIp() }, body: JSON.stringify(base) });
let toDay = 86400 - Math.floor(Date.now() / 1000) % 86400;
assert.deepEqual([r429.status, (await r429.json()).scope], [429, "day"]);
assert.ok(Math.abs(Number(r429.headers.get("Retry-After")) - toDay) <= 2, "day: Retry-After = seconds to 00:00 UTC, " + r429.headers.get("Retry-After"));

// The buyers' lane: a flood of anonymous tickets does not shut out somebody who shows a purchase.
const moR = await j("/order?plan=yearly&method=manual", { method: "POST" });   // requested: anybody can make one
const k35 = (await j("/claim?session=SB-CRDG-2345")).key;   // a card subscription's key: its purchase is the subscription
let vt = await counted(() => create({ cat: "pay", subject: "키가 안 보여요", key: k35 }));
assert.deepEqual([vt.r.http, vt.w], [201, 1], "a paid key gets in while the anonymous lane is full");
vt = vt.r;
assert.equal(stored(vt.id).owner, "s:35");
assert.equal((await reply(vt.id, vt.token, "추가로 적어요")).http, 200, "its replies too");
const vo = await create({ cat: "pay", subject: "주문 문의", order: mo9.code });
assert.equal(vo.http, 201, "so does the code of an approved order");
assert.equal(stored(vo.id).owner, "o:" + mo9.code);
assert.deepEqual(await create({ cat: "pay", subject: "주문 문의", order: moR.code }), BUSY_DAY, "an order that is only requested does not");
assert.deepEqual(await create({ cat: "pay", subject: "키", keyId: k9.id }), BUSY_DAY, "nor a key id typed alone");
assert.deepEqual(await reply(lastT.id, lastT.token, "익명 답장"), BUSY_DAY, "the anonymous lane stays full");
let dc = dayCount();
assert.deepEqual([dc.w, dc.vw, dc.vc, dc.vr, dc.by], [spent, 3, 2, 1, { "s:35": 2, ["o:" + mo9.code]: 1 }]);
// one purchase cannot fill the lane
setDay({ by: { "s:35": LIMITS.perOwnerPerDay } });
assert.deepEqual(await reply(vt.id, vt.token, "또"), BUSY_DAY, LIMITS.perOwnerPerDay + " actions a day for one purchase");
assert.equal((await reply(vo.id, vo.token, "다른 구매는 돼요")).http, 200, "another purchase still gets in");
// and the lane has its own cap
setDay({ vw: LIMITS.vWritesPerDay });
assert.deepEqual(await reply(vo.id, vo.token, "꽉 참"), BUSY_DAY, LIMITS.vWritesPerDay + " writes a day in the buyers' lane");
freshDay();
assert.equal((await reply(lastT.id, lastT.token, "다음 날")).http, 200, "a new day, a new budget");

// ---------------------------------------------------------------- config (dashboard)
let cfg = await adm("/support/config");
assert.deepEqual(cfg.config, { seller: { name: "", rep: "", address: "", bizNo: "", mailOrderNo: "", email: "", phone: "", hours: "" },
                               privacy: { dbRegion: "", effectiveDate: "" }, notice: "", refundDays: 7, faq: [], open: true });
const badCfg = [
  { seller: { name: "x".repeat(121) } }, { notice: "n".repeat(501) }, { refundDays: 31 }, { refundDays: -1 }, { refundDays: "abc" }, { refundDays: 1.5 },
  { faq: "nope" }, { faq: Array.from({ length: 31 }, (_, i) => ({ q: "q" + i, a: "a" })) }, { faq: [{ q: "q".repeat(121), a: "a" }] },
  { faq: [{ q: "q", a: "a".repeat(1501) }] }, { faq: [{ q: "질문만" }] },
  { privacy: { dbRegion: "x".repeat(121) } }, { privacy: { effectiveDate: "2026-13-01" } }, { privacy: { effectiveDate: "2026-02-30" } },
  { privacy: { effectiveDate: "2026-12-14T00:00:00Z" } }, { privacy: { effectiveDate: "내일" } },
];
for (const b of badCfg) assert.equal((await adm("/support/config", b)).http, 400, JSON.stringify(b).slice(0, 60));
assert.equal(kv.has("config:support"), false, "nothing saved from a refused form");
cfg = await adm("/support/config", { seller: { name: " DOKKU ", email: "help@example.com", unknown: "x" }, notice: "설 연휴에는 답이 늦어요", refundDays: "7",
                                    faq: [{ q: "질문", a: "답\n둘째 줄" }, { q: "", a: "" }], open: true, extra: 1 });
assert.deepEqual(cfg.config, { seller: { name: "DOKKU", rep: "", address: "", bizNo: "", mailOrderNo: "", email: "help@example.com", phone: "", hours: "" },
                               privacy: { dbRegion: "", effectiveDate: "" }, notice: "설 연휴에는 답이 늦어요", refundDays: 7, faq: [{ q: "질문", a: "답\n둘째 줄" }], open: true });
assert.equal((await j("/support/info")).privacy, undefined, "the policy's blanks are not part of /support/info");
assert.ok(await logHas("고객센터 설정"));
info = await j("/support/info?lang=en");
assert.deepEqual([info.faq, info.faqDefault, info.seller.name, info.notice], [[{ q: "질문", a: "답\n둘째 줄" }], false, "DOKKU", "설 연휴에는 답이 늦어요"]);
await adm("/support/config", { ...cfg.config, open: false });
assert.equal((await j("/support/info")).open, false);
assert.deepEqual(await create(), { http: 503, error: "closed" }, "closed: no new requests");
assert.equal((await reply(tA.id, tA.token, "닫혀 있어도 답장은 돼요")).http, 200, "existing requests still take replies");
// a withdrawal (refund) request is never shut out by the seller's desk; the app and /help read "refunds"
assert.equal((await j("/support/info")).refunds, true);
const rClosed = await create({ cat: "refund", subject: "환불", text: "환불해 주세요", order: "SB-QQQQ-2345", refund: { reason: "change_mind" } });
assert.equal(rClosed.http, 201, "closed: a refund request is still taken");
assert.deepEqual(await create({ cat: "pay", subject: "결제" }), { http: 503, error: "closed" }, "a payment question is not");
assert.equal((await adm("/support/delete", { id: rClosed.id })).ok, true);
await adm("/support/config", { ...cfg.config, open: true });
assert.equal((await create()).http, 201);

// ---------------------------------------------------------------- KV metadata and TTLs
const tBig = await create({ cat: "pay", subject: "😀".repeat(80), order: "SB-ZZZZ-ZZZZ", keyId: "f".repeat(16) }, { ...freshIp(), Authorization: "Bearer " + MEMBER });
assert.equal(tBig.http, 201);
assert.ok(Buffer.byteLength(JSON.stringify(md.get("tk:" + tBig.id))) < 1024);
assert.equal(md.get("tk:" + tBig.id).subj, "😀".repeat(40), "the list row keeps 40 characters, never half an emoji");
assert.equal(ttl.get("tk:" + tBig.id), 1095 * DAY, "payment requests: 3 years");
const huge = listRow({ cat: "other", status: "open", subject: "\u{10FFFF}".repeat(5000), created: "x", updated: "y", messages: [{ from: "user" }], order: "o".repeat(400), account: "a".repeat(400) });
assert.ok(Buffer.byteLength(JSON.stringify(huge)) < 1024, "the row trims itself under the limit");
assert.ok(huge.subj.length > 0 && [...huge.subj].length < 40, "by shortening the subject");
assert.deepEqual(bigMeta, [], "no KV write with metadata of 1024 bytes or more");
for (const [k, m] of md) if (k.startsWith("tk:")) {
  assert.ok(Buffer.byteLength(JSON.stringify(m)) < 1024, k);
  assert.equal(ttl.get(k), ["pay", "refund"].includes(m.cat) ? 1095 * DAY : 365 * DAY, "TTL by category: " + k);
}
for (const k of kv.keys()) if (k.startsWith("tkkey:")) assert.equal(ttl.get(k), 1095 * DAY);
assert.ok(ttl.get("tk:" + t1.id) === 365 * DAY && stored(t1.id).messages.length > 3, "the TTL is set again with every write");

// ---------------------------------------------------------------- summary counts
const rowsNow = [...md].filter(([k]) => k.startsWith("tk:")).map(([, m]) => m);
const sum = await adm("/summary");
assert.equal(sum.counts.ticketsOpen, rowsNow.filter(m => m.status === "open").length);
assert.equal(sum.counts.refundsOpen, rowsNow.filter(m => ["requested", "approved"].includes(m.rs)).length);
assert.equal(sum.counts.refundsOpen, 4, "still waiting: the unknown key, order B (unpaid), the order with no records, order D");
assert.equal((await adm("/support")).count.refund, 4);
for (const t of [tz, tmB, tNone, tmD]) assert.equal(stored(t.id).refund.status, "requested");

// ---------------------------------------------------------------- /help
const h1 = await call("/help"), h2 = await call("/help?lang=en");
assert.equal(h1.status, 200);
assert.match(h1.headers.get("content-type"), /^text\/html/);
const csp = h1.headers.get("content-security-policy");
const nonce = /'nonce-([A-Za-z0-9+/=]+)'/.exec(csp)[1];
assert.ok(csp.includes("default-src 'none'") && csp.includes("frame-ancestors 'none'") && csp.includes("connect-src 'self'") && !csp.includes("unsafe-inline"), csp);
assert.equal(h1.headers.get("x-frame-options"), "DENY");
const html = await h1.text(), htmlEn = await h2.text();
assert.match(html, /^<!DOCTYPE html>\s*<html lang="ko">/);
assert.ok(htmlEn.includes("<html lang=\"en\">"));
assert.ok(!html.includes("__NONCE__"));
assert.equal([...html.matchAll(/<(script|style) nonce="([^"]+)"/g)].filter(m => m[2] === nonce).length, 2, "the script and the style carry this request's nonce");
assert.notEqual(/'nonce-([^']+)'/.exec(h2.headers.get("content-security-policy"))[1], nonce, "a new nonce every time");
assert.ok(!/<script[^>]*\bsrc=/i.test(html) && !/<link\b/i.test(html) && !/https?:\/\//.test(html), "no external scripts or styles");
assert.ok(!/innerHTML|outerHTML|insertAdjacentHTML|document\.write|eval\(/.test(html), "the page builds its content with textContent only");
assert.ok(/try \{ var v = JSON\.parse\(localStorage/.test(html) && /try \{ localStorage\.setItem/.test(html), "localStorage inside try/catch");
const script = /<script nonce="[^"]+">([\s\S]*?)<\/script>/.exec(html)[1];
new Function(script);   // the inline script parses
const ids = new Set([...html.matchAll(/\bid="([^"]+)"/g)].map(m => m[1]));
for (const [, id] of script.matchAll(/\$\("([^"]+)"\)/g)) assert.ok(ids.has(id), "the page has #" + id);
for (const [, k] of html.matchAll(/data-t="([^"]+)"/g)) assert.ok(new RegExp("\\b" + k + ": \"").test(script), "a label for " + k);
assert.ok(script.includes("\\.") && script.includes("[0-9a-f]{64}"), "regexes survive as written");
assert.ok(script.includes('method: "POST", headers: { "Content-Type": "application/json" }'), "the page's POSTs send JSON");
assert.ok(!/token=/.test(script) && script.includes('{ headers: { "X-Ticket-Token": token } }') && script.includes("Object.assign({ id: current.id, token: current.token }, body)"),
          "the page sends the token in a header or the body, never in a URL");
for (const id of ["subject", "text", "contact", "thReply"]) {
  assert.ok(new RegExp('id="' + id + '"').test(html) && !new RegExp('id="' + id + '"[^>]*maxlength').test(html), "#" + id + ": no maxlength (UTF-16 units)");
  assert.ok(html.includes('id="' + id + 'N"'), "#" + id + ": a character count");
}
assert.ok(script.includes("Array.from(String(s || \"\").trim()).length") && script.includes("subject: " + LIMITS.subject + ", text: " + LIMITS.text + ", contact: " + LIMITS.contact + ", thReply: " + LIMITS.text),
          "counted in characters like the server, with the server's limits");
assert.match(html, /<noscript><div class="box warn"><p>이 페이지는 JavaScript가 있어야 동작해요\.[^<]*<\/p><p>This page needs JavaScript\./, "a line for browsers without JavaScript");
assert.ok(script.includes('r.scope === "day" ? TX.err.busyDay : TX.err.busy') && script.includes("한국 시간 오전 9시에 다시 열려요") && script.includes("9:00 Korean time (00:00 UTC)"));
assert.ok(script.includes("동의하지 않으면 문의를 보낼 수 없어요. 앱은 그대로 쓸 수 있어요.") && script.includes("마지막으로 바뀐 날부터") && !/되돌릴 수 없|one-way/.test(html + JSON.stringify(DEFAULT_FAQ)),
          "consent: refusing is possible, retention from the last change, no claim that the IP hash cannot be reversed");
assert.ok(html.includes('<a id="privacyNav" href="/privacy" data-t="privacy"></a>') && html.includes('<a id="privacyLink" href="/privacy" data-t="privacyMore"></a>') &&
          script.includes('privacyMore: "자세한 내용은 개인정보 처리방침에서 볼 수 있어요"') && script.includes('L === "en" ? "/privacy?lang=en" : "/privacy"'),
          "/help links the privacy policy next to the consent and in the menu");

// ---------------------------------------------------------------- /privacy
{
  const saved = (await adm("/support/config")).config;
  const page = async (q = "") => { const r = await call("/privacy" + q); return { r, html: await r.text() }; };
  assert.equal(buildPrivacy(), readFileSync(new URL("./src/privacy_page.js", import.meta.url), "utf8").replace(/\r\n/g, "\n"),
               "src/privacy_page.js is up to date with privacy/*.md (run node build_privacy.mjs)");
  for (const bad of ["# a\n\nsome {{OTHER}}", "# a\n\n| x |\n| y |", "# a\n\n1. numbered", "no title"])
    assert.throws(() => markdownToHtml(bad), Error, "the build stops on what it does not understand: " + bad);

  // nothing filled in yet: each blank says so; the page is static, styled by its nonce, and loads nothing
  await adm("/support/config", { ...saved, seller: { ...saved.seller, name: "", rep: "", email: "" }, privacy: { dbRegion: "", effectiveDate: "" } });
  const p1 = await page(), p2 = await page();
  assert.equal(p1.r.status, 200);
  assert.match(p1.r.headers.get("content-type"), /^text\/html; charset=utf-8/);
  const pcsp = p1.r.headers.get("content-security-policy"), pn = /'nonce-([A-Za-z0-9+/=]+)'/.exec(pcsp)[1];
  assert.ok(pcsp.startsWith("default-src 'none'; style-src 'nonce-" + pn + "'") && pcsp.includes("frame-ancestors 'none'") && pcsp.includes("base-uri 'none'") &&
            !pcsp.includes("script-src") && !pcsp.includes("unsafe-inline"), pcsp);
  assert.deepEqual([p1.r.headers.get("x-frame-options"), p1.r.headers.get("referrer-policy"), p1.r.headers.get("x-content-type-options"), p1.r.headers.get("cache-control")],
                   ["DENY", "no-referrer", "nosniff", "no-store"]);
  assert.equal([...p1.html.matchAll(/<style nonce="([^"]+)">/g)].map(m => m[1]).join(), pn, "one style, with this request's nonce");
  assert.notEqual(/'nonce-([^']+)'/.exec(p2.r.headers.get("content-security-policy"))[1], pn, "a new nonce every time");
  assert.ok(!p1.html.includes("__NONCE__") && !/<script|<link|<img|<iframe|\bsrc=|\son\w+=/i.test(p1.html), "no script, no external resources");
  assert.match(p1.html, /^<!DOCTYPE html>\s*<html lang="ko">/);
  assert.ok(p1.html.includes("<h1>SystemBoost 개인정보 처리방침</h1>") && p1.html.includes('<h2 id="s2">제1조 처리하는 개인정보와 목적</h2>') && p1.html.includes('<a href="#s2">'));
  assert.ok(!p1.html.includes("{{"), "every blank is filled");
  const empty = '<span class="blank">' + PRIVACY_EMPTY.ko + "</span>";
  assert.equal(p1.html.split(empty).length - 1, 5, "name, email, database location and the effective date (twice): 판매자가 아직 입력하지 않았어요");
  assert.ok(p1.html.includes("개인정보 보호책임자: " + empty) && p1.html.includes("메일: " + empty) && p1.html.includes("<td>" + empty + "</td>") && p1.html.includes("시행일: " + empty));
  // what the policy says about the code (src/index.js, src/support.js, src/stats.js, supabase/accounts.sql)
  for (const fact of ["마지막으로 틀린 뒤 15분", "보관 기간을 정하지 않았어요 — 판매자가 지울 때까지", "결제와 환불 문의는 3년, 그 밖의 문의는 1년", "마지막 연결 뒤 90일",
                      "마지막 요청 뒤 1시간까지", "<td>판매자 작업 기록</td><td>180일</td>", "주문이 마지막으로 바뀐 날부터 90일", "카드 결제 7일, 직접 결제 30일", "IP 주소를 저장하지 않아요"])
    assert.ok(p1.html.includes(fact), "the policy says: " + fact);
  assert.ok(!/3일 시험 키|시험 키를 제공/.test(p1.html));

  // filled from the dashboard, escaped, in one pass (a value that looks like a blank stays as it is)
  await adm("/support/config", { ...saved, seller: { ...saved.seller, name: "DOKKU", rep: "<b>홍길동</b> & \"Co\"", email: "o'brien@example.com" },
                                 privacy: { dbRegion: "{{OWNER_EMAIL}} <서울> (ap-northeast-2)", effectiveDate: "2026-12-14" } });
  const p3 = (await page()).html;
  assert.ok(p3.includes("개인정보 보호책임자: &lt;b&gt;홍길동&lt;/b&gt; &amp; &quot;Co&quot;") && !p3.includes("<b>홍길동"), "the representative, escaped");
  assert.ok(p3.includes("메일: o&#39;brien@example.com"), "the email, escaped");
  assert.ok(p3.includes("<td>{{OWNER_EMAIL}} &lt;서울&gt; (ap-northeast-2)</td>"), "the database location, escaped and not filled in again");
  assert.ok(p3.includes("시행일: 2026년 12월 14일") && !p3.includes(PRIVACY_EMPTY.ko));
  await adm("/support/config", { ...saved, seller: { ...saved.seller, name: "DOKKU", rep: "", email: "help@example.com" }, privacy: { dbRegion: "대한민국", effectiveDate: "2026-12-14" } });
  assert.ok((await page()).html.includes("개인정보 보호책임자: " + empty) && !(await page()).html.includes("개인정보 보호책임자: DOKKU"),
            "no representative: the blank mark, never the business name (the officer is a person)");

  // ?lang=en: an English summary that says the Korean text is the policy, with the same blanks
  const pe = await page("?lang=en");
  assert.equal(pe.r.status, 200);
  assert.match(pe.html, /^<!DOCTYPE html>\s*<html lang="en">/);
  assert.ok(pe.html.includes("The Korean text is the policy") && pe.html.includes('<a href="/privacy" hreflang="ko">') && pe.html.includes('<a href="/help?lang=en">'));
  assert.ok(pe.html.includes("Effective date: December 14, 2026") && pe.html.includes('Privacy officer: <span class="blank">' + PRIVACY_EMPTY.en + "</span>") &&
            pe.html.includes("Email: help@example.com") && pe.html.includes("Database location: 대한민국"));
  assert.ok(p1.html.includes('<a href="/privacy?lang=en" hreflang="en">English summary</a>'));
  await adm("/support/config", { ...saved, privacy: { dbRegion: "", effectiveDate: "" } });
  assert.ok((await page("?lang=en")).html.includes("Effective date: <span class=\"blank\">" + PRIVACY_EMPTY.en + "</span>"));
  assert.equal((await call("/privacy", { method: "POST" })).status, 404, "GET only");
  await adm("/support/config", saved);
}

// ---------------------------------------------------------------- the dashboard's feed (Stats Durable Object): buyer actions only, no text, no KV
{
  // every section above: one creation row per ticket still in KV (a deleted one's rows went with it), and the card refund
  const made = feedRows().filter(r => r.kind === "ticket" || r.kind === "refund").map(r => r.ticket);
  assert.deepEqual([...new Set(made)].sort(), [...kv.keys()].filter(k => k.startsWith("tk:")).map(k => k.slice(3)).sort(), "one creation row per ticket");
  assert.equal(made.length, new Set(made).size, "and only one");
  assert.ok(feedRows().every(r => FEED_KINDS.includes(r.kind) && ID_RE.test(r.ticket)));
  assert.ok(feedRows().some(r => r.kind === "card" && r.ticket === tcard.id), "the card refund the webhook finished");
  assert.ok(feedRows().some(r => r.kind === "refund" && r.ticket === tcard.id));
  assert.ok(!feedRows().some(r => r.ticket === tDel.id), "a deleted ticket's rows are gone");

  freshDay(); _forgetMemory();
  const feed = q => adm("/support/feed" + (q ?? ""));
  const f0 = await feed();
  assert.equal(f0.http, 200);
  assert.deepEqual(f0.events.map(e => e.seq), feedRows().slice(-FEED.recent).map(r => r.seq), "no after: the newest 20, oldest first");
  assert.deepEqual([f0.last, f0.more], [feedRows().at(-1).seq, false]);
  const from = f0.last;
  const since = async () => (await feed("?after=" + from)).events.map(e => [e.kind, e.ticket, e.cat]);
  const l0 = lists, w0 = writes;
  assert.deepEqual(await since(), []);
  assert.deepEqual([lists - l0, writes - w0], [0, 0], "the feed route lists and writes no KV");

  // each buyer action: one row, in order
  const a = await create({ cat: "bug", subject: "피드 시험 비밀 제목", text: "피드 시험 비밀 본문" });
  const rq = await create({ cat: "refund", subject: "피드 환불", text: "환불해 주세요", keyId: "00ff00ff00ff00ff", refund: { reason: "change_mind" } });
  assert.deepEqual([a.http, rq.http], [201, 201]);
  assert.equal((await reply(a.id, a.token, "더 적어요")).http, 200);
  assert.equal((await closeT(a.id, a.token)).http, 200);
  assert.equal((await closeT(a.id, a.token)).http, 200);   // already closed: nothing changes, no row
  assert.equal((await reply(a.id, a.token, "다시 열어요")).http, 200);
  const five = [["ticket", a.id, "bug"], ["refund", rq.id, "refund"], ["reply", a.id, "bug"], ["close", a.id, "bug"], ["reopen", a.id, "bug"]];
  assert.deepEqual(await since(), five);
  // the seller's own actions add none, and neither do refused buyer requests
  assert.equal((await adm("/support/reply", { id: a.id, text: "판매자 답장" })).http, 200);
  assert.equal((await adm("/support/status", { id: a.id, status: "closed" })).http, 200);
  assert.equal((await adm("/support/status", { id: a.id, status: "open" })).http, 200);
  assert.equal((await adm("/support/refund", { id: rq.id, action: "reject", reason: "기간이 지났어요" })).http, 200);
  assert.equal((await adm("/support/config", (await adm("/support/config")).config)).http, 200);
  assert.equal((await adm("/support/ticket?id=" + a.id)).http, 200);
  assert.equal((await reply(a.id, "f".repeat(64), "남의 문의")).http, 404);
  assert.equal((await create({ subject: "" })).http, 400);
  assert.equal((await post("/support/ticket", "x".repeat(LIMITS.body + 1))).http, 413);
  assert.equal((await view(a.id, a.token)).http, 200);
  assert.equal((await post("/support/tickets/status", { tickets: [{ id: a.id, token: a.token }] })).http, 200);
  assert.deepEqual(await since(), five, "seller actions, reads and refused requests write no feed row");
  // no buyer text in the Durable Object: these columns only
  const dump = JSON.stringify(feedDb.prepare("SELECT * FROM support_events").all());
  assert.ok(!/비밀|피드 환불|환불해 주세요|더 적어요|다시 열어요/.test(dump), "no subject or message in the feed");
  assert.deepEqual(Object.keys(feedDb.prepare("SELECT * FROM support_events LIMIT 1").get()).sort(), ["at", "cat", "kind", "seq", "ticket"]);

  // after= paging: at most 100 a call; last = where to go on; more while there is more
  const head = feedRows().at(-1).seq;
  for (let i = 0; i < 130; i++) feedObj.event({ kind: "reply", ticket: a.id, cat: "bug" });
  const p1 = await feed("?after=" + head);
  assert.deepEqual([p1.events.length, p1.events[0].seq, p1.last, p1.more], [FEED.page, head + 1, head + 100, true]);
  const p2 = await feed("?after=" + p1.last);
  assert.deepEqual([p2.events.length, p2.events[0].seq, p2.last, p2.more], [30, head + 101, head + 130, false]);
  assert.deepEqual(await feed("?after=" + p2.last), { http: 200, events: [], last: head + 130, more: false });
  assert.equal((await feed("?after=" + (head + 999))).last, head + 130, "a cursor past the end (a new Durable Object): go on from its end");
  for (const bad of ["-1", "x", "1.5", "1e3", "9".repeat(16), ""]) assert.equal((await feed("?after=" + encodeURIComponent(bad))).http, 400, "after=" + bad);

  // the route is behind the dashboard password like the rest of /admin/api
  const anon = await j("/admin/api/support/feed?after=0", { headers: { "CF-Connecting-IP": "198.51.100.77" } });
  const wrong = await j("/admin/api/support/feed?after=0", { headers: { "CF-Connecting-IP": "198.51.100.77", Authorization: "Bearer " + "W".repeat(32) } });
  assert.deepEqual([anon.http, anon.events, wrong.http, wrong.events], [401, undefined, 401, undefined]);
  assert.equal((await j("/support/feed")).http, 404, "not on the public side");

  // the dashboard names feed rows its list does not show (another tab) with the same single KV list
  const l1 = lists;
  const nm = await adm("/support?status=answered&names=" + [a.id, rq.id, "T-AAAA-AAAA", "bogus"].join(","));
  assert.equal(lists - l1, 1, "one KV list, as without names");
  assert.ok(!nm.tickets.some(t => t.id === a.id), "a is open, so not in this tab");
  assert.deepEqual(Object.keys(nm.named).sort(), [a.id, rq.id].sort());
  assert.deepEqual([nm.named[a.id].subj, nm.named[a.id].cat, nm.named[a.id].status], ["피드 시험 비밀 제목", "bug", "open"]);
  assert.deepEqual([nm.named[rq.id].cat, nm.named[rq.id].rs], ["refund", "rejected"]);
  assert.equal((await adm("/support?status=answered")).named, undefined);

  // deleting a ticket deletes its rows
  assert.equal((await adm("/support/delete", { id: a.id })).ok, true);
  assert.ok(!feedRows().some(r => r.ticket === a.id) && feedRows().some(r => r.ticket === rq.id));

  // in the worker, the row is written in ctx.waitUntil, after the answer
  const waits = [], ctx = { waitUntil: p => waits.push(p) };
  const wr = await worker.fetch(new Request("https://lic.example/support/ticket", { method: "POST", headers: { "Content-Type": "application/json", ...freshIp() },
                                                                                   body: JSON.stringify({ ...base, subject: "waitUntil" }) }), env, ctx);
  assert.equal(wr.status, 201);
  assert.equal(waits.length, 1);
  await Promise.all(waits);
  const wid = (await wr.json()).id;
  assert.deepEqual(feedRows().filter(r => r.ticket === wid).map(r => r.kind), ["ticket"]);

  // a failing Durable Object never fails the buyer
  statsDown = true;
  const down = await create({ subject: "통계 객체가 멈췄을 때" });
  assert.equal(down.http, 201, "the ticket is saved");
  assert.equal(stored(down.id).subject, "통계 객체가 멈췄을 때");
  assert.equal((await reply(down.id, down.token, "답장도 돼요")).http, 200);
  assert.equal((await closeT(down.id, down.token)).http, 200);
  const waits2 = [];
  const wr2 = await worker.fetch(new Request("https://lic.example/support/ticket", { method: "POST", headers: { "Content-Type": "application/json", ...freshIp() },
                                                                                    body: JSON.stringify({ ...base, subject: "waitUntil, object down" }) }), env, { waitUntil: p => waits2.push(p) });
  assert.equal(wr2.status, 201);
  await Promise.all(waits2);   // settles; never rejects
  assert.deepEqual(await feed("?after=0"), { http: 503, error: "새 소식을 불러오지 못했어요" });
  assert.equal((await adm("/support/delete", { id: down.id })).ok, true, "deleting works without the feed");
  statsDown = false;
  const binding = env.STATS;
  delete env.STATS;   // no binding at all (an old wrangler.toml)
  const nob = await create({ subject: "바인딩 없음" });
  assert.equal(nob.http, 201);
  assert.equal((await reply(nob.id, nob.token, "그래도 돼요")).http, 200);
  env.STATS = binding;
  assert.ok(!feedRows().some(r => r.ticket === down.id || r.ticket === nob.id));
}

// ---------------------------------------------------------------- nothing else reached
assert.deepEqual(unexpected, [], "no network calls besides the fakes");
for (const [k, v] of kv) assert.ok(!String(v).includes("lsk_fake") && !String(v).includes(SECRET) && !String(v).includes(MEMBER), "no secrets or tokens in KV: " + k);
console.log("support tests passed");
