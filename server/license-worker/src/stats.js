// Usage and download numbers for the owner's dashboard. All of it lives in one Durable Object with SQLite:
// a heartbeat writes on every ping, and KV allows only 1,000 writes a day on the free plan.
//
// An app sends a random install number and the fields checked in cleanPing, nothing that ties it to a key,
// a mail address or a person. The country comes from Cloudflare; the IP address is not stored.
// Keep this in step with 제8조 of app/SystemBoost.App/Assets/eula.txt.
//
// GitHub only gives running totals per release file, so the daily download numbers are the difference
// between one day's last total and the day before's. The hourly cron keeps those totals going while
// nobody has the dashboard open.
//
// The same object keeps the support dashboard's event feed ("새 소식"): one row per thing a buyer did (new ticket,
// refund request, reply, reopen, close) and per card refund the webhook finished, so the dashboard sees it without
// a reload. A row holds the time, the kind, the ticket number and its category, never what the buyer wrote; the
// dashboard takes the subject from its own ticket list. Kept 30 days and at most the newest 1,000 rows.
//
// And the worker's guard, which changes on nearly every request that counts, so it cannot be KV (1,000 writes a day):
//   counters      named counts with an end time: the admin lockout, per-address limits, the support write budget, the
//                 subscription-portal cap. One object answers them all one at a time, so a check and its count are one
//                 step (KV could not do that: tries sent at once all passed). Names hold a salted address hash at most.
//   audit_log     what the seller and the webhooks did (the dashboard's 기록), kept GUARD.auditDays.
//   admin_access  who opened the dashboard and which personal data it showed: time, salted address hash, route,
//                 action, target (a key id, ticket number or member id; "검색" for a search, never the search text),
//                 kept GUARD.accessDays (one year and a day).

import { PRE, PRE_HOOK, preNoteText } from "./preorder.js";
import { postText } from "./announce.js";

const REPO = "DOKU-web/SystemBoost";
const MINUTE = 60e3, HOUR = 36e5, DAY = 864e5;
const KEEP_DAYS = 90;          // an install silent this long is deleted; the daily totals stay
const QUIET_MS = 20e3;         // a ping this soon after the last one writes nothing
const MAX_NEW_A_DAY = 3000;    // cheap cap on made-up installs
const SCAN_MS = 5 * MINUTE;    // the numbers that scan every recent install are reused this long
const GITHUB_MS = 5 * MINUTE;  // the dashboard asks GitHub at most this often
const HISTORY_DAYS = 30;
const MULTIPLIERS = [0, 2, 3, 4, 6, 8];
export const FEED = { keepDays: 30, keepRows: 1000, page: 100, recent: 20 };
export const FEED_KINDS = ["ticket", "refund", "reply", "reopen", "close", "card"];
const TICKET_RE = /^T-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$/;   // as in support.js
// sessionMinutes: an address with no successful admin call for this long logs in again (a "login" row)
export const GUARD = { auditDays: 180, accessDays: 366, page: 100, sessionMinutes: 30, maxItems: 8 };
const NAME_RE = /^[A-Za-z0-9:._\/-]{1,160}$/;
const str = (v, n) => String(v ?? "").slice(0, n);

// The free plan stops the whole worker at 100,000 requests a day, license checks and orders included, and
// SQLite at 100,000 rows written; a ping writes 3 rows. So the more people use the app today, the less often
// each app pings. [people today below, seconds between pings]
const PACE = [[100, 60], [400, 300], [1500, 900], [Infinity, 3600]];
const pace = people => PACE.find(([below]) => people < below)[1];
const liveSeconds = next => next * 2.5;   // a missed ping or two must not look like the app was closed

