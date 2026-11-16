// 0.8.14: bank transfer, "입금 확인". The support category "deposit" needs an order code; the 24 hours run from the
// order's first "I paid" (paidAt, stored once); the dashboard's summary lists every order waiting with its time left
// (warn from 20 hours, late from 24) and its 입금 확인 ticket; the seller approves or rejects from the ticket, which
// issues the key exactly as 판매·키's '승인' does and answers the buyer in the thread. The guide reads the same in the
// FAQ, on /help and in the purchase confirmation.
// Fake KV and Stats object: nothing leaves this machine.
//   node test_deposit.mjs
import assert from "node:assert/strict";
import { createHash, generateKeyPairSync } from "node:crypto";
import worker from "./src/index.js";
import { fakeStats } from "./fake_stats.mjs";
import { DEFAULT_FAQ, DEPOSIT_GUIDE, LIMITS } from "./src/support.js";
import { HELP_HTML } from "./src/help_page.js";
import { purchaseLines } from "./src/purchase_note.js";

const kv = new Map(), md = new Map(), ttl = new Map();
const st = fakeStats();
const { privateKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
const TOKEN = "T".repeat(32);
const env = {
  LICENSES: {
    get: async k => kv.get(k) ?? null,
    getWithMetadata: async k => ({ value: kv.get(k) ?? null, metadata: md.get(k) ?? null }),
    put: async (k, v, o) => { kv.set(k, v); md.set(k, o?.metadata ?? null); ttl.set(k, o?.expirationTtl); },
    delete: async k => { kv.delete(k); md.delete(k); },
    list: async ({ prefix, limit = 1000 }) => ({ keys: [...kv.keys()].filter(k => k.startsWith(prefix)).sort().slice(0, limit).map(name => ({ name, metadata: md.get(name) })), list_complete: true }),
  },
  LICENSE_PRIVATE_KEY: privateKey.export({ type: "pkcs8", format: "pem" }),
  ADMIN_TOKEN: TOKEN,
  PUBLIC_URL: "https://lic.example",
  SALES_MODE: "test",
  STATS: st.binding,
};
kv.set("config:pay_urls", JSON.stringify({ yearly: "https://pay.example/yearly", monthly: "https://pay.example/monthly" }));

let ipN = 0;
const call = (path, init = {}) => worker.fetch(new Request("https://lic.example" + path, { ...init, headers: { "CF-Connecting-IP": "10.4." + (ipN >> 8 & 255) + "." + (++ipN & 255), ...(init.headers || {}) } }), env);
const j = async (path, init) => { const r = await call(path, init); return { http: r.status, ...(await r.json()) }; };
const postJ = (path, b) => j(path, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(b) });
const adm = (path, body) => j("/admin/api" + path, { method: body ? "POST" : "GET", headers: { Authorization: "Bearer " + TOKEN, "Content-Type": "application/json" }, body: body ? JSON.stringify(body) : undefined });
const order = async plan => (await j(`/order?plan=${plan}&method=manual`, { method: "POST" })).code;
const paid = (code, payer = "홍길동") => postJ("/order/paid", { code, payer, msg: "" });
const ticket = (b) => postJ("/support/ticket", { subject: "입금했어요", text: "방금 보냈어요", consent: true, lang: "ko", ...b });
const meta = code => md.get("ord:" + code);
const setPaidAt = (code, hoursAgo) => md.set("ord:" + code, { ...meta(code), paidAt: new Date(Date.now() - hoursAgo * 36e5).toISOString().replace(/\.\d+Z$/, "Z") });
const kid = key => createHash("sha256").update(key).digest("hex").slice(0, 16);

// 1. the category: listed, and it needs an order code of the right shape
const info = await j("/support/info");
assert.deepEqual(info.categories.find(c => c.id === "deposit"), { id: "deposit", ko: "입금 확인", en: "Payment sent" });
for (const o of [undefined, "", "SB-1234", "SB-AAAA-AAA0", "sb-aaaa"]) {
  const r = await ticket({ cat: "deposit", order: o });
  assert.deepEqual([r.http, r.error], [400, "order"], String(o));
}
assert.equal((await ticket({ cat: "pay" })).http, 201, "other categories still need no order code");

