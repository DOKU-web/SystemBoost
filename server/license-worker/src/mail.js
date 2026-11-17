// Mail to members and buyers, through Resend's HTTP API (https://api.resend.com/emails). Planned processor, not yet
// signed up for: until the owner sets the Worker secrets RESEND_API_KEY and MAIL_FROM (e.g. "SystemBoost
// <no-reply@mail.example.com>", a sender on a domain verified at Resend), mail is off: the actions that need it answer
// 503 "mail_off" (src/accounts.js) and GET /account/features says email: false.
//
// sendMail(env, ctx, {to, kind, lang, data}) never throws and resolves to {ok, why}. It hands its work to
// ctx.waitUntil, so a caller replies first and the reply's timing never depends on whether a mail goes out.
// Limits without a KV write (counters in the Stats Durable Object, src/stats.js; this isolate's memory while that cannot
// be reached): MAIL_DAY a day for the whole Worker (env, default 90: Resend's free plan allows 100), and 5 an hour to
// one address. No click or open tracking is asked for. Addresses, links and keys are never logged.
const RESEND = "https://api.resend.com/emails";
export const MAIL = { perAddressHour: 5, dayDefault: 90, dayMin: 1, dayMax: 100000, timeoutMs: 10000 };
export const MAIL_KINDS = ["verify", "already", "reset", "password_changed", "new_device", "backup_used", "recovery_requested",
                           "recovery_approved", "key_resend", "link_key", "email_added"];

export const mailOn = env => !!(String(env?.RESEND_API_KEY || "").trim() && String(env?.MAIL_FROM || "").trim());
export function mailDay(env) {
  const v = env?.MAIL_DAY, n = typeof v === "number" ? v : /^\s*\d{1,6}\s*$/.test(String(v ?? "")) ? Number(v) : NaN;
  return Number.isInteger(n) && n >= MAIL.dayMin && n <= MAIL.dayMax ? n : MAIL.dayDefault;
}

export function sendMail(env, ctx, m) {
  const p = deliver(env, m || {}).catch(() => ({ ok: false, why: "error" }));
  if (ctx?.waitUntil) { try { ctx.waitUntil(p); } catch { /* the promise still runs */ } }
  return p;
}

async function deliver(env, { to, kind, lang, data }) {
  if (!mailOn(env)) return { ok: false, why: "off" };
  to = String(to || "").trim().toLowerCase();
  if (!/^[^\s@]{1,64}@[^\s@]{1,190}\.[^\s@]{2,}$/.test(to) || to.length > 254) return { ok: false, why: "to" };
  if (!MAIL_KINDS.includes(kind)) return { ok: false, why: "kind" };
  if (!(await allow(env, to))) return { ok: false, why: "limit" };
  const m = render(kind, lang === "en" ? "en" : "ko", data || {});
  try {
    const res = await fetch(RESEND, {
      method: "POST",
      headers: { Authorization: "Bearer " + String(env.RESEND_API_KEY).trim(), "Content-Type": "application/json" },
      body: JSON.stringify({ from: String(env.MAIL_FROM).trim(), to: [to], subject: m.subject, text: m.text, html: m.html }),
      signal: AbortSignal.timeout(MAIL.timeoutMs),
    });
    if (res.ok) return { ok: true, why: "" };
    console.log("mail", kind, res.status);   // the status only: Resend's error text can repeat the address
    return { ok: false, why: "status " + res.status };
  } catch (e) {
    console.log("mail", kind, e?.name || "error");
    return { ok: false, why: "network" };
  }
}

// ---- limits
const mem = new Map();   // counter name -> { n, exp } while the Stats object cannot be reached
export function _forgetMailLimits() { mem.clear(); }   // tests
const sha = async s => [...new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(s)))].map(b => b.toString(16).padStart(2, "0")).join("");
let salt = null, saltOf = null;
async function tag(env, to) {
  const k = env?.LICENSE_PRIVATE_KEY || "";
  if (saltOf !== k) { salt = k ? await sha("systemboost-mail|" + k) : ""; saltOf = k; }
  return (await sha(salt + "|" + to)).slice(0, 24);
}
async function allow(env, to) {
  const t = new Date().toISOString(), day = "mail:day:" + t.slice(0, 10), addr = "mail:to:" + (await tag(env, to)) + ":" + t.slice(0, 13);
  const items = [{ name: day, ttl: 2 * 86400, max: mailDay(env) }, { name: addr, ttl: 3600, max: MAIL.perAddressHour }];
  try {
    const o = env.STATS.get(env.STATS.idFromName("all"));
    const r = await o.fetch("https://stats/take", { method: "POST", body: JSON.stringify({ items }) });
    if (r.ok) { const j = await r.json(); if (j?.ok === true) return true; if (j?.ok === false) return false; }
  } catch { /* memory below */ }
  const now = Date.now(), n = it => { const m = mem.get(it.name); return m && m.exp > now ? m.n : 0; };
  if (items.some(it => n(it) + 1 > it.max)) return false;
  for (const it of items) mem.set(it.name, { n: n(it) + 1, exp: now + it.ttl * 1000 });
  if (mem.size > 5000) for (const [k, v] of mem) if (v.exp <= now) mem.delete(k);
  return true;
}