const SCHEMA = `
CREATE TABLE IF NOT EXISTS installs (
  id TEXT PRIMARY KEY, first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL,
  os TEXT NOT NULL, ver TEXT NOT NULL, plan TEXT NOT NULL, fg INTEGER NOT NULL, boost INTEGER NOT NULL, country TEXT NOT NULL
) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS installs_last_seen ON installs(last_seen);
CREATE TABLE IF NOT EXISTS days (
  day TEXT PRIMARY KEY, active INTEGER NOT NULL, installs INTEGER NOT NULL, seconds INTEGER NOT NULL, fg_seconds INTEGER NOT NULL
) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS download_totals (day TEXT PRIMARY KEY, installers INTEGER NOT NULL, updates INTEGER NOT NULL) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS meta (k TEXT PRIMARY KEY, v TEXT NOT NULL) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS support_events (
  seq INTEGER PRIMARY KEY AUTOINCREMENT, at INTEGER NOT NULL, kind TEXT NOT NULL, ticket TEXT NOT NULL, cat TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS support_events_ticket ON support_events(ticket);
CREATE TABLE IF NOT EXISTS counters (name TEXT PRIMARY KEY, n INTEGER NOT NULL, exp INTEGER NOT NULL) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS counters_exp ON counters(exp);
CREATE TABLE IF NOT EXISTS audit_log (
  seq INTEGER PRIMARY KEY AUTOINCREMENT, at INTEGER NOT NULL, who TEXT NOT NULL, action TEXT NOT NULL, detail TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS audit_log_at ON audit_log(at);
CREATE TABLE IF NOT EXISTS admin_access (
  seq INTEGER PRIMARY KEY AUTOINCREMENT, at INTEGER NOT NULL, ip TEXT NOT NULL, route TEXT NOT NULL, action TEXT NOT NULL, target TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS admin_access_at ON admin_access(at);
CREATE TABLE IF NOT EXISTS preorders (
  id TEXT PRIMARY KEY, email TEXT NOT NULL, plan TEXT NOT NULL, lang TEXT NOT NULL, at INTEGER NOT NULL
) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS preorders_at ON preorders(at);
`;

const koreanDay = ms => new Date(ms + 9 * 3600e3).toISOString().slice(0, 10);

/** The ping as stored, or null for anything an app would not send. */
export function cleanPing(b, country) {
  if (!b || !/^[0-9a-f]{32}$/.test(b.id) || !/^\d{1,3}\.\d{1,3}\.\d{1,3}$/.test(b.ver) || !["win", "mac"].includes(b.os)
      || !["free", "premium", "ultra", "biz"].includes(b.plan) || !MULTIPLIERS.includes(b.fg) || ![0, 1].includes(b.boost)) return null;
  return { id: b.id, ver: b.ver, os: b.os, plan: b.plan, fg: b.fg, boost: b.boost, country: /^[A-Z]{2}$/.test(country || "") ? country : "XX" };
}

// Each installer ships under two names: the download page links the fixed one, the packager makes the other.
// RELEASES and releases.*.json are read by every update check, so they are not downloads.
function fileKind(name) {
  if (name.endsWith(".nupkg")) return "updates";
  if (/win(dows)?-setup\.exe$/i.test(name)) return "winSetup";
  if (/win(dows)?-portable\.zip$/i.test(name)) return "winPortable";
  if (name.endsWith(".pkg") || /osx.*portable\.zip$/i.test(name)) return "mac";
  return null;
}

/** GitHub's release list reduced to counts per file kind. Throws on anything that is not a release list. */
export function countDownloads(list) {
  const total = { winSetup: 0, winPortable: 0, mac: 0, updates: 0 };
  const releases = list.filter(r => !r.draft).map(r => {
    const c = { tag: String(r.tag_name), published: String(r.published_at || ""), winSetup: 0, winPortable: 0, mac: 0, updates: 0 };
    for (const a of r.assets) {
      const kind = fileKind(String(a.name));
      if (kind) { c[kind] += Number(a.download_count) || 0; total[kind] += Number(a.download_count) || 0; }
    }
    return c;
  });
  return { total, installers: total.winSetup + total.winPortable + total.mac, releases };
}

function ranked(counts, keep = 8) {
  const all = Object.entries(counts).map(([k, n]) => ({ k, n })).sort((a, b) => b.n - a.n);
  if (all.length <= keep) return all;
  const rest = all.slice(keep - 1).reduce((sum, e) => sum + e.n, 0);
  return [...all.slice(0, keep - 1), { k: "", n: rest }];   // "" = 기타
}

const reply = (o, status = 200) => new Response(JSON.stringify(o), { status, headers: { "content-type": "application/json" } });

export class Stats {
  constructor(state, env) {
    this.storage = state.storage;   // setAlarm and getAlarm for the pre-registration alert (the tests' fake has none)
    this.env = env;                 // KV for that alert's webhook
    this.sql = state.storage.sql;
    this.sql.exec(SCHEMA);
    this.scan = null;           // last full scan of recent installs
    this.githubTried = 0;
    this.githubError = "";
  }

  one(query, ...args) { return this.sql.exec(query, ...args).toArray()[0]; }

