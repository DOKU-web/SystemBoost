// GET /help: the support center for people without the app (FAQ, refund policy, seller info, a request form,
// and a way to read a request again). Served by support.js helpPage() with a per-request CSP nonce.
// Every value that comes from the server or from a person is put in with textContent, never as HTML.
// Korean by default, English with ?lang=en (the server sets <html lang>).
// The ticket token goes in the X-Ticket-Token header (reading) or the JSON body (reply, close), never in a URL.
// Lengths are counted in characters like the server ([...s]), not with maxlength (UTF-16 units): the page shows the
// count and refuses to send over the limit instead of cutting.
// Kept as String.raw so the script's backslashes stay as written: no backticks and no "${" below.
export const HELP_HTML = String.raw`<!DOCTYPE html>
<html lang="ko">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>SystemBoost 고객센터</title>
<style nonce="__NONCE__">
:root{--bg:#0b0b0c;--card:#141416;--line:#2a2a2e;--text:#e8e6e3;--muted:#8b8a88;--accent:#ff7a1a;--bad:#ff6b6b;--ok:#6bd68a}
*{box-sizing:border-box}[hidden]{display:none!important}
body{background:var(--bg);color:var(--text);font-family:system-ui,-apple-system,"Segoe UI","Malgun Gothic",sans-serif;max-width:760px;margin:0 auto;padding:32px 16px 64px;line-height:1.6}
header{display:flex;align-items:baseline;justify-content:space-between;gap:12px;flex-wrap:wrap}
h1{color:var(--accent);margin:0;font-size:26px}h2{font-size:19px;margin:0 0 12px}h3{font-size:16px;margin:0 0 8px}
a{color:var(--accent)}.m{color:var(--muted);font-size:14px}
nav{display:flex;flex-wrap:wrap;gap:8px 16px;margin:16px 0 8px;font-size:14px}
section{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:18px;margin-top:16px}
.box{border:1px solid var(--line);border-radius:10px;padding:12px;margin-top:16px;white-space:pre-wrap}.warn{border-color:var(--bad)}
noscript .box p{margin:4px 0}
details{border-top:1px solid var(--line);padding:10px 0}details:first-child{border-top:0}
summary{cursor:pointer;font-weight:600}details p{margin:8px 0 0;white-space:pre-wrap}
blockquote{margin:12px 0;padding:8px 14px;border-left:3px solid var(--accent);color:var(--text)}blockquote p{margin:6px 0}
dl{display:grid;grid-template-columns:max-content 1fr;gap:4px 14px;margin:0}dt{color:var(--muted)}dd{margin:0;overflow-wrap:anywhere}
label{display:block;margin-top:12px;font-size:14px;color:var(--muted)}
.cnt{float:right;font-size:13px;font-variant-numeric:tabular-nums}
.note{margin:4px 0 0}
input,select,textarea{width:100%;margin-top:4px;background:#0f0f11;color:var(--text);border:1px solid var(--line);border-radius:8px;padding:9px 10px;font:inherit}
textarea{resize:vertical;min-height:120px}
.consent{font-size:14px;color:var(--text)}.consent b{display:block;margin-bottom:4px}.consent a{display:block;margin-top:6px}
.check{display:flex;gap:10px;align-items:flex-start;color:var(--text)}.check input{width:auto;margin-top:5px}
button{margin-top:14px;background:var(--accent);color:#111;border:0;border-radius:8px;padding:9px 16px;font:inherit;font-weight:600;cursor:pointer}
button.ghost{background:transparent;color:var(--text);border:1px solid var(--line)}button:disabled{opacity:.6;cursor:default}
.row{display:flex;gap:8px;flex-wrap:wrap;align-items:center}.row button{margin-top:8px}
.msg{border:1px solid var(--line);border-radius:10px;padding:10px 12px;margin-top:10px}.msg.admin{border-color:var(--accent)}
.msg .who{font-size:13px;color:var(--muted)}.msg .body{white-space:pre-wrap;overflow-wrap:anywhere;margin-top:4px}
.err{color:var(--bad)}.okc{color:var(--ok)}code{background:#0f0f11;border:1px solid var(--line);border-radius:6px;padding:2px 6px;overflow-wrap:anywhere}
</style>
</head>
<body>
<noscript><div class="box warn"><p>이 페이지는 JavaScript가 있어야 동작해요. SystemBoost 앱의 '고객센터'에서도 문의할 수 있어요.</p><p>This page needs JavaScript. You can also send a request from 'Support' in the SystemBoost app.</p></div></noscript>
<header><h1 data-t="title"></h1><a id="langLink" href="?lang=en"></a></header>
<p class="m" data-t="lead"></p>
<nav><a href="#s-faq" data-t="faq"></a><a href="#s-refund" data-t="refund"></a><a href="#s-seller" data-t="seller"></a><a href="#s-ask" data-t="ask"></a><a href="#s-mine" data-t="mine"></a><a id="privacyNav" href="/privacy" data-t="privacy"></a></nav>
<div id="offline" class="box warn" hidden></div>
<div id="notice" class="box" hidden></div>

<section id="s-faq"><h2 data-t="faq"></h2><div id="faq"><p class="m" data-t="loading"></p></div></section>

<section id="s-refund"><h2 data-t="refund"></h2>
<p data-t="refundLead"></p>
<blockquote id="policy"></blockquote>
<p id="refundDays"></p>
<p data-t="refundHow"></p>
<ul><li data-t="refundCard"></li><li data-t="refundManual"></li></ul>
<p data-t="minors"></p>
</section>

<section id="s-seller"><h2 data-t="seller"></h2><dl id="seller"></dl></section>

<section id="s-ask"><h2 data-t="ask"></h2>
<p id="closedNote" class="err" data-t="closed" hidden></p>
<form id="form" novalidate>
<label><span data-t="cat"></span><select id="cat"></select></label>
<label><span data-t="subject"></span><span class="cnt" id="subjectN"></span><input id="subject" autocomplete="off"></label>
<label><span data-t="text"></span><span class="cnt" id="textN"></span><textarea id="text"></textarea></label>
<div id="refundBox" hidden>
<label><span data-t="reason"></span><select id="reason"></select></label>
<p class="m note" data-t="refundNeeds"></p>
</div>
<label><span data-t="order"></span><input id="order" maxlength="12" placeholder="SB-XXXX-XXXX" autocomplete="off"></label>
<p class="m note" data-t="orderNote"></p>
<label><span data-t="key"></span><input id="key" maxlength="4000" autocomplete="off" spellcheck="false"></label>
<p class="m note" data-t="keyNote"></p>
<label><span data-t="contact"></span><span class="cnt" id="contactN"></span><input id="contact" autocomplete="off"></label>
<p class="m note" data-t="contactNote"></p>
<div class="box consent"><b data-t="consentTitle"></b><span data-t="consentText"></span><a id="privacyLink" href="/privacy" data-t="privacyMore"></a></div>
<label class="check"><input type="checkbox" id="consent"><span data-t="consent"></span></label>
<button type="submit" id="send" data-t="send"></button>
<p id="formMsg" role="status"></p>
</form>
<div id="sent" class="box" hidden>
<p class="okc" data-t="sentTitle"></p>
<p><span data-t="ticketId"></span>: <code id="sentId"></code></p>
<p><span data-t="token"></span>: <code id="sentToken"></code></p>
<p class="m" id="sentSaved"></p>
<div class="row"><button type="button" id="sentOpen" data-t="openIt"></button><button type="button" class="ghost" id="sentNew" data-t="another"></button></div>
</div>
</section>

<section id="s-mine"><h2 data-t="mine"></h2>
<div id="saved"></div>
<form id="lookup" novalidate>
<label><span data-t="ticketId"></span><input id="lkId" maxlength="11" placeholder="T-XXXX-XXXX" autocomplete="off"></label>
<label><span data-t="token"></span><input id="lkToken" maxlength="64" autocomplete="off" spellcheck="false"></label>
<button type="submit" data-t="look"></button>
<p id="lkMsg" role="status"></p>
</form>
<div id="thread" hidden>
<h3 id="thSubject"></h3>
<p class="m" id="thMeta"></p>
<p id="thRefund" hidden></p>
<div id="thMsgs"></div>
<label><span data-t="replyLabel"></span><span class="cnt" id="thReplyN"></span><textarea id="thReply"></textarea></label>
<div class="row"><button type="button" id="thSend" data-t="reply"></button><button type="button" class="ghost" id="thClose" data-t="closeIt"></button></div>
<p id="thMsg" role="status"></p>
</div>
</section>

<script nonce="__NONCE__">
(function () {
  "use strict";
  var L = document.documentElement.lang === "en" ? "en" : "ko";
  var TX = {
    ko: {
      title: "SystemBoost 고객센터", other: "English", lead: "자주 묻는 질문과 환불 안내를 보고, 앱 없이도 문의를 남길 수 있어요.",
      faq: "자주 묻는 질문", refund: "환불 · 청약철회", seller: "판매자 정보", ask: "문의하기", mine: "내 문의", loading: "불러오는 중…",
      privacy: "개인정보 처리방침", privacyMore: "자세한 내용은 개인정보 처리방침에서 볼 수 있어요",
      refundLead: "환불(청약철회)은 사용권 계약 제9조를 따라요. 아래는 그 조항 그대로예요. 제2항과 제5항은 2027년 1월 1일부터 시행하는 문구이고, 그 전에는 이전 문구가 적용돼요.",
      refundDays: "청약철회 기간: 키를 받은 날부터 7일 (사용권 계약 제9조 제1항)",
      refundDaysLonger: "판매자가 안내하는 청약철회 기간: 키를 받은 날부터 {n}일 (법이 정한 7일보다 길어요)",
      minors: "미성년자가 법정대리인의 동의 없이 결제하면 미성년자 본인이나 법정대리인이 그 계약을 취소할 수 있어요(사용권 계약 제3조 제11항).",
      refundHow: "환불 요청은 아래 '문의하기'에서 문의 종류 '환불'로 보내 주세요. 판매자가 확인하고 이 문의로 답해요.",
      refundCard: "카드 결제(Lemon Squeezy): 판매자가 Lemon Squeezy에서 환불하면 키가 막히고, 이 문의에 환불 완료가 표시돼요.",
      refundManual: "계좌이체 · 직접 결제: 판매자가 직접 돌려 드린 뒤 환불 완료로 표시하고 키를 막아요.",
      sellerEmpty: "판매자가 아직 입력하지 않았어요",
      sellerLabels: { name: "상호", rep: "대표자", address: "주소", bizNo: "사업자등록번호", mailOrderNo: "통신판매업 신고번호", email: "메일", phone: "전화", hours: "문의 받는 시간" },
      closed: "지금은 문의를 받지 않아요. 보낸 문의는 아래 '내 문의'에서 볼 수 있어요.",
      closedRefunds: "지금은 일반 문의를 받지 않아요. 환불(청약철회) 요청은 그대로 받아요. 다른 문의는 이 페이지의 판매자 정보에 있는 연락처로 보내 주세요. 보낸 문의는 아래 '내 문의'에서 볼 수 있어요.",
      cat: "문의 종류", subject: "제목", text: "내용", reason: "환불 사유", refundNeeds: "환불 요청에는 주문 코드나 라이선스 키 중 하나가 필요해요.",
      order: "주문 코드 (선택)", orderNote: "앱의 '★ 플랜'에서 결제할 때 받은 코드예요. 적은 경우에만 보내요.",
      key: "라이선스 키 또는 키 식별 번호 (선택)",
      keyNote: "키 전체를 붙여 넣으면 서버가 서명으로 구매자의 키인지 확인해요. 키는 확인에만 쓰고 저장하지 않아요. 문의에는 키 식별 번호(16자리)만 남아요. 키 식별 번호만 적으면 주인 확인이 안 돼서 판매자가 더 물어볼 수 있어요.",
      contact: "판매자가 따로 연락할 연락처 (선택, 메일 등)",
      contactNote: "적은 경우에만 보내고, 문의와 함께 보관해요. 답은 이 페이지의 '내 문의'에 와요.",
      consentTitle: "보관 안내 (필수)",
      consentText: "보관하는 것: 문의 종류, 제목, 내용, 판매자의 답, 주문 코드와 키 식별 번호(적은 경우), 동의한 시각.\n왜: 문의에 답하고 결제와 환불 기록을 남기려고요.\n환불 요청이면 서버가 가진 주문·키 정보(플랜, 받은 날, 입금자명 등)도 함께 붙어요.\n얼마나: 판매자 서버에 결제·환불 문의는 마지막으로 바뀐 날부터 3년, 그 밖의 문의는 1년 동안 두고 지워요.\nIP 주소는 문의에 남기지 않아요. 너무 많은 요청을 막으려고 IP 주소로 만든 해시값을 판매자 서버에 1시간까지 둬요.\n동의하지 않으면 문의를 보낼 수 없어요. 앱은 그대로 쓸 수 있어요.",
      consent: "보관 안내를 읽었고 동의해요",
      count: "{n} / {max}",
      send: "보내기", sending: "보내는 중…",
      sentTitle: "문의를 보냈어요. 문의 번호와 확인 코드를 복사해 두세요. 확인 코드는 다시 보여 드리지 않고, 잃으면 다시 열 수 없어요.",
      ticketId: "문의 번호", token: "확인 코드", saved: "이 브라우저에도 저장했어요.", notSaved: "이 브라우저에 저장하지 못했어요. 꼭 복사해 두세요.",
      openIt: "문의 보기", another: "새 문의 쓰기", look: "보기", savedTitle: "이 브라우저에 저장한 문의", forget: "목록에서 빼기",
      replyLabel: "답장", reply: "답장 보내기", closeIt: "문의 닫기",
      st: { open: "답변 기다리는 중", answered: "답변 왔어요", closed: "닫힘" },
      rs: { requested: "환불 요청됨", approved: "환불 승인됨", rejected: "환불 거절됨", done: "환불 완료" },
      rejectReason: "사유", amount: "금액", won: "원", daysSince: "키를 받은 지 {n}일",
      me: "나", admin: "판매자", created: "보낸 때", updated: "바뀐 때", newReply: "새 답장이 있어요",
      err: { cat: "문의 종류를 고르세요.", subject: "제목은 1~80자예요.", text: "내용은 1~2,000자예요.", contact: "연락처는 120자까지예요.",
             order: "주문 코드 형식(SB-XXXX-XXXX)이 아니에요. 환불 요청에는 주문 코드나 키가 필요해요.", keyId: "라이선스 키나 키 식별 번호 형식이 아니에요.",
             "bad key": "이 키를 확인하지 못했어요. 키 전체를 그대로 붙여 넣었는지 확인해 주세요. 키 식별 번호(16자리)만 적어도 보낼 수 있어요.",
             consent: "보관 안내에 동의해 주세요.", reason: "환불 사유를 고르세요.", lang: "보내지 못했어요.", body: "보내지 못했어요.",
             "too large": "내용이 너무 길어요. 줄여서 다시 보내 주세요.",
             busy: "요청이 많아요. 잠시 뒤 다시 보내 주세요.", busyDay: "오늘 받을 수 있는 문의가 다 찼어요. 한국 시간 오전 9시에 다시 열려요.",
             closed503: "지금은 문의를 받지 않아요.",
             notFound: "문의를 찾지 못했어요. 문의 번호와 확인 코드를 확인하세요.",
             closed409: "닫은 지 30일이 지나 다시 열 수 없어요. 새 문의를 남겨 주세요.", full: "메시지가 30개라 더 쓸 수 없어요. 새 문의를 남겨 주세요.",
             net: "서버에 연결하지 못했어요. 잠시 뒤 다시 시도하세요.", generic: "처리하지 못했어요. 잠시 뒤 다시 시도하세요.", info: "고객센터 정보를 불러오지 못했어요. 잠시 뒤 새로고침하세요." }
    },
    en: {
      title: "SystemBoost Support", other: "한국어", lead: "Read the FAQ and the refund policy, and send a request without the app.",
      faq: "FAQ", refund: "Refunds and withdrawal", seller: "Seller", ask: "Send a request", mine: "My requests", loading: "Loading…",
      privacy: "Privacy policy", privacyMore: "Details are in the privacy policy",
      refundLead: "Refunds (withdrawal) follow Article 9 of the license agreement. Here it is word for word. Paragraphs 2 and 5 are the wording in force from January 1, 2027; until then the previous wording applies.",
      refundDays: "Withdrawal period: 7 days from receiving the key (license agreement, Article 9, Paragraph 1)",
      refundDaysLonger: "Withdrawal period announced by the seller: {n} days from receiving the key (longer than the 7 days set by law)",
      minors: "If a minor pays without the consent of a legal representative, the minor or the legal representative may cancel the purchase (license agreement, Article 3, Paragraph 11).",
      refundHow: "Send a refund request under 'Send a request' with the category 'Refund'. The seller checks it and replies in that request.",
      refundCard: "Card payment (Lemon Squeezy): when the seller refunds it in Lemon Squeezy, the key is blocked and the request shows the refund as complete.",
      refundManual: "Bank transfer · direct payment: the seller pays the money back, then marks the refund complete and blocks the key.",
      sellerEmpty: "Not entered by the seller yet",
      sellerLabels: { name: "Business name", rep: "Representative", address: "Address", bizNo: "Business registration no.", mailOrderNo: "Mail-order business no.", email: "Email", phone: "Phone", hours: "Support hours" },
      closed: "Requests are closed for now. Requests you sent can still be read under 'My requests'.",
      closedRefunds: "General questions are closed for now. Refund (withdrawal) requests are still accepted. For anything else, use the contact details in the seller information on this page. Requests you sent can still be read under 'My requests'.",
      cat: "Category", subject: "Subject", text: "Message", reason: "Reason for the refund", refundNeeds: "A refund request needs an order code or a license key.",
      order: "Order code (optional)", orderNote: "The code you got when paying on '★ Plans' in the app. Sent only if you fill it in.",
      key: "License key or key ID (optional)",
      keyNote: "Paste the full key and the server checks by its signature that it is yours. The key is used only for that check and is not stored; the request keeps only its key ID (16 characters). With only a key ID nothing can be checked, so the seller may ask you more.",
      contact: "A contact the seller can use (optional, e.g. email)",
      contactNote: "Sent only if you fill it in, and kept with the request. Replies appear under 'My requests' on this page.",
      consentTitle: "How your request is kept (required)",
      consentText: "What is kept: the category, subject, message, the seller's replies, the order code and key ID (if you give them), and the time you agreed.\nWhy: to answer you and keep a record of payments and refunds.\nFor a refund request the server also attaches the order and key details it holds (plan, date issued, payer name and more).\nHow long: on the seller's server, payment and refund requests for 3 years and other requests for 1 year, counted from the day the request last changed.\nYour IP address is not kept with the request. To stop too many requests, a hash made from your IP address stays on the seller's server for up to 1 hour.\nIf you do not agree, you cannot send a request. You can still use the app.",
      consent: "I have read this and agree",
      count: "{n} / {max}",
      send: "Send", sending: "Sending…",
      sentTitle: "Your request was sent. Copy the request number and the access code. The access code is not shown again, and without it the request cannot be opened again.",
      ticketId: "Request number", token: "Access code", saved: "Also saved in this browser.", notSaved: "Could not save it in this browser. Please copy it.",
      openIt: "Open the request", another: "Write another", look: "Open", savedTitle: "Requests saved in this browser", forget: "Remove from list",
      replyLabel: "Reply", reply: "Send reply", closeIt: "Close request",
      st: { open: "Waiting for a reply", answered: "Answered", closed: "Closed" },
      rs: { requested: "Refund requested", approved: "Refund approved", rejected: "Refund declined", done: "Refunded" },
      rejectReason: "Reason", amount: "Amount", won: " KRW", daysSince: "{n} days since the key was issued",
      me: "Me", admin: "Seller", created: "Sent", updated: "Changed", newReply: "There is a new reply",
      err: { cat: "Choose a category.", subject: "The subject must be 1 to 80 characters.", text: "The message must be 1 to 2,000 characters.", contact: "The contact can be up to 120 characters.",
             order: "That is not an order code (SB-XXXX-XXXX). A refund request needs an order code or a key.", keyId: "That is not a license key or key ID.",
             "bad key": "This key could not be checked. Make sure you pasted the whole key. You can also send just the key ID (16 characters).",
             consent: "Please agree to how your request is kept.", reason: "Choose a reason for the refund.", lang: "Could not send it.", body: "Could not send it.",
             "too large": "That is too long. Please shorten it and send again.",
             busy: "Too many requests. Please try again a little later.", busyDay: "Today's request limit is reached. It opens again at 9:00 Korean time (00:00 UTC).",
             closed503: "Requests are closed for now.",
             notFound: "Request not found. Check the request number and the access code.",
             closed409: "It was closed more than 30 days ago and cannot be reopened. Please send a new request.", full: "This request has 30 messages and cannot take more. Please send a new request.",
             net: "Could not reach the server. Please try again later.", generic: "Something went wrong. Please try again later.", info: "Could not load the support center. Please reload the page later." }
    }
  }[L];
  var STORE = "sb-support-tickets", info = null, current = null;
  var LIM = { subject: 80, text: 2000, contact: 120, thReply: 2000 };   // the server's limits, in characters

  function $(id) { return document.getElementById(id); }
  function el(tag, cls, text) { var e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = String(text); return e; }
  function fill(s, n) { return s.replace("{n}", String(n)); }
  function when(iso) { try { return new Date(iso).toLocaleString(L === "en" ? "en-US" : "ko-KR", { dateStyle: "medium", timeStyle: "short" }); } catch (e) { return String(iso || ""); } }
  function say(node, text, bad) { node.textContent = text || ""; node.className = bad ? "err" : "m"; }
  function catName(id) { var c = (info && info.categories || []).filter(function (x) { return x.id === id; })[0]; return c ? c[L] : id; }
  // characters as the server counts them: after trimming, one per code point (an emoji is one, not two)
  function chars(s) { return Array.from(String(s || "").trim()).length; }
  function count(id) {
    var n = chars($(id).value), node = $(id + "N");
    node.textContent = TX.count.replace("{n}", String(n)).replace("{max}", String(LIM[id]));
    node.className = "cnt " + (n > LIM[id] ? "err" : "m");
    return n;
  }
  Object.keys(LIM).forEach(function (id) { $(id).addEventListener("input", function () { count(id); }); count(id); });
  function errText(r, field) {
    if (r.status === 0) return TX.err.net;
    if (r.status === 429) return r.scope === "day" ? TX.err.busyDay : TX.err.busy;
    if (r.status === 413) return TX.err["too large"];
    if (r.status === 503) return TX.err.closed503;
    if (r.status === 404) return TX.err.notFound;
    if (r.status === 409) return r.error === "full" ? TX.err.full : TX.err.closed409;
    if (r.status === 400 && TX.err[r.error]) return TX.err[r.error];
    return TX.err[field] || TX.err.generic;
  }
  function api(path, opt) {
    return fetch(path, opt).then(function (res) {
      return res.json().catch(function () { return {}; }).then(function (j) { j = j && typeof j === "object" ? j : {}; j.status = res.status; return j; });
    }, function () { return { status: 0 }; });
  }
  function post(path, body) { return api(path, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body || {}) }); }

  // saved requests: a convenience only, the page works without it
  function loadSaved() { try { var v = JSON.parse(localStorage.getItem(STORE) || "[]"); return Array.isArray(v) ? v.filter(function (x) { return x && typeof x.id === "string" && typeof x.token === "string"; }) : []; } catch (e) { return []; } }
  function storeSaved(list) { try { localStorage.setItem(STORE, JSON.stringify(list.slice(0, 20))); return true; } catch (e) { return false; } }
  function remember(id, token, subject) {
    var list = loadSaved().filter(function (x) { return x.id !== id; });
    list.unshift({ id: id, token: token, subject: String(subject || "").slice(0, 80), at: new Date().toISOString() });
    return storeSaved(list);
  }
  function renderSaved() {
    var box = $("saved"), list = loadSaved();
    box.textContent = "";
    if (!list.length) return;
    box.appendChild(el("p", "m", TX.savedTitle));
    list.forEach(function (x) {
      var row = el("div", "row");
      var open = el("button", "ghost", x.id + (x.subject ? " · " + x.subject : ""));
      open.type = "button";
      open.addEventListener("click", function () { $("lkId").value = x.id; $("lkToken").value = x.token; openTicket(x.id, x.token); });
      var forget = el("button", "ghost", TX.forget);
      forget.type = "button";
      forget.addEventListener("click", function () { storeSaved(loadSaved().filter(function (y) { return y.id !== x.id; })); renderSaved(); });
      row.appendChild(open); row.appendChild(forget);
      box.appendChild(row);
    });
  }

  // static labels
  Array.prototype.forEach.call(document.querySelectorAll("[data-t]"), function (n) { var v = TX[n.getAttribute("data-t")]; if (typeof v === "string") n.textContent = v; });
  document.title = TX.title;
  var link = $("langLink");
  link.textContent = TX.other;
  link.href = L === "en" ? "?lang=ko" : "?lang=en";
  $("privacyNav").href = $("privacyLink").href = L === "en" ? "/privacy?lang=en" : "/privacy";   // Korean is the policy, English a summary

  function renderInfo() {
    if (info.notice) { $("notice").textContent = info.notice; $("notice").hidden = false; }
    var faq = $("faq");
    faq.textContent = "";
    (info.faq || []).forEach(function (f) {
      var d = el("details"), s = el("summary", "", f.q);
      d.appendChild(s); d.appendChild(el("p", "", f.a));
      faq.appendChild(d);
    });
    var pol = $("policy");
    pol.textContent = "";
    if (info.policy) {
      pol.appendChild(el("p", "", info.policy.title));
      (info.policy.items || []).forEach(function (t) { pol.appendChild(el("p", "", t)); });
    }
    // never shown below the 7 days of 제9조 제1항: the dashboard may announce a longer period, not a shorter one
    var days = Number(info.refundDays) || 0;
    $("refundDays").textContent = days > 7 ? fill(TX.refundDaysLonger, days) : TX.refundDays;
    // only the fields the seller filled in, as in the app: an empty one gets no line
    var dl = $("seller"), shown = 0;
    dl.textContent = "";
    Object.keys(TX.sellerLabels).forEach(function (k) {
      var v = info.seller && typeof info.seller[k] === "string" ? info.seller[k].trim() : "";
      if (!v) return;
      dl.appendChild(el("dt", "", TX.sellerLabels[k]));
      dl.appendChild(el("dd", "", v));
      shown++;
    });
    if (!shown) dl.appendChild(el("dd", "m", TX.sellerEmpty));
    var cat = $("cat");
    cat.textContent = "";
    // closed for questions: withdrawal stays open (info.refunds), so the form offers the refund request only
    var refundOnly = !info.open && info.refunds === true;
    (info.categories || []).forEach(function (c) { if (refundOnly && c.id !== "refund") return; var o = el("option", "", c[L]); o.value = c.id; cat.appendChild(o); });
    var reason = $("reason");
    reason.textContent = "";
    (info.refundReasons || []).forEach(function (c) { var o = el("option", "", c[L]); o.value = c.id; reason.appendChild(o); });
    if (!info.open) {
      $("closedNote").hidden = false;
      if (refundOnly) { $("closedNote").setAttribute("data-t", "closedRefunds"); $("closedNote").textContent = TX.closedRefunds; }
      else $("form").hidden = true;
    }
    syncRefund();
  }
  function syncRefund() { $("refundBox").hidden = $("cat").value !== "refund"; }
  $("cat").addEventListener("change", syncRefund);

  $("form").addEventListener("submit", function (ev) {
    ev.preventDefault();
    var msg = $("formMsg"), btn = $("send"), cat = $("cat").value;
    var order = $("order").value.trim().toUpperCase(), key = $("key").value.trim();
    if (!$("subject").value.trim() || count("subject") > LIM.subject) { say(msg, TX.err.subject, true); return; }
    if (!$("text").value.trim() || count("text") > LIM.text) { say(msg, TX.err.text, true); return; }
    if (count("contact") > LIM.contact) { say(msg, TX.err.contact, true); return; }
    if (!$("consent").checked) { say(msg, TX.err.consent, true); return; }
    if (cat === "refund" && !order && !key) { say(msg, TX.refundNeeds, true); return; }
    var body = { cat: cat, subject: $("subject").value, text: $("text").value, contact: $("contact").value, lang: L, consent: true };
    if (order) body.order = order;
    // a key id alone (16 hex) or the whole key; the whole key goes to the server only to be checked, never kept
    if (/^[0-9a-fA-F]{16}$/.test(key)) body.keyId = key.toLowerCase();
    else if (/^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/.test(key)) body.key = key;
    else if (key) { say(msg, TX.err.keyId, true); return; }
    if (cat === "refund") body.refund = { reason: $("reason").value };
    btn.disabled = true; say(msg, TX.sending);
    post("/support/ticket", body).then(function (r) {
      btn.disabled = false;
      if (r.status !== 201 || !r.id || !r.token) { say(msg, errText(r), true); return; }
      say(msg, "");
      $("sentId").textContent = r.id;
      $("sentToken").textContent = r.token;
      $("sentSaved").textContent = remember(r.id, r.token, body.subject) ? TX.saved : TX.notSaved;
      $("sentOpen").onclick = function () { $("lkId").value = r.id; $("lkToken").value = r.token; openTicket(r.id, r.token); $("s-mine").scrollIntoView(); };
      $("form").hidden = true; $("sent").hidden = false;
      $("form").reset(); syncRefund();
      Object.keys(LIM).forEach(count);
      renderSaved();
    });
  });
  $("sentNew").addEventListener("click", function () { $("sent").hidden = true; $("form").hidden = !(info && (info.open || info.refunds === true)); });

  function renderTicket(t) {   // current (id + token) is set by openTicket; this only draws
    $("thread").hidden = false;
    $("thSubject").textContent = t.subject;
    $("thMeta").textContent = [t.id, catName(t.cat), TX.st[t.status] || t.status, TX.created + " " + when(t.created), TX.updated + " " + when(t.updated)].join(" · ") +
                              (t.unreadUser ? " · " + TX.newReply : "");
    var rf = $("thRefund");
    if (t.refund) {
      var parts = [TX.rs[t.refund.status] || t.refund.status];
      if (t.refund.facts && t.refund.facts.daysSinceKey != null) parts.push(fill(TX.daysSince, t.refund.facts.daysSinceKey));
      if (t.refund.amount != null) parts.push(TX.amount + " " + Number(t.refund.amount).toLocaleString(L === "en" ? "en-US" : "ko-KR") + TX.won);
      if (t.refund.rejectReason) parts.push(TX.rejectReason + ": " + t.refund.rejectReason);
      rf.textContent = parts.join(" · "); rf.hidden = false;
    } else rf.hidden = true;
    var box = $("thMsgs");
    box.textContent = "";
    (t.messages || []).forEach(function (m) {
      var d = el("div", "msg " + (m.from === "admin" ? "admin" : "user"));
      d.appendChild(el("div", "who", (m.from === "admin" ? TX.admin : TX.me) + " · " + when(m.at)));
      d.appendChild(el("div", "body", m.text));
      box.appendChild(d);
    });
    $("thClose").hidden = t.status === "closed";
  }
  function openTicket(id, token) {
    var msg = $("lkMsg");
    say(msg, TX.loading);
    api("/support/ticket?id=" + encodeURIComponent(id), { headers: { "X-Ticket-Token": token } }).then(function (r) {
      if (r.status !== 200 || !r.ticket) { say(msg, errText(r), true); $("thread").hidden = true; current = null; return; }
      say(msg, "");
      current = { id: r.ticket.id, token: token };
      remember(r.ticket.id, token, r.ticket.subject);
      renderSaved();
      renderTicket(r.ticket);
    });
  }
  $("lookup").addEventListener("submit", function (ev) {
    ev.preventDefault();
    var id = $("lkId").value.trim().toUpperCase(), token = $("lkToken").value.trim().toLowerCase();
    if (!/^T-[A-Z0-9]{4}-[A-Z0-9]{4}$/.test(id) || !/^[0-9a-f]{64}$/.test(token)) { say($("lkMsg"), TX.err.notFound, true); return; }
    openTicket(id, token);
  });
  function act(path, body, btn) {   // the id and the token go in the body
    if (!current || !current.id) return;
    var msg = $("thMsg");
    btn.disabled = true;
    post(path, Object.assign({ id: current.id, token: current.token }, body)).then(function (r) {
      btn.disabled = false;
      if (r.status !== 200 || !r.ticket) { say(msg, errText(r, "text"), true); return; }
      say(msg, "");
      $("thReply").value = ""; count("thReply");
      renderTicket(r.ticket);
    });
  }
  $("thSend").addEventListener("click", function () {
    if (!$("thReply").value.trim() || count("thReply") > LIM.thReply) { say($("thMsg"), TX.err.text, true); return; }
    act("/support/ticket/reply", { text: $("thReply").value }, $("thSend"));
  });
  $("thClose").addEventListener("click", function () { act("/support/ticket/close", {}, $("thClose")); });

  renderSaved();
  api("/support/info?lang=" + L).then(function (r) {
    if (r.status !== 200) { $("offline").textContent = TX.err.info; $("offline").hidden = false; $("faq").textContent = ""; $("form").hidden = true; return; }
    info = r;
    renderInfo();
  });
})();
</script>
</body>
</html>
`;
