// Customer support and refund requests (KV binding LICENSES, like the rest of the worker).
//
//   GET  /support/info                    seller info, notice, FAQ, categories, refund reasons (?lang=en)
//   POST /support/ticket                  new ticket -> {id, token} (the token is shown once, only its hash is kept).
//                                         "key": the full licence key, checked by its signature like /support/subscription
//                                         and never stored: the ticket keeps keyId + keyVerified true. "keyId" alone is
//                                         taken too (keyVerified false). A bad key is 400 "bad key", key + another keyId 400.
//   GET  /support/ticket?id=              the ticket, public view; the token in the header X-Ticket-Token
//   POST /support/ticket/reply            {id, token, text}: the buyer writes again (a closed ticket reopens within 30 days)
//   POST /support/ticket/close            {id, token}
//   POST /support/tickets/status          {tickets:[{id, token}]} (1..20) -> {tickets:{<id>: {status, updated, n, unread} | null}}
//                                         for the app's polling: read only, never clears unread
//   POST /support/subscription            {key} -> {portal}: Lemon Squeezy customer portal of a signed card key
//   A ticket token never travels in a URL: "token" in the query of any /support route is 400 "token in url".
//   A wrong or missing token is the same 404 as an unknown ticket.
//   Every POST needs Content-Type: application/json (415 otherwise), so other sites cannot post through browsers.
//   A body over LIMITS.body bytes is 413 "too large", read no further; over-long strings are refused before cleaning.
//   429 "busy" says why in "scope": "ip" (this address, this hour; or a counter write to retry) or "day" (the day's
//   budget, until 00:00 UTC), with Retry-After in seconds.
//   GET  /help                            the same for people without the app (help_page.js)
//   GET  /privacy                         the privacy policy (Korean; ?lang=en an English summary): privacy_page.js, made by
//                                         build_privacy.mjs from privacy/*.md; its four blanks come from config:support
//   /admin/api/support*                   dashboard: list, read, reply, status, refund decisions, delete, config,
//                                         feed (GET /admin/api/support/feed?after=<seq> -> {events, last, more}: the
//                                         "새 소식" rows after that seq, at most 100; no after = the newest 20)
//
// The feed: every buyer action that changes a ticket (new ticket, refund request, reply, reopen, close) and a card
// refund the webhook finished add one row in the Stats Durable Object (stats.js: SQLite, not KV, so no KV write and
// no KV list), with the time, the kind, the ticket number and its category, never the buyer's text. A failing
// Durable Object never fails the buyer's request. Deleting a ticket deletes its rows.
//
// Money never moves here. The seller refunds a card payment in Lemon Squeezy (its refund webhook already blocks
// the key, and webhookRefund() below then closes the matching refund ticket), and pays a manual payment back by
// hand, then presses "환불 완료", which blocks the key(s) and marks the order refunded.
// Texts quote the licence agreement (Article 9) and decide nothing about eligibility.
//
// KV (free plan: 1,000 writes a day for the whole worker, so few writes):
//   tk:<id>                 ticket JSON, metadata = the dashboard's list row (< 1024 bytes)
//   tkkey:<keyId>           id of the newest refund ticket that names that key (the refund webhook reads only this);
//                           only for a key the buyer showed they hold (a signed key, or the key of a found order)
//   config:support          seller info, the privacy policy's blanks, notice, refund days, FAQ, open (the dashboard edits it)
// The day's write budget is counted in the Stats Durable Object (stats.js counters, SQLite: no KV write), in two lanes:
//   sup:<date>:w / c / r    anonymous tickets: KV writes, new tickets, buyer replies
//   sup:<date>:vw / vc / vr tickets of a shown purchase (owner: a paid key the server issued and still honours, or
//                           the code of an approved or refunded order), and sup:<date>:by:<owner> = actions per
//                           purchase, so a flood of anonymous tickets cannot shut out paying buyers and one purchase
//                           cannot fill its lane.
// Every public support action that writes takes its writes from its lane first (one atomic step, all counters or none),
// so support never makes more than LIMITS.writesPerDay + LIMITS.vWritesPerDay KV writes a day (plus clearing unread
// marks, which only a seller reply sets). While the object cannot be reached the same lanes are counted in one KV
// counter, count:support:<date> (its own write included; see spend()). Orders, keys, webhooks and the dashboard never
// look at it.
// Per-IP limits are counted in the same object (rl:<what>:<tag>:<hour>, an hour). When it cannot be reached they fall
// back to the Cache API (per data center) and the isolate's memory, both approximate (Cloudflare promises working Cache
// API calls only on custom domains, so on *.workers.dev the memory copy is what holds). The portal link cache and the
// "unread mark seen" notes live there too.
// An IP address is used only as the first 16 hex of SHA-256(salt + "|" + address + "sb-support"), in the name of an
// hour's counter (an hour in the object or the Cache API; memory: swept within a minute of its hour). IPv6 counts per /64,
// and anonymous writes also per /48 (LIMITS.anonPer48). The salt is SHA-256 of a fixed label and the server's
// LICENSE_PRIVATE_KEY secret, so without that secret nobody can hash every IPv4 address back to the counter names.
// Without the secret (local tests only) the salt is empty.
import { HELP_HTML } from "./help_page.js";
import { PRIVACY_HTML } from "./privacy_page.js";

const DAY = 86400;
export const LIMITS = {
  subject: 80, text: 2000, contact: 120, diag: 80, messages: 30, reason: 300,
  createPerIp: 5, repliesPerIp: 20, statusPerIp: 120, subscriptionPerIp: 20,   // per IP an hour (Stats object; Cache API / memory)
  anonPer48: 40,                                                                // anonymous writes per IPv6 /48 an hour
  writesPerDay: 150, createsPerDay: 80, repliesPerDay: 80,                      // anonymous lane, KV writes a day (UTC)
  vWritesPerDay: 100, vCreatesPerDay: 40, vRepliesPerDay: 60, perOwnerPerDay: 20,   // lane of shown purchases; one purchase's actions
  body: 64 * 1024, key: 4000,                                                   // request body bytes, licence key characters
  statusBatch: 20, statusMisses: 3, portalCache: 30 * 60, portalPerKey: 10,     // tickets per status call, KV misses read per call,
                                                                                // portal link seconds, portal links per key a day
  reopenDays: 30,                                         // a closed ticket takes a reply this long
  keepLong: 1095 * DAY, keepShort: 365 * DAY,             // "pay" / "refund" 3 years, the rest 1 year; renewed by every write
  faq: 30, faqQ: 120, faqA: 1500, seller: 120, notice: 500,
  amount: 100000000,                                      // KRW, a sanity bound for the refunded amount
};
const ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";   // like order codes: no 0/O/1/I
const ID_RE = /^T-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$/;
const ORDER_RE = /^SB-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$/;
const NOTE_ORDER_RE = /^(SB-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4})(?![A-Z0-9-])/;
const KEY_RE = /^[0-9a-f]{16}$/;
const TOKEN_RE = /^[0-9a-f]{64}$/;
const SELLER_FIELDS = ["name", "rep", "address", "bizNo", "mailOrderNo", "email", "phone", "hours"];

export const CATEGORIES = [
  { id: "pay", ko: "결제", en: "Payment" },
  { id: "refund", ko: "환불", en: "Refund" },
  { id: "bug", ko: "오류 신고", en: "Bug report" },
  { id: "howto", ko: "사용 방법", en: "How to use" },
  { id: "idea", ko: "제안", en: "Suggestion" },
  { id: "other", ko: "기타", en: "Other" },
];
export const REFUND_REASONS = [
  { id: "change_mind", ko: "단순 변심", en: "Changed my mind" },
  { id: "not_working", ko: "작동하지 않음 · 표시나 광고와 다름", en: "Does not work or differs from what was shown" },
  { id: "double", ko: "중복 결제", en: "Paid twice" },
  { id: "other", ko: "기타", en: "Other" },
];

// Article 9 of the licence agreement, word for word (app/SystemBoost.App/Assets/eula.txt, eula.en.txt; an item that
// runs over two lines there is one string here, joined with a space). When the agreement changes, change this too:
// test_support.mjs compares the two.
export const ARTICLE_9 = {
  ko: { title: "제9조 (구매와 청약철회)", items: [
    "1. 사용자는 키를 받은 날부터 7일 이내에 청약을 철회할 수 있습니다.",
    "2. 키를 등록해 유료 기능을 쓰기 시작하면 디지털콘텐츠의 제공이 시작된 것으로 보아 청약철회가 제한될 수 있습니다. 판매자는 구매 전에 쓸 수 있는 무료 플랜을 제공합니다. 유료 플랜에만 있는 기능은 구매 전에 써 볼 수 없습니다.",
    "3. 프로그램이 표시나 광고의 내용과 다르거나 계약과 다르게 제공된 경우, 사용자는 받은 날부터 3개월 이내, 그 사실을 안 날 또는 알 수 있었던 날부터 30일 이내에 청약을 철회할 수 있습니다.",
    "4. 환불이 끝나면 판매자는 해당 키를 막습니다.",
    "5. 청약철회와 환불 문의는 앱이나 도움말 페이지의 고객센터, 또는 판매 페이지에 표시한 연락처로 보냅니다.",
  ] },
  en: { title: "Article 9 (Purchase and withdrawal)", items: [
    "1. The User may withdraw from the purchase within 7 days of receiving the key.",
    "2. Once the User registers a key and starts using paid features, the digital content is treated as provided and withdrawal may be limited. The Seller offers a free plan that can be used before purchase. Features found only in paid plans cannot be tried before purchase.",
    "3. If the Program differs from what was displayed or advertised, or is provided differently from the contract, the User may withdraw within 3 months of receiving it, or within 30 days of the day the User learned or could have learned of this.",
    "4. When a refund is complete, the Seller blocks the key.",
    "5. Questions about withdrawal and refunds go to the support center in the app or on the help page, or to the contact shown on the sales page.",
  ] },
};

