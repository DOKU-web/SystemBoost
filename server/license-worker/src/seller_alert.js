// The seller's own Discord alert for bank transfers (0.8.14).
//
// The secret ADMIN_ALERT_WEBHOOK is a Discord webhook address of a channel only the seller reads. The owner types it
// in (`wrangler secret put ADMIN_ALERT_WEBHOOK`); it is never in code, KV, a reply or a log line. Without it (or with
// something that is not a Discord webhook address) nothing is sent and nothing else changes.
//
// A message carries only the order code, the plan and the time left to send the key: never the buyer's e-mail
// address, name, payer name, payment memo or IP address. Sending never throws and gives up after ALERT_TIMEOUT_MS, so
// a slow or failing Discord cannot fail the buyer's request (the caller also runs it in ctx.waitUntil).
import { DEPOSIT_HOURS, DEPOSIT_WARN_HOURS } from "./support.js";

const UA = "DiscordBot (https://github.com/DOKU-web/SystemBoost, 1.0)";
export const ALERT_HOOK_RE = /^https:\/\/(?:ptb\.|canary\.)?discord(?:app)?\.com\/api(?:\/v\d+)?\/webhooks\/\d+\/[\w-]+$/;
const ALERT_TIMEOUT_MS = 5000;
const MAX_LINES = 20;   // one reminder message; Discord takes 2,000 characters

const hookOf = env => { const h = String(env.ADMIN_ALERT_WEBHOOK || "").trim(); return ALERT_HOOK_RE.test(h) ? h : ""; };
export const alertOn = env => !!hookOf(env);
// "12-10 14:05" in Korea (UTC+9): the seller reads it there
const kst = ms => new Date(ms + 9 * 36e5).toISOString().slice(5, 16).replace("T", " ");

// true when Discord took it
export async function sellerAlert(env, content) {
  const hook = hookOf(env);
  if (!hook) return false;
  try {
    const r = await fetch(hook, {
      method: "POST",
      headers: { "User-Agent": UA, "Content-Type": "application/json" },
      body: JSON.stringify({ content: String(content).slice(0, 2000), allowed_mentions: { parse: [] } }),
      signal: AbortSignal.timeout(ALERT_TIMEOUT_MS),
    });
    return r.ok;
  } catch { return false; }
}

// A buyer's first "I paid" (index.js orderPaid). planName: the plan as the dashboard names it (index.js PLANS).
export function paidText(code, planName, paidAt) {
  const due = Date.parse(paidAt) + DEPOSIT_HOURS * 36e5;
  return `입금 알림 · ${code} · ${planName} · ${DEPOSIT_HOURS}시간 안에 확인 (${kst(due)}까지, 한국 시간)`;
}

// The hourly cron (wrangler.toml "7 * * * *"): the orders still "paid" whose first "I paid" is
// [DEPOSIT_WARN_HOURS, DEPOSIT_WARN_HOURS + 1) hours before the run's scheduled time. Each order falls in exactly one
// run's hour, so it is reminded once with nothing written to remember it. orders: [{code, plan, paidAt}] (plan named).
export function dueForReminder(orders, at) {
  return orders.filter(o => {
    const h = (at - Date.parse(o.paidAt || "")) / 36e5;
    return h >= DEPOSIT_WARN_HOURS && h < DEPOSIT_WARN_HOURS + 1;
  });
}
export function reminderText(due) {
  const lines = due.slice(0, MAX_LINES).map(o => {
    const end = Date.parse(o.paidAt) + DEPOSIT_HOURS * 36e5;
    return `${o.code} · ${o.plan} · ${DEPOSIT_HOURS - DEPOSIT_WARN_HOURS}시간 안에 확인 (${kst(end)}까지)`;
  });
  if (due.length > MAX_LINES) lines.push(`외 ${due.length - MAX_LINES}건`);
  return `입금 확인 아직 · ${due.length}건 · ${DEPOSIT_WARN_HOURS}시간이 지났어요\n` + lines.join("\n");
}
