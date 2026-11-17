// 0.8.14: a price increase of a card subscription needs the subscriber's consent (index.js "price increase").
// The dashboard's notice (30 days ahead at least, counted in Korean days), POST /sub/price and /sub/price/choose (one
// KV write, counted in the public share; "cancel" stops the subscription at Lemon Squeezy), the dashboard's list,
// and moving an agreed subscription to the new product without proration on or after the day.
// Fake KV, fake Stats object, a fake Lemon Squeezy API, signed webhooks and a fixed clock (Date.now); global fetch
// answers only as the fake: nothing leaves this machine.
//   node test_price.mjs
import assert from "node:assert/strict";
import { createHmac, generateKeyPairSync } from "node:crypto";
import worker from "./src/index.js";
import { fakeStats } from "./fake_stats.mjs";
import { _forgetIpLimits } from "./src/iplimit.js";

// 20:00 UTC is already the next day in Korea: the rule counts Korean days
const T0 = Date.parse("2026-11-17T20:00:00Z");
let clock = T0;
Date.now = () => clock;
const DAY = 864e5;
const kstDay = ms => new Date(ms + 9 * 36e5).toISOString().slice(0, 10);
assert.equal(kstDay(T0), "2026-11-18");

const kv = new Map(), md = new Map(), opts = new Map();
let writes = [];
const st = fakeStats();
const { privateKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
const SECRET = "made-up-signing-secret";
const TOKEN = "made-up-admin-token-0123456789";
const env = {
  LICENSES: {
    get: async k => kv.get(k) ?? null,
    getWithMetadata: async k => ({ value: kv.get(k) ?? null, metadata: md.get(k) ?? null }),
    put: async (k, v, o) => {
      if (o?.metadata && Buffer.byteLength(JSON.stringify(o.metadata)) > 1024) throw new Error("KV metadata over 1024 bytes: " + k);
      writes.push(k); kv.set(k, v); md.set(k, o?.metadata ?? null); opts.set(k, o || {});
    },
    delete: async k => { writes.push("-" + k); kv.delete(k); md.delete(k); opts.delete(k); },
    list: async ({ prefix, limit = 1000 }) => ({ keys: [...kv.keys()].filter(k => k.startsWith(prefix)).sort().slice(0, limit).map(name => ({ name, metadata: md.get(name) })), list_complete: true }),
  },
  LICENSE_PRIVATE_KEY: privateKey.export({ type: "pkcs8", format: "pem" }),
  ADMIN_TOKEN: TOKEN,
  PUBLIC_URL: "https://lic.example",
  SALES_MODE: "live",
  CARD_LIVE_FROM: "2000-01-01T00:00:00Z",
  LS_API_KEY: "lsk_made_up",
  LS_SIGNING_SECRET: SECRET,
  STATS: st.binding,
};

// ---- fake Lemon Squeezy: subscription PATCH (a new product) and DELETE (cancel)
const ls = [];
let lsPatch = () => new Response("{}", { status: 200 });
let lsDelete = () => new Response("{}", { status: 200 });
globalThis.fetch = async (url, init = {}) => {
  const m = /^https:\/\/api\.lemonsqueezy\.com\/v1\/subscriptions\/(\d+)$/.exec(String(url));
  assert.ok(m, "only the fake Lemon Squeezy is called: " + url);
  assert.equal(init.headers?.Authorization, "Bearer " + env.LS_API_KEY);
  ls.push({ method: init.method, sub: m[1], body: init.body ? JSON.parse(init.body) : null });
  if (init.method === "PATCH") return lsPatch(m[1]);
  if (init.method === "DELETE") return lsDelete(m[1]);
  assert.fail("unexpected Lemon Squeezy call " + init.method);
};

let ipN = 0;
const call = (path, init = {}, ip) => worker.fetch(new Request("https://lic.example" + path, { ...init, headers: { "CF-Connecting-IP": ip || "10.9." + (++ipN >> 8 & 255) + "." + (ipN & 255), ...(init.headers || {}) } }), env);
const j = async (path, init, ip) => { const r = await call(path, init, ip); return { http: r.status, ...(await r.json()) }; };
const postJ = (path, b, ip) => j(path, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(b) }, ip);
const adm = (path, body) => j("/admin/api" + path, { method: body ? "POST" : "GET", headers: { Authorization: "Bearer " + TOKEN, "Content-Type": "application/json" }, body: body ? JSON.stringify(body) : undefined });
const hook = async (event, data) => {
  const raw = JSON.stringify({ meta: { event_name: event, test_mode: false, custom_data: {} }, data });
  const r = await call("/webhook/lemonsqueezy", { method: "POST", body: raw, headers: { "X-Signature": createHmac("sha256", SECRET).update(raw).digest("hex") } });
  return { http: r.status, ...(await r.json()) };
};
const V = { monthly: "101", yearly: "102", ultra_monthly: "201", ultra_yearly: "202", biz_monthly: "301", biz_yearly: "302" };
const renewsAt = new Date(T0 + 400 * DAY).toISOString();
const newSub = async (id, plan) => {
  const r = await hook("subscription_created", { type: "subscriptions", id: String(id), attributes: { status: "active", variant_id: Number(V[plan]), order_id: 9000 + id, user_email: `p${id}@example.com`, renews_at: renewsAt } });
  assert.equal(r.http, 200, JSON.stringify(r));
  return kv.get("sub:" + id);
};
const price = (key, lang) => postJ("/sub/price", { key, ...(lang ? { lang } : {}) });
const choose = (key, id, choice, ip) => postJ("/sub/price/choose", { key, id, choice }, ip);
const pub = () => Number(st.obj.peek({ names: ["kv:pub:" + new Date().toISOString().slice(0, 10)] }).n["kv:pub:" + new Date().toISOString().slice(0, 10)]) || 0;