// Shown when the dashboard has no FAQ of its own. Every answer comes from README.md, the licence agreement
// (eula.txt / eula.en.txt) or the app's own strings (menu names as the app writes them, lang.en.json for English).
export const DEFAULT_FAQ = {
  ko: [
    { q: "라이선스 키는 어디에 넣나요?",
      a: "앱 왼쪽의 '★ 플랜' 화면에서 '라이선스 키' 칸에 키를 붙여 넣고 '등록'을 누르세요. 결제한 뒤에는 앱이 키를 기다리다가(앱을 껐다 켜도 14일까지) 키가 나오면 팝업으로 보여 줘요." },
    { q: "계좌이체 · 직접 결제는 어떻게 하나요?",
      a: "'★ 플랜'에서 '계좌이체 · 직접 결제'를 고르고 '결제하기'를 누르면 주문 코드(SB-XXXX-XXXX)와 판매자 결제 페이지가 열려요. 결제할 때 요청사항(메모)에 주문 코드를 적고, 결제를 마친 뒤 앱에 입금자명을 적어 '결제했어요'를 누르세요. 판매자가 결제를 확인하고 키를 보내요. 직접 결제로 산 키는 자동으로 결제되지 않아요(사용권 계약 제3조 제3항). 기간이 끝나기 전에 앱에서 다시 결제하면 새 키의 기간은 이 PC에 등록된 같거나 낮은 플랜의 키가 끝나는 날부터 이어서 세요(앱을 최신 버전으로 업데이트한 경우). 카드 구독은 결제한 날부터 시작해요." },
    { q: "카드 결제와 구독 해지는 어떻게 되나요?",
      a: "카드 정보는 Lemon Squeezy 결제창에서만 입력하고, SystemBoost 앱과 서버에는 오지도 저장되지도 않아요. 카드 구독으로 산 키는 해지할 때까지 같은 기간마다 자동으로 결제되고, 결제될 때마다 그 기간만큼 연장돼요. 해지는 결제 메일의 구독 관리 링크에서 언제든 할 수 있고, 해지하면 이미 결제한 기간이 끝날 때 무료 플랜으로 돌아가요(제3조 제3항, 2026년 12월 8일 시행)." },
    { q: "환불(청약철회)은 어떻게 요청하나요?",
      a: "사용권 계약 제9조를 따라요. 앱은 고객센터의 '환불 요청' 탭, 웹(/help)은 문의 종류 '환불'로 주문 코드나 키를 보내 주세요. 판매자가 확인하고 답해요. 카드 결제의 환불은 Lemon Squeezy에서 처리되고, 계좌이체 · 직접 결제는 판매자가 직접 돌려 드려요. 환불이 끝나면 판매자는 해당 키를 막아요(제9조 제4항)." },
    { q: "키가 무료 플랜으로 돌아갔어요.",
      a: "월간 키와 연간 키는 표시된 기간이 끝나면 무료 플랜으로 돌아가요(제3조 제3항). 판매자가 키를 막은 경우(환불, 키 공유 등)에는 앱이 인터넷에 연결된 상태로 켜질 때 무료 플랜으로 돌아가요(제3조 제6항, 제9조 제4항). 이유를 모르겠다면 문의 종류 '결제'로 문의해 주세요." },
    { q: "게임 부스트로 바꾼 설정을 되돌리려면?",
      a: "프로그램은 바꾸기 전 값을 백업하고 되돌리는 기능을 제공해요(제6조 제1항). 왼쪽 '↺ 백업 · 되돌리기'에서 '전체 되돌리기'를 누르세요. 게임 부스트를 썼다면 프로그램을 지우기 전에도 전체 되돌리기를 실행하세요(제11조 제1항)." },
    { q: "프레임 생성을 켜면 게임이 더 빨라지나요?",
      a: "프레임 생성은 게임이 실제로 그리는 프레임을 늘리지 않아요. 프레임 사이의 화면을 만들어 넣어요. 켜면 입력 지연이 늘어나고, 경쟁 게임에는 권장하지 않아요(제7조 제1항·제2항). 실제 결과는 PC, 게임, 설정에 따라 달라요(제7조 제4항)." },
    { q: "안티치트를 쓰는 게임은요?",
      a: "프로그램은 게임 화면을 캡처해 별도의 오버레이에 표시해요. 게임 프로세스에 코드를 주입하지 않고 게임 메모리와 게임 파일을 바꾸지 않아요(제5조 제1항). 판매자는 안티치트를 쓰는 게임에서 프로그램을 시험하지 않았고, 모든 게임의 이용약관이나 안티치트 정책에 맞는다고 보장하지 않아요. 게임의 정책은 쓰기 전에 직접 확인해 주세요(제5조 제2항·제3항)." },
    { q: "사용 통계는 무엇을 보내고, 어떻게 끄나요?",
      a: "앱이 켜져 있는 동안 무작위로 만든 설치 번호, 앱 버전, 운영체제, 플랜, 프레임 생성 배수, 원클릭 게임 부스트 사용 여부를 보내요. 키, 메일, 게임 이름, 하드웨어 정보는 보내지 않아요(제8조 제2항). 앱의 '설정'에서 '사용 통계 보내기'를 끄면 돼요." },
    { q: "회원 비밀번호를 잊었어요.",
      a: "회원 가입에는 메일을 받지 않아서, 가입할 때 받은 복구 코드로 비밀번호를 바꿔요. 앱의 '◈ 계정' 화면에서 '비밀번호 찾기'를 누르세요. 복구 코드도 잃었다면 문의 종류 '기타'로 문의해 주세요." },
    { q: "문의 내용은 얼마나 보관하나요?",
      a: "문의 종류, 제목, 내용과 답변, 적은 연락처, 주문 코드와 키 식별 번호, '앱 정보 함께 보내기'를 고른 경우 그 앱 정보를 답변과 기록을 위해 보관해요. 회원으로 로그인한 앱에서 보내면 아이디도 함께 남아요. 환불 요청이면 서버가 가진 주문·키 정보(플랜, 받은 날, 입금자명 등)도 붙어요. 키 전체를 보내도 키는 주인 확인에만 쓰고 저장하지 않아요. 결제·환불 문의는 마지막으로 바뀐 날부터 3년, 그 밖의 문의는 1년이 지나면 지워져요. IP 주소는 문의에 남기지 않아요. 너무 많은 요청을 막으려고 IP 주소로 만든 해시값을 서버 메모리와 Cloudflare 캐시에 1시간까지 둬요." },
  ],
  en: [
    { q: "Where do I enter my license key?",
      a: "Paste the key into the 'License key' box on the '★ Plans' page on the left of the app and press 'Register'. After you pay, the app waits for the key (up to 14 days, even if you restart it) and shows it in a popup when it is issued." },
    { q: "How do bank transfer and direct payment work?",
      a: "On '★ Plans', choose 'Bank transfer · direct payment', then press 'Pay': an order code (SB-XXXX-XXXX) and the seller's payment page open. Write the order code in the payment memo; after paying, type the payer's name in the app and press 'I have paid'. The seller checks the payment and sends the key. A key bought by direct payment is not charged automatically (license agreement, Article 3, Paragraph 3). If you pay again in the app before the period ends, the new key's period starts when the same- or lower-plan key registered on this PC ends (with the app updated to the latest version). A card subscription starts on the day you pay." },
    { q: "How do card payments and cancelling a subscription work?",
      a: "Card details are entered only in the Lemon Squeezy checkout; they never reach or stay in the SystemBoost app or server. A key bought as a card subscription is charged automatically every period until you cancel, and is extended by one period with each payment. You can cancel at any time through the subscription management link in the payment email; after cancelling, the app returns to the Free plan when the period already paid ends (Article 3, Paragraph 3, in force from December 8, 2026)." },
    { q: "How do I ask for a refund (withdrawal)?",
      a: "Article 9 of the license agreement applies. In the app, use the 'Refund request' tab in Support; on the web (/help), send a request with the category 'Refund'. Give your order code or key; the seller checks it and replies. Card payments are refunded through Lemon Squeezy; bank transfers and direct payments are paid back by the seller. When a refund is complete, the seller blocks the key (Article 9, Paragraph 4)." },
    { q: "My key went back to the Free plan.",
      a: "With a monthly or yearly key, the app returns to the Free plan when the stated period ends (Article 3, Paragraph 3). If the seller blocked the key (a refund, a shared key and so on), it returns to the Free plan when the app starts online (Article 3, Paragraph 6; Article 9, Paragraph 4). If you do not know why, send a request with the category 'Payment'." },
    { q: "How do I undo the settings game boost changed?",
      a: "The program backs up the previous values and provides a way to restore them (Article 6, Paragraph 1). Open '↺ Backup · Restore' on the left and press 'Restore everything'. If you used game boost, also run it before deleting the program (Article 11, Paragraph 1)." },
    { q: "Does frame generation make the game faster?",
      a: "Frame generation does not increase the frames the game actually draws. It inserts images between frames. Turning it on increases input delay, and it is not recommended for competitive games (Article 7, Paragraphs 1 and 2). Actual results differ by PC, game and settings (Article 7, Paragraph 4)." },
    { q: "What about games with anti-cheat?",
      a: "The program captures the game's picture and shows it in a separate overlay. It does not inject code into game processes and does not change game memory or game files (Article 5, Paragraph 1). The seller has not tested the program in games that use anti-cheat and does not guarantee that it complies with the terms of use or anti-cheat policy of every game. Please check a game's policy before using it (Article 5, Paragraphs 2 and 3)." },
    { q: "What do usage statistics send, and how do I turn them off?",
      a: "While it is on, the app sends a randomly created install number, the app version, operating system, plan, frame generation multiplier, and whether one-click game boost is in use. Keys, email addresses, game names and hardware information are not sent (Article 8, Paragraph 2). Turn off 'Send usage statistics' in the app's 'Settings'." },
    { q: "I forgot my member password.",
      a: "Sign-up takes no email address, so the password is changed with the recovery code you got at sign-up. Open '◈ Account' in the app and press 'Forgot password'. If you lost the recovery code too, send a request with the category 'Other'." },
    { q: "How long are support requests kept?",
      a: "The category, subject, message and replies, the contact you wrote, the order code and key ID, and, if you chose to send it, the app information are kept to answer you and keep a record. Sent from an app signed in as a member, the member ID is kept too. For a refund request the server also attaches the order and key details it holds (plan, date issued, payer name and more). If you send the full key, it is used only to check that it is yours and is not stored. Payment and refund requests are deleted 3 years after they last changed, other requests after 1 year. Your IP address is not kept with the request. To stop too many requests, a hash made from your IP address stays in server memory and the Cloudflare cache for up to 1 hour." },
  ],
};

