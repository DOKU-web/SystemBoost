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

const REPO = "DOKU-web/SystemBoost";
const MINUTE = 60e3, DAY = 864e5;
const KEEP_DAYS = 90;          // an install silent this long is deleted; the daily totals stay
const QUIET_MS = 20e3;         // a ping this soon after the last one writes nothing
const MAX_NEW_A_DAY = 3000;    // cheap cap on made-up installs
const SCAN_MS = 5 * MINUTE;    // the numbers that scan every recent install are reused this long
const GITHUB_MS = 5 * MINUTE;  // the dashboard asks GitHub at most this often
const HISTORY_DAYS = 30;
const MULTIPLIERS = [0, 2, 3, 4, 6, 8];

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
`;

const koreanDay = ms => new Date(ms + 9 * 3600e3).toISOString().slice(0, 10);

/** The ping as stored, or null for anything an app would not send. */
export function cleanPing(b, country) {
  if (!b || !/^[0-9a-f]{32}$/.test(b.id) || !/^\d{1,3}\.\d{1,3}\.\d{1,3}$/.test(b.ver) || !["win", "mac"].includes(b.os)
      || !["free", "premium", "ultra"].includes(b.plan) || !MULTIPLIERS.includes(b.fg) || ![0, 1].includes(b.boost)) return null;
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
  constructor(state) {
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
    await this.askGitHub();
    this.sql.exec("DELETE FROM installs WHERE last_seen < ?", Date.now() - KEEP_DAYS * DAY);
  }
}