// ---- the mails. Every one ends with what to do if it was not you.
const esc = s => String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const kst = ms => { const d = new Date(Number(ms) + 9 * 36e5); return `${d.getUTCFullYear()}년 ${d.getUTCMonth() + 1}월 ${d.getUTCDate()}일 ${String(d.getUTCHours()).padStart(2, "0")}:${String(d.getUTCMinutes()).padStart(2, "0")} (한국 시간)`; };
const utc = ms => new Date(Number(ms)).toLocaleString("en-US", { year: "numeric", month: "long", day: "numeric", hour: "2-digit", minute: "2-digit", hour12: false, timeZone: "UTC" }) + " UTC";
const when = (lang, ms) => lang === "en" ? utc(ms) : kst(ms);
const planKo = p => ({ premium: "프로", ultra: "울트라", biz: "산업" }[p] || "프로");
const planEn = p => ({ premium: "Pro", ultra: "Ultra", biz: "Business" }[p] || "Pro");

const T = {
  verify: {   // d.add: an email being added to an account that has a 아이디 (add_email), not a sign-up
    ko: d => d.add ? ({ subject: "[SystemBoost] 메일 주소를 확인해 주세요",
                body: ["SystemBoost 계정에 이 메일 주소를 연결하려면 아래 버튼을 누르고, 열린 페이지에서 '연결 완료'를 누르세요. 연결되면 이 주소로도 로그인하고 비밀번호 재설정 링크를 받을 수 있어요.",
                       "링크는 24시간 동안 한 번만 쓸 수 있어요."],
                button: ["메일 주소 연결하기", d.link],
                wasnt: "본인이 요청하지 않았다면 이 메일을 무시하세요. 링크를 누르지 않으면 아무 계정에도 연결되지 않고, 24시간 뒤 링크도 사라져요." }) : ({ subject: "[SystemBoost] 메일 주소를 확인해 주세요",
                body: ["SystemBoost 회원 가입을 마치려면 아래 버튼을 누르세요. 열린 페이지에서 '가입 완료'를 누르면 계정이 만들어지고, 그다음 앱에서 이 메일 주소와 비밀번호로 로그인하면 돼요.",
                       "링크는 24시간 동안 한 번만 쓸 수 있어요."],
                button: ["가입 완료하기", d.link],
                wasnt: "본인이 가입하지 않았다면 이 메일을 무시하세요. 링크를 누르지 않으면 계정은 만들어지지 않고, 24시간 뒤 링크도 사라져요." }),
    en: d => d.add ? ({ subject: "[SystemBoost] Please confirm your email address",
                body: ["To link this email address to your SystemBoost account, press the button below, then 'Finish' on the page that opens. Once linked you can also log in with it and get password reset links at it.",
                       "The link works once, for 24 hours."],
                button: ["Link this address", d.link],
                wasnt: "If you did not ask for this, ignore this email. Without the link it is not linked to any account, and the link expires in 24 hours." }) : ({ subject: "[SystemBoost] Please confirm your email address",
                body: ["To finish signing up for SystemBoost, press the button below. On the page that opens, press 'Finish sign-up' and the account is created; then log in to the app with this email address and your password.",
                       "The link works once, for 24 hours."],
                button: ["Finish sign-up", d.link],
                wasnt: "If you did not sign up, ignore this email. Without the link no account is created, and the link expires in 24 hours." }),
  },
  already: {
    ko: () => ({ subject: "[SystemBoost] 이미 가입된 메일 주소예요",
                 body: ["누군가 이 메일 주소로 SystemBoost 회원 가입이나 메일 연결을 시도했어요. 이 주소는 이미 계정이 있어서 아무것도 바뀌지 않았어요.",
                        "비밀번호가 생각나지 않으면 앱의 '◈ 계정' → '비밀번호 찾기'에서 이 주소로 재설정 링크를 받으세요."],
                 wasnt: "본인이 시도하지 않았다면 이 메일을 무시하세요. 계정은 바뀌지 않았어요. 걱정되면 앱에서 비밀번호를 바꾸고 2단계 인증을 켜세요." }),
    en: () => ({ subject: "[SystemBoost] This email address already has an account",
                 body: ["Someone tried to sign up for SystemBoost, or to add an email to an account, with this address. It already has an account, so nothing changed.",
                        "If you forgot the password, use '◈ Account' → 'Forgot password' in the app to get a reset link at this address."],
                 wasnt: "If this wasn't you, ignore this email: your account did not change. If you are worried, change your password in the app and turn on 2-step verification." }),
  },
  reset: {
    ko: d => ({ subject: "[SystemBoost] 비밀번호 재설정 링크",
                body: ["아래 버튼을 눌러 새 비밀번호를 정하세요. 링크는 30분 동안 한 번만 쓸 수 있어요. 새 비밀번호를 정하면 모든 기기에서 로그아웃돼요."],
                button: ["새 비밀번호 정하기", d.link],
                wasnt: "본인이 요청하지 않았다면 이 메일을 무시하세요. 링크를 누르지 않으면 비밀번호는 바뀌지 않아요. 이런 메일이 계속 오면 앱에서 2단계 인증을 켜세요." }),
    en: d => ({ subject: "[SystemBoost] Password reset link",
                body: ["Press the button below to choose a new password. The link works once, for 30 minutes. A new password logs you out on every device."],
                button: ["Choose a new password", d.link],
                wasnt: "If you did not ask for this, ignore this email: without the link your password does not change. If these keep coming, turn on 2-step verification in the app." }),
  },
  password_changed: {
    ko: d => ({ subject: "[SystemBoost] 비밀번호가 바뀌었어요",
                body: [`계정의 비밀번호가 ${when("ko", d.when ?? Date.now())}에 바뀌었어요. 그래서 다른 기기에서는 모두 로그아웃됐어요.`],
                wasnt: "본인이 바꾸지 않았다면 바로 앱의 '◈ 계정' → '비밀번호 찾기'로 이 메일에 재설정 링크를 받아 비밀번호를 다시 정하세요(모든 기기에서 로그아웃돼요). 메일로 받을 수 없다면 '계정 복구 요청'을 보내세요." }),
    en: d => ({ subject: "[SystemBoost] Your password was changed",
                body: [`The password of your account was changed on ${when("en", d.when ?? Date.now())}, so every other device was logged out.`],
                wasnt: "If you did not change it, use '◈ Account' → 'Forgot password' in the app right away to get a reset link at this address and choose a new password (this logs out every device). If that does not work, send an account recovery request." }),
  },
  new_device: {
    ko: d => ({ subject: "[SystemBoost] 새 기기에서 로그인했어요",
                body: [`새 기기에서 계정에 로그인했어요.`, `기기: ${d.device || "SystemBoost app"} · 국가: ${d.country || "알 수 없음"} · 시각: ${when("ko", d.when ?? Date.now())}`],
                wasnt: "본인이 아니라면 앱의 '◈ 계정' → '로그인된 기기'에서 그 기기를 로그아웃하고, 비밀번호를 바꾸고, 2단계 인증을 켜세요. 로그인할 수 없다면 '비밀번호 찾기'로 이 메일에 재설정 링크를 받으세요." }),
    en: d => ({ subject: "[SystemBoost] New device logged in",
                body: ["Your account was logged in on a new device.", `Device: ${d.device || "SystemBoost app"} · Country: ${d.country || "unknown"} · Time: ${when("en", d.when ?? Date.now())}`],
                wasnt: "If this wasn't you, log that device out under '◈ Account' → 'Logged-in devices' in the app, change your password and turn on 2-step verification. If you cannot log in, use 'Forgot password' to get a reset link at this address." }),
  },
  backup_used: {
    ko: d => ({ subject: "[SystemBoost] 복구 코드로 로그인했어요",
                body: [`복구 코드 하나로 계정에 로그인했어요. 쓴 코드는 다시 쓸 수 없고, 남은 코드는 ${Number(d.left) || 0}개예요.`,
                       "인증 앱을 잃어버렸다면 앱의 '◈ 계정'에서 2단계 인증을 다시 설정하세요. 남은 복구 코드가 3개 이하라면 새로 만드세요."],
                wasnt: "본인이 아니라면 바로 비밀번호를 바꾸고(모든 기기에서 로그아웃돼요) 복구 코드를 새로 만드세요. 로그인할 수 없다면 '비밀번호 찾기'로 이 메일에 재설정 링크를 받으세요." }),
    en: d => ({ subject: "[SystemBoost] A backup code was used to log in",
                body: [`A backup code was used to log in to your account. That code cannot be used again; ${Number(d.left) || 0} are left.`,
                       "If you lost your authenticator app, set up 2-step verification again under '◈ Account'. If 3 or fewer codes are left, make new ones."],
                wasnt: "If this wasn't you, change your password right away (this logs out every device) and make new backup codes. If you cannot log in, use 'Forgot password' to get a reset link at this address." }),
  },
  recovery_requested: {
    ko: d => ({ subject: "[SystemBoost] 계정 복구 요청이 들어왔어요",
                body: ["이 메일의 계정에 '계정 복구 요청'이 들어왔어요. 판매자는 결제 기록으로 본인인지 확인하고, " + (d.due ? when("ko", d.due) : "72시간 뒤") + " 이후에 승인할 수 있어요. 승인되면 2단계 인증이 꺼지고, 모든 기기에서 로그아웃되고, 이 주소로 비밀번호 재설정 링크가 가요."],
                button: ["복구 요청 취소", d.link],
                wasnt: "본인이 요청하지 않았다면 위의 '복구 요청 취소'를 누르세요. 그다음 앱에서 비밀번호를 바꾸세요. 취소 링크는 요청이 처리되기 전까지 쓸 수 있어요." }),
    en: d => ({ subject: "[SystemBoost] An account recovery request was made",
                body: ["An account recovery request was made for the account of this email. The seller checks it against payment records and can approve it after " + (d.due ? when("en", d.due) : "72 hours") + ". Approval turns 2-step verification off, logs out every device and sends a password reset link to this address."],
                button: ["Cancel the request", d.link],
                wasnt: "If you did not ask for this, press 'Cancel the request' above, then change your password in the app. The cancel link works until the request is decided." }),
  },
  recovery_approved: {
    ko: d => ({ subject: "[SystemBoost] 계정 복구 요청을 승인했어요",
                body: ["판매자가 계정 복구 요청을 승인했어요. 2단계 인증을 껐고, 모든 기기에서 로그아웃했어요.", "아래 버튼으로 새 비밀번호를 정하세요(24시간 동안 한 번). 로그인한 뒤 '◈ 계정'에서 2단계 인증을 다시 켜세요."],
                button: ["새 비밀번호 정하기", d.link],
                wasnt: "본인이 요청하지 않았다면 링크를 누르지 말고, 바로 앱의 '비밀번호 찾기'로 비밀번호를 바꾼 뒤 앱의 고객센터(문의 종류 '기타')로 알려 주세요." }),
    en: d => ({ subject: "[SystemBoost] Your account recovery request was approved",
                body: ["The seller approved your account recovery request: 2-step verification is off and every device was logged out.", "Use the button below to choose a new password (once, within 24 hours). After logging in, turn 2-step verification on again under '◈ Account'."],
                button: ["Choose a new password", d.link],
                wasnt: "If you did not ask for this, do not use the link: change your password right away with 'Forgot password' in the app, then tell us through Support in the app (category 'Other')." }),
  },
  key_resend: {
    ko: d => ({ subject: "[SystemBoost] 라이선스 키를 다시 보내 드려요",
                body: ["이 메일 주소로 산 SystemBoost 라이선스 키예요. 앱의 '★ 플랜' → '라이선스 키'에 붙여 넣으세요.",
                       `플랜: ${planKo(d.plan)}${d.expires ? " · 만료: " + String(d.expires).slice(0, 10) : ""}`],
                code: d.key,
                wasnt: "본인이 요청하지 않았다면 이 메일을 무시하세요. 키는 이 메일 주소로만 보냈어요. 키를 다른 사람에게 보여 주지 마세요." }),
    en: d => ({ subject: "[SystemBoost] Your license key, sent again",
                body: ["This is the SystemBoost license key bought with this email address. Paste it into '★ Plans' → 'License key' in the app.",
                       `Plan: ${planEn(d.plan)}${d.expires ? " · Expires: " + String(d.expires).slice(0, 10) : ""}`],
                code: d.key,
                wasnt: "If you did not ask for this, ignore this email: the key was sent only to this address. Do not show the key to anyone." }),
  },
  link_key: {
    ko: d => ({ subject: "[SystemBoost] 키를 계정에 연결할까요?",
                body: [`이 메일 주소로 산 키(식별 번호 ${d.keyId || ""})를 SystemBoost 회원 계정에 연결해 달라는 요청이 왔어요. 아래 버튼을 누르고, 열린 페이지에서 '연결하기'를 누르면 연결돼요. 링크는 24시간 동안 한 번만 쓸 수 있어요.`],
                button: ["키 연결하기", d.link],
                wasnt: "본인이 요청하지 않았다면 링크를 누르지 마세요. 누르지 않으면 키는 어느 계정에도 연결되지 않아요. 키가 다른 사람 손에 있을 수 있으니 앱의 고객센터로 알려 주세요." }),
    en: d => ({ subject: "[SystemBoost] Link your key to an account?",
                body: [`Someone asked to link the key bought with this email address (key ID ${d.keyId || ""}) to a SystemBoost member account. Press the button below, then 'Link' on the page that opens. The link works once, for 24 hours.`],
                button: ["Link the key", d.link],
                wasnt: "If you did not ask for this, do not use the link: without it the key is not linked to any account. Someone else may have your key, so tell us through Support in the app." }),
  },
  email_added: {
    ko: d => ({ subject: "[SystemBoost] 계정에 메일 주소를 연결했어요",
                body: [`이 메일 주소를 SystemBoost 계정${d.id ? " '" + d.id + "'" : ""}에 연결했어요. 이제 이 주소로 로그인하고, 비밀번호를 잊으면 재설정 링크를 받을 수 있어요.`],
                wasnt: "본인이 연결하지 않았다면 메일 계정의 비밀번호부터 바꾸고, 앱의 고객센터(문의 종류 '기타')로 알려 주세요." }),
    en: d => ({ subject: "[SystemBoost] Email address added to your account",
                body: [`This email address is now linked to the SystemBoost account${d.id ? " '" + d.id + "'" : ""}. You can log in with it and get reset links at it.`],
                wasnt: "If you did not do this, change the password of your email account first, then tell us through Support in the app (category 'Other')." }),
  },
};