// Admin messages posted into the thread when the seller gives none.
const SAY = {
  approved: { ko: "환불 요청을 승인했어요. 환불을 마치면 여기로 알려 드릴게요.",
              en: "Your refund request is approved. We'll tell you here when it's done." },
  rejected: { ko: r => "환불 요청을 받아들이지 못했어요. 사유: " + r + "\n궁금한 점은 이 문의에 답장해 주세요.",
              en: r => "We could not accept the refund request. Reason: " + r + "\nReply here with any questions." },
  done:     { ko: "환불을 마쳤어요.", en: "The refund is complete." },
  doneKeys: { ko: "환불을 마쳤어요. 환불한 키는 막았어요(사용권 계약 제9조 제4항).",
              en: "The refund is complete. The refunded key is blocked (license agreement, Article 9, Paragraph 4)." },
  card:     { ko: "Lemon Squeezy에서 카드 환불이 처리됐어요. 환불한 키는 막았어요(사용권 계약 제9조 제4항).",
              en: "Lemon Squeezy has processed the card refund. The refunded key is blocked (license agreement, Article 9, Paragraph 4)." },
};

// ---------------------------------------------------------------- helpers
const enc = new TextEncoder();
const hex = bytes => [...new Uint8Array(bytes)].map(x => x.toString(16).padStart(2, "0")).join("");
const sha = async text => hex(await crypto.subtle.digest("SHA-256", enc.encode(text)));
const now = () => new Date().toISOString().replace(/\.\d+Z$/, "Z");
const chars = s => [...s].length;
const cut = (s, n) => [...s].slice(0, n).join("");
const keepFor = cat => (cat === "pay" || cat === "refund" ? LIMITS.keepLong : LIMITS.keepShort);
const lang2 = v => (v === "en" ? "en" : "ko");
function json(o, status = 200) {   // public endpoints: CORS like the rest
  return new Response(JSON.stringify(o), { status, headers: { "content-type": "application/json", "cache-control": "no-store", "access-control-allow-origin": "*" } });
}
const bad = field => json({ error: field }, 400);
const notFound = () => json({ error: "not found" }, 404);
const tooLarge = () => json({ error: "too large" }, 413);
// 429 with why: "ip" = this address's hourly count (its bucket ends on the hour), "day" = the day's budget (00:00 UTC).
const untilHour = () => 3600 - (Math.floor(Date.now() / 1000) % 3600);
const untilDay = () => DAY - (Math.floor(Date.now() / 1000) % DAY);
function busy(scope, seconds) {
  const r = json({ error: "busy", scope }, 429);
  r.headers.set("Retry-After", String(seconds ?? (scope === "day" ? untilDay() : untilHour())));
  return r;
}
// What spend() said no with: the day's lane is used up, or the counter could not be written (try again in a moment).
// One row in the dashboard's feed. Best effort: the buyer's request has already been saved, and must not fail here.
function feedNote(env, h, ctx, kind, t) {
  let p;
  try { p = Promise.resolve(h?.events?.(env, "/event", { kind, ticket: t.id, cat: t.cat })).catch(() => {}); } catch { return; }
  if (ctx?.waitUntil) { try { ctx.waitUntil(p); return; } catch { /* awaited below */ } }
  return p;
}
const refused = why => why === "day" ? busy("day") : why === "ip" ? busy("ip") : busy("ip", 2);

// The body as JSON, read no further than LIMITS.body bytes, so a huge body costs nothing past that.
// BIG = too large, null = not JSON.
const BIG = Symbol("too large");
async function readBody(req) {
  if (Number(req.headers.get("Content-Length")) > LIMITS.body) return BIG;
  if (!req.body) return null;
  const reader = req.body.getReader(), parts = [];
  let size = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    size += value.byteLength;
    if (size > LIMITS.body) { reader.cancel().catch(() => {}); return BIG; }
    parts.push(value);
  }
  const all = new Uint8Array(size);
  let at = 0;
  for (const p of parts) { all.set(p, at); at += p.byteLength; }
  try { return JSON.parse(new TextDecoder().decode(all)); } catch { return null; }
}
// A raw string far longer than its limit can allow is refused before clean() and chars() walk it. Cleaning only
// takes characters out, and a character is at most 2 UTF-16 units, so 4x the limit leaves room for any real text.
const tooLong = (v, max) => typeof v === "string" && v.length > max * 4 + 16;

// Text from people is data: strings only, control and bidi-override characters out (messages keep newlines and tabs).
function clean(v, multiline = false) {
  if (typeof v !== "string") return "";
  let s = v.replace(/\r\n?/g, "\n").replace(/[‪-‮⁦-⁩]/g, "");
  s = multiline ? s.replace(/[\u0000-\u0008\u000B-\u001F\u007F]/g, "") : s.replace(/[\u0000-\u001F\u007F]/g, " ");
  return s.trim();
}
function sameHex(a, b) {   // constant time for strings of the same length
  if (typeof a !== "string" || typeof b !== "string" || a.length !== b.length) return false;
  let d = 0;
  for (let i = 0; i < a.length; i++) d |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return d === 0;
}
// IPv4 as it is (an IPv4-mapped IPv6 address too), IPv6 by its /64: one home or phone network gets one bucket.
export function ipBucket(ip) {
  ip = String(ip || "unknown").trim().toLowerCase().replace(/%.*$/, "");
  const v4 = /^(?:::ffff:)?(\d{1,3}(?:\.\d{1,3}){3})$/.exec(ip);
  if (v4) return v4[1];
  if (!ip.includes(":")) return ip;
  let groups;
  if (ip.includes("::")) {
    const [head, tail] = ip.split("::", 2), h = head ? head.split(":") : [], t = tail ? tail.split(":") : [];
    groups = [...h, ...Array(Math.max(0, 8 - h.length - t.length)).fill("0"), ...t];
  } else groups = ip.split(":");
  return groups.slice(0, 4).map(g => g.replace(/^0+(?=.)/, "") || "0").join(":") + "::/64";
}
// The /48 around an IPv6 /64 bucket (a site's whole allocation); "" for IPv4.
export const bucket48 = b => /::\/64$/.test(b) ? b.split(":").slice(0, 3).join(":") + "::/48" : "";
// Mixed with a value made from the signing key, like the member accounts' IP values, so a list of every IPv4 address
// hashed by anyone else does not map a tag back to an address. Without the key (tests) the plain form is used.
let ipSalt = null, ipSaltOf = null;
async function ipSaltFor(env) {
  const k = env?.LICENSE_PRIVATE_KEY || "";
  if (ipSaltOf !== k) { ipSalt = k ? await sha("systemboost-support-ip|" + k) : ""; ipSaltOf = k; }
  return ipSalt;
}
const tagOf = async (env, bucket) => (await sha((await ipSaltFor(env)) + "|" + bucket + "sb-support")).slice(0, 16);
const ipTag = async (req, env) => tagOf(env, ipBucket(req.headers.get("CF-Connecting-IP")));
async function ipTag48(req, env) {
  const b = bucket48(ipBucket(req.headers.get("CF-Connecting-IP")));
  return b ? tagOf(env, b) : "";
}

// ---------------------------------------------------------------- limits and cache without KV writes
const mem = new Map();   // name -> { v, exp }: this isolate's copy
export function _forgetMemory() { mem.clear(); }   // tests: as if a new isolate answered
function edge() { try { return globalThis.caches?.default || null; } catch { return null; } }
const cacheUrl = (origin, name) => origin + "/__support-cache/" + encodeURIComponent(name);
async function recall(origin, name) {
  const m = mem.get(name);
  if (m && m.exp > Date.now()) return m.v;
  const c = edge();
  if (!c) return null;
  try { const r = await c.match(cacheUrl(origin, name)); return r ? await r.text() : null; } catch { return null; }
}
let swept = 0;
async function keep(origin, name, v, seconds) {
  if (Date.now() - swept > 60000) {   // expired entries (an hour's IP counter among them) go within a minute
    swept = Date.now();
    for (const [k, m] of mem) if (m.exp <= swept) mem.delete(k);
  }
  if (mem.size > 5000) for (const k of [...mem.keys()].slice(0, Math.max(0, mem.size - 4000))) mem.delete(k);
  mem.set(name, { v, exp: Date.now() + seconds * 1000 });
  const c = edge();
  if (c) try { await c.put(cacheUrl(origin, name), new Response(v, { headers: { "Cache-Control": "max-age=" + seconds } })); } catch {}
}
// One more call from this IP this hour; true when it is over the limit. The Stats object counts it atomically; while
// it cannot be reached, memory and the Cache API do (approximate).
async function overIp(req, env, h, origin, what, max) {
  const name = "rl:" + what + ":" + (await ipTag(req, env)) + ":" + now().slice(0, 13);
  try {
    const r = await h.stats(env, "/take", { items: [{ name, ttl: 3600, max }] });
    if (typeof r?.ok === "boolean") return !r.ok;
  } catch { /* the fallback below */ }
  const m = mem.get(name), mine = m && m.exp > Date.now() ? Number(m.v) || 0 : 0;
  let there = 0;
  const c = edge();
  if (c) try { const r = await c.match(cacheUrl(origin, name)); if (r) there = Number(await r.text()) || 0; } catch {}
  const n = Math.max(mine, there);
  if (n >= max) return true;
  await keep(origin, name, String(n + 1), 3600);
  return false;
}

