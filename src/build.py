# Builds index.html and license.html (all languages in one file each; the page picks one).
#   python3 build.py <eula.txt> <eula.en.txt>
import base64, html, json, re, sys
sys.path.insert(0, "/home/claude/work/site")
from i18n import LANGS, S, LIC_NOTE

DC = "https://discord.gg/zUyeV2SJwm"
REPO = "DOKU-web/SystemBoost"
REL = f"https://github.com/{REPO}/releases"
LOGO = base64.b64encode(open("/home/claude/work/logo/icon_256.png", "rb").read()).decode()
FAV = base64.b64encode(open("/home/claude/work/logo/favicon.png", "rb").read()).decode()

def f(s): return s.replace("{dc}", DC).replace("{rel}", f'<a href="{REL}">github.com/{REPO}/releases</a>')
def lis(items): return "".join(f"<li>{f(x)}</li>" for x in items)

CSS = open("/home/claude/work/site/style.css").read()

def lang_select(cur_label):
    opts = "".join(f'<option value="{c}">{n}</option>' for c, n in LANGS)
    return f'<label class="lang" title="{cur_label}">🌐<select id="lang" aria-label="{cur_label}">{opts}</select></label>'

def header(s, page):
    home = "./" if page == "license" else "#top"
    nav = (f'<a href="#news">{s["nav_news"]}</a><a href="#plans">{s["nav_plans"]}</a>' if page == "index"
           else f'<a href="./#plans">{s["nav_plans"]}</a>')
    nav += f'<a href="license.html">{s["nav_license"]}</a><a class="dc" href="{DC}">{s["nav_discord"]}</a>'
    return (f'<header><a class="brand" href="{home}"><img src="icon.png" alt=""><b>System<span>Boost</span></b></a>'
            f'<nav>{nav}</nav>{lang_select(s["lang_label"])}</header>')