// 2. paidAt: set by the first "I paid", never moved by a later note
const A = await order("yearly");
assert.equal(meta(A).paidAt, undefined);
assert.equal((await paid(A)).status, "paid");
const firstPaid = meta(A).paidAt;
assert.ok(firstPaid);
await new Promise(r => setTimeout(r, 1100));
assert.equal((await paid(A, "홍길동 (다른 이름)")).status, "paid");
assert.equal(meta(A).payer, "홍길동 (다른 이름)");
assert.equal(meta(A).paidAt, firstPaid, "paidAt is stored once");

// a deposit ticket for it: kept 3 years like payment questions, found by its order
const tA = await ticket({ cat: "deposit", order: A.toLowerCase() });
assert.equal(tA.http, 201, JSON.stringify(tA));
assert.equal(ttl.get("tk:" + tA.id), LIMITS.keepLong);
assert.equal(JSON.parse(kv.get("tk:" + tA.id)).order, A);

// 3. the dashboard's to-do: each order waiting, time left from paidAt, its ticket
const B = await order("monthly"), C = await order("monthly"), D = await order("yearly");
for (const c of [B, C, D]) await paid(c);
setPaidAt(B, 21); setPaidAt(C, 25); setPaidAt(D, 2);
let s = await adm("/summary");
const by = Object.fromEntries(s.deposits.map(d => [d.code, d]));
assert.deepEqual(s.deposits.map(d => d.code), [C, B, D, A], "oldest first");
assert.deepEqual([by[A].state, by[B].state, by[C].state, by[D].state], ["ok", "warn", "late", "ok"]);
assert.equal(by[A].ticket, tA.id);
assert.equal(by[B].ticket, undefined);
assert.ok(Math.abs(by[B].hoursLeft - 3) < 0.1 && Math.abs(by[C].hoursLeft + 1) < 0.1, JSON.stringify(by));
assert.equal(Date.parse(by[A].due) - Date.parse(firstPaid), 24 * 36e5);
assert.equal(s.counts.ordersPaid, 4);

// 4. the ticket view shows the order; approving from it issues the key as 판매·키 does and answers the buyer
let tv = await adm("/support/ticket?id=" + tA.id);
assert.equal(tv.depositOrder.code, A);
assert.equal(tv.depositOrder.status, "paid");
assert.equal(tv.depositOrder.payer, "홍길동 (다른 이름)");
assert.equal(tv.depositOrder.paidAt, firstPaid);
const ap = await adm("/support/deposit", { id: tA.id, action: "approve", email: "buyer@example.com" });
assert.equal(ap.http, 200, JSON.stringify(ap));
assert.ok(ap.key && ap.id === kid(ap.key));
assert.equal(ap.replied, true);
assert.equal(meta(A).status, "approved");
assert.equal(meta(A).keyId, ap.id);
assert.equal(md.get("lic:" + ap.id).src, "payment");
assert.ok(md.get("lic:" + ap.id).note.startsWith(A), "the key's note starts with the order code, as an approval from 판매·키");
assert.deepEqual((await postJ("/claim", { session: A })).key, ap.key, "the buyer's app gets the key by its order code");
const last = ap.ticket.messages.at(-1);
assert.equal(last.from, "admin");
assert.ok(last.text.startsWith("입금을 확인했어요. 키를 보냈어요.") && last.text.includes(A), last.text);
assert.equal(ap.ticket.status, "answered");
assert.equal(ap.ticket.unreadUser, true);
assert.equal(ap.depositOrder.status, "approved");
// twice: the order's own answer
const ap2 = await adm("/support/deposit", { id: tA.id, action: "approve" });
assert.deepEqual([ap2.http, ap2.error], [409, "이미 승인된 주문이에요"]);
s = await adm("/summary");
assert.ok(!s.deposits.some(d => d.code === A), "an approved order leaves the to-do");