// The day's support write budget, in the Stats object (see the top of this file): checked and counted in one step
// together with the action's own KV writes. Two lanes: the anonymous one (w, c, r) and the one of tickets whose buyer
// showed a purchase (vw, vc, vr; who = "k:<keyId>", "s:<subscription>" or "o:<order code>", with
// LIMITS.perOwnerPerDay actions each), so a flood of anonymous tickets cannot shut out paying buyers. An anonymous
// write from IPv6 also counts against its /48 for the hour (LIMITS.anonPer48).
// Returns "" = go ahead, "day" = that lane (or that purchase) is used up today (UTC), "ip" = this /48 is over its hour,
// "retry" = the count could not be written (nothing else is written then: the budget fails closed).
// While the object cannot be reached, the budget falls back to one KV counter, count:support:<date> (as before the
// object held it): the same lanes and caps, the counter's own write counted in them, so even a day split between the
// two stays under 2 x (writesPerDay + vWritesPerDay) writes. A buyer is not turned away because the object is down.
export const budgetNames = (day = now().slice(0, 10)) => ({ w: `sup:${day}:w`, c: `sup:${day}:c`, r: `sup:${day}:r`,
  vw: `sup:${day}:vw`, vc: `sup:${day}:vc`, vr: `sup:${day}:vr`, by: who => `sup:${day}:by:${who}` });
const LANE = { "": { w: "w", c: "c", r: "r", max: { w: LIMITS.writesPerDay, c: LIMITS.createsPerDay, r: LIMITS.repliesPerDay } },
               v:  { w: "vw", c: "vc", r: "vr", max: { w: LIMITS.vWritesPerDay, c: LIMITS.vCreatesPerDay, r: LIMITS.vRepliesPerDay } } };
// true when the lane has no write left today
async function laneFull(env, h, who) {
  const L = LANE[who ? "v" : ""], name = budgetNames()[L.w];
  try {
    const r = await h.stats(env, "/peek", { names: [name] });
    if (r?.n) return (Number(r.n[name]) || 0) >= L.max.w;
  } catch { /* KV below */ }
  return (await kvToday(env))[L.w] >= L.max.w;
}
async function spend(env, h, req, writes, kind = "", who = "") {
  const L = LANE[who ? "v" : ""], n = budgetNames(), ttl = 2 * DAY;
  const items = [{ name: n[L.w], ttl, max: L.max.w, by: writes }];
  if (kind) items.push({ name: n[L[kind]], ttl, max: L.max[kind] });
  if (who) items.push({ name: n.by(who), ttl, max: LIMITS.perOwnerPerDay });
  const t48 = !who && req ? await ipTag48(req, env) : "";
  if (t48) items.push({ name: "rl:w48:" + t48 + ":" + now().slice(0, 13), ttl: 3600, max: LIMITS.anonPer48, by: writes });
  let r;
  try { r = await h.stats(env, "/take", { items }); } catch { return kvSpend(env, writes, kind, who); }
  if (r?.ok === true) return "";
  return r?.ok === false ? (String(r.over || "").startsWith("rl:") ? "ip" : "day") : kvSpend(env, writes, kind, who);
}
const kvBudgetKey = () => "count:support:" + now().slice(0, 10);
async function kvToday(env) {
  let d = {};
  try { d = JSON.parse((await env.LICENSES.get(kvBudgetKey())) || "{}") || {}; } catch {}
  const o = {};
  for (const k of ["w", "c", "r", "vw", "vc", "vr"]) o[k] = Number(d[k]) || 0;
  o.by = d.by && typeof d.by === "object" && !Array.isArray(d.by) ? d.by : {};
  return o;
}
async function kvSpend(env, writes, kind, who) {
  const d = await kvToday(env), L = LANE[who ? "v" : ""];
  if (d[L.w] + writes + 1 > L.max.w) return "day";
  if (kind && d[L[kind]] >= L.max[kind]) return "day";
  if (who && (Number(d.by[who]) || 0) >= LIMITS.perOwnerPerDay) return "day";
  d[L.w] += writes + 1;
  if (kind) d[L[kind]]++;
  if (who) d.by[who] = (Number(d.by[who]) || 0) + 1;
  const next = { w: d.w, c: d.c, r: d.r, ...(d.vw || d.vc || d.vr ? { vw: d.vw, vc: d.vc, vr: d.vr, by: d.by } : {}) };
  try { await env.LICENSES.put(kvBudgetKey(), JSON.stringify(next), { expirationTtl: 2 * DAY }); } catch { return "retry"; }   // KV takes one write a second per key
  return "";
}

// ---------------------------------------------------------------- config
export function checkConfig(b) {
  b = b && typeof b === "object" ? b : {};
  const seller = {};
  for (const f of SELLER_FIELDS) {
    const v = clean(b.seller?.[f]);
    if (chars(v) > LIMITS.seller) return { error: "판매자 정보는 항목마다 " + LIMITS.seller + "자까지예요" };
    seller[f] = v;
  }
  // the privacy policy's blanks (/privacy): where the member database is, and the day the policy takes effect
  const dbRegion = clean(b.privacy?.dbRegion), effectiveDate = clean(b.privacy?.effectiveDate);
  if (chars(dbRegion) > LIMITS.seller) return { error: "회원 DB 위치는 " + LIMITS.seller + "자까지예요" };
  if (effectiveDate && !isDay(effectiveDate)) return { error: "개인정보 처리방침 시행일은 날짜(예: 2026-12-14)로 적어 주세요" };
  const notice = clean(b.notice, true);
  if (chars(notice) > LIMITS.notice) return { error: "공지는 " + LIMITS.notice + "자까지예요" };
  const rd = b.refundDays == null || b.refundDays === "" ? 7
           : typeof b.refundDays === "number" || /^\d{1,2}$/.test(String(b.refundDays)) ? Number(b.refundDays) : NaN;
  if (!Number.isInteger(rd) || rd < 0 || rd > 30) return { error: "청약철회 기간은 0~30일이에요" };
  if (b.faq != null && !Array.isArray(b.faq)) return { error: "자주 묻는 질문 형식이 아니에요" };
  const faq = [];
  for (const it of b.faq || []) {
    const q = clean(it?.q), a = clean(it?.a, true);
    if (!q && !a) continue;
    if (!q || !a) return { error: "질문과 답을 모두 적어 주세요" };
    if (chars(q) > LIMITS.faqQ || chars(a) > LIMITS.faqA) return { error: "질문은 " + LIMITS.faqQ + "자, 답은 " + LIMITS.faqA + "자까지예요" };
    faq.push({ q, a });
  }
  if (faq.length > LIMITS.faq) return { error: "자주 묻는 질문은 " + LIMITS.faq + "개까지예요" };
  return { config: { seller, privacy: { dbRegion, effectiveDate }, notice, refundDays: rd, faq, open: b.open !== false } };
}
const isDay = s => /^\d{4}-\d{2}-\d{2}$/.test(s) && !Number.isNaN(Date.parse(s + "T00:00:00Z")) && new Date(s + "T00:00:00Z").toISOString().startsWith(s);
export async function supportConfig(env) {
  let o = {};
  try { o = JSON.parse((await env.LICENSES.get("config:support")) || "{}"); } catch {}
  return (checkConfig(o).config) || checkConfig({}).config;
}

// ---------------------------------------------------------------- tickets
async function load(env, id) {
  id = String(id || "").trim().toUpperCase();
  if (!ID_RE.test(id)) return null;
  try { return JSON.parse((await env.LICENSES.get("tk:" + id)) || "null"); } catch { return null; }
}
// The dashboard's list row. Short on purpose: KV metadata must stay under 1024 bytes.
export function listRow(t) {
  const r = { cat: t.cat, status: t.status, subj: cut(t.subject, 40), created: t.created, updated: t.updated,
              last: t.messages.at(-1)?.from || "user", n: t.messages.length, rs: t.refund?.status || "",
              order: t.order || t.refund?.facts?.order || "", acct: t.account || "", key: t.keyId || t.refund?.facts?.keyId || "" };
  while (r.subj && enc.encode(JSON.stringify(r)).length >= 1000) r.subj = cut(r.subj, chars(r.subj) - 4);
  return r;
}
async function save(env, t) {
  await env.LICENSES.put("tk:" + t.id, JSON.stringify(t), { expirationTtl: keepFor(t.cat), metadata: listRow(t) });
}
// An admin message, when the thread has room (30 messages). The buyer's app shows a badge until it reads it.
function say(t, text) {
  t.updated = now();
  t.unreadUser = true;
  if (t.messages.length >= LIMITS.messages) return false;
  t.messages.push({ from: "admin", at: t.updated, text });
  return true;
}
function publicView(t) {
  const v = { id: t.id, cat: t.cat, subject: t.subject, status: t.status, created: t.created, updated: t.updated,
              lang: t.lang, messages: t.messages, unreadUser: !!t.unreadUser };
  for (const k of ["contact", "order", "keyId", "closedAt"]) if (t[k]) v[k] = t[k];
  if (t.keyId) v.keyVerified = !!t.keyVerified;
  if (t.refund) {
    const r = t.refund, f = r.facts || {};
    v.refund = { status: r.status, reason: r.reason, asked: r.asked, facts: {} };
    for (const k of ["decidedAt", "rejectReason", "amount", "method"]) if (r[k] !== undefined && r[k] !== "") v.refund[k] = r[k];
    // the server's facts only to a buyer who showed the purchase: a signed key, or an order code the server found.
    // A key id typed alone is no proof, and its plan and dates are not the filer's business.
    if (t.keyVerified || t.orderMatched) for (const k of ["issued", "plan", "daysSinceKey"]) if (f[k] !== undefined) v.refund.facts[k] = f[k];
  }
  return v;
}
function adminView(t) { const { tokenHash, ...rest } = t; return rest; }
// The purchase a ticket's buyer showed, which decides its budget lane (set when the ticket is made): a paid key the
// server issued and still honours (its subscription "s:<id>", its order "o:<code>", else "k:<keyId>"), or an order the
// seller approved (or refunded) "o:<code>"; "" for none. A referral or hand-made key is free to get, so it is not a
// purchase here; nor is a requested or "paid" order (anybody can make an order and say it is paid), nor a blocked or
// expired key, nor a key written any other way than the one the server issued (each spelling would be a new owner).
const ownerOf = t => typeof t.owner === "string" ? t.owner : "";
const PAID_SRC = ["payment", "subscription", "order"];
function paidOwner(held) {
  if (!held || !PAID_SRC.includes(held.meta?.src)) return "";
  const sub = String(held.meta.sub || held.payload?.sub || ""), code = (NOTE_ORDER_RE.exec(held.meta.note || "") || [])[1] || "";
  return /^\d{1,20}$/.test(sub) ? "s:" + sub : code ? "o:" + code : "k:" + held.id;
}

