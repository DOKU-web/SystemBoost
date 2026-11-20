# Plans since 0.8.10: Premium is renamed Pro, Business (산업) is added, 5/10-PC yearly bundles from 2026-12-09.
# 0.8.14 is sold and released for Windows only, so the "Windows only" tags and the Mac remarks are gone, and the
# news box lives in i18n.py.
# Applied on top of i18n.S (Korean only).

BUNDLES = [("premium", 84_500, 16_900, 149_000, 14_900), ("ultra", 254_500, 50_900, 449_000, 44_900),
           ("biz", 382_000, 76_400, 674_000, 67_400)]

P = {}

P["ko"] = dict(
  prem="프로", biz="산업", scr="플랜", old_scr="프리미엄",
  py_b="또는 ₩89,900 / 년 (월 ₩7,492꼴)",
  rb=["방송 모드 (방송 프로그램 우선 · 방송 중 알림 끄기)", "업무 모드 (화상 회의 · 업무 프로그램 우선)", "시작 프로그램 정리"],
  pl="무료로 기본 기능을 계속 쓸 수 있어요. 프로는 자동·3·4배와 품질 모드, 울트라는 여기에 6·8배와 듀얼 GPU 등을, 산업은 울트라에 방송 · 업무 기능을 더해요. 효과는 게임과 PC마다 달라서, 무료로 먼저 써 보고 앱의 벤치마크로 확인한 다음 결정하세요.",
  bh="5대 · 10대 묶음 키 (2026년 12월 9일부터)", b5="5대", b10="10대", each="대당", tot="합계",
  bn="같은 플랜의 연간 키 5개나 10개를 한 번에 사요. 키 하나에 PC 한 대이고, 회사 업무용 PC나 가족 PC처럼 구매자가 관리하는 PC에 써요. 계좌이체 · 직접 결제로만 팔고, 자동 결제와 할인 티켓은 없어요.",
  fine_add=["<b>산업</b> 키는 0.8.10 이상에서 써요.",
            "이미 받은 프리미엄 키는 그대로 프로로 쓸 수 있어요."],
)

def apply(S):
    for L, p in P.items():
        s = S[L]
        old = p["old_scr"]
        s["fine"] = [x.replace(f"<b>{old}</b>", f"<b>{p['scr']}</b>").replace(old, p["scr"]) for x in s["fine"]] + p["fine_add"]
        s["ft3"] = s["ft3"].replace(old, p["scr"])
        for k in ("prem", "biz", "py_b", "rb", "pl", "bh", "b5", "b10", "each", "tot", "bn"):
            s[k] = p[k]
