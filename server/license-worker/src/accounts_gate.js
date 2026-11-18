// When accounts v2 start: ACCOUNTS_V2_FROM in wrangler.toml [vars], an instant with its offset, "2027-01-01T00:00:00+09:00".
// The privacy policy in force promises 30 days' notice before new items are processed, and the licence agreement's
// Article 8 (accounts v2) is in force from 2027-01-01. So until that instant the Worker does for accounts what the server
// of 2026-11-13 (commit 776dd71) did and shows that server's privacy policy, with the new one as a preview
// (src/accounts.js legacy(), src/account_web.js legacyWeb(), src/support.js privacyPage(); POST /keys/resend in index.js).
// Missing, empty or not exactly such an instant: not yet (it fails closed). Tests set ACCOUNTS_V2_FROM.
const FROM_RE = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:Z|([+-])(\d{2}):(\d{2}))$/;

/** The start in ms since 1970, or NaN when ACCOUNTS_V2_FROM is not a valid instant (then accounts v2 never start). */
export function accountsV2From(env) {
  const v = env?.ACCOUNTS_V2_FROM;
  const m = typeof v === "string" ? FROM_RE.exec(v) : null;
  if (!m) return NaN;
  const [y, mo, d, h, mi, s] = m.slice(1, 7).map(Number);
  const oh = Number(m[8] || 0), om = Number(m[9] || 0);
  if (h > 23 || mi > 59 || s > 59 || oh > 23 || om > 59) return NaN;
  const local = Date.UTC(y, mo - 1, d, h, mi, s);
  const t = new Date(local);   // a day that does not exist (2027-02-30) or a two-digit year comes back different
  if (t.getUTCFullYear() !== y || t.getUTCMonth() !== mo - 1 || t.getUTCDate() !== d) return NaN;
  return local - (m[7] === "-" ? -1 : 1) * (oh * 60 + om) * 60000;
}

export const accountsV2Open = (env, now = Date.now()) => now >= accountsV2From(env);

/** The start's day in Korea (UTC+9) as "YYYY-MM-DD", or "" when ACCOUNTS_V2_FROM is not a valid instant. */
export function accountsV2Day(env) {
  const t = accountsV2From(env);
  return Number.isFinite(t) ? new Date(t + 9 * 3600e3).toISOString().slice(0, 10) : "";
}