await adm("/card", { store: "42", variants: V });
const keys = {};
for (const [id, plan] of [[1, "monthly"], [2, "monthly"], [3, "yearly"], [4, "monthly"], [5, "monthly"], [6, "monthly"], [7, "monthly"], [8, "monthly"]]) keys[id] = await newSub(id, plan);
const hand = (await adm("/issue", { plan: "monthly", note: "선물" })).key;   // a key that is no card subscription

// 1. before any notice: nothing to show; only a held card subscription key may ask
assert.deepEqual(await price(keys[1]), { http: 200, notice: null, choice: "" });
for (const k of [hand, "", "not.a.key", keys[1] + "x"]) assert.deepEqual(await price(k), { http: 403, error: "key" }, String(k).slice(0, 10));

// 2. the notice: a card plan, whole prices, a higher new price, a new product number, 30 Korean days ahead
const min = kstDay(T0 + 30 * DAY);
assert.equal(min, "2026-12-18");
const good = { plan: "monthly", oldPrice: 2900, newPrice: 3900, variant: "111", effective: min };
const bad = async (b, re) => { const r = await adm("/price", { ...good, ...b }); assert.equal(r.http, 400, JSON.stringify(b)); assert.match(r.error, re); };
await bad({ plan: "yearly_x5" }, /카드 구독 플랜/);
await bad({ plan: "lifetime" }, /카드 구독 플랜/);
await bad({ plan: "__proto__" }, /카드 구독 플랜/);
for (const p of [0, -1, 1.5, "abc", null, 10000001]) await bad({ oldPrice: p }, /1~10,000,000원/);
await bad({ newPrice: 2900 }, /높을 때만/);
await bad({ newPrice: 1900 }, /높을 때만/);
await bad({ variant: "abc" }, /숫자/);
await bad({ variant: "101" }, /지금 상품 번호와 같아요/);
await bad({ variant: "102" }, /다른 플랜/);
await bad({ effective: "2026-12-17" }, /2026-12-18이나 그 뒤/);   // 30 days in UTC, 29 in Korea: refused
await bad({ effective: kstDay(T0) }, /30일 뒤/);
await bad({ effective: "2026-02-30" }, /날짜/);
await bad({ effective: "18.12.2026" }, /날짜/);
await bad({ effective: kstDay(T0 + 400 * DAY) }, /1년 안/);
assert.equal(kv.has("config:price_notices"), false, "nothing stored for a refused notice");
writes = [];
const made = await adm("/price", good);
assert.equal(made.http, 200, JSON.stringify(made));
assert.deepEqual(writes, ["config:price_notices"], "one write, uncounted (the seller's)");
const N = made.notice;
assert.match(N.id, /^[0-9a-f]{8}$/);
assert.deepEqual([N.plan, N.oldPrice, N.newPrice, N.variant, N.effective], ["monthly", 2900, 3900, "111", "2026-12-18"]);
assert.ok(made.text.ko.includes("프로 월간 구독 가격이 2026년 12월 18일 이후 자동 결제부터 2,900원에서 3,900원으로 바뀌어요."), made.text.ko);
assert.ok(made.text.ko.includes("답하지 않으면 동의한 것으로 보지 않고, 새 가격으로 결제하지 않아요.") && made.text.ko.includes("2026년 12월 17일까지 고를 수 있어요."));
assert.ok(made.text.en.includes("from ₩2,900 to ₩3,900") && made.text.en.includes("December 18, 2026") && made.text.en.includes("No answer is not taken as agreement"), made.text.en);
assert.ok(made.email.ko.subject.includes("프로 월간") && made.email.ko.body.includes("- 새 가격: 3,900원") && made.email.en.body.includes("Please choose by December 17, 2026"));
assert.equal((await adm("/price", { ...good, variant: "112" })).http, 409, "one open notice a plan");
const yearly = await adm("/price", { plan: "yearly", oldPrice: 19900, newPrice: 24900, variant: "122", effective: "2027-01-20" });
assert.equal(yearly.http, 200);
// the day after T0 the earliest day moves with it
clock = T0 + DAY;
assert.equal((await adm("/price", { ...good, plan: "ultra_monthly", variant: "211", effective: min })).http, 400);
clock = T0;