def index_body(L):
    s = S[L]
    rows = s["r"]
    W = '<span class="w">Windows</span>'
    WN = '<span class="w">Windows · NVIDIA</span>'
    n = '<td class="n">—</td>'
    dot, pd, ud = "<td>●</td>", '<td class="p">●</td>', '<td class="u">●</td>'
    table = f'''<tr><th>{s["th"]}</th><th>{s["free"]}</th><th class="ph">{s["prem"]}</th><th class="uh">{s["ultra"]}</th></tr>
<tr><td>{rows[0]}</td><td>{s["x2"]}</td><td class="p">{s["xp"]}</td><td class="u">{s["xu"]}</td></tr>
<tr><td>{rows[1]}</td><td>{s["qf"]}</td><td class="p">{s["qp"]}</td><td class="u">{s["qp"]}</td></tr>
<tr><td>{rows[2]}</td>{dot}{pd}{ud}</tr><tr><td>{rows[3]}</td>{dot}{pd}{ud}</tr><tr><td>{rows[4]}</td>{dot}{pd}{ud}</tr><tr><td>{rows[5]}</td>{dot}{pd}{ud}</tr>
<tr><td>{rows[6]} {W}</td>{n}{pd}{ud}</tr><tr><td>{rows[7]}</td>{n}{n}{ud}</tr><tr><td>{rows[8]} {W}</td>{n}{n}{ud}</tr><tr><td>{rows[9]} {WN}</td>{n}{n}{ud}</tr>'''
    return f'''{header(s, "index")}
<section class="hero" id="top">
  <img src="icon.png" alt="SystemBoost">
  <h1>{s["h1"]}</h1>
  <p class="lead">{s["lead"]}</p>
  <a class="btn main" href="#">{s["dl_win"]}</a>
  <div class="ver">{s["dl_sub"]}</div>
  <div class="others"><a data-k="win-setup" href="#">{s["o_win"]}</a><a data-k="win-portable" href="#">{s["o_winp"]}</a><a data-k="mac-arm" href="#">{s["o_arm"]}</a><a data-k="mac-intel" href="#">{s["o_intel"]}</a></div>
</section>
<div class="grid">
  <div class="card"><h3>{s["f1t"]}</h3><p>{s["f1d"]}</p></div><div class="card"><h3>{s["f2t"]}</h3><p>{s["f2d"]}</p></div>
  <div class="card"><h3>{s["f3t"]}</h3><p>{s["f3d"]}</p></div><div class="card"><h3>{s["f4t"]}</h3><p>{s["f4d"]}</p></div>
</div>
<section id="news" class="news">
  <div class="nh"><span class="badge">{s["nb"]}</span><h2>{s["nt"]}</h2></div>
  <div class="ng"><div class="ni"><h3>{s["n1t"]}</h3><p>{s["n1p"]}</p><ul>{lis(s["n1l"])}</ul></div>
  <div class="ni"><h3>{s["n2t"]}</h3><ul>{lis(s["n2l"])}</ul></div></div>
  <p class="nf">{f(s["nf"])}</p>
</section>
<h2 id="plans">{s["ph"]}</h2>
<p class="pl">{s["pl"]}</p>
<div class="tiers">
  <div class="tier"><div class="tn">{s["free"]}</div><div class="tp">₩0</div><div class="ts">{s["free_sub"]}</div></div>
  <div class="tier hot"><div class="tn">{s["prem"]}</div><div class="tp">₩2,900<small> {s["mo"]}</small></div><div class="ts">{s["py_p"]}</div></div>
  <div class="tier ultra"><div class="tn">{s["ultra"]}</div><div class="tp">₩8,900<small> {s["mo"]}</small></div><div class="ts">{s["py_u"]}</div></div>
</div>
<div class="card tbl"><table>{table}</table></div>
<ul class="fine"><li>{s["krw"]}</li>{lis(s["fine"])}</ul>
<h2>{s["ih"]}</h2>
<div class="note"><b>{s["iw"]}</b><ol>{lis(s["iwl"])}</ol></div>
<div class="note"><b>{s["im"]}</b><ol>{lis(s["iml"])}</ol></div>
<div class="note"><b>{s["kh"]}</b>{"".join(f"<p>{x}</p>" for x in s["kl"])}</div>
<div class="discord"><div class="t"><h2>{s["dch"]}</h2><p>{s["dcp"]}</p></div><a class="btn dcb" href="{DC}">{s["dcb"]}</a></div>
<footer>SystemBoost · DOKKU · <a href="license.html">{s["ft1"]}</a> · <a href="{DC}">{s["nav_discord"]}</a><br>{f(s["ft2"])}<br>{s["ft3"]}</footer>'''

# ---------------------------------------------------------------- EULA
def parse_eula(path):
    lines = open(path).read().splitlines()
    head, secs, cur, i = [], [], None, 0
    while not lines[i].startswith("## "):
        if lines[i].strip(): head.append(lines[i])
        i += 1
    for ln in lines[i:]:
        if ln.startswith("## "): cur = {"t": ln[3:].strip(), "l": []}; secs.append(cur)
        elif cur and ln.strip(): cur["l"].append(ln)
    return head, secs

def eula_html(head, secs, rev_label, art3):
    out, toc = [], []
    for k, s in enumerate(secs):
        toc.append(f'<li><a href="#a{k+1}">{html.escape(s["t"])}</a></li>')
        body = []
        for ln in s["l"]:
            imp = ln.startswith("! "); t = html.escape((ln[2:] if imp else ln).strip())
            m = re.match(r"(\d+)\.\s+(.*)", t)
            changed = k == 2 and m and m.group(1) == "3"
            if m: t = f'<span class="no">{m.group(1)}.</span> {m.group(2)}'
            if changed: t += f' <span class="rev">{rev_label}</span>'
            cls = "chg" if changed else ("imp" if imp else ("cont" if not m and body else ""))
            body.append(f'<p class="{cls}">{t}</p>' if cls else f"<p>{t}</p>")
        out.append(f'<h2 id="a{k+1}">{html.escape(s["t"])}</h2>\n' + "\n".join(body))
    return toc, "\n".join(out)