// What the server knows about the key and the order a refund names. The buyer's words are not checked against it;
// the seller reads both and decides.
// Keys made for an order start their note with its code (dashboard issue and card checkout alike), so a key alone
// still finds its order, also after the order record expired (90 days).
const noteCode = k => (NOTE_ORDER_RE.exec(k?.note || "") || [])[1] || "";
async function keyMeta(env, id) {
  if (!KEY_RE.test(id || "")) return null;
  const r = await env.LICENSES.getWithMetadata("lic:" + id);
  return r?.value ? r.metadata || {} : null;
}
async function refundFacts(env, h, order, keyId, typed) {   // typed: the record of `order`, when already read
  const given = keyId ? await keyMeta(env, keyId) : null;
  let o = order ? (typed !== undefined ? typed : await h.getOrder(env, order)) : null, code = order, kid = "", k = null, other = null;
  if (order && keyId && (given || o) && noteCode(given) !== order && o?.keyId !== keyId) {
    // the key is of another purchase: the order stays the subject, the key is kept aside for the seller
    other = { keyId, order: noteCode(given) };
    kid = o?.keyId || "";
  } else if (order && !keyId) kid = o?.keyId || "";
  else kid = keyId;
  if (kid) k = kid === keyId ? given : await keyMeta(env, kid);
  if (!order) { code = noteCode(k); if (code) o = await h.getOrder(env, code); }
  const facts = {};
  const set = (name, v) => { if (v !== undefined && v !== null && v !== "") facts[name] = v; };
  set("plan", o?.plan || k?.plan); set("issued", k?.issued); set("expires", k?.expires); set("src", k?.src); set("sub", k?.sub);
  set("order", code); set("payer", o?.payer); set("orderStatus", o?.status);
  if (k?.issued && !Number.isNaN(Date.parse(k.issued))) facts.daysSinceKey = Math.max(0, Math.floor((Date.now() - Date.parse(k.issued)) / 864e5));
  if (k) facts.keyId = kid;
  if (other) { facts.mismatch = true; facts.otherKeyId = other.keyId; set("otherOrder", other.order); }
  // no order record (expired): a key issued for a code without a subscription was a direct payment
  const method = o?.method === "card" || o?.method === "manual" ? o.method : k?.sub ? "card" : k?.src === "payment" && code ? "manual" : "unknown";
  return { facts, method, key: k ? kid : "" };
}

async function member(env, req, h) {   // best effort: a paused Supabase must not stop a support request
  const token = (req.headers.get("Authorization") || "").replace(/^Bearer /, "");
  if (!TOKEN_RE.test(token)) return "";
  try {
    const r = await h.accounts(env, "me", { token });
    return typeof r?.id === "string" ? r.id.slice(0, 20) : "";
  } catch { return ""; }
}

async function newId(env) {
  for (let i = 0; i < 5; i++) {
    const c = [...crypto.getRandomValues(new Uint8Array(8))].map(b => ALPHABET[b % ALPHABET.length]).join("");
    const id = "T-" + c.slice(0, 4) + "-" + c.slice(4);
    if ((await env.LICENSES.get("tk:" + id)) === null) return id;
  }
  throw new Error("no ticket id");
}

function cleanDiag(d) {   // filled in by the app, so cut rather than refused
  if (!d || typeof d !== "object") return null;
  const o = {};
  for (const k of ["app", "os", "gpu"]) { const v = cut(clean(typeof d[k] === "string" ? d[k].slice(0, LIMITS.diag * 4) : d[k]), LIMITS.diag); if (v) o[k] = v; }
  return Object.keys(o).length ? o : null;
}

async function create(req, url, env, h, ctx) {
  const c = await supportConfig(env);
  const b = await readBody(req);
  if (b === BIG) return tooLarge();
  // Closing support stops questions, never a withdrawal (refund) request: the law lets the buyer withdraw whatever
  // the seller's desk says (info() tells the app and /help that refunds stay open).
  if (!c.open && b?.cat !== "refund") return json({ error: "closed" }, 503);
  if (!b || typeof b !== "object" || Array.isArray(b)) return bad("body");
  if (!CATEGORIES.some(x => x.id === b.cat)) return bad("cat");
  const cat = b.cat;
  if (tooLong(b.subject, LIMITS.subject)) return bad("subject");
  const subject = clean(b.subject);
  if (!subject || chars(subject) > LIMITS.subject) return bad("subject");
  if (tooLong(b.text, LIMITS.text)) return bad("text");
  const text = clean(b.text, true);
  if (!text || chars(text) > LIMITS.text) return bad("text");
  if (tooLong(b.contact, LIMITS.contact)) return bad("contact");
  const contact = clean(b.contact);
  if (chars(contact) > LIMITS.contact) return bad("contact");
  const order = b.order == null || b.order === "" ? "" : typeof b.order === "string" && b.order.length <= 64 ? b.order.trim().toUpperCase() : "?";
  if (order && !ORDER_RE.test(order)) return bad("order");
  let keyId = b.keyId == null || b.keyId === "" ? "" : typeof b.keyId === "string" && b.keyId.length <= 64 ? b.keyId.trim().toLowerCase() : "?";
  if (keyId && !KEY_RE.test(keyId)) return bad("keyId");
  // The full key (License.KeyText() in the app): only its shape here, the signature after the per-IP limit because
  // it costs CPU. It is never stored, logged or sent back; the ticket keeps its keyId and keyVerified.
  const key = b.key == null || b.key === "" ? "" : typeof b.key === "string" && b.key.length <= LIMITS.key ? b.key.trim() : "?";
  if (key && !/^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/.test(key)) return bad("bad key");
  if (b.lang != null && b.lang !== "ko" && b.lang !== "en") return bad("lang");
  if (b.consent !== true) return bad("consent");
  let reason = "";
  if (cat === "refund") {
    if (!order && !keyId && !key) return bad("order");
    reason = REFUND_REASONS.some(x => x.id === b.refund?.reason) ? b.refund.reason : "";
    if (!reason) return bad("reason");
  }

  if (await overIp(req, env, h, url.origin, "new", LIMITS.createPerIp)) return busy("ip");
  let keyVerified = false, held = null;
  if (key) {
    const [p, s] = key.split(".");
    if (!(await h.verifySig(env, p, s))) return bad("bad key");
    const id = await h.keyId(key);
    if (keyId && keyId !== id) return bad("keyId");
    keyId = id;
    keyVerified = true;
    held = await h.heldKey(env, key);   // the purchase lane needs the key as issued, still honoured
    if (held && held.id !== keyId) held = null;
  }
  const o = order ? await h.getOrder(env, order) : null;   // reads only, up to spend()
  const f = cat === "refund" ? await refundFacts(env, h, order, keyId, o) : null;
  const owner = paidOwner(held) || (o?.status === "approved" || o?.status === "refunded" ? "o:" + order : "");
  // The refund webhook's index, only for a key the buyer showed they hold: the signed key itself, or a key the found
  // order points to (refundFacts takes the order's own key whenever an order was found). A key id typed alone could be
  // anybody's: it gets no entry, so a stranger's request cannot catch that key's card refund.
  const index = f?.key && ((keyVerified && f.key === keyId) || o) ? f.key : "";
  // the ticket and the index, in the lane of the purchase shown (if any)
  const why = await spend(env, h, req, index ? 2 : 1, "c", owner);
  if (why) return refused(why);

  const t = now(), token = hex(crypto.getRandomValues(new Uint8Array(32)));
  const ticket = { id: await newId(env), tokenHash: await sha(token), cat, subject, status: "open", created: t, updated: t, consentAt: t,
                   messages: [{ from: "user", at: t, text }], lang: lang2(b.lang), unreadUser: false };
  if (contact) ticket.contact = contact;
  if (order) ticket.order = order;
  if (o) ticket.orderMatched = true;
  if (keyId) { ticket.keyId = keyId; ticket.keyVerified = keyVerified; }
  if (owner) ticket.owner = owner;
  const account = await member(env, req, h);
  if (account) ticket.account = account;
  const diag = cleanDiag(b.diag);
  if (diag) ticket.diag = diag;
  if (f) ticket.refund = { status: "requested", reason, asked: t, method: f.method, facts: f.facts };
  await save(env, ticket);
  // the refund webhook finds the ticket with one read
  if (index) await env.LICENSES.put("tkkey:" + index, ticket.id, { expirationTtl: LIMITS.keepLong });
  await feedNote(env, h, ctx, f ? "refund" : "ticket", ticket);
  return json({ id: ticket.id, token }, 201);
}

// The same 404 for an unknown ticket and a wrong token; the token is hashed either way. The token comes from the
// X-Ticket-Token header (GET) or the JSON body (POST), never from a URL, where it would land in logs and history.
async function opened(env, id, token) {
  if (typeof token !== "string" || !TOKEN_RE.test(token)) return null;   // no KV read for what cannot be a token
  const t = await load(env, id);
  const given = await sha(token);
  return t && sameHex(given, t.tokenHash || "") ? t : null;
}
// The buyer saw this unread mark while the lane's budget kept it from being cleared in KV: remembered without a write
// (memory / Cache API, approximate), so the same mark is not reported as new again. Cleared for real tomorrow.
const seenKey = t => "seen:" + t.id;
async function stillUnread(origin, t) {
  return !!t.unreadUser && (await recall(origin, seenKey(t))) !== t.updated;
}