// 3. POST /sub/price: the notice of the subscription's own plan, Korean or English, with the holder's answer
let r = await price(keys[1]);
assert.deepEqual(Object.keys(r.notice).sort(), ["effective", "id", "newPrice", "oldPrice", "plan", "text"]);
assert.deepEqual([r.notice.id, r.notice.plan, r.notice.oldPrice, r.notice.newPrice, r.notice.effective, r.choice], [N.id, "monthly", 2900, 3900, "2026-12-18", ""]);
assert.equal(r.notice.text, made.text.ko);
assert.equal((await price(keys[1], "en")).notice.text, made.text.en);
assert.equal((await price(keys[3])).notice.id, yearly.notice.id, "a yearly subscription sees the yearly notice");

// 4. POST /sub/price/choose
assert.deepEqual(await choose(keys[1], N.id, "maybe"), { http: 400, error: "bad request" });
assert.deepEqual(await choose(keys[1], "zzzzzzzz", "agree"), { http: 400, error: "bad request" });
assert.deepEqual(await choose(hand, N.id, "agree"), { http: 403, error: "key" });
assert.deepEqual(await choose(keys[1], yearly.notice.id, "agree"), { http: 404, error: "notice" }, "another plan's notice");
assert.deepEqual(await choose(keys[1], "0000aaaa", "agree"), { http: 404, error: "notice" });
// agree: one KV write, counted in the day's public share
let p0 = pub();
writes = [];
r = await choose(keys[1], N.id, "agree");
assert.deepEqual(r, { http: 200, ok: true, choice: "agree" });
assert.deepEqual(writes, ["pricechoice:" + N.id + ":1"]);
assert.equal(opts.get("pricechoice:" + N.id + ":1").expirationTtl, 3 * 365 * 86400, "kept three years");
assert.equal(md.get("pricechoice:" + N.id + ":1").choice, "agree");
assert.equal(pub(), p0 + 1, "counted in the public share");
assert.equal(ls.length, 0, "agreeing calls nobody");
// the same answer again: nothing written
writes = [];
assert.deepEqual(await choose(keys[1], N.id, "agree"), { http: 200, ok: true, choice: "agree" });
assert.deepEqual(writes, []);
assert.equal((await price(keys[1])).choice, "agree");
// cancel: Lemon Squeezy cancels the subscription, one KV write
writes = [];
r = await choose(keys[2], N.id, "cancel");
assert.deepEqual(r, { http: 200, ok: true, choice: "cancel", cancelled: true });
assert.deepEqual(ls.pop(), { method: "DELETE", sub: "2", body: null });
assert.deepEqual(writes, ["pricechoice:" + N.id + ":2"]);
assert.deepEqual(await choose(keys[2], N.id, "agree"), { http: 409, error: "already" }, "a cancel is final");
assert.deepEqual(await choose(keys[2], N.id, "cancel"), { http: 200, ok: true, choice: "cancel" });
// Lemon Squeezy then ends the subscription; the holder still sees what was chosen
await hook("subscription_updated", { type: "subscriptions", id: "2", attributes: { status: "cancelled", variant_id: 101 } });
assert.equal(kv.has("sub:2"), false);
assert.equal((await price(keys[2])).choice, "cancel");
// agreeing can still turn into a cancel before the day
assert.equal((await choose(keys[4], N.id, "agree")).http, 200);
assert.deepEqual(await choose(keys[4], N.id, "cancel"), { http: 200, ok: true, choice: "cancel", cancelled: true });
assert.equal(ls.pop().sub, "4");
// Lemon Squeezy refuses the cancel: recorded, and the answer says the seller has to cancel it by hand
lsDelete = () => new Response("{}", { status: 500 });
assert.deepEqual(await choose(keys[5], N.id, "cancel"), { http: 200, ok: true, choice: "cancel", cancelled: false });
assert.equal(md.get("pricechoice:" + N.id + ":5").cancelled, false);
assert.ok((await adm("/log")).log.some(l => l.action === "구독 해지 실패" && l.detail.includes("구독 5")));
lsDelete = () => new Response("{}", { status: 200 });
ls.length = 0;
// an ended subscription: no notice, no answer
kv.delete("sub:7");
assert.deepEqual(await price(keys[7]), { http: 200, notice: null, choice: "" });
assert.deepEqual(await choose(keys[7], N.id, "agree"), { http: 409, error: "inactive" });
// the day's public share used up: refused before any write
const cap = 400;
st.obj.take({ items: [{ name: "kv:pub:" + new Date().toISOString().slice(0, 10), ttl: 172800, by: cap - pub() }] });
writes = [];
assert.deepEqual(await choose(keys[6], N.id, "agree"), { http: 429, error: "busy" });
assert.deepEqual(writes, []);
st.db.exec("DELETE FROM counters WHERE name LIKE 'kv:%'");
// 20 answers an hour from one address
_forgetIpLimits?.();
for (let i = 0; i < 20; i++) assert.notEqual((await choose(keys[6], N.id, "agree", "10.77.0.1")).http, 429, "call " + i);
assert.deepEqual(await choose(keys[6], N.id, "agree", "10.77.0.1"), { http: 429, error: "busy" });

