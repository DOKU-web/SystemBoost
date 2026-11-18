# Builds index.html and license.html (all languages in one file each; the page picks one) in the site root,
# the folder above this one. The agreement text comes from the app's own files, never from a copy here:
#   python -X utf8 -I src/build.py <app>/Assets/eula.txt <app>/Assets/eula.en.txt
# e.g. python -X utf8 -I src/build.py C:/dev/SystemBoost-preview/app/SystemBoost.App/Assets/eula.txt C:/dev/SystemBoost-preview/app/SystemBoost.App/Assets/eula.en.txt
import html, json, os, re, sys
HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, HERE)
from i18n import LANGS, S, LIC_NOTE
from i18n_plans import apply as apply_plans, BUNDLES
apply_plans(S)

def read(path): return open(path, encoding="utf-8").read()

DC = "https://discord.gg/zUyeV2SJwm"
# The GitHub repository whose releases the download buttons, the "official downloads" line and the release-notes
# link point at. It becomes "DOKU-web/SystemBoost-releases" when the release-only repository goes live:
# change this one line and rebuild.
RELEASES_REPO = "DOKU-web/SystemBoost"
REL = f"https://github.com/{RELEASES_REPO}/releases"

def f(s): return (s.replace("{dc}", DC).replace("{rel_latest}", f"{REL}/latest")
                   .replace("{rel}", f'<a href="{REL}">github.com/{RELEASES_REPO}/releases</a>'))
def lis(items): return "".join(f"<li>{f(x)}</li>" for x in items)

# Front-page notice of the agreement revision effective 2027-01-01: the license.html anchor of each line of S[..]["ai"],
# in the order of the agreement's "개정 시행일: 2027년 1월 1일 (...)" header line: Art. 1, 3(2), 3(3), 3(10), 3(11),
# 6-2 (#oc), 8, 9(2)(5), 10(3), 12-2.
AGR_ANCHORS = ["a1", "a3", "a3", "a3", "a3", "oc", "a8", "a9", "a10", "a12-2"]

