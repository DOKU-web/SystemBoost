// The seller's details in public: GET /seller (JSON for the official site and the app), the block under the agreement
// on /terms, the rows on /help (client side), the Korea-only notice on /help, and the dashboard's 판매자 정보 bar.
// Fake KV; global fetch refuses everything: nothing leaves this machine.
//   node test_seller_info.mjs
import assert from "node:assert/strict";
import { generateKeyPairSync } from "node:crypto";
import worker from "./src/index.js";
import { fakeStats } from "./fake_stats.mjs";
import { ADMIN_HTML } from "./src/admin_page.js";
import { HELP_HTML } from "./src/help_page.js";

const kv = new Map();
const { privateKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
const env = {
  LICENSES: { get: async k => kv.get(k) ?? null, getWithMetadata: async k => ({ value: kv.get(k) ?? null, metadata: null }),
              put: async (k, v) => { kv.set(k, v); }, delete: async k => { kv.delete(k); },
              list: async () => ({ keys: [], list_complete: true }) },
  LICENSE_PRIVATE_KEY: privateKey.export({ type: "pkcs8", format: "pem" }),
  ADMIN_TOKEN: "T".repeat(32),
  STATS: fakeStats().binding,
};
globalThis.fetch = async url => assert.fail("no network in this test: " + url);
const call = (path, init = {}) => worker.fetch(new Request("https://lic.example" + path, { ...init, headers: { "CF-Connecting-IP": "10.1.2.3", ...(init.headers || {}) } }), env);
const setSeller = seller => kv.set("config:support", JSON.stringify({ seller }));
const SITE = "https://doku-web.github.io";

// ---- GET /seller
let r = await call("/seller");
assert.equal(r.status, 200);
assert.deepEqual(await r.json(), {}, "nothing entered: {}");
setSeller({ name: "  SystemBoost  ", rep: "Riye", address: "", phone: " ", email: "help@example.com", bizNo: "", mailOrderNo: "", hours: "평일 10:00~18:00" });
r = await call("/seller", { headers: { Origin: SITE } });
const body = await r.json();
assert.deepEqual(body, { name: "SystemBoost", rep: "Riye", email: "help@example.com", hours: "평일 10:00~18:00" }, "only the filled fields, trimmed");
assert.equal(r.headers.get("access-control-allow-origin"), SITE);
assert.match(r.headers.get("vary") || "", /Origin/);
assert.equal(r.headers.get("cache-control"), "public, max-age=300");
assert.equal(r.headers.get("content-type"), "application/json");
setSeller({ name: "N", rep: "R", address: "A", phone: "P", email: "E@x.kr", bizNo: "B", mailOrderNo: "M", hours: "H", extra: "not a field" });
assert.deepEqual(Object.keys(await (await call("/seller")).json()), ["name", "rep", "address", "phone", "email", "bizNo", "mailOrderNo", "hours"], "the eight fields, in order, nothing else");
for (const o of ["https://evil.example", "https://doku-web.github.io.evil.example", "null"]) {
  r = await call("/seller", { headers: { Origin: o } });
  assert.equal(r.status, 200, o);
  assert.equal(r.headers.get("access-control-allow-origin"), null, "no CORS for " + o);
}
assert.equal((await call("/seller")).headers.get("access-control-allow-origin"), null, "no Origin (the app): no CORS header needed");
let opt = await call("/seller", { method: "OPTIONS", headers: { Origin: SITE, "Access-Control-Request-Method": "GET" } });
assert.deepEqual([opt.status, opt.headers.get("access-control-allow-origin"), opt.headers.get("access-control-allow-methods")], [204, SITE, "GET"]);
opt = await call("/seller", { method: "OPTIONS", headers: { Origin: "https://evil.example" } });
assert.deepEqual([opt.status, opt.headers.get("access-control-allow-origin")], [403, null]);
for (const m of ["POST", "PUT", "DELETE"]) {
  r = await call("/seller", { method: m, headers: { Origin: SITE }, body: "{}" });
  assert.equal(r.status, 405, m);
  assert.equal(r.headers.get("allow"), "GET");
}
kv.set("config:support", "not json");
assert.deepEqual(await (await call("/seller")).json(), {}, "a broken config: {}");

// ---- /terms: the seller block outside the agreement text, empty rows hidden, escaped, both languages
setSeller({ name: "Riye <b>Shop</b>", rep: "Riye", address: "", phone: "", email: "help@example.com", bizNo: "", mailOrderNo: "", hours: "" });
for (const [path, labels, missing, title] of [["/terms", ["상호", "대표자", "메일"], ["주소", "전화", "사업자등록번호", "통신판매업 신고번호", "문의 받는 시간"], "판매자 정보"],
                                               ["/terms?lang=en", ["Business name", "Representative", "Email"], ["Address", "Phone", "Business registration no.", "Mail-order business no.", "Support hours"], "Seller"]]) {
  r = await call(path);
  const html = await r.text();
  assert.equal(r.status, 200);
  const block = /<footer class="seller" id="seller" aria-label="[^"]+"><b>([^<]+)<\/b>([\s\S]*?)<\/footer>/.exec(html);
  assert.ok(block, path + ": the seller block");
  assert.equal(block[1], title);
  assert.ok(html.indexOf("</main>") < html.indexOf('<footer class="seller"'), path + ": after the agreement text (outside <main>)");
  for (const l of labels) assert.ok(block[2].includes("<dt>" + l + "</dt>"), path + " shows " + l);
  for (const l of missing) assert.ok(!block[2].includes("<dt>" + l + "</dt>"), path + " hides the empty " + l);
  assert.ok(block[2].includes("<dd>Riye &lt;b&gt;Shop&lt;/b&gt;</dd>") && !html.includes("<b>Shop</b>"), path + ": escaped");
  const nonce = /style-src 'nonce-([^']+)'/.exec(r.headers.get("content-security-policy"))[1];
  assert.equal([...html.matchAll(/<style nonce="([^"]+)">/g)].map(m => m[1]).join(), nonce, "still one style, this load's nonce");
  assert.ok(html.includes(".seller{"), "its style is in that one style element");
  assert.ok(!/<script/i.test(html));
}
setSeller({});
const emptyTerms = await (await call("/terms")).text();
assert.ok(/<footer class="seller"[^>]*><b>판매자 정보<\/b><p class="lead">판매자가 아직 입력하지 않았어요<\/p><\/footer>/.test(emptyTerms), "nothing entered: one line saying so");
kv.delete("config:support");
assert.equal((await call("/terms?lang=en")).status, 200);