// 5. the dashboard's list: who agreed, who cancelled
let list = await adm("/price");
assert.deepEqual([list.today, list.minEffective, list.noticeDays], ["2026-11-18", "2026-12-18", 30]);
let ln = list.notices.find(x => x.id === N.id);
assert.deepEqual([ln.open, ln.agree, ln.cancel], [true, 2, 3]);
assert.deepEqual(ln.choices.filter(c => c.choice === "agree").map(c => c.sub).sort(), ["1", "6"]);
assert.deepEqual(ln.choices.filter(c => c.choice === "cancel").map(c => [c.sub, c.cancelled]).sort(), [["2", true], ["4", true], ["5", false]]);
assert.ok((await adm("/access")).rows.some(x => x.action === "가격 변경 동의"), "viewing the answers is in the access log");

// 6. moving an agreed subscription: not before the day (Korean), then without proration
assert.match((await adm("/price/move", { id: N.id, sub: "1" })).error, /시행일\(2026-12-18, 한국 시간\)부터/);
assert.equal(ls.length, 0);
clock = Date.parse("2026-12-17T15:00:00Z");   // 2026-12-18 00:00 in Korea
assert.deepEqual(await price(keys[1]), { http: 200, notice: null, choice: "" }, "the day has come: no notice");
assert.deepEqual(await choose(keys[8], N.id, "agree"), { http: 409, error: "closed" });
assert.match((await adm("/price/move", { id: N.id, sub: "2" })).error, /동의한 구독만/);
assert.match((await adm("/price/move", { id: N.id, sub: "8" })).error, /동의한 구독만/, "no answer is no consent");
writes = [];
r = await adm("/price/move", { id: N.id, sub: "1" });
assert.equal(r.http, 200, JSON.stringify(r));
assert.deepEqual(ls.pop(), { method: "PATCH", sub: "1", body: { data: { type: "subscriptions", id: "1", attributes: { variant_id: 111, disable_prorations: true } } } });
assert.deepEqual(writes, ["pricechoice:" + N.id + ":1"]);
assert.ok(md.get("pricechoice:" + N.id + ":1").moved);
assert.match((await adm("/price/move", { id: N.id, sub: "1" })).error, /이미 옮겼어요/);
// the new product's update webhook leaves the key alone, and renewals still know the plan
await hook("subscription_updated", { type: "subscriptions", id: "1", attributes: { status: "active", variant_id: 111 } });
assert.equal(kv.get("subplan:1"), "monthly");
// a subscription that changed plan since it agreed is not moved; Lemon Squeezy refusing is reported
kv.set("subplan:6", "ultra_monthly");
assert.match((await adm("/price/move", { id: N.id, sub: "6" })).error, /다른 플랜/);
kv.set("subplan:6", "monthly");
lsPatch = () => new Response("{}", { status: 422 });
r = await adm("/price/move", { id: N.id, sub: "6" });
assert.deepEqual([r.http, r.error], [502, "Lemon Squeezy가 받지 않았어요 (422)"]);
assert.equal(md.get("pricechoice:" + N.id + ":6").moved, undefined);
lsPatch = () => new Response("{}", { status: 200 });
list = await adm("/price");
ln = list.notices.find(x => x.id === N.id);
assert.equal(ln.open, false);

