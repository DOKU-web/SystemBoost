# Pre-registration popup (사전예약), open until 2026-11-27 23:59 KST.
# The popup only appears when the license server answers GET /preorder with {"open": true}, so the page is safe to
# publish before the server route is deployed: until then nothing shows. PRE_UNTIL also closes it in the browser.
# Korean only. Two required boxes: personal information (consent) and the one launch mail, which is advertising
# (adConsent); the server refuses a sign-up without either with 400 {"error": "consent"}.
PRE_API = "https://systemboost-license.euning97.workers.dev/preorder"
PRE_UNTIL = "2026-11-27T14:59:59Z"   # 2026-11-27 23:59:59 KST
PRE_UNSUB = "https://systemboost-license.euning97.workers.dev/preorder/unsubscribe"   # the launch mail's unsubscribe page (license.html privacy table)
PRE_KEEP = "2027-02-28"              # the server deletes the entries on this day (src/preorder.js PRE.deleteAt, the Stats object's SQLite)

# plan values sent to the server, in this order: undecided, free, premium (Pro), ultra, biz
PLAN_IDS = ["undecided", "free", "premium", "ultra", "biz"]

PRE = {}
PRE["ko"] = dict(
  bar='사전예약 받는 중 · 11월 27일(금) 23:59까지 · 출시되면 메일로 알려 드려요', go='신청하기',
  nav="사전예약", badge="사전예약", title="SystemBoost 사전예약",
  lead="SystemBoost는 2026년 12월 4일(금)에 정식 출시해요. 사전예약하면 출시일에 적어 주신 메일로 한 번 알려 드려요.",
  until="신청 마감: 2026년 11월 27일(금) 23:59 (한국 시간)",
  free="결제는 하지 않아요. 사전예약은 출시 알림 신청이에요.",
  email="메일 주소", plan="관심 있는 플랜",
  plans=["아직 모르겠어요", "무료", "프로", "울트라", "산업"],
  consent="<b>만 14세 이상이고, 개인정보 수집 · 이용에 동의해요 (필수)</b><br>메일 주소, 관심 플랜, 사이트 언어, 신청 시각, 광고성 정보 수신 동의 시각을 받아 정식 출시 알림 메일을 한 번 보내는 데만 써요. 2027년 2월 28일에 모두 지워요. 동의하지 않을 수 있지만, 그러면 사전예약을 할 수 없어요(앱 이용에는 영향 없음). 만 14세 이상만 신청할 수 있어요. <a href=\"https://systemboost-license.euning97.workers.dev/privacy#preorder\" target=\"_blank\" rel=\"noopener\">자세한 안내</a>",
  adconsent="<b>출시 알림 메일(광고성 정보)을 한 번 받는 데 동의해요 (필수)</b><br>제목이 (광고)로 시작하는 메일을 출시일에 한 번 보내요. 언제든 수신을 거부할 수 있어요.",
  send="사전예약 신청", later="다음에 할게요", close="닫기",
  done="신청됐어요. 출시되면 메일로 알려 드릴게요.", already="이미 신청된 메일이에요. 출시되면 알려 드릴게요.",
  e_mail="메일 주소를 확인해 주세요.", e_consent="만 14세 이상이고 개인정보 수집 · 이용에 동의해야 신청할 수 있어요.",
  e_ad="출시 알림 메일(광고성 정보)을 받는 데 동의해야 신청할 수 있어요.",
  e_both="개인정보 수집 · 이용과 출시 알림 메일(광고성 정보) 수신에 모두 동의해야 신청할 수 있어요.",
  e_closed="사전예약이 마감됐어요.", e_busy="신청이 몰려 지금은 받지 못했어요. 잠시 뒤 다시 시도해 주세요.",
  e_net="지금 신청을 받지 못했어요. 잠시 뒤 다시 시도하거나 공식 디스코드 #help에 남겨 주세요.",
  left="{d}일 남음", left1="1일 남음", today="오늘 마감",
  prow=["공식 사이트 사전예약 (선택, 만 14세 이상, 2026년 11월 27일 23:59 한국 시간까지)", "판매자 서버", "메일 주소, 관심 플랜, 사이트 언어, 신청 시각, 광고성 정보 수신 동의 시각. 출시 알림 메일(제목이 (광고)로 시작)을 출시일에 한 번 보내는 데만 써요. 수신 거부는 <a href=\"https://systemboost-license.euning97.workers.dev/preorder/unsubscribe\">https://systemboost-license.euning97.workers.dev/preorder/unsubscribe</a>에서 언제든 할 수 있어요. 너무 많은 신청을 막으려고 IP 주소로 만든 해시값을 잠깐 쓰고, IP 주소는 저장하지 않아요.", "2027년 2월 28일에 삭제"],
)
PREJS = r'''<script>
const PRE_API = %(api)s, PRE_UNTIL = Date.parse(%(until)s), PRE = %(pre)s, PRE_PLANS = %(plans)s;
let preOpen = false, preRet = null;
const preEl = document.getElementById("pre"), preRoot = document.getElementById("root");
function preLeft() { const ms = PRE_UNTIL - Date.now(); return ms <= 0 ? -1 : Math.floor(ms / 86400000); }
// A page left open past the deadline: the bar and the nav link go instead of offering a popup that says "-1 days left".
function preLive() {
  if (preOpen && preLeft() < 0) { preOpen = false; document.querySelectorAll(".prebar,.prenav").forEach(x => x.remove()); }
  return preOpen;
}
function preSeen() { try { return !!(localStorage.getItem("sbPreSeen") || localStorage.getItem("sbPreDone")); } catch (e) { return false; } }
function preNav(t) {
  const nav = document.querySelector("header nav"); if (!nav || nav.querySelector(".prenav")) return;
  const a = document.createElement("a"); a.href = "#"; a.className = "prenav"; a.textContent = t.nav;
  a.onclick = e => { e.preventDefault(); preShow(true); }; nav.prepend(a);
}
function preRender(t) {
  const d = preLeft();
  // method="post" without action: should this script's submit handler ever fail, the browser must not put the email
  // into the address bar with a GET (GitHub Pages refuses the POST).
  preEl.innerHTML = `<div class="pbox" role="dialog" aria-modal="true" aria-labelledby="pt">
  <button class="px" type="button" aria-label="${t.close}">×</button>
  <span class="badge">${t.badge}</span> <span class="pd">${d === 0 ? t.today : d === 1 ? t.left1 : t.left.replace("{d}", d)}</span>
  <h2 id="pt">${t.title}</h2><p>${t.lead}</p><p class="pu">${t.until}<br>${t.free}</p>
  <form method="post" novalidate><label>${t.email}<input type="email" name="email" autocomplete="email" maxlength="254" required></label>
  <label>${t.plan}<select name="plan">${PRE_PLANS.map((p, i) => `<option value="${p}">${t.plans[i]}</option>`).join("")}</select></label>
  <label class="pc"><input type="checkbox" name="consent"><span>${t.consent}</span></label>
  <label class="pc"><input type="checkbox" name="adConsent"><span>${t.adconsent}</span></label>
  <div class="pm" role="status" aria-live="polite"></div>
  <div class="pb"><button class="btn" type="submit">${t.send}</button><button class="pl2" type="button">${t.later}</button></div></form></div>`;
  // Closing gives the page back (inert off) and returns focus to whatever opened the popup.
  const hide = () => { preEl.hidden = true; preRoot.inert = false; try { localStorage.setItem("sbPreSeen", "1"); } catch (e) {}
    if (preRet && preRet.isConnected) preRet.focus(); preRet = null; };
  preEl.querySelector(".px").onclick = hide; preEl.querySelector(".pl2").onclick = hide;
  // Only a click that starts on the backdrop closes it, not a text selection dragged out of the email field.
  let downOut = false;
  preEl.onpointerdown = e => { downOut = e.target === preEl; };
  preEl.onclick = e => { if (e.target === preEl && downOut) hide(); };
  const form = preEl.querySelector("form"), msg = preEl.querySelector(".pm"), btn = form.querySelector("button[type=submit]");
  const say = (s, ok) => { msg.textContent = s; msg.className = "pm" + (ok ? " ok" : " bad"); };
  form.onsubmit = async e => {
    e.preventDefault();
    const email = form.email.value.trim();
    if (!/^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/.test(email) || email.length > 254) return say(t.e_mail);
    if (!form.consent.checked && !form.adConsent.checked) return say(t.e_both);
    if (!form.consent.checked) return say(t.e_consent);
    if (!form.adConsent.checked) return say(t.e_ad);
    if (preLeft() < 0) return say(t.e_closed);
    btn.disabled = true;
    try {
      const r = await fetch(PRE_API, { method: "POST", headers: { "content-type": "application/json" },
        body: JSON.stringify({ email, plan: form.plan.value, lang: "ko", consent: true, adConsent: true }) });
      const j = await r.json().catch(() => ({}));
      if (j.ok) { say(j.already ? t.already : t.done, true); form.querySelectorAll("input,select").forEach(x => x.disabled = true);
                  btn.hidden = true; const c = preEl.querySelector(".pl2"); c.textContent = t.close; c.focus();
                  try { localStorage.setItem("sbPreDone", "1"); } catch (e) {} return; }
      say(j.error === "email" ? t.e_mail : j.error === "consent" ? t.e_both : j.error === "closed" ? t.e_closed
          : r.status === 429 || j.error === "busy" ? t.e_busy : t.e_net);
    } catch (err) { say(t.e_net); }
    btn.disabled = false;
    if (!preEl.contains(document.activeElement)) btn.focus();   // disabling the focused button had dropped focus to the page
  };
}
function preBar(t) {
  const hero = document.querySelector(".hero"); if (!hero || document.querySelector(".prebar")) return;
  const d = document.createElement("div"); d.className = "prebar";
  d.innerHTML = `<span class="badge">${t.badge}</span><span class="pt">${t.bar}</span><button type="button" class="btn">${t.go}</button>`;
  d.querySelector("button").onclick = () => preShow(true);
  hero.parentNode.insertBefore(d, hero);
}
function preShow(force) {
  if (!preLive()) return;
  if (preEl.hidden) preRet = document.activeElement;
  preRender(PRE);
  // Modal: the page behind is inert, so Tab stays in the popup and the page cannot be clicked or read meanwhile.
  preEl.hidden = false; preRoot.inert = true; const i = preEl.querySelector("input[type=email]"); if (i) i.focus();
}
document.addEventListener("keydown", e => { if (e.key === "Escape" && !preEl.hidden) preEl.querySelector(".px").click(); });
function preAfter() {
  if (!preLive()) return;
  preNav(PRE); preBar(PRE);
  if (!preEl.hidden) preShow(true);
}
if (preLeft() >= 0 && document.body.classList.contains("index")) {
  fetch(PRE_API, { cache: "no-store" }).then(r => r.ok ? r.json() : {}).then(j => {
    if (!j || j.open !== true) return;
    preOpen = true; preAfter();
    // Auto-open once per browser, but not over a popup already opened from the bar or the nav link (it would wipe the form).
    // It counts as seen when it opens by itself, so a reload or a visitor who left without closing it does not get it again.
    if (!preSeen()) setTimeout(() => { if (preEl.hidden && !preSeen()) { preShow(); try { localStorage.setItem("sbPreSeen", "1"); } catch (e) {} } }, 1200);
  }).catch(() => {});
}
</script>'''