CSS = read(os.path.join(HERE, "style.css"))
# Rules only the front page uses (the agreement change notice) come from style.index.css and go right after the news
# box rules, so license.html keeps the shared stylesheet as it is.
_NEWS_CSS_END = ".nf{color:var(--mut);font-size:13px;margin:14px 0 0}\n"
if CSS.count(_NEWS_CSS_END) != 1: raise SystemExit("style.css: the .nf rule (end of the news box rules) must appear once")
CSS_INDEX = CSS.replace(_NEWS_CSS_END, _NEWS_CSS_END + read(os.path.join(HERE, "style.index.css")))

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
    NV = '<span class="w">NVIDIA</span>'   # GPU overclock / power limit: NVIDIA cards only
    n = '<td class="n">—</td>'
    dot, pd, ud, bd = "<td>●</td>", '<td class="p">●</td>', '<td class="u">●</td>', '<td class="b">●</td>'
    table = f'''<tr><th>{s["th"]}</th><th>{s["free"]}</th><th class="ph">{s["prem"]}</th><th class="uh">{s["ultra"]}</th><th class="bh">{s["biz"]}</th></tr>
<tr><td>{rows[0]}</td><td>{s["x2"]}</td><td class="p">{s["xp"]}</td><td class="u">{s["xu"]}</td><td class="b">{s["xu"]}</td></tr>
<tr><td>{rows[1]}</td><td>{s["qf"]}</td><td class="p">{s["qp"]}</td><td class="u">{s["qp"]}</td><td class="b">{s["qp"]}</td></tr>'''
    table += "".join(f"<tr><td>{rows[i]}</td>{dot}{pd}{ud}{bd}</tr>" for i in (2, 3, 4, 5))
    table += f"<tr><td>{rows[6]}</td>{n}{pd}{ud}{bd}</tr><tr><td>{rows[7]}</td>{n}{n}{ud}{bd}</tr><tr><td>{rows[8]}</td>{n}{n}{ud}{bd}</tr><tr><td>{rows[9]} {NV}<a class='ocn' href='license.html#oc'>{s['ocn']}</a></td>{n}{n}{ud}{bd}</tr>"
    table += "".join(f"<tr><td>{r}</td>{n}{n}{n}{bd}</tr>" for r in s["rb"])
    name = {"premium": ("prem", "p"), "ultra": ("ultra", "u"), "biz": ("biz", "b")}
    won = lambda v: f"₩{v:,}"
    btable = f'<tr><th></th><th>{s["b5"]}</th><th>{s["b10"]}</th></tr>' + "".join(
        f'<tr><td class="{name[t][1]}">{s[name[t][0]]}</td><td>{won(t5)} <span class="w">{s["each"]} {won(e5)}</span></td>'
        f'<td>{won(t10)} <span class="w">{s["each"]} {won(e10)}</span></td></tr>' for t, t5, e5, t10, e10 in BUNDLES)
    # The news box is a list of cards (nc): (title, paragraph or "", list items or []).
    cards = "\n  ".join(f'<div class="ni"><h3>{t}</h3>' + (f"<p>{f(p)}</p>" if p else "") + (f"<ul>{lis(l)}</ul>" if l else "")
                        + "</div>" for t, p, l in s["nc"])
    # The agreement change notice (Article 13: posted on the sales page 30 days before the change takes effect):
    # one line per changed article (ai), each linking to its anchor in license.html.
    if len(s["ai"]) != len(AGR_ANCHORS): raise SystemExit(f"{L}: the agreement notice needs {len(AGR_ANCHORS)} lines")
    agr = "".join(f'<li><a href="license.html#{a}">{lab}</a>{f(txt)}</li>' for a, (lab, txt) in zip(AGR_ANCHORS, s["ai"]))
    return f'''{header(s, "index")}
<section class="hero" id="top">
  <img src="icon.png" alt="SystemBoost">
  <h1>{s["h1"]}</h1>
  <p class="lead">{s["lead"]}</p>
  <a class="btn main" href="#">{s["dl_win"]}</a>
  <div class="ver">{s["dl_sub"]}</div>
  <div class="others"><a data-k="win-setup" href="#">{s["o_win"]}</a><a data-k="win-portable" href="#">{s["o_winp"]}</a></div>
</section>
<div class="grid">
  <div class="card"><h3>{s["f1t"]}</h3><p>{s["f1d"]}</p></div><div class="card"><h3>{s["f2t"]}</h3><p>{s["f2d"]}</p></div>
  <div class="card"><h3>{s["f3t"]}</h3><p>{s["f3d"]}</p></div><div class="card"><h3>{s["f4t"]}</h3><p>{s["f4d"]}</p></div>
</div>
<section id="news" class="news">
  <div class="nh"><span class="badge">{s["nb"]}</span><h2>{s["nt"]}</h2></div>
  <p class="nd">{s["nd"]}</p>
  <div class="ng">{cards}</div>
  <p class="nf">{f(s["nf"])}</p>
</section>
<section id="agreement" class="agr">
  <div class="nh"><span class="badge">{s["ab"]}</span><h2>{s["ah"]}</h2></div>
  <p>{f(s["ap1"])}</p>
  <ul>{agr}</ul>
  <p>{f(s["ap2"])}</p>
</section>
<h2 id="plans">{s["ph"]}</h2>
<p class="pl">{s["pl"]}</p>
<div class="tiers">
  <div class="tier"><div class="tn">{s["free"]}</div><div class="tp">₩0</div><div class="ts">{s["free_sub"]}</div></div>
  <div class="tier hot"><div class="tn">{s["prem"]}</div><div class="tp">₩2,900<small> {s["mo"]}</small></div><div class="ts">{s["py_p"]}</div></div>
  <div class="tier ultra"><div class="tn">{s["ultra"]}</div><div class="tp">₩8,900<small> {s["mo"]}</small></div><div class="ts">{s["py_u"]}</div></div>
  <div class="tier biz"><div class="tn">{s["biz"]}</div><div class="tp">₩12,900<small> {s["mo"]}</small></div><div class="ts">{s["py_b"]}</div></div>
</div>
<div class="card tbl"><table>{table}</table></div>
<h3 class="bt">{s["bh"]}</h3>
<div class="card tbl"><table class="bund">{btable}</table></div>
<p class="pl">{s["bn"]}</p>
<ul class="fine"><li>{s["krw"]}</li>{lis(s["fine"])}</ul>
<h2>{s["ih"]}</h2>
<div class="note"><b>{s["iw"]}</b><ol>{lis(s["iwl"])}</ol></div>
<div class="note"><b>{s["kh"]}</b>{"".join(f"<p>{x}</p>" for x in s["kl"])}</div>
<div class="discord"><div class="t"><h2>{s["dch"]}</h2><p>{s["dcp"]}</p></div><a class="btn dcb" href="{DC}">{s["dcb"]}</a></div>
<footer>SystemBoost · DOKKU · <a href="license.html">{s["ft1"]}</a> · <a href="{DC}">{s["nav_discord"]}</a><br>{f(s["ft2"])}<br>{s["ft3"]}<br><span class="tm">{s["ft4"]}</span></footer>'''

