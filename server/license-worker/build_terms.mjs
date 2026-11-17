// Regenerates src/terms_page.js (GET /terms) from the app's licence agreement: app/SystemBoost.App/Assets/eula.txt
// (the agreement) and eula.en.txt (a reference translation), and Eula.Version from Services/Eula.cs. Run after
// changing any of them:  node build_terms.mjs   (test_terms.mjs fails while the module is stale)
//
// The page is what the app shows on its agreement screen, so the sales page (/help, EULA Article 1) can carry a
// change for the 30 days Article 13 asks for. The text files use only: lines before the first "## " (dates and the
// seller), "## " article headings, "! " for a line the app shows in bold, and plain lines. Every character is
// HTML-escaped here; anything else stops the build.
import { readFileSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

const esc = s => s.replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const ASSETS = "../../app/SystemBoost.App/Assets/";

export function version() {
  const cs = readFileSync(new URL("../../app/SystemBoost.App/Services/Eula.cs", import.meta.url), "utf8");
  const m = /public const string Version = "(\d{4}-\d{2}-\d{2})";/.exec(cs);
  if (!m) throw new Error("Eula.Version not found in Eula.cs");
  return m[1];
}

export function textToHtml(text) {
  const lines = text.replace(/^﻿/, "").replace(/\r\n?/g, "\n").split("\n").map(l => l.trimEnd());
  const head = [], body = [], toc = [];
  for (const line of lines) {
    if (!line) continue;
    if (line.startsWith("## ")) {
      const t = line.slice(3).trim();
      if (!t) throw new Error("an empty heading");
      const id = "a" + (toc.length + 1);
      toc.push({ id, text: t });
      body.push('<h2 id="' + id + '">' + esc(t) + "</h2>");
      continue;
    }
    if (/^#|^\s/.test(line)) throw new Error("not understood here: " + line);
    const strong = line.startsWith("! ");
    const p = (strong ? '<p class="em">' : "<p>") + esc(strong ? line.slice(2) : line) + "</p>";
    (toc.length ? body : head).push(p);
  }
  if (toc.length < 10) throw new Error("fewer than 10 articles: is this the agreement?");
  return { head: head.join("\n"), body: body.join("\n"), toc };
}

const TEXT = {
  ko: { title: "SystemBoost 사용권 계약", help: "고객센터", helpHref: "/help", other: "English", otherHref: "/terms?lang=en", otherLang: "en",
        privacy: "개인정보 처리방침", privacyHref: "/privacy", toc: "목차",
        lead: v => "SystemBoost는 Windows용 프로그램이에요. 앱의 동의 화면에 나오는 사용권 계약 그대로예요(판 " + v + ", 앱 0.8.18부터). 2027년 1월 1일에 바뀌는 조항의 요약은 고객센터의 '사용권 계약 변경 안내'에 있어요.",
        notice: "/help#terms" },
  en: { title: "SystemBoost License Agreement", help: "Support", helpHref: "/help?lang=en", other: "한국어 (the agreement)", otherHref: "/terms", otherLang: "ko",
        privacy: "Privacy policy", privacyHref: "/privacy?lang=en", toc: "Contents",
        lead: v => "SystemBoost is a program for Windows. The agreement as the app's consent screen shows it (version " + v + ", from app 0.8.18), in a reference translation: the Korean text governs. A summary of the articles that change on January 1, 2027 is under 'License agreement changes' in Support.",
        notice: "/help?lang=en#terms" },
};

// The whole page; __NONCE__ is this request's CSP nonce (support.js termsPage()). No script.
export function page(lang, text, v) {
  const doc = textToHtml(text), t = TEXT[lang];
  const toc = '<nav class="toc" aria-label="' + esc(t.toc) + '"><b>' + esc(t.toc) + "</b><ol>" +
              doc.toc.map(s => '<li><a href="#' + s.id + '">' + esc(s.text) + "</a></li>").join("") + "</ol></nav>";
  return `<!DOCTYPE html>
<html lang="${lang}">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>${esc(t.title)}</title>
<style nonce="__NONCE__">
:root{--bg:#0b0b0c;--card:#141416;--line:#2a2a2e;--text:#e8e6e3;--muted:#8b8a88;--accent:#ff7a1a}
*{box-sizing:border-box}
body{background:var(--bg);color:var(--text);font-family:system-ui,-apple-system,"Segoe UI","Malgun Gothic",sans-serif;max-width:900px;margin:0 auto;padding:24px 16px 64px;line-height:1.65;overflow-wrap:anywhere}
header{display:flex;align-items:baseline;justify-content:space-between;gap:12px;flex-wrap:wrap;font-size:14px}
header .brand{color:var(--accent);font-weight:700}header nav{display:flex;gap:16px;flex-wrap:wrap}
a{color:var(--accent)}
h1{color:var(--accent);font-size:26px;margin:20px 0 12px}h2{font-size:19px;margin:32px 0 10px;padding-top:12px;border-top:1px solid var(--line)}
p{margin:6px 0}.lead{color:var(--muted);font-size:14px}.em{font-weight:700}
.meta{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:10px 16px;margin:16px 0;font-size:14px}
.toc{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:12px 16px;margin:18px 0;font-size:14px}.toc ol{margin:6px 0 0;padding-left:22px}
</style>
</head>
<body>
<header><span class="brand">SystemBoost</span><nav><a href="${t.helpHref}">${esc(t.help)}</a><a href="${t.privacyHref}">${esc(t.privacy)}</a><a href="${t.otherHref}" hreflang="${t.otherLang}">${esc(t.other)}</a></nav></header>
<main>
<h1>${esc(t.title)}</h1>
<p class="lead">${esc(t.lead(v))} <a href="${t.notice}">${esc(lang === "en" ? "Summary" : "요약 보기")}</a></p>
<div class="meta">
${doc.head}
</div>
${toc}
${doc.body}
</main>
</body>
</html>
`;
}

export function build() {
  const read = name => readFileSync(new URL(ASSETS + name, import.meta.url), "utf8");
  const v = version();
  const pages = { ko: page("ko", read("eula.txt"), v), en: page("en", read("eula.en.txt"), v) };
  return "// Generated by build_terms.mjs from app/SystemBoost.App/Assets/eula.txt, eula.en.txt and Eula.Version - edit those instead.\n" +
         "export const TERMS_VERSION = " + JSON.stringify(v) + ";\n" +
         "export const TERMS_HTML = " + JSON.stringify(pages, null, 1) + ";\n";
}

const self = resolve(fileURLToPath(import.meta.url)).toLowerCase();
if (process.argv[1] && resolve(process.argv[1]).toLowerCase() === self) {
  writeFileSync(new URL("./src/terms_page.js", import.meta.url), build());
  console.log("src/terms_page.js written");
}
