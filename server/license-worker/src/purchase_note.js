// The purchase confirmation on /thanks: the part of the contract a card buyer keeps (plan, dates, how it is paid,
// the licence agreement's version, withdrawal, the minors notice, the seller). The app's key window shows the same
// (app/SystemBoost.App/Pages/PurchaseInfo.cs). The seller's details are the dashboard's 고객센터 settings
// (config:support), as on /help and in the app; an empty field gets no line, so nothing hints at a registration
// number that does not exist. Only relative links: the page is the server's own.
//   thanks() in index.js:  ${await purchaseNote(env, key)}  after the key
import { supportConfig } from "./support.js";

// The licence agreement the app asks for (app/SystemBoost.App/Services/Eula.cs Version; test_purchase_note.mjs
// compares the two). Its text is in the app only: '설정' → '사용권 계약 보기'.
export const EULA_VERSION = "2026-11-12";
// 사용권 계약 제9조 제1항: the dashboard's 청약철회 기간 may be longer, never shorter (PurchaseInfo.LegalDays)
export const LEGAL_DAYS = 7;

const TIER_KO = { premium: "프로", ultra: "울트라", biz: "산업" };
const SELLER_ROWS = [["name", "상호"], ["rep", "대표자"], ["address", "주소"], ["bizNo", "사업자등록번호"],
                     ["mailOrderNo", "통신판매업 신고번호"], ["email", "메일"], ["phone", "전화"], ["hours", "문의 받는 시간"]];

const esc = s => String(s).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const kstDay = ms => new Date(ms + 9 * 3600e3).toISOString().slice(0, 10);   // the date in Korea

// The key's signed part, as the server wrote it (the key on /thanks comes from the server's own store).
function payloadOf(key) {
  const p = String(key || "").trim().split(".")[0] || "";
  try {
    const bin = atob(p.replace(/-/g, "+").replace(/_/g, "/") + "===".slice((p.length + 3) % 4));
    const o = JSON.parse(new TextDecoder().decode(Uint8Array.from(bin, c => c.charCodeAt(0))));
    return o && typeof o === "object" && !Array.isArray(o) ? o : null;
  } catch { return null; }
}

/// The confirmation's lines (plain text) and the seller's filled-in rows.
export async function purchaseLines(env, key) {
  const c = await supportConfig(env);
  const days = Math.max(LEGAL_DAYS, Number(c.refundDays) || 0);
  const p = payloadOf(key);
  const lines = [];
  if (p) {
    let plan = TIER_KO[p.plan] || "프로";
    if (p.sub) {
      let id = null;
      try { id = await env.LICENSES.get("subplan:" + String(p.sub)); } catch {}
      if (/yearly/.test(id || "")) plan += " 연간";
      else if (/monthly/.test(id || "")) plan += " 월간";
    }
    lines.push("플랜: " + plan);
    const issued = Date.parse(p.issued || "");
    if (Number.isFinite(issued)) {
      lines.push("키를 받은 날: " + kstDay(issued));
      lines.push(`청약철회: ${kstDay(issued + days * 864e5)}까지 (키를 받은 날부터 ${days}일)`);
    } else lines.push(`청약철회: 키를 받은 날부터 ${days}일 안에`);
    const end = Date.parse(p.expires || "");
    lines.push(Number.isFinite(end) ? "사용 기간: " + kstDay(end) + "까지" : "사용 기간: 기간 제한 없음");
    lines.push(p.sub
      ? "결제 방식: 카드 구독 · 해지할 때까지 같은 기간마다 자동으로 결제돼요. 해지는 결제 메일의 구독 관리 링크에서 언제든 할 수 있고, " +
        "해지하면 다음 결제가 멈추고 이미 결제한 기간은 끝까지 쓸 수 있어요(사용권 계약 제3조 제3항)."
      : "결제 방식: 직접 결제 · 자동으로 결제되지 않아요");
    if (p.test) lines.push("시험 결제의 키예요. 실제로 결제되지 않았고 하루 뒤 끝나요.");
  } else lines.push(`청약철회: 키를 받은 날부터 ${days}일 안에`);
  lines.push("SystemBoost가 켜져 있으면 키가 이 PC에 바로 등록돼요. 등록한 키로 유료 기능을 쓰기 시작하면 청약철회가 제한될 수 있어요(사용권 계약 제9조 제2항).");
  lines.push("표시·광고와 다르거나 계약과 다르게 제공되면 받은 날부터 3개월, 그 사실을 안 날부터 30일 안에 철회할 수 있어요(제9조 제3항).");
  lines.push("환불은 앱의 고객센터 → '환불 요청'이나 판매자 서버의 도움말 페이지(/help)에서 요청해요.");
  lines.push("미성년자가 법정대리인의 동의 없이 결제하면 미성년자 본인이나 법정대리인이 그 계약을 취소할 수 있어요(사용권 계약 제3조 제11항).");
  lines.push(`사용권 계약: ${EULA_VERSION} 판 (앱의 '설정' → '사용권 계약 보기')`);
  const seller = SELLER_ROWS.map(([f, label]) => [label, String(c.seller?.[f] || "").trim()]).filter(([, v]) => v);
  return { lines, seller };
}

/// The confirmation as an HTML fragment for /thanks (everything escaped).
export async function purchaseNote(env, key) {
  const { lines, seller } = await purchaseLines(env, key);
  const li = lines.map(l => "<li>" + esc(l).replace("(/help)", '(<a href="/help">/help</a>)') + "</li>").join("");
  const who = seller.length
    ? "<dl>" + seller.map(([k, v]) => `<dt>${esc(k)}</dt><dd>${esc(v)}</dd>`).join("") + "</dl>"
    : "<p>판매자가 아직 입력하지 않았어요</p>";
  return `<style>.pn{border:1px solid #333;border-radius:10px;padding:4px 18px 12px;margin-top:28px}.pn h2{font-size:18px}.pn li{margin:4px 0}
.pn dt{color:#8b8a88;font-size:14px}.pn dd{margin:0 0 6px}.pn a{color:#ff9a4d}</style>
<section class="pn"><h2>구매 확인서 (보관용)</h2><p class="m">계약 내용이에요. 이 페이지를 저장하거나 인쇄해 두세요. 앱의 키 창에서도 복사하거나 파일로 저장할 수 있어요.</p>
<ul>${li}</ul><h3>판매자</h3>${who}<p class="m"><a href="/privacy">개인정보 처리방침</a> · <a href="/help">고객센터</a></p></section>`;
}