async function read(req, url, env, h) {
  const t = await opened(env, url.searchParams.get("id"), req.headers.get("X-Ticket-Token"));
  if (!t) return notFound();
  const view = publicView(t);   // still says unread, so the app can show what is new
  if (t.unreadUser) {
    // Not counted in the budget: only a seller reply sets the flag. With the ticket's lane used up (or not known) it
    // stays set in KV.
    if (!(await laneFull(env, h, ownerOf(t)))) { t.unreadUser = false; await save(env, t); }
    else if (!(await stillUnread(url.origin, t))) view.unreadUser = false;
    else await keep(url.origin, seenKey(t), t.updated, DAY);
  }
  return json({ ticket: view });
}

// The app's polling: many tickets in one call, nothing written, unread left as it is. A ticket that is not found (or
// whose token is wrong) costs a KV read like a found one, so after LIMITS.statusMisses of them the call reads no
// further: the tickets after that are left out of the answer (the app then keeps what it showed; null means gone).
async function statusBatch(req, url, env, h) {
  const b = await readBody(req);
  if (b === BIG) return tooLarge();
  const list = b && typeof b === "object" && !Array.isArray(b) ? b.tickets : null;
  if (!Array.isArray(list) || list.length < 1 || list.length > LIMITS.statusBatch ||
      !list.every(x => x && typeof x === "object" && typeof x.id === "string" && typeof x.token === "string" && x.id.length <= 64 && x.token.length <= 128))
    return bad("tickets");
  if (await overIp(req, env, h, url.origin, "status", LIMITS.statusPerIp)) return busy("ip");
  const out = Object.create(null);   // any id, "__proto__" too, is just a key
  let misses = 0;
  for (const x of list) {
    if (x.id in out) continue;
    if (misses >= LIMITS.statusMisses) break;
    const reads = TOKEN_RE.test(x.token) && ID_RE.test(String(x.id).trim().toUpperCase());   // what opened() reads KV for
    const t = await opened(env, x.id, x.token);
    if (!t && reads) misses++;
    out[x.id] = t ? { status: t.status, updated: t.updated, n: t.messages.length, unread: await stillUnread(url.origin, t) } : null;
  }
  return json({ tickets: out });
}

async function reply(req, url, env, h, ctx) {
  const b = await readBody(req);
  if (b === BIG) return tooLarge();
  const t = await opened(env, b?.id, b?.token);
  if (!t) return notFound();
  if (tooLong(b.text, LIMITS.text)) return bad("text");
  const text = clean(b.text, true);
  if (!text || chars(text) > LIMITS.text) return bad("text");
  if (t.status === "closed" && Date.now() - Date.parse(t.closedAt || t.updated) > LIMITS.reopenDays * 864e5) return json({ error: "closed" }, 409);
  if (t.messages.length >= LIMITS.messages) return json({ error: "full" }, 409);
  if (await overIp(req, env, h, url.origin, "reply", LIMITS.repliesPerIp)) return busy("ip");
  const why = await spend(env, h, req, 1, "r", ownerOf(t));
  if (why) return refused(why);
  const reopened = t.status === "closed";
  t.updated = now();
  t.messages.push({ from: "user", at: t.updated, text });
  t.status = "open";
  t.unreadUser = false;
  delete t.closedAt;
  await save(env, t);
  await feedNote(env, h, ctx, reopened ? "reopen" : "reply", t);
  return json({ ticket: publicView(t) });
}

async function close(req, url, env, h, ctx) {
  const b = await readBody(req);
  if (b === BIG) return tooLarge();
  const t = await opened(env, b?.id, b?.token);
  if (!t) return notFound();
  if (t.status !== "closed") {
    const why = await spend(env, h, req, 1, "", ownerOf(t));
    if (why) return refused(why);
    t.status = "closed"; t.closedAt = t.updated = now(); await save(env, t);
    await feedNote(env, h, ctx, "close", t);
  }
  return json({ ticket: publicView(t) });
}

// The customer portal of a card subscription: a pre-signed Lemon Squeezy link where the buyer can cancel or change
// the card. Only for a key the server issued and still honours, checked like /renew checks one (h.heldKey: the text
// as issued, after the same clean-up as the app's; not revoked under either signature; not expired) and naming the
// subscription in its record too. A key that is anything else, and a subscription that is no longer active (no
// sub:<id>), get the same 404. Each key gets LIMITS.portalPerKey links a day (UTC; counted in the Stats object, so
// none while it cannot be reached), and every link handed out leaves a line in the dashboard's 기록.
// The link is kept 30 minutes per subscription, so pressing the button again does not call Lemon Squeezy again.
async function subscription(req, url, env, h) {
  if (await overIp(req, env, h, url.origin, "sub", LIMITS.subscriptionPerIp)) return busy("ip");
  const b = await readBody(req);
  if (b === BIG) return tooLarge();
  const held = typeof b?.key === "string" && b.key.length <= LIMITS.key ? await h.heldKey(env, b.key) : null;
  const sub = String(held?.payload?.sub || "");
  if (!held || !/^\d{1,20}$/.test(sub) || String(held.meta?.sub || "") !== sub) return notFound();
  if ((await env.LICENSES.get("sub:" + sub)) === null) return notFound();
  if (!env.LS_API_KEY) return json({ error: "unavailable" }, 503);
  let cap;
  try { cap = await h.stats(env, "/take", { items: [{ name: "portal:" + now().slice(0, 10) + ":" + held.id, ttl: 2 * DAY, max: LIMITS.portalPerKey }] }); }
  catch { return json({ error: "unavailable" }, 503); }
  if (cap?.ok !== true) return busy("day");
  const handed = async portal => {
    try { await h.audit(env, "app", "구독 관리 링크", "구독 " + sub + " · " + held.id); } catch {}
    return json({ portal });
  };
  const cached = await recall(url.origin, "portal:" + sub);
  if (cached) return handed(cached);
  let res;
  try {
    res = await fetch("https://api.lemonsqueezy.com/v1/subscriptions/" + sub, {
      headers: { Authorization: "Bearer " + env.LS_API_KEY, Accept: "application/vnd.api+json" },
      signal: AbortSignal.timeout(10000),
    });
  } catch { return json({ error: "unavailable" }, 503); }
  if (res.status === 404) return notFound();
  if (!res.ok) return json({ error: "unavailable" }, 503);
  const portal = (await res.json().catch(() => null))?.data?.attributes?.urls?.customer_portal;
  if (typeof portal !== "string" || !/^https:\/\/\S+$/.test(portal)) return json({ error: "unavailable" }, 503);
  await keep(url.origin, "portal:" + sub, portal, LIMITS.portalCache);
  return handed(portal);
}

// refunds: true always: refund (withdrawal) requests are taken even while the seller has closed support ("open").
async function info(url, env) {
  const c = await supportConfig(env), lang = lang2(url.searchParams.get("lang"));
  return json({ open: c.open, refunds: true, seller: c.seller, notice: c.notice, refundDays: c.refundDays,
                faq: c.faq.length ? c.faq : DEFAULT_FAQ[lang], faqDefault: !c.faq.length,
                categories: CATEGORIES, refundReasons: REFUND_REASONS, policy: ARTICLE_9[lang] });
}

// A browser on another site can send text/plain or a form without asking; application/json needs a CORS preflight,
// and nothing here answers OPTIONS (404), so such a page cannot post at all. The app and /help send JSON.
const JSON_TYPE = /^\s*application\/json\s*(;|$)/i;

// /support/* (h: helpers from index.js: getOrder, putOrder, revoke, revokeSub, audit, accounts, verifySig, unb64url,
// allRecords, state, keyId, canonicalKey, heldKey, events, stats, cancelSubscription;
// ctx: the request's context, for ctx.waitUntil)
export async function support(req, url, env, h, ctx) {
  // a ticket token in a URL is refused outright, so a client that still puts it there shows up at once
  if (url.searchParams.has("token")) return json({ error: "token in url" }, 400);
  if (req.method === "POST" && !JSON_TYPE.test(req.headers.get("Content-Type") || "")) return json({ error: "content-type" }, 415);
  const route = req.method + " " + url.pathname;
  switch (route) {
    case "GET /support/info": return info(url, env);
    case "POST /support/ticket": return create(req, url, env, h, ctx);
    case "GET /support/ticket": return read(req, url, env, h);
    case "POST /support/ticket/reply": return reply(req, url, env, h, ctx);
    case "POST /support/ticket/close": return close(req, url, env, h, ctx);
    case "POST /support/tickets/status": return statusBatch(req, url, env, h);
    case "POST /support/subscription": return subscription(req, url, env, h);
  }
  return notFound();
}

// GET /help. Static page: every value it shows comes from the API and is put in with textContent.
export function helpPage(url) {
  const lang = lang2(url.searchParams.get("lang"));
  const nonce = btoa(String.fromCharCode(...crypto.getRandomValues(new Uint8Array(16))));
  const html = HELP_HTML.replaceAll("__NONCE__", nonce).replace('<html lang="ko">', '<html lang="' + lang + '">');
  return new Response(html, { headers: {
    "content-type": "text/html; charset=utf-8", "cache-control": "no-store",
    "content-security-policy": `default-src 'none'; script-src 'nonce-${nonce}'; style-src 'nonce-${nonce}'; connect-src 'self'; img-src data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'`,
    "x-frame-options": "DENY", "referrer-policy": "no-referrer", "x-content-type-options": "nosniff" } });
}

