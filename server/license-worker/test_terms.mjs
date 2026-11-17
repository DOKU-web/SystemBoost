// GET /terms (the licence agreement as the app shows it) and the change notice on /help (EULA Article 13: a change is
// announced on the sales page and in the app 30 days before it is in force).
//   node test_terms.mjs
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { generateKeyPairSync } from "node:crypto";
import worker from "./src/index.js";
import { fakeStats } from "./fake_stats.mjs";
import { build, textToHtml, version } from "./build_terms.mjs";

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
const get = path => worker.fetch(new Request("https://lic.example" + path, { headers: { "CF-Connecting-IP": "10.1.2.3" } }), env);
const eula = name => readFileSync(new URL("../../app/SystemBoost.App/Assets/" + name, import.meta.url), "utf8");

// 1. the module is made from the app's own files
assert.equal(build(), readFileSync(new URL("./src/terms_page.js", import.meta.url), "utf8").replace(/\r\n/g, "\n"),
             "src/terms_page.js is up to date with the app's eula.txt, eula.en.txt and Eula.Version (run node build_terms.mjs)");
assert.match(version(), /^\d{4}-\d{2}-\d{2}$/);
const purchaseNote = readFileSync(new URL("./src/purchase_note.js", import.meta.url), "utf8");
assert.ok(purchaseNote.includes('"' + version() + '"'), "the /thanks page quotes the same agreement version");

// 2. the page: every line of the Korean agreement, escaped, no script
for (const [path, file, lang, art13] of [["/terms", "eula.txt", "ko", "제13조 (계약의 변경)"], ["/terms?lang=en", "eula.en.txt", "en", "Article 13"]]) {
  const r = await get(path), html = await r.text();
  assert.equal(r.status, 200, path);
  assert.equal(r.headers.get("content-type"), "text/html; charset=utf-8");
  const csp = r.headers.get("content-security-policy");
  const nonce = /style-src 'nonce-([^']+)'/.exec(csp)?.[1];
  assert.ok(nonce && html.includes('<style nonce="' + nonce + '">') && !html.includes("__NONCE__"), "this load's nonce on the one style");
  assert.ok(csp.startsWith("default-src 'none';") && !/script-src/.test(csp), "no script allowed");
  assert.ok(!/<script/i.test(html), "no script on the page");
  assert.equal(r.headers.get("x-frame-options"), "DENY");
  assert.ok(html.includes('<html lang="' + lang + '">'));
  assert.ok(html.includes(art13), path + ": " + art13);
  const esc = s => s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;").replace(/'/g, "&#39;");
  for (const line of eula(file).replace(/\r\n?/g, "\n").split("\n").map(l => l.trimEnd()).filter(Boolean)) {
    const text = line.startsWith("## ") ? line.slice(3) : line.startsWith("! ") ? line.slice(2) : line;
    assert.ok(html.includes(esc(text)), path + " shows every line of " + file + ": " + text.slice(0, 40));
  }
  assert.equal((html.match(/<h2 id="a\d+">/g) || []).length, (eula(file).match(/^## /gm) || []).length, "one heading per article");
}
assert.ok((await (await get("/terms")).text()).includes("2027년 1월 1일"), "the date of the coming change is on the page");

// 3. the builder escapes and stops on what it does not know
const doc = textToHtml("시행일: <b>\n" + Array.from({ length: 10 }, (_, i) => "## 제" + (i + 1) + "조 (x)\n! 1. \"강조\" & <i>\n2. 보통").join("\n"));
assert.ok(doc.head.includes("&lt;b&gt;") && doc.body.includes('<p class="em">1. &quot;강조&quot; &amp; &lt;i&gt;</p>') && !doc.body.includes("<i>"));
for (const bad of ["### 소제목", " 들여쓴 줄", "# 제목"]) assert.throws(() => textToHtml(Array.from({ length: 10 }, (_, i) => "## 제" + i + "조").join("\n") + "\n" + bad), Error, bad);
assert.throws(() => textToHtml("## 제1조\n본문"), /fewer than 10/);

// 4. /help announces the change and links the full text, in both languages
for (const [path, link, n] of [["/help", "/terms", 11], ["/help?lang=en", "/terms?lang=en", 11]]) {
  const html = await (await get(path)).text();
  assert.ok(html.includes('<div id="terms" class="box terms" hidden>') && html.includes('id="termsNav" href="/terms"'), path + ": the notice box and the link");
  assert.ok(html.includes('$("termsNav").href = $("termsLink").href = L === "en" ? "/terms?lang=en" : "/terms"'), path + ": " + link);
  assert.ok(html.includes("Date.now() < Date.UTC(2027, 0, 31, 15)"), "shown until the end of January 2027 (KST)");
  const lists = [...html.matchAll(/termsItems: \[([\s\S]*?)\]/g)].map(m => (m[1].match(/^\s*"/gm) || []).length);
  assert.deepEqual(lists, [n, n], path + ": eleven changed articles in Korean and in English");
}
// every article the agreement names for 2027-01-01 is in the Korean notice
const help = await (await get("/help")).text();
const ko = /termsItems: \[([\s\S]*?)\]/.exec(help)[1];
const line2027 = eula("eula.txt").split("\n").find(l => l.startsWith("개정 시행일: 2027년 1월 1일"));
assert.ok(line2027, "the agreement names what changes on 2027-01-01");
for (const art of ["제1조", "제3조 제2항", "제3조 제3항", "제3조 제10항", "제3조 제11항", "제6조의2", "제8조", "제9조 제2항", "제9조 제5항", "제10조 제3항", "제12조의2"]) {
  assert.ok(ko.includes(art + ":") || ko.includes(art + "("), "the notice names " + art);
}
for (const [a, ps] of [["제1조", []], ["제3조", ["제2항", "제3항", "제10항", "제11항"]], ["제6조의2", []], ["제8조", []], ["제9조", ["제2항", "제5항"]], ["제10조", ["제3항"]], ["제12조의2", []]]) {
  assert.ok(line2027.includes(a), "the agreement's 2027-01-01 line names " + a);
  for (const p of ps) assert.ok(line2027.includes(p.replace("제", "")) || line2027.includes(p), a + " " + p);
}

console.log("terms tests passed");
