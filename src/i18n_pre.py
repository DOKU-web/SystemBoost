# Pre-registration popup (사전예약), open until 2026-11-27 23:59 KST.
# The popup only appears when the license server answers GET /preorder with {"open": true}, so the page is safe to
# publish before the server route is deployed: until then nothing shows. PRE_UNTIL also closes it in the browser.
PRE_API = "https://systemboost-license.euning97.workers.dev/preorder"
PRE_UNTIL = "2026-11-27T14:59:59Z"   # 2026-11-27 23:59:59 KST
PRE_KEEP = "2027-02-28"              # the server deletes the entries on this day (KV expiration)

# plan values sent to the server, in this order: undecided, free, premium (Pro), ultra, biz
PLAN_IDS = ["undecided", "free", "premium", "ultra", "biz"]

PRE = {}
PRE["ko"] = dict(
  nav="사전예약", badge="사전예약", title="SystemBoost 사전예약",
  lead="2027년 1월 정식 출시 전에 사전예약을 받아요. 출시되면 적어 주신 메일로 한 번 알려 드려요.",
  until="신청 마감: 2026년 11월 27일(금) 23:59 (한국 시간)",
  free="결제는 하지 않아요. 사전예약은 출시 알림 신청이에요.",
  email="메일 주소", plan="관심 있는 플랜",
  plans=["아직 모르겠어요", "무료", "프로", "울트라", "산업"],
  consent="<b>개인정보 수집 · 이용에 동의해요 (필수)</b><br>메일 주소와 관심 플랜을 받아 출시 알림을 보내는 데만 써요. 2027년 2월 28일에 모두 지워요. 동의하지 않으면 신청할 수 없어요. 만 14세 이상만 신청할 수 있어요.",
  send="사전예약 신청", later="다음에 할게요", close="닫기",
  done="신청됐어요. 출시되면 메일로 알려 드릴게요.", already="이미 신청된 메일이에요. 출시되면 알려 드릴게요.",
  e_mail="메일 주소를 확인해 주세요.", e_consent="개인정보 수집 · 이용에 동의해야 신청할 수 있어요.",
  e_closed="사전예약이 마감됐어요.", e_busy="신청이 몰려 지금은 받지 못했어요. 잠시 뒤 다시 시도해 주세요.",
  e_net="지금 신청을 받지 못했어요. 잠시 뒤 다시 시도하거나 공식 디스코드 #help에 남겨 주세요.",
  left="{d}일 남음", today="오늘 마감",
  prow=["사전예약 (2026년 11월 27일까지)", "판매자 서버", "메일 주소, 관심 플랜, 사이트 언어, 신청 시각. 출시 알림을 한 번 보내는 데만 써요.", "2027년 2월 28일에 삭제"],
)
PRE["en"] = dict(
  nav="Pre-register", badge="Pre-register", title="Pre-register for SystemBoost",
  lead="SystemBoost launches in January 2027. Pre-register and we'll email you once when it's out.",
  until="Closes: November 27, 2026, 23:59 KST (UTC+9)",
  free="No payment. Pre-registering only signs you up for the launch email.",
  email="Email address", plan="Plan you're interested in",
  plans=["Not sure yet", "Free", "Pro", "Ultra", "Business"],
  consent="<b>I agree to the collection and use of my personal information (required)</b><br>Your email and plan choice are used only to send the launch email, and are all deleted on February 28, 2027. You can't pre-register without agreeing. You must be 14 or older.",
  send="Pre-register", later="Maybe later", close="Close",
  done="You're signed up. We'll email you at launch.", already="This email is already signed up. We'll email you at launch.",
  e_mail="Please check your email address.", e_consent="Please agree to the collection and use of your information to pre-register.",
  e_closed="Pre-registration has closed.", e_busy="Too many sign-ups right now. Please try again a little later.",
  e_net="We couldn't take your sign-up right now. Try again later, or post in #help on the official Discord.",
  left="{d} days left", today="Closes today",
  prow=["Pre-registration (until Nov 27, 2026)", "Seller's server", "Email address, plan of interest, site language, time of sign-up. Used only to send one launch email.", "Deleted on Feb 28, 2027"],
)
PRE["ja"] = dict(
  nav="事前登録", badge="事前登録", title="SystemBoost 事前登録",
  lead="2027年1月の正式リリース前に事前登録を受け付けています。リリースしたら、入力したメールに一度だけお知らせします。",
  until="締め切り：2026年11月27日（金）23:59（日本時間・韓国時間）",
  free="お支払いはありません。事前登録はリリースのお知らせの申し込みです。",
  email="メールアドレス", plan="気になるプラン",
  plans=["まだ決めていない", "無料", "プロ", "ウルトラ", "ビジネス"],
  consent="<b>個人情報の収集・利用に同意します（必須）</b><br>メールアドレスと気になるプランは、リリースのお知らせを送るためだけに使い、2027年2月28日にすべて削除します。同意しない場合は登録できません。14歳以上の方のみ登録できます。",
  send="事前登録する", later="あとで", close="閉じる",
  done="登録しました。リリースしたらメールでお知らせします。", already="このメールはすでに登録されています。リリースしたらお知らせします。",
  e_mail="メールアドレスを確認してください。", e_consent="登録するには個人情報の収集・利用への同意が必要です。",
  e_closed="事前登録は締め切りました。", e_busy="申し込みが集中しています。少し時間をおいてもう一度お試しください。",
  e_net="今は登録できませんでした。しばらくしてからもう一度お試しいただくか、公式Discordの#helpにご連絡ください。",
  left="残り{d}日", today="本日締め切り",
)
PRE["zh"] = dict(
  nav="预约", badge="预约", title="SystemBoost 预约登记",
  lead="SystemBoost 将于 2027 年 1 月正式发布，现在开放预约。发布时我们会给你填写的邮箱发一次通知。",
  until="截止：2026 年 11 月 27 日（周五）23:59（韩国时间，UTC+9）",
  free="无需付款。预约只是登记发布通知。",
  email="邮箱地址", plan="感兴趣的套餐",
  plans=["还没想好", "免费", "专业版", "至尊版", "商业版"],
  consent="<b>我同意收集和使用我的个人信息（必选）</b><br>邮箱地址和所选套餐仅用于发送发布通知，并将在 2027 年 2 月 28 日全部删除。不同意则无法预约。须年满 14 岁。",
  send="提交预约", later="以后再说", close="关闭",
  done="预约成功。发布时我们会发邮件通知你。", already="这个邮箱已经预约过了。发布时会通知你。",
  e_mail="请检查邮箱地址。", e_consent="需要同意收集和使用个人信息才能预约。",
  e_closed="预约已截止。", e_busy="当前预约人数过多，请稍后再试。",
  e_net="暂时无法提交预约。请稍后再试，或在官方 Discord 的 #help 留言。",
  left="还剩 {d} 天", today="今天截止",
)
PRE["es"] = dict(
  nav="Prerregistro", badge="Prerregistro", title="Prerregistro de SystemBoost",
  lead="SystemBoost sale en enero de 2027. Regístrate antes y te avisaremos una vez por correo cuando esté disponible.",
  until="Cierra: 27 de noviembre de 2026, 23:59 KST (UTC+9)",
  free="No se paga nada. El prerregistro solo te apunta al aviso de lanzamiento.",
  email="Correo electrónico", plan="Plan que te interesa",
  plans=["Aún no lo sé", "Gratis", "Pro", "Ultra", "Business"],
  consent="<b>Acepto la recogida y el uso de mis datos personales (obligatorio)</b><br>Tu correo y el plan elegido solo se usan para enviar el aviso de lanzamiento y se borran todos el 28 de febrero de 2027. Sin aceptar no puedes registrarte. Debes tener 14 años o más.",
  send="Registrarme", later="Más tarde", close="Cerrar",
  done="¡Listo! Te avisaremos por correo en el lanzamiento.", already="Este correo ya está registrado. Te avisaremos en el lanzamiento.",
  e_mail="Revisa tu correo electrónico.", e_consent="Para registrarte debes aceptar la recogida y el uso de tus datos.",
  e_closed="El prerregistro ha cerrado.", e_busy="Hay muchos registros ahora mismo. Inténtalo de nuevo en un rato.",
  e_net="No pudimos registrarte ahora. Inténtalo más tarde o escribe en #help del Discord oficial.",
  left="Quedan {d} días", today="Cierra hoy",
)
PRE["pt"] = dict(
  nav="Pré-cadastro", badge="Pré-cadastro", title="Pré-cadastro do SystemBoost",
  lead="O SystemBoost será lançado em janeiro de 2027. Faça o pré-cadastro e avisaremos uma vez por e-mail no lançamento.",
  until="Encerra: 27 de novembro de 2026, 23:59 KST (UTC+9)",
  free="Nada é cobrado. O pré-cadastro só inscreve você no aviso de lançamento.",
  email="E-mail", plan="Plano de interesse",
  plans=["Ainda não sei", "Grátis", "Pro", "Ultra", "Business"],
  consent="<b>Concordo com a coleta e o uso dos meus dados pessoais (obrigatório)</b><br>Seu e-mail e o plano escolhido são usados só para enviar o aviso de lançamento e são todos apagados em 28 de fevereiro de 2027. Sem concordar, não é possível se cadastrar. É preciso ter 14 anos ou mais.",
  send="Fazer pré-cadastro", later="Depois", close="Fechar",
  done="Pronto! Avisaremos por e-mail no lançamento.", already="Este e-mail já está cadastrado. Avisaremos no lançamento.",
  e_mail="Confira o seu e-mail.", e_consent="Para se cadastrar, é preciso concordar com a coleta e o uso dos dados.",
  e_closed="O pré-cadastro foi encerrado.", e_busy="Muitos cadastros agora. Tente de novo daqui a pouco.",
  e_net="Não foi possível fazer o cadastro agora. Tente mais tarde ou escreva em #help no Discord oficial.",
  left="Faltam {d} dias", today="Encerra hoje",
)
PRE["ru"] = dict(
  nav="Предрегистрация", badge="Предрегистрация", title="Предрегистрация SystemBoost",
  lead="SystemBoost выходит в январе 2027 года. Зарегистрируйтесь заранее, и мы один раз напишем вам на почту в день выхода.",
  until="Приём до: 27 ноября 2026 г., 23:59 KST (UTC+9)",
  free="Ничего платить не нужно. Предрегистрация — это только подписка на письмо о выходе.",
  email="Электронная почта", plan="Интересующий тариф",
  plans=["Пока не знаю", "Бесплатно", "Pro", "Ultra", "Business"],
  consent="<b>Я согласен на сбор и использование моих персональных данных (обязательно)</b><br>Почта и выбранный тариф используются только для письма о выходе и будут удалены 28 февраля 2027 года. Без согласия зарегистрироваться нельзя. Только для лиц от 14 лет.",
  send="Зарегистрироваться", later="Позже", close="Закрыть",
  done="Готово! Мы напишем вам в день выхода.", already="Эта почта уже зарегистрирована. Мы напишем вам в день выхода.",
  e_mail="Проверьте адрес почты.", e_consent="Чтобы зарегистрироваться, нужно согласие на сбор и использование данных.",
  e_closed="Предрегистрация закрыта.", e_busy="Сейчас слишком много заявок. Попробуйте чуть позже.",
  e_net="Сейчас не удалось принять заявку. Попробуйте позже или напишите в #help на официальном Discord.",
  left="Осталось дней: {d}", today="Последний день",
)
PRE["de"] = dict(
  nav="Vorregistrierung", badge="Vorregistrierung", title="Vorregistrierung für SystemBoost",
  lead="SystemBoost erscheint im Januar 2027. Registriere dich vorab, dann schreiben wir dir zum Start einmal eine E-Mail.",
  until="Schluss: 27. November 2026, 23:59 KST (UTC+9)",
  free="Es wird nichts bezahlt. Die Vorregistrierung ist nur die Anmeldung für die Start-E-Mail.",
  email="E-Mail-Adresse", plan="Plan, der dich interessiert",
  plans=["Weiß ich noch nicht", "Kostenlos", "Pro", "Ultra", "Business"],
  consent="<b>Ich willige in die Erhebung und Nutzung meiner personenbezogenen Daten ein (erforderlich)</b><br>E-Mail-Adresse und gewählter Plan werden nur für die Start-E-Mail verwendet und am 28. Februar 2027 vollständig gelöscht. Ohne Einwilligung ist keine Registrierung möglich. Nur ab 14 Jahren.",
  send="Vorregistrieren", later="Später", close="Schließen",
  done="Fertig! Wir schreiben dir zum Start.", already="Diese E-Mail ist schon registriert. Wir schreiben dir zum Start.",
  e_mail="Bitte prüfe deine E-Mail-Adresse.", e_consent="Für die Registrierung ist die Einwilligung in die Datennutzung nötig.",
  e_closed="Die Vorregistrierung ist beendet.", e_busy="Gerade gibt es zu viele Anmeldungen. Bitte versuche es gleich noch einmal.",
  e_net="Die Anmeldung hat gerade nicht geklappt. Versuche es später noch einmal oder schreib in #help auf dem offiziellen Discord.",
  left="Noch {d} Tage", today="Endet heute",
)
PRE["fr"] = dict(
  nav="Pré-inscription", badge="Pré-inscription", title="Pré-inscription à SystemBoost",
  lead="SystemBoost sort en janvier 2027. Pré-inscrivez-vous et nous vous enverrons un seul e-mail à la sortie.",
  until="Clôture : 27 novembre 2026, 23:59 KST (UTC+9)",
  free="Aucun paiement. La pré-inscription vous inscrit seulement à l’e-mail de sortie.",
  email="Adresse e-mail", plan="Offre qui vous intéresse",
  plans=["Je ne sais pas encore", "Gratuit", "Pro", "Ultra", "Business"],
  consent="<b>J’accepte la collecte et l’utilisation de mes données personnelles (obligatoire)</b><br>Votre e-mail et l’offre choisie servent uniquement à envoyer l’e-mail de sortie et seront tous supprimés le 28 février 2027. Sans accord, la pré-inscription est impossible. Réservé aux 14 ans et plus.",
  send="Me pré-inscrire", later="Plus tard", close="Fermer",
  done="C’est fait ! Nous vous écrirons à la sortie.", already="Cet e-mail est déjà inscrit. Nous vous écrirons à la sortie.",
  e_mail="Vérifiez votre adresse e-mail.", e_consent="Pour vous pré-inscrire, vous devez accepter la collecte et l’utilisation de vos données.",
  e_closed="La pré-inscription est close.", e_busy="Trop d’inscriptions en ce moment. Réessayez dans un instant.",
  e_net="Impossible d’enregistrer votre inscription pour le moment. Réessayez plus tard ou écrivez dans #help sur le Discord officiel.",
  left="Encore {d} jours", today="Dernier jour",
)