// GET /privacy. The policy is static HTML made from privacy/*.md by build_privacy.mjs (no Markdown at run time); only
// its four blanks change, filled here from the dashboard's settings, every value escaped, in one pass so a value can
// never fill in another blank. A blank the seller has not filled in says so. No script; a nonce for the one style.
export const PRIVACY_EMPTY = { ko: "판매자가 아직 입력하지 않았어요", en: "not entered by the seller yet" };
const MONTHS = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
const escHtml = s => String(s).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
// What fills each blank. The privacy officer is a person: the representative's name, never the business name in its
// place; while it is empty the page shows the "not entered" mark and the dashboard warns in red.
export function privacyBlanks(c, lang = "ko") {
  const s = c.seller || {}, p = c.privacy || {}, d = /^(\d{4})-(\d{2})-(\d{2})$/.exec(p.effectiveDate || "");
  const day = !d ? "" : lang === "en" ? MONTHS[Number(d[2]) - 1] + " " + Number(d[3]) + ", " + d[1] : d[1] + "년 " + Number(d[2]) + "월 " + Number(d[3]) + "일";
  return { OWNER_NAME: s.rep || "", OWNER_EMAIL: s.email || "", SUPABASE_REGION: p.dbRegion || "", EFFECTIVE_DATE: day };
}
export async function privacyPage(url, env) {
  const lang = lang2(url.searchParams.get("lang"));
  const v = privacyBlanks(await supportConfig(env), lang);
  const nonce = btoa(String.fromCharCode(...crypto.getRandomValues(new Uint8Array(16))));
  const html = PRIVACY_HTML[lang].replaceAll("__NONCE__", nonce)
    .replace(/\{\{(OWNER_NAME|OWNER_EMAIL|SUPABASE_REGION|EFFECTIVE_DATE)\}\}/g,
             (_, k) => v[k] ? escHtml(v[k]) : '<span class="blank">' + PRIVACY_EMPTY[lang] + "</span>");
  return new Response(html, { headers: {
    "content-type": "text/html; charset=utf-8", "cache-control": "no-store",
    "content-security-policy": `default-src 'none'; style-src 'nonce-${nonce}'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'`,
    "x-frame-options": "DENY", "referrer-policy": "no-referrer", "x-content-type-options": "nosniff" } });
}

// ---------------------------------------------------------------- dashboard
const refundOpen =m => m?.rs === "requested" || m?.rs === "approved";
// Up to 10 list pages (10,000 tickets); out.truncated says there were more, and the dashboard says so.
async function rows(env) {
  const out = [];
  out.truncated = true;
  let cursor;
  for (let i = 0; i < 10; i++) {
    const r = await env.LICENSES.list({ prefix: "tk:", cursor, limit: 1000 });
    for (const k of r.keys) if (k.metadata) out.push({ id: k.name.slice(3), ...k.metadata });
    if (r.list_complete) { out.truncated = false; break; }
    cursor = r.cursor;
  }
  return out;
}
export async function supportCounts(env) {   // for the dashboard summary: one list call (ticketsMore: counted from the first 1,000)
  const r = await env.LICENSES.list({ prefix: "tk:", limit: 1000 });
  return { ticketsOpen: r.keys.filter(k => k.metadata?.status === "open").length, refundsOpen: r.keys.filter(k => refundOpen(k.metadata)).length,
           ...(r.list_complete === false ? { ticketsMore: true } : {}) };
}
// names: ticket numbers the dashboard's feed shows but its list does not (another tab, older): their row comes back
// in `named` from the same list call, so the feed costs no KV list of its own.
async function list(env, status, q, names = "") {
  if (!["open", "answered", "closed", "refund", "all"].includes(status)) status = "open";
  const all = await rows(env);
  const count = { open: 0, answered: 0, closed: 0, refund: 0 };
  for (const r of all) { if (r.status in count) count[r.status]++; if (refundOpen(r)) count.refund++; }
  q = q.trim().toLowerCase();
  const pick = all.filter(r => (status === "all" || (status === "refund" ? refundOpen(r) : r.status === status)) &&
                               (!q || [r.id, r.subj, r.order, r.acct, r.key].some(v => String(v || "").toLowerCase().includes(q))));
  pick.sort((a, b) => (b.status === "open") - (a.status === "open") || String(b.updated).localeCompare(String(a.updated)));
  const want = new Set(String(names).split(",").filter(id => ID_RE.test(id)).slice(0, 100)), named = {};
  for (const r of all) if (want.has(r.id)) named[r.id] = { subj: r.subj, cat: r.cat, status: r.status, ...(r.rs ? { rs: r.rs } : {}) };
  // more: the filter matched more than the 300 shown; truncated: there are more than 10,000 tickets, some not looked at
  return { tickets: pick.slice(0, 300), count, ...(pick.length > 300 ? { more: true } : {}), ...(all.truncated ? { truncated: true } : {}),
           ...(want.size ? { named } : {}) };
}

// The keys a refund would block: every key of a manual order (a bundle has several, each note starts with the
// order code); for a card subscription every key it was given (a plan change or a renewal gives a new one);
// otherwise the one key of the request: the order's own key, or the key the buyer named when it belongs to that
// order. A key of another purchase (facts.mismatch) is never blocked by this request.
const keyRow = (m, h) => ({ id: m.id, plan: m.plan || "", issued: m.issued || "", expires: m.expires || "", state: h.state(m), note: m.note || "",
                            ...(m.sub ? { sub: String(m.sub) } : {}) });
// owned: the buyer showed they hold that key. It is the key they pasted (signature checked), or a key of the order
// they named and the server found, or of the order a pasted key belongs to, or another key of the same card
// subscription. A key id typed alone shows nothing: anybody who once saw it could type it. o = the record of the
// request's order code, if any.
function owns(t, k, o) {
  const f = t.refund?.facts || {}, codes = [];
  if (t.keyVerified && k.id === t.keyId) return true;
  if (f.sub && !f.mismatch && (t.keyVerified || t.orderMatched) && String(k.sub || "") === String(f.sub)) return true;
  if (t.orderMatched && t.order) codes.push(t.order);
  if (t.keyVerified && !f.mismatch && f.order) codes.push(f.order);
  return codes.some(c => String(k.note || "").startsWith(c) || (o?.code === c && o.keyId === k.id));
}
async function subKeyIds(env, sub) {
  const ids = new Set();
  if (!/^\d{1,20}$/.test(sub)) return ids;
  try { for (const id of JSON.parse((await env.LICENSES.get("subkeys:" + sub)) || "[]")) if (KEY_RE.test(id)) ids.add(id); } catch {}
  return ids;
}
// shown: the buyer pasted this key in the request. That is all the server can say about a key's use: it does not
// record which PC registered a key or when the app last checked it.
async function keysFor(env, t, h) {
  const f = t.refund?.facts || {}, code = t.order || f.order || "";
  const o = code ? await h.getOrder(env, code) : null;
  let recs = [];
  if (t.refund?.method === "manual" && code) {
    recs = (await h.allRecords(env)).filter(m => String(m.note || "").startsWith(code));
  } else {
    const id = f.keyId || (!f.mismatch && t.keyId) || o?.keyId || "";
    const m = await keyMeta(env, id);
    if (m) recs = [{ id, ...m }];
    if (m?.sub) for (const sid of await subKeyIds(env, String(m.sub))) {
      if (sid === id) continue;
      const s = await keyMeta(env, sid);
      if (s && String(s.sub || "") === String(m.sub)) recs.push({ id: sid, ...s });
    }
  }
  return recs.map(m => ({ ...keyRow(m, h), owned: owns(t, m, o), ...(t.keyVerified && m.id === t.keyId ? { shown: true } : {}) }));
}
// For the dashboard's warning: the purchase the other key belongs to (all of its order's keys when they are known).
async function otherKeys(env, t, h) {
  const f = t.refund?.facts || {};
  if (!f.mismatch) return null;
  const recs = f.otherOrder ? (await h.allRecords(env)).filter(m => String(m.note || "").startsWith(f.otherOrder)) : [];
  if (!recs.some(m => m.id === f.otherKeyId)) { const m = await keyMeta(env, f.otherKeyId); if (m) recs.push({ id: f.otherKeyId, ...m }); }
  return recs.map(m => keyRow(m, h));
}

// Only a paid (approved) order becomes "refunded"; a request, a rejected or an unknown order stays as it is.
async function markRefunded(env, h, o) {
  if (o?.status !== "approved") return false;
  o.status = "refunded"; o.refundedAt = now();
  await h.putOrder(env, o);
  return true;
}
// /claim keeps handing out an order's key (and a bundle's keys) for 30 days; after a refund it must not.
async function dropClaims(env, code) {
  for (const k of ["session:" + code, "bundle:" + code]) if ((await env.LICENSES.get(k)) !== null) await env.LICENSES.delete(k);
}

// Returns a plain object; `status` (if any) is the HTTP status. Admin errors are Korean sentences like the rest.
export async function supportAdmin(env, route, body, url, h) {
  const nf = { status: 404, error: "문의를 찾지 못했어요" };
  switch (route) {
    // a search goes in a POST body (the words may be a buyer's order code or member id: not for addresses and logs)
    case "GET /support": return list(env, url.searchParams.get("status") || "open", "", url.searchParams.get("names") || "");
    case "POST /support": return list(env, String(body.status || "open"), String(body.q || ""), String(body.names || ""));
    case "GET /support/feed": {   // the Durable Object only, no KV
      const a = url.searchParams.get("after");
      if (a !== null && !/^\d{1,15}$/.test(a)) return { status: 400, error: "after는 숫자예요" };
      try { return await h.events(env, "/feed", a === null ? {} : { after: Number(a) }); }
      catch { return { status: 503, error: "새 소식을 불러오지 못했어요" }; }
    }
    case "GET /support/config": return { config: await supportConfig(env) };
    case "POST /support/config": {
      const r = checkConfig(body);
      if (r.error) return { status: 400, error: r.error };
      await env.LICENSES.put("config:support", JSON.stringify(r.config));
      await h.audit(env, "admin", "고객센터 설정", (r.config.open ? "문의 받기" : "문의 닫음") + " · 청약철회 " + r.config.refundDays + "일 · FAQ " + r.config.faq.length + "개");
      return { config: r.config };
    }
    case "POST /support/delete": {
      const id = String(body.id || "").trim().toUpperCase();
      if (!ID_RE.test(id)) return { status: 400, error: "문의 번호 형식이 아니에요 (T-XXXX-XXXX)" };
      await env.LICENSES.delete("tk:" + id);
      try { await h.events(env, "/forget", { ticket: id }); } catch { /* the feed keeps at most 30 days anyway */ }
      await h.audit(env, "admin", "문의 삭제", id);
      return { ok: true };
    }
  }
  const t = await load(env, route.startsWith("GET ") ? url.searchParams.get("id") : body.id);
  switch (route) {
    case "GET /support/ticket": {
      if (!t) return nf;
      const other = await otherKeys(env, t, h);
      return { ticket: adminView(t), keys: await keysFor(env, t, h), ...(other ? { otherKeys: other } : {}) };
    }
    case "POST /support/reply": {
      if (!t) return nf;
      const text = clean(body.text, true);
      if (!text || chars(text) > LIMITS.text) return { status: 400, error: "답장은 1~" + LIMITS.text + "자예요" };
      if (t.messages.length >= LIMITS.messages) return { status: 409, error: "메시지가 " + LIMITS.messages + "개라 더 쓸 수 없어요" };
      say(t, text);
      t.status = body.close ? "closed" : "answered";
      if (body.close) t.closedAt = t.updated; else delete t.closedAt;
      await save(env, t);
      return { ticket: adminView(t) };
    }
    case "POST /support/status": {
      if (!t) return nf;
      if (body.status !== "open" && body.status !== "closed") return { status: 400, error: "상태는 open 또는 closed예요" };
      if (t.status !== body.status) {
        t.status = body.status;
        t.updated = now();
        if (body.status === "closed") t.closedAt = t.updated; else delete t.closedAt;
        await save(env, t);
      }
      return { ticket: adminView(t) };
    }
    case "POST /support/refund": return refundAction(env, t, body, h);
  }
  return { status: 404, error: "not found" };
}

