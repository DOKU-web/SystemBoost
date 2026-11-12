// Per-IP limits for the public order and payment routes (index.js), without a single KV write.
// The same idea as src/support.js: an hour's counter per IP address (IPv6 per /64) lives in this isolate's memory and in
// the Cache API (per data center). Cloudflare promises working Cache API calls only on custom domains, so on
// *.workers.dev the memory copy is what holds; both are approximate, and the daily KV caps stay the backstop.
// An address is used only as the first 16 hex of SHA-256 of a value made from the signing key, a label and the address,
// so the counter names cannot be mapped back to addresses without that secret (tests have none: plain form).
import { ipBucket } from "./support.js";

const mem = new Map();   // name -> { n, exp }
let swept = 0;
export function _forgetIpLimits() { mem.clear(); }   // tests: as if a new isolate answered

async function sha(s) {
  const h = new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(s)));
  return [...h].map(b => b.toString(16).padStart(2, "0")).join("");
}
let salt = null, saltOf = null;
async function tag(req, env, what) {
  const k = env?.LICENSE_PRIVATE_KEY || "";
  if (saltOf !== k) { salt = k ? await sha("systemboost-pay-ip|" + k) : ""; saltOf = k; }
  return (await sha(salt + "|" + what + "|" + ipBucket(req.headers.get("CF-Connecting-IP")))).slice(0, 16);
}
function edge() { try { return globalThis.caches?.default || null; } catch { return null; } }

/** One more call of `what` from this request's address this hour; true when that is over `max` (the call is refused). */
export async function overIp(req, env, what, max) {
  const hour = new Date().toISOString().slice(0, 13);
  const name = "pl:" + what + ":" + (await tag(req, env, what)) + ":" + hour;
  const t = Date.now();
  if (t - swept > 60000) {   // an hour's counters go within a minute of the hour's end
    swept = t;
    for (const [k, m] of mem) if (m.exp <= t) mem.delete(k);
  }
  if (mem.size > 5000) for (const k of [...mem.keys()].slice(0, mem.size - 4000)) mem.delete(k);
  const m = mem.get(name);
  let n = m && m.exp > t ? m.n : 0;
  const c = edge(), url = new URL(req.url).origin + "/__pay-limit/" + name;
  if (c) try { const r = await c.match(url); if (r) n = Math.max(n, Number(await r.text()) || 0); } catch {}
  if (n >= max) return true;
  mem.set(name, { n: n + 1, exp: t + 3600 * 1000 });
  if (c) try { await c.put(url, new Response(String(n + 1), { headers: { "Cache-Control": "max-age=3600" } })); } catch {}
  return false;
}