// an English ticket gets the English reply
const tB = await ticket({ cat: "deposit", order: B, lang: "en" });
const apB = await adm("/support/deposit", { id: tB.id, action: "approve" });
assert.ok(apB.ticket.messages.at(-1).text.startsWith("We have confirmed your payment and sent the key.") && apB.ticket.messages.at(-1).text.includes(B));

// 5. rejecting needs a reason; it rejects the order and tells the buyer why
const tC = await ticket({ cat: "deposit", order: C });
for (const reason of [undefined, "", "   ", "x".repeat(101)]) {
  const r = await adm("/support/deposit", { id: tC.id, action: "reject", reason });
  assert.equal(r.http, 400, String(reason));
}
assert.equal(meta(C).status, "paid", "nothing changed by a refused call");
const rj = await adm("/support/deposit", { id: tC.id, action: "reject", reason: "입금 내역이 없어요" });
assert.equal(rj.http, 200, JSON.stringify(rj));
assert.deepEqual([meta(C).status, meta(C).reason], ["rejected", "입금 내역이 없어요"]);
assert.ok(rj.ticket.messages.at(-1).text.includes("사유: 입금 내역이 없어요"));
assert.deepEqual(await postJ("/claim", { session: C }), { http: 200, status: "rejected", reason: "입금 내역이 없어요" });
assert.equal((await adm("/support/deposit", { id: tC.id, action: "nope" })).http, 400);

// 6. only a 입금 확인 ticket; an unknown order is shown as such
const tPay = await ticket({ cat: "pay", order: D });
assert.equal((await adm("/support/deposit", { id: tPay.id, action: "approve" })).http, 400);
assert.equal(meta(D).status, "paid");
const tX = await ticket({ cat: "deposit", order: "SB-ZZZZ-ZZZZ" });
assert.equal(tX.http, 201, "the order code's shape is checked, not that it exists");
assert.equal((await adm("/support/ticket?id=" + tX.id)).depositOrder, null);
assert.equal((await adm("/support/deposit", { id: tX.id, action: "approve" })).http, 404);
assert.equal((await adm("/support/ticket?id=" + tPay.id)).depositOrder, undefined, "other tickets carry no depositOrder");

// 7. 판매·키's own buttons answer as before
assert.equal((await adm("/orders/approve", { code: "SB-ZZZZ-ZZZZ" })).http, 404);
assert.equal((await adm("/orders/approve", { code: A })).http, 409);
assert.equal((await adm("/orders/reject", { code: A })).http, 409);
const ok = await adm("/orders/approve", { code: D });
assert.equal(ok.http, 200);
assert.ok(ok.key);

// 8. the same guide everywhere
assert.ok(DEPOSIT_GUIDE.ko.includes("24시간은 입금을 알린 때부터예요") && DEPOSIT_GUIDE.en.includes("within 24 hours"));
assert.ok(DEFAULT_FAQ.ko.some(f => f.a.includes(DEPOSIT_GUIDE.ko)) && DEFAULT_FAQ.en.some(f => f.a.includes(DEPOSIT_GUIDE.en)));
assert.ok(HELP_HTML.includes(DEPOSIT_GUIDE.ko) && HELP_HTML.includes(DEPOSIT_GUIDE.en), "/help ko and en");
const b64 = o => Buffer.from(JSON.stringify(o)).toString("base64url");
const { lines } = await purchaseLines(env, b64({ plan: "premium", email: "", issued: "2026-12-10T01:00:00Z" }) + ".sig");
assert.ok(lines.includes("계좌이체 안내: " + DEPOSIT_GUIDE.ko));
// /help: the deposit category makes the order code required and sends the "I paid" note first
assert.ok(HELP_HTML.includes('post("/order/paid", { code: order, payer: $("payer").value.trim() })'));
assert.ok(HELP_HTML.includes('id="depositBox"') && HELP_HTML.includes('id="payer"') && HELP_HTML.includes('id="orderLabel"'));

console.log("test_deposit: all passed");