  async fetch(req) {
    const path = new URL(req.url).pathname;
    const body = req.method === "POST" ? await req.json() : null;
    if (path === "/ping") return reply(this.ping(body));
    if (path === "/read") return reply(await this.read());
    if (path === "/downloads") { this.saveDownloads(body); return reply({ ok: true }); }
    if (path === "/tick") { await this.tick(); return reply({ ok: true }); }
    if (path === "/event") { const r = this.event(body); return reply(r, r.error ? 400 : 200); }
    if (path === "/feed") return reply(this.feed(body));
    if (path === "/forget") { this.sql.exec("DELETE FROM support_events WHERE ticket = ?", String(body?.ticket || "")); return reply({ ok: true }); }
    if (path === "/take") { const r = this.take(body); return reply(r, r.error ? 400 : 200); }
    if (path === "/peek") { const r = this.peek(body); return reply(r, r.error ? 400 : 200); }
    if (path === "/gate") { const r = this.gate(body); return reply(r, r.error ? 400 : 200); }
    if (path === "/audit") { const r = this.audit(body); return reply(r, r.error ? 400 : 200); }
    if (path === "/audit/read") return reply(this.auditRead());
    if (path === "/access") { const r = this.access(body, Date.now()); return reply(r, r.error ? 400 : 200); }
    if (path === "/access/read") return reply(this.accessRead(body));
    if (path.startsWith("/pre/")) {
      const r = this.pre(path, body || {});
      if (r.added) { delete r.added; await this.preWake(Date.now()); }
      return reply(r, r.error && r.error !== "full" && r.error !== "ip" ? 400 : 200);
    }
    return reply({ error: "not found" }, 404);
  }

  ping(p) {
    const t = Date.now(), day = koreanDay(t);
    const today = this.one("SELECT active, installs FROM days WHERE day = ?", day) || { active: 0, installs: 0 };
    const next = pace(today.active);
    const old = this.one("SELECT last_seen FROM installs WHERE id = ?", p.id);
    if (old ? t - old.last_seen < QUIET_MS : today.installs >= MAX_NEW_A_DAY) return { next };
    const gap = old ? t - old.last_seen : Infinity;
    const seconds = gap <= liveSeconds(next) * 1000 ? Math.round(gap / 1000) : 0;   // a longer gap: the app was closed
    this.sql.exec(`INSERT INTO installs (id, first_seen, last_seen, os, ver, plan, fg, boost, country) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
                   ON CONFLICT(id) DO UPDATE SET last_seen = excluded.last_seen, os = excluded.os, ver = excluded.ver, plan = excluded.plan,
                                                 fg = excluded.fg, boost = excluded.boost, country = excluded.country`,
                  p.id, t, t, p.os, p.ver, p.plan, p.fg, p.boost, p.country);
    this.sql.exec(`INSERT INTO days (day, active, installs, seconds, fg_seconds) VALUES (?, ?, ?, ?, ?)
                   ON CONFLICT(day) DO UPDATE SET active = active + excluded.active, installs = installs + excluded.installs,
                                                  seconds = seconds + excluded.seconds, fg_seconds = fg_seconds + excluded.fg_seconds`,
                  day, !old || koreanDay(old.last_seen) !== day ? 1 : 0, old ? 0 : 1, seconds, p.fg ? seconds : 0);
    return { next };
  }

  async read() {
    const t = Date.now();
    if (t - this.githubTried > GITHUB_MS) await this.askGitHub();
    const today = this.one("SELECT active, installs, seconds, fg_seconds FROM days WHERE day = ?", koreanDay(t)) || { active: 0, installs: 0, seconds: 0, fg_seconds: 0 };
    const liveFor = liveSeconds(pace(today.active));
    const live = { total: 0, frameGen: 0, boost: 0, multipliers: {} };
    for (const r of this.sql.exec("SELECT fg, boost, COUNT(*) AS n FROM installs WHERE last_seen > ? GROUP BY fg, boost", t - liveFor * 1000)) {
      live.total += r.n;
      if (r.boost) live.boost += r.n;
      if (r.fg) { live.frameGen += r.n; live.multipliers[r.fg] = (live.multipliers[r.fg] || 0) + r.n; }
    }
    if (!this.scan || t - this.scan.at > SCAN_MS) this.scan = this.scanInstalls(t);
    const saved = this.one("SELECT v FROM meta WHERE k = 'downloads'");
    return { now: t, liveFor, live, today: { active: today.active, installs: today.installs, seconds: today.seconds, frameGenSeconds: today.fg_seconds },
             ...this.scan, installs: this.one("SELECT COALESCE(SUM(installs), 0) AS n FROM days").n, history: this.history(t),
             downloads: saved ? JSON.parse(saved.v) : null, downloadsError: this.githubError };
  }

