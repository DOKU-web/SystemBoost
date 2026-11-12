// Local test of the purchase confirmation on /thanks (src/purchase_note.js): fake KV, no network.
//   node test_purchase_note.mjs
// Also checks that it quotes the licence agreement version and the legal withdrawal period the app uses.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { EULA_VERSION, LEGAL_DAYS, purchaseLines, purchaseNote } from "./src/purchase_note.js";

const app = f => readFileSync(new URL("../../app/SystemBoost.App/" + f, import.meta.url), "utf8");
assert.equal(EULA_VERSION, /const string Version = "([^"]+)"/.exec(app("Services/Eula.cs"))[1], "Eula.cs Version");
assert.equal(LEGAL_DAYS, Number(/const int LegalDays = (\d+);/.exec(app("Pages/PurchaseInfo.cs"))[1]), "PurchaseInfo.LegalDays");

const kv = new Map();
const env = { LICENSES: { get: async k => kv.get(k) ?? null } };
const b64url = s => Buffer.from(s).toString("base64url");
const keyOf = payload => b64url(JSON.stringify(payload)) + "." + b64url("signature");

// a card subscription key, issued late in the day UTC (already the next day in Korea)
kv.set("subplan:4242", "ultra_yearly");
const card = keyOf({ plan: "ultra", email: "a@b.c", issued: "2026-12-08T16:30:00Z", sub: "4242", expires: "2027-12-12T16:30:00Z" });
let { lines, seller } = await purchaseLines(env, card);
assert.ok(lines.includes("플랜: 울트라 연간"), lines.join("\n"));
assert.ok(lines.includes("키를 받은 날: 2026-12-09"));
assert.ok(lines.includes("청약철회: 2026-12-16까지 (키를 받은 날부터 7일)"));
assert.ok(lines.includes("사용 기간: 2027-12-13까지"));
assert.ok(lines.some(l => l.startsWith("결제 방식: 카드 구독") && l.includes("제3조 제3항")));
assert.ok(lines.some(l => l.includes("제3조 제11항")), "minors notice");
assert.ok(lines.includes(`사용권 계약: ${EULA_VERSION} 판 (앱의 '설정' → '사용권 계약 보기')`));
assert.deepEqual(seller, []);   // nothing entered on the dashboard yet

// nothing entered: one line that says so, no labels for numbers that do not exist
let html = await purchaseNote(env, card);
assert.ok(html.includes("판매자가 아직 입력하지 않았어요"));
for (const label of ["사업자등록번호", "통신판매업 신고번호", "판매 페이지 참고"]) assert.ok(!html.includes(label), label);
assert.ok(!/https?:\/\//.test(html), "only relative links");
assert.ok(html.includes('<a href="/help">/help</a>') && html.includes('<a href="/privacy">'));

// the dashboard's details: only filled fields, escaped; a shorter 청약철회 기간 never goes below 7 days
kv.set("config:support", JSON.stringify({ refundDays: 3, seller: { name: "DOKKU <b>&", email: "seller@example.com", bizNo: "" } }));
({ lines, seller } = await purchaseLines(env, card));
assert.deepEqual(seller, [["상호", "DOKKU <b>&"], ["메일", "seller@example.com"]]);
assert.ok(lines.includes("청약철회: 2026-12-16까지 (키를 받은 날부터 7일)"));
html = await purchaseNote(env, card);
assert.ok(html.includes("<dd>DOKKU &lt;b&gt;&amp;</dd>") && !html.includes("<b>&"));
assert.ok(!html.includes("사업자등록번호") && !html.includes("판매자가 아직 입력하지 않았어요"));

// a longer period from the dashboard is shown as it is
kv.set("config:support", JSON.stringify({ refundDays: 14 }));
({ lines } = await purchaseLines(env, card));
assert.ok(lines.includes("청약철회: 2026-12-23까지 (키를 받은 날부터 14일)"));

// a direct-payment key without an end date, a test key, and something that is not a key
({ lines } = await purchaseLines(env, keyOf({ plan: "biz", email: "", issued: "2026-12-10T01:00:00Z" })));
assert.ok(lines.includes("플랜: 산업") && lines.includes("사용 기간: 기간 제한 없음") && lines.includes("결제 방식: 직접 결제 · 자동으로 결제되지 않아요"));
({ lines } = await purchaseLines(env, keyOf({ plan: "premium", email: "", issued: "2026-12-10T01:00:00Z", sub: "7", expires: "2026-12-11T01:00:00Z", test: true })));
assert.ok(lines.includes("플랜: 프로") && lines.some(l => l.startsWith("시험 결제의 키")));
html = await purchaseNote(env, "<script>alert(1)</script>");
assert.ok(html.includes("청약철회: 키를 받은 날부터 14일 안에") && !html.includes("<script>"));

// /help says the same: no "see the sales page" for empty seller fields, the minors notice, never under 7 days
const { HELP_HTML } = await import("./src/help_page.js");
assert.ok(!HELP_HTML.includes("판매 페이지 참고") && !HELP_HTML.includes("See the sales page"));
assert.ok(HELP_HTML.includes('<p data-t="minors"></p>') && HELP_HTML.includes("제3조 제11항"));
assert.ok(HELP_HTML.includes("days > 7 ? fill(TX.refundDaysLonger, days) : TX.refundDays"));
assert.ok(HELP_HTML.includes('sellerEmpty: "판매자가 아직 입력하지 않았어요"'));

console.log("purchase note tests passed");