# ---------------------------------------------------------------- trademarks
# The footer line ft4 names exactly the brands in TM_NAMES. The build stops if the visible text of index.html
# mentions a brand the line leaves out, or the line names a brand the page no longer mentions; then fix TM_NAMES and
# ft4 in all languages together. Full Korean wording with every brand we have used:
#   NVIDIA, GeForce, DLSS는 NVIDIA Corporation의, AMD, Radeon은 Advanced Micro Devices, Inc.의,
#   Intel은 Intel Corporation의, Windows는 Microsoft Corporation의 상표예요. SystemBoost는 이 회사들과 제휴하거나 승인받지 않았어요.
TM_NAMES = ["NVIDIA", "AMD", "Radeon", "Intel", "Windows"]
TM_WORDS = {"NVIDIA": ["NVIDIA"], "GeForce": ["GeForce"], "DLSS": ["DLSS"], "AMD": ["AMD"], "Radeon": ["Radeon", "라데온"],
            "Intel": ["Intel", "인텔"], "Windows": ["Windows", "윈도우"]}

def visible_text(markup):
    markup = re.sub(r"<(script|style)\b.*?</\1>", " ", markup, flags=re.S)
    return html.unescape(re.sub(r"<[^>]+>", " ", markup))

def check_trademarks(bodies):
    for L, body in bodies.items():
        text = visible_text(body.replace(S[L]["ft4"], ""))
        used = [b for b, words in TM_WORDS.items() if any(w in text for w in words)]
        named = [b for b in TM_WORDS if b in TM_NAMES]
        if used != named:
            raise SystemExit(f"trademark line ({L}) names {named} but the page mentions {used}: update TM_NAMES and ft4")
        missing = [b for b in TM_NAMES if b not in S[L]["ft4"]]
        if missing: raise SystemExit(f"trademark line ({L}) is missing {missing}")

# ---------------------------------------------------------------- EULA
def parse_eula(path):
    lines = read(path).splitlines()
    head, secs, cur, i = [], [], None, 0
    while not lines[i].startswith("## "):
        if lines[i].strip(): head.append(lines[i])
        i += 1
    for ln in lines[i:]:
        if ln.startswith("## "): cur = {"t": ln[3:].strip(), "l": []}; secs.append(cur)
        elif cur and ln.strip(): cur["l"].append(ln)
    return head, secs