  scanInstalls(t) {
    const counts = { plan: {}, os: {}, ver: {}, country: {} };
    let week = 0, month = 0;
    const rows = this.sql.exec(`SELECT plan, os, ver, country, last_seen > ? AS this_week, COUNT(*) AS n FROM installs
                                WHERE last_seen > ? GROUP BY plan, os, ver, country, this_week`, t - 7 * DAY, t - 30 * DAY);
    for (const r of rows) {
      month += r.n;
      if (r.this_week) week += r.n;
      for (const field in counts) counts[field][r[field]] = (counts[field][r[field]] || 0) + r.n;
    }
    return { at: t, week, month, plans: ranked(counts.plan), systems: ranked(counts.os), versions: ranked(counts.ver), countries: ranked(counts.country) };
  }

  history(t) {
    const days = new Map(this.sql.exec("SELECT day, active, installs, seconds, fg_seconds FROM days ORDER BY day DESC LIMIT ?", HISTORY_DAYS).toArray().map(d => [d.day, d]));
    const totals = this.sql.exec("SELECT day, installers, updates FROM download_totals ORDER BY day DESC LIMIT ?", HISTORY_DAYS + 2).toArray().reverse();
    const before = day => totals.filter(x => x.day < day).at(-1);   // the last total of an earlier day
    const upTo = day => totals.filter(x => x.day <= day).at(-1);
    const out = [];
    for (let i = HISTORY_DAYS - 1; i >= 0; i--) {
      const day = koreanDay(t - i * DAY), d = days.get(day), end = upTo(day), start = before(day);
      out.push({ day, active: d?.active ?? 0, installs: d?.installs ?? 0, seconds: d?.seconds ?? 0, frameGenSeconds: d?.fg_seconds ?? 0,
                 // null before counting began: there is nothing to subtract on the first day
                 downloads: end && start ? Math.max(0, end.installers - start.installers) : null,
                 updates: end && start ? Math.max(0, end.updates - start.updates) : null });
    }
    return out;
  }

  // A Worker's address is shared with other sites, so GitHub may refuse it when its hourly limit is used up:
  // the error goes to the dashboard, which then asks from the owner's browser and posts the list to /downloads.
  async askGitHub() {
    this.githubTried = Date.now();
    try {
      const res = await fetch(`https://api.github.com/repos/${REPO}/releases?per_page=100`,
                              { headers: { Accept: "application/vnd.github+json", "User-Agent": "SystemBoost-license-worker" } });
      if (!res.ok) { this.githubError = "GitHub " + res.status; return; }
      this.saveDownloads(await res.json());
      this.githubError = "";
    } catch (e) { this.githubError = "GitHub 연결 실패"; }
  }

  saveDownloads(list) {
    const counts = countDownloads(list), t = Date.now();
    this.sql.exec("INSERT OR REPLACE INTO meta (k, v) VALUES ('downloads', ?)", JSON.stringify({ at: t, ...counts }));
    this.sql.exec("INSERT OR REPLACE INTO download_totals (day, installers, updates) VALUES (?, ?, ?)", koreanDay(t), counts.installers, counts.total.updates);
  }

  async tick() {
    // the local deletions first: the dated ones (pre-registration rows, an hour's address hashes) must not wait on GitHub
    this.prePurge();
    this.sql.exec("DELETE FROM installs WHERE last_seen < ?", Date.now() - KEEP_DAYS * DAY);
    this.pruneFeed();
    this.pruneGuard();
    await this.askGitHub();
  }

