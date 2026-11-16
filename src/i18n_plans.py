# Plans since 0.8.10: Premium is renamed Pro, Business (산업) is added, 5/10-PC yearly bundles from 2026-12-09.
# 0.8.14 is sold and released for Windows only, so the "Windows only" tags and the Mac remarks are gone, and the
# news box (0.8.14) lives in i18n.py.
# Applied on top of i18n.S. The app is in Korean and English only, so other languages name the app screen in English.

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

P["en"] = dict(
  prem="Pro", biz="Business", scr="★ Plans", old_scr="Premium",
  py_b="or ₩89,900 / year (≈ ₩7,492 / month)",
  rb=["Stream mode (streaming app first · notifications off while live)", "Work mode (video calls and work apps first)", "Startup programs"],
  pl="The free plan keeps working with no time limit. Pro adds Auto, 3× and 4× and Quality mode; Ultra adds 6×, 8×, dual GPU and more; Business adds streaming and work features to Ultra. Results differ by game and PC, so try it free first and check with the built-in benchmark before you buy.",
  bh="5-PC and 10-PC bundles (from December 9, 2026)", b5="5 PCs", b10="10 PCs", each="per PC", tot="Total",
  bn="5 or 10 yearly keys of the same plan in one purchase. One key per PC, for PCs you manage such as work PCs or family PCs. Bank transfer / direct payment only; no automatic renewal and no discount tickets.",
  fine_add=["<b>Business</b> keys need 0.8.10 or later.",
            "Premium keys you already have keep working as Pro."],
)

def _other(prem, biz, old_scr, py_b, rb, pl, bh, b5, b10, each, tot, bn, fine_add):
    return dict(prem=prem, biz=biz, scr="★ Plans", old_scr=old_scr, py_b=py_b, rb=rb, pl=pl, bh=bh, b5=b5, b10=b10,
                each=each, tot=tot, bn=bn, fine_add=fine_add)

P["ja"] = _other("プロ", "ビジネス", "プレミアム", "または ₩89,900 / 年（月あたり約 ₩7,492）",
  ["配信モード（配信ソフト優先・配信中は通知オフ）", "仕事モード（ビデオ会議・仕事用アプリ優先）", "スタートアッププログラムの整理"],
  "無料プランでも基本機能をずっと使えます。プロは自動・3・4倍と品質モード、ウルトラはさらに6・8倍やデュアルGPUなど、ビジネスはウルトラに配信・仕事向けの機能を加えます。効果はゲームとPCによって違うので、まず無料で試し、アプリのベンチマークで確かめてから決めてください。",
  "5台・10台まとめ買いキー（2026年12月9日から）", "5台", "10台", "1台あたり", "合計",
  "同じプランの年額キー5個または10個をまとめて購入します。キー1つにつきPC1台で、会社の業務用PCや家族のPCなど購入者が管理するPCに使います。振込・直接決済のみで、自動更新と割引チケットはありません。",
  ["<b>ビジネス</b>キーは0.8.10以降で使えます。", "すでにお持ちのプレミアムキーはそのままプロとして使えます。"])

P["zh"] = _other("专业版", "商业版", "高级版", "或 ₩89,900 / 年（约每月 ₩7,492）",
  ["直播模式（直播软件优先 · 直播时关闭通知）", "工作模式（视频会议和工作软件优先）", "启动项管理"],
  "免费版的基础功能可以一直使用。专业版增加自动、3 倍、4 倍和画质模式；至尊版再增加 6 倍、8 倍和双 GPU 等；商业版在至尊版基础上增加直播和工作功能。效果因游戏和电脑而异，请先免费试用，并用应用内的基准测试确认后再决定。",
  "5 台 · 10 台套装密钥（2026 年 12 月 9 日起）", "5 台", "10 台", "每台", "合计",
  "一次购买同一套餐的 5 个或 10 个年付密钥。每个密钥用于一台电脑，适用于购买者管理的电脑，例如公司办公电脑或家人的电脑。仅支持转账 / 直接付款，不自动续费，不能使用折扣券。",
  ["<b>商业版</b>密钥需要 0.8.10 或更高版本。", "已有的高级版密钥会继续作为专业版使用。"])

P["es"] = _other("Pro", "Business", "Premium", "o ₩89,900 / año (≈ ₩7,492 / mes)",
  ["Modo directo (la app de streaming primero · sin notificaciones en directo)", "Modo trabajo (videollamadas y apps de trabajo primero)", "Programas de inicio"],
  "El plan gratuito sigue funcionando sin límite de tiempo. Pro añade Auto, 3× y 4× y el modo Calidad; Ultra añade 6×, 8×, GPU dual y más; Business añade a Ultra funciones para streaming y trabajo. Los resultados varían según el juego y el PC: pruébalo gratis primero y compruébalo con el benchmark de la app antes de comprar.",
  "Paquetes de 5 y 10 PC (desde el 9 de diciembre de 2026)", "5 PC", "10 PC", "por PC", "Total",
  "5 o 10 claves anuales del mismo plan en una sola compra. Una clave por PC, para PC que tú gestionas, como los del trabajo o la familia. Solo transferencia / pago directo; sin renovación automática ni cupones de descuento.",
  ["Las claves <b>Business</b> requieren 0.8.10 o posterior.", "Las claves Premium que ya tienes siguen funcionando como Pro."])