# Which revision changed what, as listed in the agreement's own header lines (개정 시행일 ...). Only these marks are
# added; the agreement text itself is rendered line by line from eula.txt / eula.en.txt as it is.
R8, R9, R27 = "2026-12-08", "2026-12-09", "2027-01-01"
# (article number, paragraph) -> revisions. The article number is "2" for 제2조 / Article 2 and "6-2" for
# 제6조의2 / Article 6-2, so an inserted article never moves the others' marks or anchors.
REVS = {("2", "1"): (R9,),                                                    # Art. 2(1)
        ("3", "2"): (R9, R27), ("3", "3"): (R8, R27), ("3", "5"): (R9,), ("3", "8"): (R9,), ("3", "9"): (R9,),
        ("3", "10"): (R27,), ("3", "11"): (R27,),                             # Art. 3
        ("6", "1"): (R9,),                                                    # Art. 6(1)
        ("9", "2"): (R27,), ("9", "5"): (R27,),                               # Art. 9(2), 9(5)
        ("10", "3"): (R27,),                                                  # Art. 10(3)
        ("11", "2"): (R9,)}                                                   # Art. 11(2)
# whole articles: Art. 1 (sales page, Windows program), Art. 6-2 (overclocking, new), Art. 8 (personal information),
# Art. 12-2 (AMD components, new)
REV_ARTICLES = {"1": (R27,), "6-2": (R27,), "8": (R27,), "12-2": (R27,)}

def art_no(title):
    # "제6조의2 (...)" or "Article 6-2 (...)" -> "6-2"; "제7조 (...)" or "Article 7 (...)" -> "7"
    m = re.match(r"제(\d+)조(?:의(\d+))?\s|Article (\d+)(?:-(\d+))?\s", title + " ")
    if not m: raise SystemExit(f"not an article heading: {title}")
    n, sub = (m.group(1), m.group(2)) if m.group(1) else (m.group(3), m.group(4))
    return n + ("-" + sub if sub else "")

# Article 6-2 is linked from the plan table and the app as #oc; every other article is #a<number>.
def art_id(no):
    return "oc" if no == "6-2" else "a" + no

def eula_html(secs, rev_label):
    out, toc, seen = [], [], set()
    nos = [art_no(s["t"]) for s in secs]
    if len(set(nos)) != len(nos): raise SystemExit(f"article numbers repeat: {nos}")
    if set(REV_ARTICLES) - set(nos): raise SystemExit(f"revision marks point at missing articles: {sorted(set(REV_ARTICLES) - set(nos))}")
    for no, s in zip(nos, secs):
        toc.append(f'<li><a href="#{art_id(no)}">{html.escape(s["t"])}</a></li>')
        body = []
        for ln in s["l"]:
            imp = ln.startswith("! "); t = html.escape((ln[2:] if imp else ln).strip())
            m = re.match(r"(\d+)\.\s+(.*)", t)
            changed = REVS.get((no, m.group(1))) if m else None
            if changed: seen.add((no, m.group(1)))
            if m: t = f'<span class="no">{m.group(1)}.</span> {m.group(2)}'
            if changed: t += "".join(f' <span class="rev">{rev_label[r]}</span>' for r in changed)
            cls = "chg" if changed else ("imp" if imp else ("cont" if not m and body else ""))
            body.append(f'<p class="{cls}">{t}</p>' if cls else f"<p>{t}</p>")
        marks = "".join(f' <span class="rev">{rev_label[r]}</span>' for r in REV_ARTICLES.get(no, ()))
        out.append(f'<h2 id="{art_id(no)}">{html.escape(s["t"])}{marks}</h2>\n' + "\n".join(body))
    if seen != set(REVS): raise SystemExit(f"revision marks point at missing paragraphs: {sorted(set(REVS) - seen)}")
    return toc, "\n".join(out)

