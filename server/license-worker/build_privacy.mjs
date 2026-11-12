// Regenerates src/privacy_page.js from privacy/privacy.ko.md (the policy) and privacy/privacy.en.md (an English
// summary). Run after editing either:  node build_privacy.mjs   (test_support.mjs fails while the module is stale)
//
// The Markdown uses only what this file turns into HTML: one "# " title, "## " and "### " headings, paragraphs,
// "- " lists (one nested level, two spaces) and tables (a header row, a |---| row, body rows). There is no inline
// formatting: every character of the text is HTML-escaped here, so the Worker needs no Markdown library at run time.
// The blanks {{OWNER_NAME}}, {{OWNER_EMAIL}}, {{SUPABASE_REGION}} and {{EFFECTIVE_DATE}} stay as they are;
// support.js privacyPage() fills them on every request from the dashboard's 고객센터 settings, escaped.
// Any other {{...}}, or a line this file does not understand, stops the build.
import { readFileSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

export const BLANKS = ["OWNER_NAME", "OWNER_EMAIL", "SUPABASE_REGION", "EFFECTIVE_DATE"];
const esc = s => s.replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

export function markdownToHtml(md) {
  const lines = md.replace(/\r\n?/g, "\n").split("\n").map(l => l.trimEnd());
  const out = [], toc = [];
  let title = "", para = [], i = 0;
  const flush = () => { if (para.length) out.push("<p>" + esc(para.join(" ")) + "</p>"); para = []; };
  const cells = row => row.replace(/^\|/, "").replace(/\|$/, "").split("|").map(c => c.trim());
  while (i < lines.length) {
    const line = lines[i];
    let m;
    if (!line) { flush(); i++; continue; }
    if ((m = /^(#{1,3}) (\S.*)$/.exec(line))) {
      flush();
      const text = m[2].trim();
      if (m[1].length === 1) {
        if (title) throw new Error("a second title: " + text);
        title = text;
        out.push("<h1>" + esc(text) + "</h1>");
      } else if (m[1].length === 2) {
        const id = "s" + (toc.length + 1);
        toc.push({ id, text });
        out.push('<h2 id="' + id + '">' + esc(text) + "</h2>");
      } else out.push("<h3>" + esc(text) + "</h3>");
      i++;
      continue;
    }
    if (line.startsWith("|")) {
      flush();
      const rows = [];
      while (i < lines.length && lines[i].startsWith("|")) rows.push(lines[i++]);
      if (rows.length < 3 || !/^\|(\s*:?-{3,}:?\s*\|)+$/.test(rows[1])) throw new Error("a table needs a header row, a |---| row and a body: " + rows[0]);
      const head = cells(rows[0]), body = rows.slice(2).map(cells);
      for (const r of body) if (r.length !== head.length) throw new Error("a table row has " + r.length + " cells, its header " + head.length + ": " + r[0]);
      out.push('<div class="tbl"><table><thead><tr>' + head.map(c => "<th>" + esc(c) + "</th>").join("") + "</tr></thead><tbody>" +
               body.map(r => "<tr>" + r.map(c => "<td>" + esc(c) + "</td>").join("") + "</tr>").join("") + "</tbody></table></div>");
      continue;
    }
    if (line.startsWith("- ")) {
      flush();
      const items = [];
      while (i < lines.length && /^(  )?- \S/.test(lines[i])) {
        const l = lines[i++];
        if (l.startsWith("  ")) items.at(-1).sub.push(l.slice(4));
        else items.push({ text: l.slice(2), sub: [] });
      }
      out.push("<ul>" + items.map(it => "<li>" + esc(it.text) +
               (it.sub.length ? "<ul>" + it.sub.map(s => "<li>" + esc(s) + "</li>").join("") + "</ul>" : "") + "</li>").join("") + "</ul>");
      continue;
    }
    if (/^\s|^[#|>*`]|^\d+\. /.test(line)) throw new Error("not understood here: " + line);
    para.push(line);
    i++;
  }
  flush();
  if (!title) throw new Error("no title");
  const html = out.join("\n");
  for (const [, name] of html.matchAll(/\{\{([^}]*)\}\}/g)) if (!BLANKS.includes(name)) throw new Error("unknown blank {{" + name + "}}");
  for (const name of BLANKS) if (!html.includes("{{" + name + "}}")) throw new Error("the blank {{" + name + "}} is missing");
  return { title, toc, html };
}

const TEXT = {
  ko: { help: "고객센터", helpHref: "/help", other: "English summary", otherHref: "/privacy?lang=en", otherLang: "en", toc: "목차" },
  en: { help: "Support", helpHref: "/help?lang=en", other: "한국어 (the policy)", otherHref: "/privacy", otherLang: "ko", toc: "Contents" },
};

// The whole page; __NONCE__ is this request's CSP nonce (support.js privacyPage()).
export function page(lang, md) {
  const doc = markdownToHtml(md), t = TEXT[lang];
  const toc = '<nav class="toc" aria-label="' + esc(t.toc) + '"><b>' + esc(t.toc) + "</b><ol>" +
              doc.toc.map(s => '<li><a href="#' + s.id + '">' + esc(s.text) + "</a></li>").join("") + "</ol></nav>\n";
  const at = doc.html.indexOf("<h2");
  const body = at < 0 ? doc.html : doc.html.slice(0, at) + toc + doc.html.slice(at);
  return `<!DOCTYPE html>
<html lang="${lang}">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>${esc(doc.title)}</title>
<style nonce="__NONCE__">
:root{--bg:#0b0b0c;--card:#141416;--line:#2a2a2e;--text:#e8e6e3;--muted:#8b8a88;--accent:#ff7a1a;--warn:#f5c04a}
*{box-sizing:border-box}
body{background:var(--bg);color:var(--text);font-family:system-ui,-apple-system,"Segoe UI","Malgun Gothic",sans-serif;max-width:900px;margin:0 auto;padding:24px 16px 64px;line-height:1.65;overflow-wrap:anywhere}
header{display:flex;align-items:baseline;justify-content:space-between;gap:12px;flex-wrap:wrap;font-size:14px}
header .brand{color:var(--accent);font-weight:700}header nav{display:flex;gap:16px;flex-wrap:wrap}
a{color:var(--accent)}
h1{color:var(--accent);font-size:26px;margin:20px 0 12px}h2{font-size:19px;margin:32px 0 10px;padding-top:12px;border-top:1px solid var(--line)}h3{font-size:16px;margin:20px 0 8px}
p,li{margin:6px 0}ul{padding-left:22px}
.toc{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:12px 16px;margin:18px 0;font-size:14px}.toc ol{margin:6px 0 0;padding-left:22px}
.tbl{overflow-x:auto;margin:10px 0}
table{border-collapse:collapse;width:100%;font-size:14px}
th,td{border:1px solid var(--line);padding:8px 10px;text-align:left;vertical-align:top}
th{background:var(--card);color:var(--muted);font-weight:600}td{min-width:7em}
.blank{color:var(--warn);font-weight:600}
</style>
</head>
<body>
<header><span class="brand">SystemBoost</span><nav><a href="${t.helpHref}">${esc(t.help)}</a><a href="${t.otherHref}" hreflang="${t.otherLang}">${esc(t.other)}</a></nav></header>
<main>
${body}
</main>
</body>
</html>
`;
}

export function build() {
  const read = name => readFileSync(new URL("./privacy/" + name, import.meta.url), "utf8");
  const pages = { ko: page("ko", read("privacy.ko.md")), en: page("en", read("privacy.en.md")) };
  return "// Generated by build_privacy.mjs from privacy/privacy.ko.md and privacy/privacy.en.md - edit those files instead.\n" +
         "export const PRIVACY_HTML = " + JSON.stringify(pages, null, 1) + ";\n";
}

const self = resolve(fileURLToPath(import.meta.url)).toLowerCase();
if (process.argv[1] && resolve(process.argv[1]).toLowerCase() === self) {
  writeFileSync(new URL("./src/privacy_page.js", import.meta.url), build());
  console.log("src/privacy_page.js written");
}
