// The KV writes (puts and deletes) each action makes, as docs/KV_BUDGET.md counts them. If a number here changes,
// change the document too. Fake KV, fake Stats object, signed webhooks; global fetch refuses everything.
//   node test_kv_writes.mjs          (prints the table; fails if it differs from WANT)
import assert from "node:assert/strict";
import { createHmac, generateKeyPairSync } from "node:crypto";
import worker from "./src/index.js";
import { fakeStats } from "./fake_stats.mjs";

const kv = new Map(), md = new Map();
let puts = [], dels = [];
const st = fakeStats();
const { privateKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
const SECRET = "made-up-signing-secret";
const TOKEN = "T".repeat(32);
const env = {
  LICENSES: {
    get: async k => kv.get(k) ?? null,
    getWithMetadata: async k => ({ value: kv.get(k) ?? null, metadata: md.get(k) ?? null }),
    put: async (k, v, o) => { puts.push(k); kv.set(k, v); md.set(k, o?.metadata ?? null); },
    delete: async k => { dels.push(k); kv.delete(k); md.delete(k); },
    list: async ({ prefix, limit = 1000 }) => ({ keys: [...kv.keys()].filter(k => k.startsWith(prefix)).sort().slice(0, limit).map(name => ({ name, metadata: md.get(name) })), list_complete: true }),
  },
  LICENSE_PRIVATE_KEY: privateKey.export({ type: "pkcs8", format: "pem" }),
  ADMIN_TOKEN: TOKEN,
  PUBLIC_URL: "https://lic.example",
  SALES_MODE: "test",
  CARD_LIVE_FROM: "2000-01-01T00:00:00Z",
  LS_API_KEY: "lsk_made_up",
  LS_SIGNING_SECRET: SECRET,
  STATS: st.binding,
};
globalThis.fetch = async url => {
  if (String(url) === "https://api.lemonsqueezy.com/v1/checkouts") return new Response(JSON.stringify({ data: { attributes: { url: "https://shop.example/c" } } }), { status: 201 });
  assert.fail("no other network in this test: " + url);
};
kv.set("config:pay_urls", JSON.stringify({ yearly: "https://pay.example/yearly" }));
kv.set("config:ls", JSON.stringify({ store: "42", variants: { monthly: "101", yearly: "102" } }));

let ipN = 0;
const call = (path, init = {}) => worker.fetch(new Request("https://lic.example" + path, { ...init, headers: { "CF-Connecting-IP": "10.1." + (ipN >> 8 & 255) + "." + (++ipN & 255), ...(init.headers || {}) } }), env);
const j = async (path, init) => { const r = await call(path, init); return { http: r.status, ...(await r.json()) }; };
const postJ = (path, b) => j(path, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(b) });
const adm = (path, body) => j("/admin/api" + path, { method: body ? "POST" : "GET", headers: { Authorization: "Bearer " + TOKEN, "Content-Type": "application/json" }, body: body ? JSON.stringify(body) : undefined });
const hook = async (event, data, custom = {}) => {
  const raw = JSON.stringify({ meta: { event_name: event, test_mode: true, custom_data: custom }, data });
  const r = await call("/webhook/lemonsqueezy", { method: "POST", body: raw, headers: { "X-Signature": createHmac("sha256", SECRET).update(raw).digest("hex") } });
  return { http: r.status, ...(await r.json()) };
};
const rows = [];
async function count(label, fn) {
  puts = []; dels = [];
  const r = await fn();
  rows.push({ label, puts: puts.length, deletes: dels.length, keys: [...puts, ...dels.map(k => "-" + k)].map(k => k.replace(/:[^:]*$/, ":…")).join(" ") });
  return r;
}

// an install and a running app: status, a usage ping, a key check
await count("install / app start (status, ping, key check)", async () => {
  assert.equal((await j("/status")).http, 200);
  await postJ("/ping", { id: "a".repeat(32), v: "0.8.14", os: "win", plan: "free" });
  await j("/check?id=" + "0".repeat(16));
});
// bank transfer, start to end
const bank = await count("bank transfer: order (POST /order)", async () => j("/order?plan=yearly&method=manual", { method: "POST" }));
await count("bank transfer: first \"I paid\" (POST /order/paid)", async () => assert.equal((await postJ("/order/paid", { code: bank.code, payer: "홍길동" })).status, "paid"));
const tk = await count("bank transfer: 입금 확인 ticket (POST /support/ticket)", async () => postJ("/support/ticket", { cat: "deposit", order: bank.code, subject: "입금했어요", text: "보냈어요", consent: true }));
await count("seller: approve from the ticket (key + auto reply)", async () => assert.equal((await adm("/support/deposit", { id: tk.id, action: "approve" })).http, 200));
await count("buyer: reads the reply (unread mark cleared)", async () => assert.equal((await j("/support/ticket?id=" + tk.id, { headers: { "X-Ticket-Token": tk.token } })).http, 200));
await count("app: picks up the key (POST /claim)", async () => assert.equal((await postJ("/claim", { session: bank.code })).status, "ready"));
const bank2 = await j("/order?plan=yearly&method=manual", { method: "POST" });
await postJ("/order/paid", { code: bank2.code, payer: "김철수" });
await count("seller: approve from 판매·키 (POST /orders/approve)", async () => assert.equal((await adm("/orders/approve", { code: bank2.code })).http, 200));
// card subscription
const card = await count("card: order (POST /order method=card)", async () => j("/order?plan=monthly&method=card", { method: "POST" }));
const sub = { type: "subscriptions", id: "77", attributes: { status: "active", variant_id: 101, order_id: 9077, user_email: "c@example.com", renews_at: "2027-01-10T10:00:00.000000Z" } };
await count("card: first payment (webhook subscription_created)", async () => assert.equal((await hook("subscription_created", sub, { session: card.code })).http, 200));
await count("card: renewal (webhook subscription_payment_success)", async () => assert.equal((await hook("subscription_payment_success", { type: "subscription-invoices", id: "8001", attributes: { subscription_id: 77, billing_reason: "renewal", status: "paid", user_email: "c@example.com" } })).http, 200));
// a referral gift
await adm("/referrals", { code: "LAUNCH", days: 3, max: 1000 });
await count("referral code gift (POST /referral)", async () => assert.ok((await postJ("/referral", { code: "LAUNCH", device: "1".repeat(32) })).key));

console.table(rows.map(r => ({ action: r.label, puts: r.puts, deletes: r.deletes })));
const WANT = {
  "install / app start (status, ping, key check)": [0, 0],
  "bank transfer: order (POST /order)": [1, 0],
  "bank transfer: first \"I paid\" (POST /order/paid)": [1, 0],
  "bank transfer: 입금 확인 ticket (POST /support/ticket)": [1, 0],
  "seller: approve from the ticket (key + auto reply)": [4, 0],
  "buyer: reads the reply (unread mark cleared)": [1, 0],
  "app: picks up the key (POST /claim)": [0, 0],
  "seller: approve from 판매·키 (POST /orders/approve)": [3, 0],
  "card: order (POST /order method=card)": [1, 0],
  "card: first payment (webhook subscription_created)": [10, 0],
  "card: renewal (webhook subscription_payment_success)": [7, 0],
  "referral code gift (POST /referral)": [3, 0],
};
for (const r of rows) {
  const [p, d] = WANT[r.label];
  if (p !== null) assert.equal(r.puts, p, r.label + ": " + r.keys);
  assert.equal(r.deletes, d, r.label + ": " + r.keys);
}
console.log(rows.map(r => r.label + " = " + r.keys).join("\n"));
console.log("test_kv_writes: all passed");