  // ---- pre-registration (src/preorder.js): one row per email, every row deleted at PRE.deleteAt.
  // The free plan reads 5 million rows a day (rows written: 100,000; an index entry counts as a row), and the dashboard
  // reads /pre/read every 60 s while it is open, so neither that nor a sign-up walks the table: the totals (all, by plan,
  // by language, today's in Korean time, new since 확인) are one meta row, pre_totals, changed with every insert and
  // delete, and "recent" and "new" go through the index on at. When pre_totals is missing (the first call after this
  // version on a table that has rows, or after the purge) it is counted again from the table once; an export, which
  // reads every row anyway, counts it again too (so a rollback to a version without it cannot leave it wrong for long).
  // The per-address counts run in hour windows that end at minute 7, when the hourly cron (wrangler.toml "7 * * * *")
  // runs the tick, whose pruneGuard deletes them: a hash is kept about an hour after the last sign-up, as /privacy says.
  prePurge(t = Date.now()) {
    if (t < Date.parse(PRE.deleteAt)) return;
    this.sql.exec("DELETE FROM preorders");
    this.sql.exec("DELETE FROM meta WHERE k IN ('pre_seen', 'pre_totals', 'pre_noted', 'pre_note_err')");
  }
  preSeen() { return Number(this.one("SELECT v FROM meta WHERE k = 'pre_seen'")?.v || 0); }
  /** Count one row {plan, lang, at} into the totals s (by 1) or out of them (by -1). */
  preTally(s, r, by, t, seen) {
    const add = (o, k) => { o[k] = (o[k] || 0) + by; if (o[k] <= 0) delete o[k]; };
    s.n = Math.max(0, s.n + by);
    add(s.plan, r.plan);
    add(s.lang, r.lang);
    if (r.at >= Date.parse(s.day + "T00:00:00+09:00")) s.today = Math.max(0, s.today + by);
    if (r.at > seen) s.fresh = Math.max(0, s.fresh + by);
  }
  preCount(rows, t, seen = this.preSeen()) {
    const s = { n: 0, plan: {}, lang: {}, day: koreanDay(t), today: 0, fresh: 0 };
    for (const r of rows) this.preTally(s, r, 1, t, seen);
    return s;
  }
  preSave(s) { this.sql.exec("INSERT INTO meta (k, v) VALUES ('pre_totals', ?) ON CONFLICT(k) DO UPDATE SET v = excluded.v", JSON.stringify(s)); }
  preTotals(t) {
    let s = null;
    try { s = JSON.parse(this.one("SELECT v FROM meta WHERE k = 'pre_totals'")?.v || "null"); } catch { /* counted again below */ }
    const whole = o => o && typeof o === "object" && !Array.isArray(o);
    if (!(whole(s) && Number.isInteger(s.n) && Number.isInteger(s.today) && Number.isInteger(s.fresh) && whole(s.plan) && whole(s.lang)
          && typeof s.day === "string")) {
      s = this.preCount(this.sql.exec("SELECT plan, lang, at FROM preorders").toArray(), t);   // the one full pass
      this.preSave(s);
    } else if (s.day !== koreanDay(t)) {   // a new day in Korea: today's rows through the index (none just after midnight)
      s.day = koreanDay(t);
      s.today = this.one("SELECT COUNT(*) AS n FROM preorders WHERE at >= ?", Date.parse(s.day + "T00:00:00+09:00")).n;
      this.preSave(s);
    }
    return s;
  }
  pre(path, b, t = Date.now()) {
    this.prePurge(t);
    if (path === "/pre/add") {
      if (!/^[0-9a-f]{32}$/.test(b.id) || typeof b.email !== "string" || b.email.length > 254 || !PRE.plans.includes(b.plan) || !PRE.langs.includes(b.lang)
          || !/^[0-9a-f]{16}$/.test(b.ip) || !/^[0-9a-f]{16}$/.test(b.net))
        return { error: "bad" };
      if (t >= Date.parse(PRE.deleteAt)) return { error: "full" };
      // the day's cap before "already": on a full day an address on the list gets the same "full" as a new one
      const s = this.preTotals(t), max = Number.isInteger(b.max) && b.max > 0 ? b.max : PRE.perDay;
      if (s.today >= max) return { error: "full" };
      // then this address (IPv4, IPv6 /64) and its network (IPv4 /24, IPv6 /48) for the hour, counted in this same step:
      // salted hashes from the Worker, never an address. Counted for an address on the list too.
      this.sql.exec("DELETE FROM counters WHERE exp <= ? AND name LIKE 'pre:%'", t);
      const start = Math.floor((t - 7 * MINUTE) / HOUR) * HOUR + 7 * MINUTE, ttl = Math.max(1, Math.ceil((start + HOUR - t) / 1000));
      const w = new Date(start).toISOString().slice(0, 16);
      const lim = this.take({ items: [{ name: `pre:ip:${b.ip}:${w}`, ttl, max: PRE.perIpHour }, { name: `pre:net:${b.net}:${w}`, ttl, max: PRE.perNetHour }] });
      if (lim.ok !== true) return { error: "ip" };
      if (this.one("SELECT 1 AS x FROM preorders WHERE id = ?", b.id)) return { ok: true, already: true };
      this.sql.exec("INSERT INTO preorders (id, email, plan, lang, at) VALUES (?, ?, ?, ?, ?)", b.id, b.email, b.plan, b.lang, t);
      this.preTally(s, { plan: b.plan, lang: b.lang, at: t }, 1, t, this.preSeen());
      this.preSave(s);
      return { ok: true, added: true };   // fetch drops `added` after waking the alert
    }
    if (path === "/pre/read") {
      const seen = this.preSeen(), s = this.preTotals(t);
      const n = Math.min(Math.max(Number(b.recent) || 0, 0), 500);
      const recent = this.sql.exec("SELECT plan, lang, at FROM preorders ORDER BY at DESC LIMIT ?", n).toArray()
        .map(r => ({ plan: r.plan, lang: r.lang, at: new Date(r.at).toISOString(), fresh: r.at > seen }));
      return { count: s.n, fresh: s.fresh, today: s.today, byPlan: { ...s.plan }, byLang: { ...s.lang }, recent, note: this.preNoteErr() };
    }
    if (path === "/pre/noted") {
      // the alert was (re)connected: it starts from now, and an old error is forgotten
      this.preMeta("pre_noted", String(Math.max(this.one("SELECT MAX(at) AS m FROM preorders").m || 0, this.preNoted())));
      this.sql.exec("DELETE FROM meta WHERE k = 'pre_note_err'");
      return { ok: true };
    }
    if (path === "/pre/seen") {
      // up to the newest row the dashboard showed (upTo, when given), so a sign-up that came after its last refresh
      // stays new; never earlier than a 확인 already stored (another, older tab)
      const s = this.preTotals(t), newest = this.one("SELECT MAX(at) AS m FROM preorders").m || t;
      const seen = Math.max(this.preSeen(), Math.min(newest, Number.isFinite(b.upTo) ? b.upTo : Infinity));
      this.sql.exec("INSERT INTO meta (k, v) VALUES ('pre_seen', ?) ON CONFLICT(k) DO UPDATE SET v = excluded.v", String(seen));
      s.fresh = this.one("SELECT COUNT(*) AS n FROM preorders WHERE at > ?", seen).n;   // through the index: the newer rows only
      this.preSave(s);
      return { ok: true };
    }
    if (path === "/pre/export") {
      const rows = this.sql.exec("SELECT email, plan, lang, at FROM preorders ORDER BY at").toArray();
      this.preSave(this.preCount(rows, t));
      return { rows };
    }
    if (path === "/pre/delete") {
      if (!/^[0-9a-f]{32}$/.test(b.id)) return { error: "bad" };
      const r = this.one("SELECT plan, lang, at FROM preorders WHERE id = ?", b.id);
      if (r) {
        const s = this.preTotals(t);   // before the delete: counted again, it still holds the row
        this.sql.exec("DELETE FROM preorders WHERE id = ?", b.id);
        this.preTally(s, r, -1, t, this.preSeen());
        this.preSave(s);
      }
      return { ok: true, deleted: !!r };
    }
    return { error: "bad" };
  }
  preMeta(k, v) { this.sql.exec("INSERT INTO meta (k, v) VALUES (?, ?) ON CONFLICT(k) DO UPDATE SET v = excluded.v", k, v); }
  preNoted() { return Number(this.one("SELECT v FROM meta WHERE k = 'pre_noted'")?.v || 0); }
  /** The last failed alert {at, since, error, gone} (the dashboard shows it), or null. */
  preNoteErr() { try { return JSON.parse(this.one("SELECT v FROM meta WHERE k = 'pre_note_err'")?.v || "null"); } catch { return null; } }
  // A new row: one alarm PRE.noteWait later (one already set takes this row too). Only where storage has alarms and the
  // object has KV, i.e. on Cloudflare; a failure here never fails the sign-up.
  async preWake(t) {
    if (typeof this.storage?.setAlarm !== "function" || !this.env?.LICENSES) return;
    try { if ((await this.storage.getAlarm()) == null) await this.storage.setAlarm(t + PRE.noteWait); } catch { /* the next sign-up tries again */ }
  }
  async alarm() {
    try { await this.preNote(Date.now()); } catch { /* the next sign-up wakes it again */ }
  }
  // Every row since the last alert, as one message of counts. No webhook: those rows are passed over, so connecting one
  // later does not send them as new (saving one sets pre_noted too).
  async preNote(t) {
    this.prePurge(t);
    const rows = this.sql.exec("SELECT plan, lang, at FROM preorders WHERE at > ? ORDER BY at", this.preNoted()).toArray();
    if (!rows.length) return;
    const last = String(rows[rows.length - 1].at);
    const hook = await this.env?.LICENSES?.get(PRE_HOOK);
    if (!hook) { this.preMeta("pre_noted", last); return; }
    const r = await postText(hook, preNoteText(rows, this.preTotals(t)));
    if (!r.error) { this.preMeta("pre_noted", last); this.sql.exec("DELETE FROM meta WHERE k = 'pre_note_err'"); return; }
    const old = this.preNoteErr(), since = old?.since || t, gone = r.status === 404;
    this.preMeta("pre_note_err", JSON.stringify({ at: new Date(t).toISOString(), since: since, error: String(r.error).slice(0, 200), gone }));
    if (!gone && t - since < PRE.noteGiveUp && typeof this.storage?.setAlarm === "function") await this.storage.setAlarm(t + PRE.noteRetry);
  }
  pruneGuard(t = Date.now()) {
    this.sql.exec("DELETE FROM counters WHERE exp <= ?", t);
    this.sql.exec("DELETE FROM audit_log WHERE at < ?", t - GUARD.auditDays * DAY);
    this.sql.exec("DELETE FROM admin_access WHERE at < ?", t - GUARD.accessDays * DAY);
  }