LT = {
 "ko": dict(h1="사용권 계약 · 개인정보", sub='앱을 처음 실행할 때 보여 주는 사용권 계약과 같은 내용이에요. 앱의 설정 → "사용권 계약 보기"에서도 볼 수 있어요.',
   revh="계약 개정 안내", revp='계약이 세 번 바뀌어요. 바뀐 조항에는 개정 날짜가 붙어 있어요.<br>• <b>2026년 12월 8일</b>: 카드 구독 결제를 위해 <a href="#a3">제3조 제3항</a>이 바뀌고, 카드 구독도 그날부터 팔아요. 그 전까지 산 키와 계좌이체 등 직접 결제로 산 키는 자동으로 결제되지 않아요.<br>• <b>2026년 12월 9일</b>: 산업 플랜, 5대 · 10대 묶음 키, 플랜 이름에 맞춰 <a href="#a2">제2조 제1항</a>, <a href="#a3">제3조 제2항 · 제5항 · 제8항 · 제9항</a>, <a href="#a6">제6조 제1항</a>, <a href="#a11">제11조 제2항</a>이 바뀌어요. 프리미엄 플랜은 이름만 프로로 바뀌고 기능과 가격은 그대로예요. 묶음 키는 그날부터 팔아요.<br>• <b>2027년 1월 1일</b>: <a href="#a1">제1조</a>의 판매 페이지와 Windows용 프로그램, <a href="#a3">제3조 제2항 · 제3항의 해지와 환급 · 제10항 · 제11항</a>, <a href="#a8">제8조</a> 개인정보, <a href="#a9">제9조 제2항 · 제5항</a>, <a href="#a10">제10조 제3항</a>이 바뀌고, <a href="#oc">제6조의2</a>(오버클럭 및 하드웨어 조정)와 <a href="#a12-2">제12조의2</a>(AMD 구성 요소)가 새로 생겨요. 처음에는 꺼져 있는 사용 통계(제8조 제3항)도 여기에 들어 있어요.<br>앱을 업데이트하면 바뀐 계약에 한 번 다시 동의해요.',
   rev="2026년 12월 8일 개정", rev9="2026년 12월 9일 개정", rev27="2027년 1월 1일 개정", toc="목차", imp="표시가 있는 조항(주황 테두리)은 꼭 읽어 주세요.",
   priv="개인정보 처리 안내", oss="오픈소스 라이선스", contact="판매자 정보와 문의",
   privsum="사용권 계약 제8조를 표로 정리했어요. 보내는 정보는 아래가 전부예요.",
   cols=["언제", "어디로", "보내는 것", "보관"],
   rows=[["키 확인 (앱을 켤 때)", "판매자 라이선스 서버", "키의 식별 번호 (키 원문 아님)", "키 관리 동안"],
         ["사용 통계 (선택, 처음에는 꺼져 있음. 켠 동안에만 보내고, 설정에서 바꿀 수 있음)", "판매자 서버", "무작위 설치 번호, 앱 버전, 운영체제, 플랜, 프레임 생성 배수, 원클릭 게임 부스트 사용 여부. 서버가 접속 국가를 기록해요. 키·메일·게임 이름·하드웨어 정보는 보내지 않고, IP 주소는 저장하지 않아요.", "마지막 연결 뒤 90일"],
         ["결제 주문", "판매자 라이선스 서버", "플랜, 주문 코드, 사용자가 직접 적은 입금자명과 메모", "키 관리와 환불 처리 동안, 법정 보관 기간"],
         ["카드 구독 결제 (2026년 12월 8일부터)", "Lemon Squeezy 결제창 → 판매자 서버", "카드 정보는 Lemon Squeezy 결제창에만 입력하고, 판매자는 받지도 저장하지도 않아요. 판매자 서버는 결제 메일, 주문·구독 번호, 결제·해지 상태를 받아 키 발급과 연장에 써요.", "키 관리와 환불 처리 동안, 법정 보관 기간"],
         ["추천 코드 입력", "판매자 서버", "코드, 무작위 기기 번호", "90일"],
         ["회원 가입 (선택, 만 14세 이상, 아이디나 메일 주소로)", "Supabase 데이터베이스", "아이디, 메일 주소(메일로 가입하거나 계정에 더한 경우), 해시값으로 바꾼 비밀번호와 복구 코드, 가입·마지막 로그인·동의 시각, 초대 코드와 초대 관계, 로그인한 기기(기기 이름, 접속 국가), 계정에 연결한 키, 2단계 인증(켠 경우, 비밀 값은 암호화), 오버클럭 안내의 동의 기록", "탈퇴하면 바로 삭제 (백업에 최대 7일)"],
         ["회원 보안 기록", "Supabase 데이터베이스", "로그인, 비밀번호 변경, 2단계 인증 변경 같은 일의 시각과 그때의 IP 주소, 접속 국가, 기기 이름", "30일"],
         ["계정 복구 요청 (비밀번호와 복구 코드를 모두 잃었을 때)", "Supabase 데이터베이스", "요청과 직접 적은 확인 정보(주문 코드, 결제한 날, 금액 등)", "90일"],
         ["메일 (가입 확인, 비밀번호 재설정, 로그인 알림, 키 다시 받기)", "Resend(미국 회사)를 거쳐 받는 사람에게", "받는 메일 주소와 메일 내용. 키 다시 받기에 적은 메일 주소는 저장하지 않아요. 광고 메일은 보내지 않아요.", "Resend가 정한 기간"],
         ["가입·로그인·메일 요청 남용 방지", "판매자 서버", "접속 IP를 되돌릴 수 없게 바꾼 값", "1시간"],
         ["업데이트 확인", "GitHub", "없음 (최신 버전 목록만 받아요)", "—"]],
   privfoot=f'정보를 지우거나 확인하고 싶으면 <a href="{DC}">공식 디스코드</a> #help로 요청해 주세요. 회원 정보는 앱의 계정 메뉴에서 탈퇴하면 바로 지워져요.',
   ossp="SystemBoost가 쓰는 다른 사람의 소프트웨어와 그 라이선스예요. 앱 설정 화면의 \"라이선스 전문 보기\"에서 전문을 볼 수 있어요.",
   osscols=["구성 요소", "라이선스", "쓰는 곳"],
   ossuse=["앱 화면", "설치 파일에 함께 들어 있음", "설치와 자동 업데이트", "Windows NVIDIA 드라이버 설정"],
   own="만든 방법: 코드, 셰이더, 문서는 Anthropic Claude(claude.ai, Claude Code)를 써서 만들었고, 로고는 Anthropic Claude(claude.ai)로 만들었어요. AI 도구로 만든 부분에 저작권이 생기는지, 생긴다면 누구에게 있는지는 확인하지 않았어요. 이 고지로 저작권을 주장하지 않아요.",
   contactp=f'판매자: DOKKU<br>라이선스, 키, 결제, 청약철회·환불, 개인정보 문의는 <a href="{DC}">공식 디스코드</a>의 #help에 글을 남겨 주세요. 키 전체를 공개 채널에 올리지 마세요. 운영진은 DM으로 먼저 연락하지 않아요.'),
 "en": dict(h1="License Agreement · Privacy", sub='This is the same agreement the app shows on first start. You can read it again in the app under Settings → "View license agreement".',
   revh="Upcoming revisions", revp='The agreement changes three times. Changed paragraphs are marked with their date.<br>• <b>December 8, 2026</b>: <a href="#a3">Article 3, Paragraph 3</a> changes for card subscriptions, which go on sale that day. Keys bought before then, and keys bought by bank transfer or other direct payment, are never charged automatically.<br>• <b>December 9, 2026</b>: for the Business plan, 5-PC and 10-PC bundle keys and the plan names, <a href="#a2">Article 2(1)</a>, <a href="#a3">Article 3(2), (5), (8) and (9)</a>, <a href="#a6">Article 6(1)</a> and <a href="#a11">Article 11(2)</a> change. Premium is renamed Pro; its features and prices stay the same. Bundle keys go on sale that day.<br>• <b>January 1, 2027</b>: the sales page and the Windows program in <a href="#a1">Article 1</a>; cancellation and refunds in <a href="#a3">Article 3(3)</a>; <a href="#a3">Article 3(2), (10) and (11)</a>; personal information in <a href="#a8">Article 8</a>; <a href="#a9">Article 9(2) and (5)</a>; and <a href="#a10">Article 10(3)</a> change, and <a href="#oc">Article 6-2</a> (overclocking and hardware tuning) and <a href="#a12-2">Article 12-2</a> (AMD components) are added. This includes usage statistics that start off (Article 8(3)).<br>After updating the app you accept the revised agreement once.',
   rev="Revised Dec 8, 2026", rev9="Revised Dec 9, 2026", rev27="Revised Jan 1, 2027", toc="Contents", imp="Please read the marked articles (orange border) carefully.",
   priv="Privacy summary", oss="Open-source licenses", contact="Seller and contact",
   privsum="Article 8 of the agreement as a table. This is everything the app sends.",
   cols=["When", "Where", "What is sent", "Kept for"],
   rows=[["Key check (when the app starts)", "Seller's license server", "An identifier derived from the key (not the key itself)", "While the key is managed"],
         ["Usage statistics (optional and off at first; sent only while turned on, can be changed in Settings)", "Seller's server", "Random install ID, app version, OS, plan, frame-generation multiplier, whether one-click game boost is used. The server records the country. No key, email, game names or hardware info; IP addresses are not stored.", "90 days after last contact"],
         ["Payment order", "Seller's license server", "Plan, order code, and the payer name and memo you type", "While the key and refunds are handled, plus legally required periods"],
         ["Card subscription (from Dec 8, 2026)", "Lemon Squeezy checkout → seller's server", "Card details are entered only in Lemon Squeezy's checkout; the seller never receives or stores them. The seller's server receives the payment email, order/subscription numbers and payment/cancellation status to issue and extend keys.", "While the key and refunds are handled, plus legally required periods"],
         ["Referral code", "Seller's server", "The code and a random device ID", "90 days"],
         ["Account sign-up (optional, 14+, with a username or an email address)", "Supabase database", "Username, email address (when you sign up by email or add one), hashed password and recovery code, sign-up/last login/consent times, invite code and invite relations, signed-in devices (device name, country), keys linked to the account, 2-step login (if turned on; the secret is encrypted), overclocking notice consent records", "Deleted immediately when you delete the account (up to 7 days in backups)"],
         ["Account security log", "Supabase database", "The time of events such as logins, password changes and 2-step login changes, with the IP address, country and device name at that time", "30 days"],
         ["Account recovery request (when both the password and the recovery code are lost)", "Supabase database", "The request and the details you enter to confirm it (order code, payment date, amount and the like)", "90 days"],
         ["Email (sign-up confirmation, password reset, login alerts, key resend)", "Through Resend (a US company) to the recipient", "The recipient's email address and the content of the email. An email address entered to have keys sent again is not stored. No advertising email.", "As Resend sets"],
         ["Sign-up / login / email request abuse prevention", "Seller's server", "A one-way hash of the connecting IP", "1 hour"],
         ["Update check", "GitHub", "Nothing (only fetches the list of versions)", "—"]],
   privfoot=f'To check or delete your data, ask in #help on the <a href="{DC}">official Discord</a>. Account data is deleted immediately when you delete your account in the app.',
   ossp="Other people's software that SystemBoost uses, and its licenses. Full texts are in the app under Settings → \"View full licenses\".",
   osscols=["Component", "License", "Used for"],
   ossuse=["App UI", "Bundled with the installer", "Installer and automatic updates", "Windows NVIDIA driver settings"],
   own="How it was made: the code, shaders and documents were made with Anthropic Claude (claude.ai and Claude Code); the logo was made with Anthropic Claude (claude.ai). Whether copyright exists in the AI-made parts, and who would hold it, has not been checked, and this notice does not claim it.",
   contactp=f'Seller: DOKKU<br>For license, key, payment, withdrawal/refund and privacy questions, post in #help on the <a href="{DC}">official Discord</a>. Never post your full key in a public channel. Staff never DM you first.'),
}
OSS = [('<a href="https://github.com/AvaloniaUI/Avalonia">Avalonia</a>', "MIT"), ('<a href="https://github.com/dotnet/runtime">.NET</a>', "MIT"),
       ('<a href="https://github.com/velopack/velopack">Velopack</a>', "MIT"), ('<a href="https://github.com/NVIDIA/nvapi">NVAPI</a>', "MIT")]