/** {subject, text, html} of one mail (exported for the tests and the dashboard's preview). */
export function render(kind, lang, data) {
  const m = T[kind][lang](data);
  const wasntHead = lang === "en" ? "If this wasn't you" : "본인이 아니라면";
  const text = [...m.body, ...(m.button ? [m.button[0] + ": " + m.button[1]] : []), ...(m.code ? [m.code] : []), "",
                "■ " + wasntHead + "\n" + m.wasnt, "", "SystemBoost"].join("\n\n").replace(/\n{3,}/g, "\n\n");
  const html = `<!DOCTYPE html><html lang="${lang}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>${esc(m.subject)}</title></head>
<body style="margin:0;background:#0b0b0c;color:#e8e6e3;font-family:system-ui,-apple-system,'Segoe UI','Malgun Gothic',sans-serif;line-height:1.6">
<div style="max-width:560px;margin:0 auto;padding:28px 20px">
<div style="color:#FF7A1A;font-weight:700;font-size:18px;margin-bottom:16px">SystemBoost</div>
${m.body.map(p => `<p style="margin:0 0 12px">${esc(p)}</p>`).join("")}
${m.button ? `<p style="margin:20px 0"><a href="${esc(m.button[1])}" style="display:inline-block;background:#FF7A1A;color:#0b0b0c;text-decoration:none;font-weight:700;padding:12px 20px;border-radius:10px">${esc(m.button[0])}</a></p><p style="margin:0 0 12px;color:#8b8a88;font-size:13px;word-break:break-all">${esc(m.button[1])}</p>` : ""}
${m.code ? `<pre style="white-space:pre-wrap;word-break:break-all;background:#141416;border:1px solid #333;border-radius:10px;padding:12px;font-size:13px">${esc(m.code)}</pre>` : ""}
<div style="margin-top:24px;padding:12px 14px;border-left:3px solid #FF7A1A;background:#141416;border-radius:6px"><b>${esc(wasntHead)}</b><br>${esc(m.wasnt)}</div>
</div></body></html>`;
  return { subject: m.subject, text, html };
}