  // ---- counters
  // A count that has passed its end time is 0. Every change sets the end time to now + ttl (so a 15-minute
  // lockout runs from the last miss); a name that carries its hour or day ends with that window anyway.
  value(name, t) {
    const r = this.one("SELECT n, exp FROM counters WHERE name = ?", name);
    return r && r.exp > t ? r.n : 0;
  }
  bump(name, ttl, by, t) {
    this.sql.exec(`INSERT INTO counters (name, n, exp) VALUES (?, ?, ?)
                   ON CONFLICT(name) DO UPDATE SET n = CASE WHEN counters.exp > ? THEN counters.n + excluded.n ELSE excluded.n END, exp = excluded.exp`,
                  name, by, t + ttl * 1000, t);
  }
  // items: [{name, ttl (seconds), max?, by?}]. All of them or none: when one would pass its max, nothing is counted
  // and the answer names it. n: the counts after (or, refused, before) this call.
  take(b) {
    const items = Array.isArray(b?.items) ? b.items : null;
    if (!items || !items.length || items.length > GUARD.maxItems) return { error: "bad items" };
    for (const it of items) {
      if (!it || !NAME_RE.test(it.name) || !Number.isInteger(it.ttl) || it.ttl < 1 || it.ttl > 400 * 86400
          || (it.max != null && !(Number.isInteger(it.max) && it.max >= 0)) || (it.by != null && !(Number.isInteger(it.by) && it.by >= 1 && it.by <= 1000)))
        return { error: "bad items" };
    }
    const t = Date.now(), n = {};
    for (const it of items) n[it.name] = this.value(it.name, t);
    for (const it of items) if (it.max != null && n[it.name] + (it.by || 1) > it.max) return { ok: false, over: it.name, n };
    for (const it of items) { this.bump(it.name, it.ttl, it.by || 1, t); n[it.name] += it.by || 1; }
    return { ok: true, n };
  }
  peek(b) {
    const names = Array.isArray(b?.names) ? b.names : null;
    if (!names || !names.length || names.length > GUARD.maxItems || !names.every(x => NAME_RE.test(x))) return { error: "bad names" };
    const t = Date.now(), n = {};
    for (const x of names) n[x] = this.value(x, t);
    return { n };
  }