LT = {
 "ko": dict(h1="사용권 계약 · 개인정보", sub='앱을 처음 실행할 때 보여 주는 사용권 계약과 같은 내용이에요. 앱의 설정 → "사용권 계약 보기"에서도 볼 수 있어요.',
   revh="계약 개정 안내", revp='카드 구독 결제를 위해 <a href="#a3">제3조 제3항</a>이 바뀌어요. 바뀐 조항은 <b>2026년 12월 8일</b>부터 시행하고, 카드 구독도 그날부터 팔아요. 그 전까지 산 키와 계좌이체 등 직접 결제로 산 키는 자동으로 결제되지 않아요. 0.8.8로 업데이트하면 바뀐 계약에 한 번 다시 동의해요.',
   rev="2026년 12월 8일 개정", toc="목차", imp="표시가 있는 조항(주황 테두리)은 꼭 읽어 주세요.",
   priv="개인정보 처리 안내", oss="오픈소스 라이선스", contact="판매자 정보와 문의",
   privsum="사용권 계약 제8조를 표로 정리했어요. 보내는 정보는 아래가 전부예요.",
   cols=["언제", "어디로", "보내는 것", "보관"],
   rows=[["키 확인 (앱을 켤 때)", "판매자 라이선스 서버", "키의 식별 번호 (키 원문 아님)", "키 관리 동안"],
         ["사용 통계 (켜져 있는 동안, 설정에서 끌 수 있음)", "판매자 서버", "무작위 설치 번호, 앱 버전, 운영체제, 플랜, 프레임 생성 배수, 원클릭 게임 부스트 사용 여부. 서버가 접속 국가를 기록해요. 키·메일·게임 이름·하드웨어 정보는 보내지 않고, IP 주소는 저장하지 않아요.", "마지막 연결 뒤 90일"],
         ["결제 주문", "판매자 라이선스 서버", "플랜, 주문 코드, 사용자가 직접 적은 입금자명과 메모", "키 관리와 환불 처리 동안, 법정 보관 기간"],
         ["카드 구독 결제 (2026년 12월 8일부터)", "Lemon Squeezy 결제창 → 판매자 서버", "카드 정보는 Lemon Squeezy 결제창에만 입력하고, 판매자는 받지도 저장하지도 않아요. 판매자 서버는 결제 메일, 주문·구독 번호, 결제·해지 상태를 받아 키 발급과 연장에 써요.", "키 관리와 환불 처리 동안, 법정 보관 기간"],
         ["추천 코드 입력", "판매자 서버", "코드, 무작위 기기 번호", "90일"],
         ["회원 가입 (선택, 만 14세 이상)", "Supabase 데이터베이스", "아이디, 암호화한 비밀번호와 복구 코드, 가입·마지막 로그인·동의 시각, 초대 코드와 초대 관계. 메일은 받지 않아요.", "탈퇴하면 바로 삭제"],
         ["가입·로그인 남용 방지", "판매자 서버", "접속 IP를 되돌릴 수 없게 바꾼 값", "1시간"],
         ["업데이트 확인", "GitHub", "없음 (최신 버전 목록만 받아요)", "—"],
         ["맥에서 윈도우 게임 환경 설정", "GitHub (Wine, DXVK), 고른 경우 Steam", "없음 (필요한 파일만 내려받아요)", "—"]],
   privfoot=f'정보를 지우거나 확인하고 싶으면 <a href="{DC}">공식 디스코드</a> #help로 요청해 주세요. 회원 정보는 앱의 계정 메뉴에서 탈퇴하면 바로 지워져요.',
   ossp="SystemBoost가 쓰거나 내려받는 다른 사람의 소프트웨어와 그 라이선스예요. 앱 설정 화면의 \"라이선스 전문 보기\"에서 전문을 볼 수 있어요.",
   osscols=["구성 요소", "라이선스", "쓰는 곳"],
   ossuse=["앱 화면", "설치 파일에 함께 들어 있음", "설치와 자동 업데이트", "Windows NVIDIA 드라이버 설정", "맥의 윈도우 게임 기능. 원본 그대로 내려받아 별도 프로그램으로 실행해요.", "맥의 윈도우 게임 기능 (내려받음)", "Wine 빌드 안에 포함", "배포하지 않아요. 사용자가 Apple에서 직접 받아 연결해요."],
   own="SystemBoost 자체 코드(앱, 프레임 생성 엔진, 셰이더)는 DOKKU가 직접 만든 것이에요.",
   contactp=f'판매자: DOKKU<br>라이선스, 키, 시험 키, 결제, 청약철회·환불, 개인정보 문의는 <a href="{DC}">공식 디스코드</a>의 #help에 글을 남겨 주세요. 키 전체를 공개 채널에 올리지 마세요. 운영진은 DM으로 먼저 연락하지 않아요.'),
 "en": dict(h1="License Agreement · Privacy", sub='This is the same agreement the app shows on first start. You can read it again in the app under Settings → "View license agreement".',
   revh="Upcoming revision", revp='<a href="#a3">Article 3, Paragraph 3</a> changes for card subscriptions. The revised paragraph takes effect on <b>December 8, 2026</b>, and card subscriptions go on sale that day. Keys bought before then, and keys bought by bank transfer or other direct payment, are never charged automatically. After updating to 0.8.8 you accept the revised agreement once.',
   rev="Revised Dec 8, 2026", toc="Contents", imp="Please read the marked articles (orange border) carefully.",
   priv="Privacy summary", oss="Open-source licenses", contact="Seller and contact",
   privsum="Article 8 of the agreement as a table. This is everything the app sends.",
   cols=["When", "Where", "What is sent", "Kept for"],
   rows=[["Key check (when the app starts)", "Seller's license server", "An identifier derived from the key (not the key itself)", "While the key is managed"],
         ["Usage statistics (while running; can be turned off in Settings)", "Seller's server", "Random install ID, app version, OS, plan, frame-generation multiplier, whether one-click game boost is used. The server records the country. No key, email, game names or hardware info; IP addresses are not stored.", "90 days after last contact"],
         ["Payment order", "Seller's license server", "Plan, order code, and the payer name and memo you type", "While the key and refunds are handled, plus legally required periods"],
         ["Card subscription (from Dec 8, 2026)", "Lemon Squeezy checkout → seller's server", "Card details are entered only in Lemon Squeezy's checkout; the seller never receives or stores them. The seller's server receives the payment email, order/subscription numbers and payment/cancellation status to issue and extend keys.", "While the key and refunds are handled, plus legally required periods"],
         ["Referral code", "Seller's server", "The code and a random device ID", "90 days"],
         ["Account sign-up (optional, 14+)", "Supabase database", "Username, hashed password and recovery code, sign-up/last login/consent times, invite code and invite relations. No email.", "Deleted immediately when you delete the account"],
         ["Sign-up / login abuse prevention", "Seller's server", "A one-way hash of the connecting IP", "1 hour"],
         ["Update check", "GitHub", "Nothing (only fetches the list of versions)", "—"],
         ["Windows games on Mac setup", "GitHub (Wine, DXVK), Steam if chosen", "Nothing (downloads the needed files only)", "—"]],
   privfoot=f'To check or delete your data, ask in #help on the <a href="{DC}">official Discord</a>. Account data is deleted immediately when you delete your account in the app.',
   ossp="Other people's software that SystemBoost uses or downloads, and its licenses. Full texts are in the app under Settings → \"View full licenses\".",
   osscols=["Component", "License", "Used for"],
   ossuse=["App UI", "Bundled with the installer", "Installer and automatic updates", "Windows NVIDIA driver settings", "Windows games on Mac. Downloaded unmodified and run as a separate program.", "Windows games on Mac (downloaded)", "Included in the Wine build", "Not distributed. You download it from Apple and point the app to it."],
   own="SystemBoost's own code (app, frame-generation engine, shaders) is original work by DOKKU.",
   contactp=f'Seller: DOKKU<br>For license, key, trial key, payment, withdrawal/refund and privacy questions, post in #help on the <a href="{DC}">official Discord</a>. Never post your full key in a public channel. Staff never DM you first.'),
}
OSS = [('<a href="https://github.com/AvaloniaUI/Avalonia">Avalonia</a>', "MIT"), ('<a href="https://github.com/dotnet/runtime">.NET</a>', "MIT"),
       ('<a href="https://github.com/velopack/velopack">Velopack</a>', "MIT"), ('<a href="https://github.com/NVIDIA/nvapi">NVAPI</a>', "MIT"),
       ('<a href="https://gitlab.winehq.org/wine/wine">Wine</a> (<a href="https://github.com/Gcenx/macOS_Wine_builds">Gcenx</a>)', "LGPL-2.1+"),
       ('<a href="https://github.com/Gcenx/DXVK-macOS">DXVK-macOS</a>', "zlib"), ('<a href="https://github.com/KhronosGroup/MoltenVK">MoltenVK</a>', "Apache-2.0"),
       ('<a href="https://developer.apple.com/games/">Apple Game Porting Toolkit</a>', "Apple")]