def license_body(L, eulas):
    s = S[L]; base = "ko" if L == "ko" else "en"; t = LT[base]
    head, secs = eulas[base]
    toc, body = eula_html(secs, {R8: t["rev"], R9: t["rev9"], R27: t["rev27"]})
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

# SystemBoost is sold and released for Windows only. On Windows the big button downloads the installer; on any other
# system (macOS, Linux, phones) it turns into a plain "Windows only for now" box. The two small Windows links stay
# for people fetching the file for a Windows PC.
DLJS = '''<script>
const REPO = "%s";
const FILES = { "win-setup": "SystemBoost-Windows-Setup.exe", "win-portable": "SystemBoost-Windows-Portable.zip" };
const DLKEY = %s;
const url = k => `https://github.com/${REPO}/releases/latest/download/${FILES[k]}`;
const plat = (navigator.userAgentData && navigator.userAgentData.platform) || navigator.platform || "";
const isWin = plat ? /^win/i.test(plat) : /Windows NT/i.test(navigator.userAgent);
function afterShow(lang) {
  document.querySelectorAll("[data-k]").forEach(a => a.href = url(a.dataset.k));
  const m = document.querySelector(".btn.main"); if (!m) return;
  if (isWin) { m.href = url("win-setup"); m.textContent = DLKEY[lang].win; return; }
  m.removeAttribute("href"); m.classList.add("off"); m.setAttribute("role", "status"); m.textContent = DLKEY[lang].only;
}
</script>'''