// 7. deleting a notice
assert.equal((await adm("/price/delete", { id: "nope" })).http, 404);
assert.equal((await adm("/price/delete", { id: N.id })).http, 200);
assert.deepEqual((await adm("/price")).notices.map(x => x.id), [yearly.notice.id]);
assert.ok((await adm("/log")).log.some(l => l.action === "가격 변경 안내 삭제"));

// 8. the plan change of a card subscription still sends the same request through the shared helper
clock = T0;
r = await postJ("/subscription/change", { key: keys[3], plan: "ultra_yearly" });
assert.equal(r.http, 503, "live sales without seller details: " + JSON.stringify(r));   // index.js sellerBlocked
kv.set("config:support", JSON.stringify({ seller: { name: "Test Seller", rep: "Test Rep", address: "Test Address 1", bizNo: "000-00-00000", mailOrderNo: "2026-Test-0000", email: "seller@example.com", phone: "000-0000-0000" } }));
r = await postJ("/subscription/change", { key: keys[3], plan: "ultra_yearly" });
assert.deepEqual([r.http, r.plan], [200, "ultra_yearly"]);
assert.deepEqual(ls.pop(), { method: "PATCH", sub: "3", body: { data: { type: "subscriptions", id: "3", attributes: { variant_id: 202 } } } });

console.log("test_price: all passed");