PRECSS = '''
#pre{position:fixed;inset:0;background:rgba(0,0,0,.66);display:flex;align-items:center;justify-content:center;padding:16px;z-index:50}
#pre[hidden]{display:none}
.pbox{position:relative;background:var(--card);border:1px solid #4a2c10;border-top:4px solid var(--or);border-radius:18px;max-width:460px;width:100%;max-height:92vh;max-height:calc(100dvh - 32px);overflow:auto;padding:22px 22px 18px}
.pbox h2{margin:10px 0 6px;font-size:22px}.pbox p{margin:6px 0;color:var(--mut);font-size:15px}.pbox .pu{font-size:13px}
.pbox .pd{color:var(--or);font-size:13px;font-weight:700;margin-left:6px}
.pbox .px{position:sticky;top:0;float:right;margin:-12px -10px 0 6px;padding:0 4px 2px;background:var(--card);border:0;border-radius:8px;color:var(--mut);font-size:26px;cursor:pointer;line-height:1;z-index:1}
.pbox form{margin-top:12px;display:flex;flex-direction:column;gap:12px}
.pbox label{display:flex;flex-direction:column;gap:6px;font-size:14px;color:var(--txt)}
.pbox input[type=email],.pbox select{background:#0e0e10;border:1px solid var(--line);border-radius:10px;color:var(--txt);padding:11px 12px;font-size:15px}
.pbox input:focus,.pbox select:focus{outline:2px solid var(--or);outline-offset:1px}
.pbox .pc{flex-direction:row;align-items:flex-start;gap:10px;font-size:13px;color:var(--mut)}.pbox .pc b{color:var(--txt)}
.pbox .pc input{margin-top:3px;accent-color:var(--or);width:18px;height:18px;flex:none}
.pbox .pm{font-size:14px;min-height:1px}.pbox .pm.ok{color:#7bd88f}.pbox .pm.bad{color:#ff8a7a}
.pbox .pb{display:flex;gap:10px;align-items:center;flex-wrap:wrap}.pbox .btn{padding:12px 22px;font-size:16px;border:0;cursor:pointer}
.pbox .btn:disabled{opacity:.6;cursor:default}
.pbox .pl2{background:none;border:0;color:var(--mut);cursor:pointer;font-size:14px;text-decoration:underline}
#pre .pbox [hidden]{display:none}
.prebar{display:flex;align-items:center;gap:12px;flex-wrap:wrap;background:var(--card);border:1px solid #4a2c10;border-left:4px solid var(--or);border-radius:14px;padding:12px 16px;margin:8px 0 0}
.prebar .pt{flex:1;min-width:200px;font-size:15px}.prebar .btn{padding:9px 18px;font-size:15px;border:0;cursor:pointer}
header nav a.prenav{color:var(--or);font-weight:700}
'''