PREJS = r'''<script>
const PRE_API = %(api)s, PRE_UNTIL = Date.parse(%(until)s), PRE = %(pre)s, PRE_PLANS = %(plans)s;
let preOpen = false, preLang = "en";
const preEl = document.getElementById("pre");
function preLeft() { const ms = PRE_UNTIL - Date.now(); return ms <= 0 ? -1 : Math.floor(ms / 86400000); }
function preNav(t) {
  const nav = document.querySelector("header nav"); if (!nav || nav.querySelector(".prenav")) return;
  const a = document.createElement("a"); a.href = "#"; a.className = "prenav"; a.textContent = t.nav;
  a.onclick = e => { e.preventDefault(); preShow(true); }; nav.prepend(a);
}
function preRender(t) {
  const d = preLeft();
  preEl.innerHTML = `<div class="pbox" role="dialog" aria-modal="true" aria-labelledby="pt">
  <button class="px" type="button" aria-label="${t.close}">×</button>
  <span class="badge">${t.badge}</span> <span class="pd">${d === 0 ? t.today : t.left.replace("{d}", d)}</span>
  <h2 id="pt">${t.title}</h2><p>${t.lead}</p><p class="pu">${t.until}<br>${t.free}</p>
  <form novalidate><label>${t.email}<input type="email" name="email" autocomplete="email" maxlength="254" required></label>
  <label>${t.plan}<select name="plan">${PRE_PLANS.map((p, i) => `<option value="${p}">${t.plans[i]}</option>`).join("")}</select></label>
  <label class="pc"><input type="checkbox" name="consent"><span>${t.consent}</span></label>
  <div class="pm" role="status" aria-live="polite"></div>
  <div class="pb"><button class="btn" type="submit">${t.send}</button><button class="pl2" type="button">${t.later}</button></div></form></div>`;
  const hide = () => { preEl.hidden = true; try { localStorage.setItem("sbPreSeen", "1"); } catch (e) {} };
  preEl.querySelector(".px").onclick = hide; preEl.querySelector(".pl2").onclick = hide;
  preEl.onclick = e => { if (e.target === preEl) hide(); };
  const form = preEl.querySelector("form"), msg = preEl.querySelector(".pm"), btn = form.querySelector("button[type=submit]");
  const say = (s, ok) => { msg.textContent = s; msg.className = "pm" + (ok ? " ok" : " bad"); };
  form.onsubmit = async e => {
    e.preventDefault();
    const email = form.email.value.trim();
    if (!/^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/.test(email) || email.length > 254) return say(t.e_mail);
    if (!form.consent.checked) return say(t.e_consent);
    if (preLeft() < 0) return say(t.e_closed);
    btn.disabled = true;
    try {
      const r = await fetch(PRE_API, { method: "POST", headers: { "content-type": "application/json" },
        body: JSON.stringify({ email, plan: form.plan.value, lang: preLang, consent: true }) });
      const j = await r.json().catch(() => ({}));
      if (j.ok) { say(j.already ? t.already : t.done, true); form.querySelectorAll("input,select").forEach(x => x.disabled = true);
                  btn.hidden = true; preEl.querySelector(".pl2").textContent = t.close; try { localStorage.setItem("sbPreDone", "1"); } catch (e) {} return; }
      say(j.error === "email" ? t.e_mail : j.error === "consent" ? t.e_consent : j.error === "closed" ? t.e_closed
          : r.status === 429 || j.error === "busy" ? t.e_busy : t.e_net);
    } catch (err) { say(t.e_net); }
    btn.disabled = false;
  };
}
function preShow(force) {
  if (!preOpen) return;
  preRender(PRE[preLang] || PRE.en);
  preEl.hidden = false; const i = preEl.querySelector("input[type=email]"); if (i) i.focus();
}
document.addEventListener("keydown", e => { if (e.key === "Escape" && !preEl.hidden) preEl.querySelector(".px").click(); });
function preAfter(lang) {
  preLang = lang;
  if (!preOpen) return;
  preNav(PRE[lang] || PRE.en);
  if (!preEl.hidden) preShow(true);
}
if (preLeft() >= 0 && document.body.classList.contains("index")) {
  fetch(PRE_API, { cache: "no-store" }).then(r => r.ok ? r.json() : {}).then(j => {
    if (!j || j.open !== true) return;
    preOpen = true; preAfter(preLang);
    let seen = false; try { seen = !!(localStorage.getItem("sbPreSeen") || localStorage.getItem("sbPreDone")); } catch (e) {}
    if (!seen) setTimeout(() => preShow(), 1200);
  }).catch(() => {});
}
</script>'''

PRECSS = '''
#pre{position:fixed;inset:0;background:rgba(0,0,0,.66);display:flex;align-items:center;justify-content:center;padding:16px;z-index:50}
#pre[hidden]{display:none}
.pbox{position:relative;background:var(--card);border:1px solid #4a2c10;border-top:4px solid var(--or);border-radius:18px;max-width:460px;width:100%;max-height:92vh;overflow:auto;padding:22px 22px 18px}
.pbox h2{margin:10px 0 6px;font-size:22px}.pbox p{margin:6px 0;color:var(--mut);font-size:15px}.pbox .pu{font-size:13px}
.pbox .pd{color:var(--or);font-size:13px;font-weight:700;margin-left:6px}
.pbox .px{position:absolute;top:10px;right:12px;background:none;border:0;color:var(--mut);font-size:26px;cursor:pointer;line-height:1}
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
header nav a.prenav{color:var(--or);font-weight:700}
'''