def license_body(L, eulas):
    s = S[L]; base = "ko" if L == "ko" else "en"; t = LT[base]
    head, secs = eulas[base]
    toc, body = eula_html(head, secs, t["rev"], 3)
    rows = "".join("<tr>" + "".join(f"<td>{c}</td>" for c in r) + "</tr>" for r in t["rows"])
    oss = "".join(f"<tr><td>{a}</td><td>{b}</td><td>{u}</td></tr>" for (a, b), u in zip(OSS, t["ossuse"]))
    note = f'<div class="langnote">{LIC_NOTE[L]}</div>' if L in LIC_NOTE else ""
    return f'''{header(s, "license")}
<h1>{t["h1"]}</h1>
<div class="date">{t["sub"]}</div>
{note}
<div class="revbox"><b>{t["revh"]}</b><br>{t["revp"]}</div>
<div class="toc"><b>{t["toc"]}</b><ol>{"".join(toc)}<li><a href="#privacy">{t["priv"]}</a></li><li><a href="#oss">{t["oss"]}</a></li><li><a href="#contact">{t["contact"]}</a></li></ol></div>
<p class="note">{"<br>".join(html.escape(h) for h in head)}<br>{t["imp"]}</p>
{body}
<h2 id="privacy">{t["priv"]}</h2>
<div class="sum">{t["privsum"]}</div>
<div class="tbl"><table><tr>{"".join(f"<th>{c}</th>" for c in t["cols"])}</tr>{rows}</table></div>
<p>{t["privfoot"]}</p>
<h2 id="oss">{t["oss"]}</h2>
<p>{t["ossp"]}</p>
<div class="tbl"><table><tr>{"".join(f"<th>{c}</th>" for c in t["osscols"])}</tr>{oss}</table></div>
<p>{t["own"]}</p>
<h2 id="contact">{t["contact"]}</h2>
<p>{t["contactp"]}</p>
<footer>SystemBoost · DOKKU · <a href="./">SystemBoost</a> · <a href="{DC}">{s["nav_discord"]}</a></footer>'''