  // ---- the admin lockout and access log in one call per admin request (src/index.js admin()).
  // {ip, hour, ipMax, allMax, fail: true}: a wrong password; both counts go up, the new counts come back.
  // {ip, hour, ipMax, allMax, route, action, target}: the right password; refused (locked) while either count is at
  // its max, else the access is written: a "login" row when this address had no successful call for
  // GUARD.sessionMinutes, and a row for the personal data the route shows (action, when given).
  gate(b) {
    if (!b || !/^[0-9a-f]{16}$/.test(b.ip) || !/^\d{4}-\d{2}-\d{2}T\d{2}$/.test(b.hour) || !Number.isInteger(b.ipMax) || !Number.isInteger(b.allMax))
      return { error: "bad gate" };
    const t = Date.now(), ipName = "adm:ip:" + b.ip, allName = "adm:all:" + b.hour;
    if (b.fail === true) {
      this.bump(ipName, 15 * 60, 1, t);
      this.bump(allName, 2 * 3600, 1, t);
      return { ip: this.value(ipName, t), all: this.value(allName, t) };
    }
    const ip = this.value(ipName, t), all = this.value(allName, t);
    if (ip >= b.ipMax || all >= b.allMax) return { locked: true, ip, all };
    return { ok: true, ...this.access(b, t) };
  }
  access(b, t) {
    if (!b || !/^[0-9a-f]{16}$/.test(b.ip)) return { error: "bad access" };
    const route = str(b.route, 80), sess = "sess:" + b.ip;
    let login = false;
    const left = this.one("SELECT exp FROM counters WHERE name = ?", sess)?.exp || 0;
    if (left <= t) {
      login = true;
      this.sql.exec("INSERT INTO admin_access (at, ip, route, action, target) VALUES (?, ?, ?, 'login', '')", t, b.ip, route);
    }
    // the session's end moves on at most every 5 minutes, so a busy dashboard does not write a row a request
    if (left - t < (GUARD.sessionMinutes - 5) * 60e3) this.bump(sess, GUARD.sessionMinutes * 60, 1, t);
    if (b.action) this.sql.exec("INSERT INTO admin_access (at, ip, route, action, target) VALUES (?, ?, ?, ?, ?)", t, b.ip, route, str(b.action, 40), str(b.target, 80));
    if (login || b.action) this.sql.exec("DELETE FROM admin_access WHERE at < ?", t - GUARD.accessDays * DAY);
    return { login };
  }
  accessRead(b) {
    const before = Number.isSafeInteger(b?.before) && b.before > 0 ? b.before : Number.MAX_SAFE_INTEGER;
    const rows = this.sql.exec("SELECT seq, at, ip, route, action, target FROM admin_access WHERE seq < ? AND at >= ? ORDER BY seq DESC LIMIT ?",
                               before, Date.now() - GUARD.accessDays * DAY, GUARD.page + 1).toArray();
    return { rows: rows.slice(0, GUARD.page).map(r => ({ seq: r.seq, at: r.at, ip: r.ip, route: r.route, action: r.action, target: r.target })),
             more: rows.length > GUARD.page, keepDays: GUARD.accessDays };
  }