function amountOf(v) {   // undefined = not given, null = wrong
  if (v === undefined || v === null || v === "") return undefined;
  const n = typeof v === "number" ? v : /^\d{1,9}$/.test(String(v).replace(/,/g, "").trim()) ? Number(String(v).replace(/,/g, "").trim()) : NaN;
  return Number.isInteger(n) && n >= 0 && n <= LIMITS.amount ? n : null;
}

async function refundAction(env, t, body, h) {
  if (!t) return { status: 404, error: "문의를 찾지 못했어요" };
  const r = t.refund;
  if (!r) return { status: 400, error: "환불 요청이 아니에요" };
  if (!["approve", "reject", "done"].includes(body.action)) return { status: 400, error: "action은 approve, reject, done 중 하나예요" };
  if (r.status === "done") return { status: 409, error: "이미 환불을 마친 요청이에요" };
  const amount = amountOf(body.amount);
  if (amount === null) return { status: 400, error: "금액은 0 이상의 정수(원)예요" };
  const message = clean(body.message, true);
  if (chars(message) > LIMITS.text) return { status: 400, error: "메시지는 " + LIMITS.text + "자까지예요" };
  const lang = lang2(t.lang), code = t.order || r.facts?.order || "", at = now();
  const what = [t.id, code, amount !== undefined ? amount.toLocaleString("en-US") + "원" : ""].filter(Boolean);

  if (body.action === "approve") {
    Object.assign(r, { status: "approved", decidedAt: at });
    delete r.rejectReason;
    if (amount !== undefined) r.amount = amount;
    say(t, message || SAY.approved[lang]);
    t.status = "answered";
    await save(env, t);
    await h.audit(env, "admin", "환불 승인", what.join(" · "));
    return { ticket: adminView(t) };
  }
  if (body.action === "reject") {
    const reason = clean(body.reason, true);
    if (!reason || chars(reason) > LIMITS.reason) return { status: 400, error: "거절 사유를 적어 주세요 (" + LIMITS.reason + "자까지, 구매자에게 보여요)" };
    Object.assign(r, { status: "rejected", rejectReason: reason, decidedAt: at });
    say(t, message || SAY.rejected[lang](reason));
    t.status = "answered";
    await save(env, t);
    await h.audit(env, "admin", "환불 거절", [t.id, code, reason].filter(Boolean).join(" · "));
    return { ticket: adminView(t) };
  }
  // done: the seller has paid the money back (by hand, or in Lemon Squeezy)
  const all = await keysFor(env, t, h), o = code ? await h.getOrder(env, code) : null;
  if (!all.length && o?.status !== "approved")
    return { status: 409, error: "이 요청에는 막을 키가 없고, 승인된(결제가 확인된) 주문도 아니에요. 주문 코드와 키를 확인해 주세요" +
                                 (r.facts?.mismatch ? ". 구매자가 적은 키는 다른 구매의 키예요" : "") + "." };
  // keys: the ones to block when only part of a bundle is refunded (the seller ticks them); all of them when not given
  let keys = all;
  if (body.keys != null) {
    if (!Array.isArray(body.keys) || !body.keys.length || body.keys.length > 100 || !body.keys.every(id => typeof id === "string" && all.some(k => k.id === id)))
      return { status: 400, error: "막을 키를 이 요청의 키 중에서 하나 이상 골라 주세요" };
    keys = all.filter(k => body.keys.includes(k.id));
  }
  // A key the buyer did not show they hold is blocked only on a second, explicit yes from the seller: a stranger may
  // have typed somebody else's key id to get that key blocked.
  const doubt = body.revoke !== false ? keys.filter(k => k.state !== "revoked" && !k.owned).map(k => k.id) : [];
  if (doubt.length && body.confirmUnverified !== true)
    return { status: 409, unverified: doubt,
             error: "키 소유 확인 안 됨: 구매자가 키 식별 번호만 적어서 이 키(" + doubt.join(", ") + ")가 구매자의 것인지 확인되지 않았어요. " +
                    "다른 사람의 키를 적었을 수도 있어요. 구매자의 키가 맞는지 확인한 뒤 한 번 더 확인하고 누르세요." };
  const ids = [], subs = [...new Set(keys.filter(k => k.sub).map(k => k.sub))], cancelled = [], cancelFailed = [];
  if (body.revoke !== false) {
    // a card subscription: every key it was given goes off (h.revokeSub), and its renewals stop. Cancelling it in
    // Lemon Squeezy (h.cancelSubscription) is the order code's part; called only where it exists.
    const bySub = new Set();
    for (const sub of subs) for (const id of (typeof h.revokeSub === "function" ? await h.revokeSub(env, sub, "환불", "admin") : [])) bySub.add(id);
    for (const k of keys) {
      if (k.state !== "revoked" && !bySub.has(k.id)) await h.revoke(env, k.id, "환불", "admin");
      ids.push(k.id);
    }
    for (const id of bySub) if (!ids.includes(id)) ids.push(id);
    if (typeof h.cancelSubscription === "function")
      for (const sub of subs) {
        try { const r = await h.cancelSubscription(env, sub, "환불");   // {ok: false, reason} when it could not (or no API key)
              (r === false || r?.ok === false) ? cancelFailed.push(sub) : cancelled.push(sub); }
        catch { cancelFailed.push(sub); }
      }
  }
  // part of a bundle: the order stays approved and its keys claimable (the kept keys are still the buyer's)
  const kept = body.revoke !== false ? all.filter(k => k.state !== "revoked" && !ids.includes(k.id)).map(k => k.id) : [];
  if (!kept.length) {
    await markRefunded(env, h, o);
    if (code) await dropClaims(env, code);
  }
  Object.assign(r, { status: "done", decidedAt: at });
  delete r.rejectReason;
  if (amount !== undefined) r.amount = amount;
  if (ids.length) r.revoked = ids;
  if (kept.length) r.kept = kept; else delete r.kept;
  say(t, message || (ids.length ? SAY.doneKeys[lang] : SAY.done[lang]));
  t.status = "answered";
  await save(env, t);
  await h.audit(env, "admin", "환불 완료", [...what, ids.length ? "키 " + ids.join(", ") : "키 그대로", kept.length ? "남긴 키 " + kept.length + "개" : "",
                                            cancelled.length ? "구독 해지 " + cancelled.join(", ") : "", cancelFailed.length ? "구독 해지 실패 " + cancelFailed.join(", ") : "",
                                            doubt.length ? "소유 확인 안 된 키를 판매자가 확인함" : ""].filter(Boolean).join(" · "));
  return { ticket: adminView(t), revoked: ids, ...(kept.length ? { kept } : {}), ...(subs.length ? { subs, cancelled, cancelFailed, cancelAvailable: typeof h.cancelSubscription === "function" } : {}) };
}

// Called by the Lemon Squeezy refund webhooks after they blocked keyId: the newest refund ticket naming that key
// is marked done. A key the buyer named from another purchase does not count. When no ticket names that key and it
// belongs to a card subscription, the subscription's other keys are tried too (a request may name the key before a
// plan change or a renewal): two KV reads when the key has no subscription and there is no ticket.
export async function webhookRefund(env, keyId, h, expand = true) {
  if (await closeRefund(env, keyId, h, [keyId])) return true;
  if (!expand || !KEY_RE.test(keyId || "")) return false;
  const sub = String((await keyMeta(env, keyId))?.sub || "");
  return sub ? (await webhookRefundSub(env, sub, h, [keyId])) > 0 : false;
}
// Called by revokeSub (index.js) after a refund webhook blocked a subscription's keys, also when the subscription was
// cancelled first (then sub:<id> is gone and the refund webhook alone would not find the request): every refund ticket
// naming one of its keys (ids and subkeys:<sub>) is marked done. -> how many were.
export async function webhookRefundSub(env, sub, h, ids = []) {
  const all = await subKeyIds(env, String(sub));
  for (const id of ids) if (KEY_RE.test(id)) all.add(id);
  let n = 0;
  for (const id of all) if (await closeRefund(env, id, h, [...all])) n++;
  return n;
}
async function closeRefund(env, keyId, h, revoked) {
  const id = await env.LICENSES.get("tkkey:" + keyId);
  const t = id ? await load(env, id) : null;
  const r = t?.refund, f = r?.facts || {};
  if (!r || r.status === "done" || (f.keyId !== keyId && (f.mismatch || t.keyId !== keyId))) return false;
  Object.assign(r, { status: "done", decidedAt: now(), revoked: revoked.slice(0, 50) });
  delete r.rejectReason;
  if (r.method === "unknown") r.method = "card";
  const code = t.order || f.order || "";
  if (code) { await markRefunded(env, h, await h.getOrder(env, code)); await dropClaims(env, code); }
  say(t, SAY.card[lang2(t.lang)]);
  t.status = "answered";
  await save(env, t);
  await feedNote(env, h, null, "card", t);
  await h.audit(env, "webhook", "환불 완료", t.id + " · " + keyId);
  return true;
}