SCRIPT = '''<script>
const LANGS = %s, META = %s;
function pick() {
  const q = new URLSearchParams(location.search).get("lang");
  if (q && LANGS.includes(q)) return q;
  try { const s = localStorage.getItem("sbLang"); if (s && LANGS.includes(s)) return s; } catch (e) {}
  for (const l of (navigator.languages || [navigator.language || "en"])) {
    const p = String(l).toLowerCase().slice(0, 2); if (LANGS.includes(p)) return p;
  }
  return "en";
}
function show(lang, keepScroll) {
  const t = document.getElementById("t-" + lang);
  const root = document.getElementById("root");
  root.innerHTML = ""; root.appendChild(t.content.cloneNode(true));
  document.documentElement.lang = lang === "zh" ? "zh-CN" : lang;
  document.title = META[lang].title;
  const d = document.querySelector('meta[name="description"]'); if (d) d.content = META[lang].desc;
  const sel = document.getElementById("lang"); sel.value = lang;
  sel.onchange = () => { try { localStorage.setItem("sbLang", sel.value); } catch (e) {}
    const u = new URL(location.href); u.searchParams.set("lang", sel.value); history.replaceState(null, "", u); show(sel.value, true); };
  if (typeof afterShow === "function") afterShow(lang);
  if (!keepScroll && location.hash) { const el = document.getElementById(location.hash.slice(1)); if (el) el.scrollIntoView(); }
}
show(pick());
</script>'''