def page(kind, bodies, meta, extra="", css=CSS):
    temps = "\n".join(f'<template id="t-{c}">{b}</template>' for c, b in bodies.items())
    return f'''<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>{html.escape(meta["en"]["title"])}</title>
<meta name="description" content="{html.escape(meta["en"]["desc"])}">
<link rel="icon" type="image/png" href="favicon.png">
<style>{css}</style>
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
    en_note = read(sys.argv[2]).splitlines()[0]
    LT["en"]["sub"] = html.escape(en_note) + " " + LT["en"]["sub"]
    meta = {c: {"title": S[c]["title"], "desc": S[c]["desc"]} for c, _ in LANGS}
    dlkey = {c: {"win": S[c]["dl_win"], "only": S[c]["dl_only"]} for c, _ in LANGS}
    bodies = {c: index_body(c) for c, _ in LANGS}
    check_trademarks(bodies)
    idx = page("index", bodies, meta, DLJS % (RELEASES_REPO, json.dumps(dlkey, ensure_ascii=False)), CSS_INDEX)
    lmeta = {c: {"title": ("SystemBoost 사용권 계약 · 개인정보" if c == "ko" else "SystemBoost License Agreement · Privacy"), "desc": S[c]["desc"]} for c, _ in LANGS}
    lic = page("license", {c: license_body(c, eulas) for c, _ in LANGS}, lmeta)
    links = set(re.findall(r"license\.html#([\w-]+)", idx))
    missing = sorted(a for a in links if f'id="{a}"' not in lic)
    if missing: raise SystemExit(f"index.html links to license.html anchors that do not exist: {missing}")
    for name, text in (("index.html", idx), ("license.html", lic)):
        with open(os.path.join(ROOT, name), "w", encoding="utf-8", newline="\n") as out: out.write(text)
    print("index", len(idx), "license", len(lic))