// ---- /help: the seller rows are put in by its script only for filled fields; the Korea-only notice is on the page
assert.ok(/if \(!v\) return;\s*\r?\n\s*dl\.appendChild\(el\("dt"/.test(HELP_HTML), "/help skips an empty seller field");
assert.ok(HELP_HTML.includes('<p class="m" data-t="koreaOnly"></p>'));
assert.ok(HELP_HTML.includes('koreaOnly: "SystemBoost는 대한민국에서만 판매해요."') && HELP_HTML.includes('koreaOnly: "SystemBoost is sold only in the Republic of Korea."'));
// the Radeon driver features are left out of the next builds; Article 12-2 is said to apply only where they are included
assert.ok(HELP_HTML.includes("라데온 드라이버 기능은 라이선스 확인이 끝날 때까지 빠졌어요. 프레임 생성은 라데온에서도 그대로 쓸 수 있어요."));
assert.ok(HELP_HTML.includes("Radeon driver features are left out until the license check is finished. Frame generation still works on Radeon."));
assert.ok(HELP_HTML.includes("라데온 드라이버 기능이 들어 있는 빌드에만 적용돼요") && HELP_HTML.includes("apply only to builds that include the Radeon driver features"));
const helpPage = await (await call("/help")).text();
assert.ok(helpPage.includes('data-t="koreaOnly"'));

// ---- the dashboard: the 판매자 정보 bar on 홈 and in 고객센터 설정, with the six a buyer must see
assert.ok(ADMIN_HTML.includes('<div id="sellerWarn" class="warnBar hide" role="alert"></div>'), "the bar on 홈");
assert.ok(ADMIN_HTML.includes('id="sfSellerWarn"') && ADMIN_HTML.includes('id="sfSellerNote"'), "and in 고객센터 설정");
assert.ok(ADMIN_HTML.includes("판매자 정보가 덜 채워졌어요: ${missing.join(\", \")}"));
// run the dashboard's own list against a few settings
const src = /const SELLER_DISCLOSE = [\s\S]*?\n[^\n]*\];\r?\nconst sellerMissingOf = [^\n]*\n/.exec(ADMIN_HTML)[0];
const sellerMissingOf = new Function(src + "return sellerMissingOf;")();
assert.deepEqual(sellerMissingOf({}), ["상호", "대표자 성명", "주소", "전화번호", "전자우편주소", "사업자등록번호"]);
assert.deepEqual(sellerMissingOf({ name: "x", rep: " ", address: "a", phone: "p", email: "e", bizNo: "", mailOrderNo: "" }), ["대표자 성명", "사업자등록번호"]);
assert.deepEqual(sellerMissingOf({ name: "x", rep: "r", address: "a", phone: "p", email: "e", bizNo: "b", mailOrderNo: "" }), [], "통신판매업 신고번호 is not in the bar");

console.log("test_seller_info: all passed");