DLJS = '''<script>
const REPO = "%s";
const FILES = { "win-setup": "SystemBoost-Windows-Setup.exe", "win-portable": "SystemBoost-Windows-Portable.zip",
                "mac-arm": "SystemBoost-macOS-AppleSilicon.pkg", "mac-intel": "SystemBoost-macOS-Intel.pkg" };
const DLKEY = %s;
const url = k => `https://github.com/${REPO}/releases/latest/download/${FILES[k]}`;
let osKey = "win-setup";
if (/Mac/i.test(navigator.platform) || /Mac OS X/i.test(navigator.userAgent)) {
  osKey = "mac-arm";
  try { const gl = document.createElement("canvas").getContext("webgl"); const r = gl && gl.getExtension("WEBGL_debug_renderer_info");
        if (r && /Intel/i.test(gl.getParameter(r.UNMASKED_RENDERER_WEBGL))) osKey = "mac-intel"; } catch (e) {}
}
function afterShow(lang) {
  document.querySelectorAll("[data-k]").forEach(a => a.href = url(a.dataset.k));
  const m = document.querySelector(".btn.main"); if (m) { m.href = url(osKey); m.textContent = DLKEY[lang][osKey]; }
}
</script>'''

def page(kind, bodies, meta, extra=""):
    temps = "\n".join(f'<template id="t-{c}">{b}</template>' for c, b in bodies.items())
    return f'''<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>{html.escape(meta["en"]["title"])}</title>
<meta name="description" content="{html.escape(meta["en"]["desc"])}">
<link rel="icon" type="image/png" href="favicon.png">
<style>{CSS}</style>
</head>
<body class="{kind}">
<div class="wrap" id="root"></div>
{temps}
{extra}
{SCRIPT % (json.dumps([c for c, _ in LANGS]), json.dumps(meta, ensure_ascii=False))}
</body>
</html>'''

if __name__ == "__main__":
    eulas = {"ko": parse_eula(sys.argv[1]), "en": parse_eula(sys.argv[2])}
    eulas["en"] = (eulas["en"][0][1:], eulas["en"][1])  # first line is the "reference translation" note, kept below
    en_note = open(sys.argv[2]).read().splitlines()[0]
    LT["en"]["sub"] = html.escape(en_note) + " " + LT["en"]["sub"]
    meta = {c: {"title": S[c]["title"], "desc": S[c]["desc"]} for c, _ in LANGS}
    dlkey = {c: {"win-setup": S[c]["dl_win"], "win-portable": S[c]["dl_winp"], "mac-arm": S[c]["dl_mac_arm"], "mac-intel": S[c]["dl_mac_intel"]} for c, _ in LANGS}
    idx = page("index", {c: index_body(c) for c, _ in LANGS}, meta, DLJS % (REPO, json.dumps(dlkey, ensure_ascii=False)))
    lmeta = {c: {"title": ("SystemBoost 사용권 계약 · 개인정보" if c == "ko" else "SystemBoost License Agreement · Privacy"), "desc": S[c]["desc"]} for c, _ in LANGS}
    lic = page("license", {c: license_body(c, eulas) for c, _ in LANGS}, lmeta)
    open("/home/claude/work/site/index.html", "w").write(idx)
    open("/home/claude/work/site/license.html", "w").write(lic)
    print("index", len(idx), "license", len(lic))
