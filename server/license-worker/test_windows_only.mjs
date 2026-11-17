// 0.8.14 sells Windows only: the pages and texts buyers see on the server name no Mac, macOS, Apple, GPTK, Wine or
// Steam, /help says "Windows only for now" with the release date, and its trademark line covers the brands it shows.
// The code that still accepts os "mac" (stats.js, the dashboard's download table) is not user text and stays.
// Reads the built pages and sources only; no network.
//   node test_windows_only.mjs
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { HELP_HTML } from "./src/help_page.js";
import { PRIVACY_HTML } from "./src/privacy_page.js";
import { ADMIN_HTML } from "./src/admin_page.js";
import { CATEGORIES, DEFAULT_FAQ, DEPOSIT_GUIDE, REFUND_REASONS } from "./src/support.js";

const src = f => readFileSync(new URL(f, import.meta.url), "utf8");
// "-apple-system" is a CSS font name
const clean = s => String(s).replace(/-apple-system/g, "");
const MAC = /\bmac\b|macos|os x|osx|apple|gptk|game porting toolkit|\bwine\b|steam|valve|맥|애플|스팀/i;
const noMac = (what, s) => { const m = MAC.exec(clean(s)); assert.equal(m, null, what + ": " + (m && clean(s).slice(Math.max(0, m.index - 40), m.index + 40))); };

// 1. what buyers read
noMac("/help", HELP_HTML);
noMac("/privacy ko", PRIVACY_HTML.ko);
noMac("/privacy en", PRIVACY_HTML.en);
noMac("privacy ko source", src("./privacy/privacy.ko.md"));
noMac("privacy en source", src("./privacy/privacy.en.md"));
noMac("FAQ", JSON.stringify(DEFAULT_FAQ));
noMac("support categories and reasons", JSON.stringify([CATEGORIES, REFUND_REASONS, DEPOSIT_GUIDE]));
noMac("purchase note", src("./src/purchase_note.js"));
noMac("support.js", src("./src/support.js"));
noMac("seller alerts", src("./src/seller_alert.js"));

// 2. the dashboard: what it sends to buyers (canned replies, the Discord download template) has no Mac either;
// its download statistics may still count old macOS packages (admin only)
const canned = /const CANNED = \[([\s\S]*?)\n\];/.exec(ADMIN_HTML.replace(/\r\n/g, "\n"));
assert.ok(canned, "the canned replies");
noMac("canned replies", canned[1]);
assert.ok(canned[1].includes("앱 버전과 Windows 버전"));
const tpl = /const AN_TEMPLATES = \[([\s\S]*?)\n\];/.exec(ADMIN_HTML.replace(/\r\n/g, "\n"));
assert.ok(tpl, "the announcement templates");
noMac("announcement templates", tpl[1]);
assert.ok(tpl[1].includes("🪟 Windows (설치판)") && tpl[1].includes("🪟 Windows (포터블)"));

// 3. /help: Windows only, the release date, the trademark line, in both languages
for (const s of ["지원 환경: 현재 윈도우만 지원해요",
                 "Supported system: Windows only for now",
                 "Windows(윈도우)는 Microsoft 그룹 회사의 상표예요.",
                 "Windows is a trademark of the Microsoft group of companies.",
                 '<p class="m" data-t="about"></p>', '<footer class="m" data-t="trademark"></footer>'])
  assert.ok(HELP_HTML.includes(s), s);
// the footer comes after the last section, before the script
assert.ok(HELP_HTML.indexOf('data-t="trademark"') > HELP_HTML.lastIndexOf("</section>") && HELP_HTML.indexOf('data-t="trademark"') < HELP_HTML.indexOf("<script"));
// every brand the page or the default FAQ names is in the trademark line of each language, and nothing else
const BRANDS = ["Windows", "Microsoft", "Cloudflare", "Supabase", "Resend", "Lemon Squeezy", "Discord", "GitHub", "NVIDIA", "DLSS", "AMD", "Radeon", "Intel", "Google", "Stripe"];
const tm = lang => new RegExp(lang + ': \\{[\\s\\S]*?trademark: "([^"]+)"').exec(HELP_HTML)[1];
const shown = HELP_HTML.replace(/trademark: "[^"]+"/g, "").replace(/\/\/[^\n]*/g, "") + JSON.stringify(DEFAULT_FAQ);
for (const lang of ["ko", "en"]) {
  const line = tm(lang);
  for (const b of BRANDS) {
    const onPage = new RegExp("\\b" + b + "\\b").test(shown) || (b === "Windows" && shown.includes("윈도우")) || (b === "Microsoft" && /\bWindows\b|윈도우/.test(shown));
    assert.equal(line.includes(b), onPage, lang + " trademark line and " + b);
  }
}

console.log("test_windows_only: all passed");