  // ---- the dashboard's 기록 (src/index.js audit(); the detail is already cut down to what the record needs)
  audit(b) {
    if (!b || typeof b.action !== "string" || !b.action) return { error: "bad audit" };
    const t = Date.now();
    this.sql.exec("INSERT INTO audit_log (at, who, action, detail) VALUES (?, ?, ?, ?)", t, str(b.who, 20), str(b.action, 80), str(b.detail, 300));
    this.sql.exec("DELETE FROM audit_log WHERE at < ?", t - GUARD.auditDays * DAY);
    return { ok: true };
  }
  auditRead() {
    const rows = this.sql.exec("SELECT at, who, action, detail FROM audit_log WHERE at >= ? ORDER BY seq DESC LIMIT ?",
                               Date.now() - GUARD.auditDays * DAY, GUARD.page).toArray();
    return { log: rows.map(r => ({ at: new Date(r.at).toISOString().replace(/\.\d+Z$/, "Z"), who: r.who, action: r.action, detail: r.detail })) };
  }

  // ---- support feed
  event(e) {
    if (!e || !FEED_KINDS.includes(e.kind) || !TICKET_RE.test(e.ticket)) return { error: "bad event" };
    this.sql.exec("INSERT INTO support_events (at, kind, ticket, cat) VALUES (?, ?, ?, ?)", Date.now(), e.kind, e.ticket,
                  /^[a-z]{1,12}$/.test(e.cat) ? e.cat : "");
    this.pruneFeed();
    return { ok: true };
  }
  pruneFeed() {
    this.sql.exec(`DELETE FROM support_events WHERE at < ?
                   OR seq <= (SELECT seq FROM support_events ORDER BY seq DESC LIMIT 1 OFFSET ?)`, Date.now() - FEED.keepDays * DAY, FEED.keepRows);
  }
  // after: the last seq the dashboard has; the rows after it, oldest first, at most FEED.page. Without it the newest
  // FEED.recent. last: what to send as after next time. It is below after when the feed started again (an empty
  // object), so the dashboard simply goes on from there.
  feed(b) {
    const since = Date.now() - FEED.keepDays * DAY, after = b?.after;
    const head = this.one("SELECT COALESCE(MAX(seq), 0) AS n FROM support_events").n;
    const rows = Number.isSafeInteger(after) && after >= 0
      ? this.sql.exec("SELECT seq, at, kind, ticket, cat FROM support_events WHERE seq > ? AND at >= ? ORDER BY seq LIMIT ?", after, since, FEED.page).toArray()
      : this.sql.exec("SELECT seq, at, kind, ticket, cat FROM support_events WHERE at >= ? ORDER BY seq DESC LIMIT ?", since, FEED.recent).toArray().reverse();
    const last = rows.length ? rows.at(-1).seq : head;
    return { events: rows.map(r => ({ seq: r.seq, at: r.at, kind: r.kind, ticket: r.ticket, cat: r.cat })), last, more: last < head };
  }
}