P["pt"] = _other("Pro", "Business", "Premium", "ou ₩89,900 / ano (≈ ₩7,492 / mês)",
  ["Modo live (app de transmissão primeiro · notificações desligadas ao vivo)", "Modo trabalho (videochamadas e apps de trabalho primeiro)", "Programas de inicialização"],
  "O plano grátis continua funcionando sem limite de tempo. Pro adiciona Auto, 3× e 4× e o modo Qualidade; Ultra adiciona 6×, 8×, GPU dupla e mais; Business adiciona ao Ultra recursos de transmissão e trabalho. Os resultados variam por jogo e PC: teste grátis primeiro e confira com o benchmark do app antes de comprar.",
  "Pacotes de 5 e 10 PCs (a partir de 9 de dezembro de 2026)", "5 PCs", "10 PCs", "por PC", "Total",
  "5 ou 10 chaves anuais do mesmo plano em uma compra. Uma chave por PC, para PCs que você gerencia, como os do trabalho ou da família. Só transferência / pagamento direto; sem renovação automática e sem cupons de desconto.",
  ["Chaves <b>Business</b> precisam da versão 0.8.10 ou posterior.", "Chaves Premium que você já tem continuam valendo como Pro."])

P["ru"] = _other("Pro", "Business", "Premium", "или ₩89,900 / год (≈ ₩7,492 / мес.)",
  ["Режим стрима (программа для стрима в приоритете · без уведомлений в эфире)", "Рабочий режим (видеозвонки и рабочие программы в приоритете)", "Автозагрузка"],
  "Бесплатный тариф работает без ограничения по времени. Pro добавляет «Авто», 3× и 4× и режим «Качество»; Ultra — ещё 6×, 8×, две видеокарты и другое; Business добавляет к Ultra функции для стримов и работы. Результат зависит от игры и ПК, поэтому сначала попробуйте бесплатно и проверьте встроенным бенчмарком.",
  "Наборы на 5 и 10 ПК (с 9 декабря 2026 года)", "5 ПК", "10 ПК", "за ПК", "Всего",
  "5 или 10 годовых ключей одного тарифа за одну покупку. Один ключ — один ПК, для компьютеров, которыми вы управляете: рабочих или семейных. Только перевод / прямая оплата; без автопродления и скидочных купонов.",
  ["Ключам <b>Business</b> нужна версия 0.8.10 или новее.", "Уже полученные ключи Premium продолжают работать как Pro."])

P["de"] = _other("Pro", "Business", "Premium", "oder ₩89,900 / Jahr (≈ ₩7,492 / Monat)",
  ["Stream-Modus (Streaming-App zuerst · keine Benachrichtigungen im Stream)", "Arbeitsmodus (Videocalls und Arbeits-Apps zuerst)", "Autostart-Programme"],
  "Der kostenlose Plan läuft ohne Zeitlimit. Pro bringt Auto, 3× und 4× und den Qualitätsmodus; Ultra dazu 6×, 8×, Dual-GPU und mehr; Business ergänzt Ultra um Funktionen für Streaming und Arbeit. Ergebnisse hängen von Spiel und PC ab – probiere es zuerst kostenlos und prüfe es mit dem Benchmark der App, bevor du kaufst.",
  "Pakete für 5 und 10 PCs (ab 9. Dezember 2026)", "5 PCs", "10 PCs", "pro PC", "Gesamt",
  "5 oder 10 Jahresschlüssel desselben Plans in einem Kauf. Ein Schlüssel pro PC, für PCs, die du verwaltest, etwa Arbeits- oder Familien-PCs. Nur Überweisung / Direktzahlung; keine automatische Verlängerung und keine Rabatt-Tickets.",
  ["<b>Business</b>-Schlüssel brauchen 0.8.10 oder neuer.", "Vorhandene Premium-Schlüssel funktionieren weiter als Pro."])

P["fr"] = _other("Pro", "Business", "Premium", "ou ₩89,900 / an (≈ ₩7,492 / mois)",
  ["Mode stream (logiciel de stream prioritaire · notifications coupées en direct)", "Mode travail (visioconférences et applis de travail prioritaires)", "Programmes au démarrage"],
  "L’offre gratuite fonctionne sans limite de durée. Pro ajoute Auto, 3× et 4× et le mode Qualité ; Ultra ajoute 6×, 8×, le double GPU et plus ; Business ajoute à Ultra des fonctions pour le stream et le travail. Les résultats varient selon le jeu et le PC : essayez d’abord gratuitement et vérifiez avec le benchmark de l’app avant d’acheter.",
  "Lots de 5 et 10 PC (à partir du 9 décembre 2026)", "5 PC", "10 PC", "par PC", "Total",
  "5 ou 10 clés annuelles de la même offre en un seul achat. Une clé par PC, pour les PC que vous gérez, comme ceux du travail ou de la famille. Virement / paiement direct uniquement ; pas de renouvellement automatique ni de bons de réduction.",
  ["Les clés <b>Business</b> demandent la version 0.8.10 ou plus récente.", "Les clés Premium que vous avez déjà continuent de fonctionner comme Pro."])


def apply(S):
    for L, p in P.items():
        s = S[L]
        old = p["old_scr"]
        s["fine"] = [x.replace(f"<b>{old}</b>", f"<b>{p['scr']}</b>").replace(old, p["scr"]) for x in s["fine"]] + p["fine_add"]
        s["ft3"] = s["ft3"].replace(old, p["scr"])
        for k in ("prem", "biz", "py_b", "rb", "pl", "bh", "b5", "b10", "each", "tot", "bn"):
            s[k] = p[k]
