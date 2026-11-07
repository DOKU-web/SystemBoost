// Discord announcements from the dashboard.
//
// A webhook address is enough to post in the channel, and this repository and the dashboard page are public. So the
// addresses stay on the server: the owner enters them in the dashboard, they live in KV, and every call to Discord
// happens here. No reply, error or log line carries one; the page only learns whether a channel is connected.

const HOOKS = "config:discord_hooks", HIST = "announce:hist", KEEP = 50;
const BOT = "SystemBoost";
const UA = "DiscordBot (https://github.com/DOKU-web/SystemBoost, 1.0)";
const HOOK_RE = /^https:\/\/(?:ptb\.|canary\.)?discord(?:app)?\.com\/api(?:\/v\d+)?\/webhooks\/\d+\/[\w-]+$/;
const MAX_WAIT = 5;   // seconds; Discord can ask for longer than anyone should watch a button for

// Channel and role numbers are visible to everyone on the server; they open nothing without a webhook.
const CHANNELS = [
  { key: "ann", name: "📣・announcements", id: "1568551676565258280" },
  { key: "log", name: "📝・changelog", id: "1568551691924541491" },
  { key: "road", name: "🗺️・roadmap", id: "1568551707221295145" },
  { key: "dl", name: "⬇️・download", id: "1568551722891354304" },
  { key: "faq", name: "❓・faq", id: "1568551785428164618" },
  { key: "rules", name: "rules", id: "1568489319247777813" },
  { key: "beta", name: "🧪・beta-news", id: "1568552111866650634" },
];
const ROLES = {
  ann: { id: "1568490088395046953", name: "🔔 Announcements" },
  log: { id: "1568490110952153189", name: "🔔 Changelog" },
  beta: { id: "1568490022242623589", name: "🧪 Beta Tester" },
  premium: { id: "1568489973823447070", name: "⭐ Premium" },
};
const OTHERS = {   // only so the preview can name a channel a post links to
  "1568551659527741451": "👋・welcome", "1568551800800280576": "🆘・help", "1568551818311766107": "🐞・bug-reports",
  "1568551836343083028": "💡・suggestions", "1568551916663869460": "🎮・game-compatibility", "1568551933969567834": "📊・benchmarks",
  "1568552127369056256": "📦・beta-builds", "1568552144016113764": "🗣️・beta-feedback", "1568552095852920832": "🛠️・premium-support",
};

const fail = (error, status = 400) => ({ error, status });
const sleep = ms => new Promise(r => setTimeout(r, ms));
const parsed = (text, empty) => { try { return JSON.parse(text) || empty; } catch { return empty; } };
const hooks = async env => parsed(await env.LICENSES.get(HOOKS), {});
const history = async env => parsed(await env.LICENSES.get(HIST), []);
const channels = h => CHANNELS.map(c => ({ ...c, set: !!h[c.key] }));
const titleOf = p => String(p.embeds[0]?.title || p.content || "(제목 없음)").slice(0, 200);
const REFUSED = "디스코드가 이 서버의 주소를 잠시 막았어요. 몇 분 뒤 다시 시도해 주세요";

// Workers answer the page, and the page reads 401 and 429 as "log in again", so Discord's own codes stop here.
async function discord(hook, method, path, body) {
  for (let i = 0; i < 3; i++) {
    let r;
    try {
      r = await fetch(hook + path, { method, headers: { "User-Agent": UA, ...(body ? { "Content-Type": "application/json" } : {}) }, body: body ? JSON.stringify(body) : undefined });
    } catch { return fail("디스코드에 연결하지 못했어요. 잠시 뒤 다시 시도해 주세요", 502); }
    if (r.status === 429) {
      // A Worker's address is shared with other sites. Discord's own limit is JSON; a refusal of the address is a web page.
      const wait = (await r.json().catch(() => null))?.retry_after;
      if (!(wait >= 0)) return fail(REFUSED, 503);
      if (wait > MAX_WAIT) return fail("요청이 너무 많아요. 잠시 뒤 다시 시도해 주세요", 503);
      await sleep(wait * 1000 + 300);
      continue;
    }
    if (r.status === 204) return { data: null };
    if (r.ok) return { data: await r.json() };
    if (r.status === 404) return fail("디스코드에서 찾지 못했어요", 404);
    if (r.status === 400) return fail("디스코드가 글을 받지 않았어요. 글자 수, 이미지 주소, 링크를 확인해 주세요", 502);
    return fail(`디스코드가 거절했어요 (${r.status})`, 502);
  }
  return fail("요청이 너무 많아요. 잠시 뒤 다시 시도해 주세요", 503);
}

// What goes out is built here, so a post cannot ping anyone unless the page said so.
function clean(p) {
  const out = { username: BOT, allowed_mentions: p?.allowed_mentions || { parse: [] }, embeds: Array.isArray(p?.embeds) ? p.embeds.slice(0, 1) : [] };
  if (typeof p?.content === "string" && p.content) out.content = p.content;
  return out;
}
const formOf = body => JSON.stringify(body.form || null).length <= 20000 ? body.form : null;

async function saveHooks(env, body, log) {
  const given = Object.entries(body.hooks || {}).map(([k, v]) => [CHANNELS.find(c => c.key === k), String(v || "").trim()]).filter(([c, v]) => c && v);
  if (!given.length) return fail("넣은 주소가 없어요");
  const notes = {}, good = [];
  await Promise.all(given.map(async ([c, hook]) => {
    if (!HOOK_RE.test(hook)) { notes[c.key] = "디스코드 웹후크 주소가 아니에요"; return; }
    const r = await discord(hook, "GET", "");   // reads the webhook; nothing is posted
    if (r.status === 404) { notes[c.key] = "디스코드에 없는 웹후크예요"; return; }
    if (r.error) notes[c.key] = "저장했지만 확인하지 못했어요: " + r.error;
    else if (r.data?.channel_id !== c.id) notes[c.key] = "저장했지만 다른 채널의 웹후크예요";
    good.push([c, hook]);
  }));
  const h = await hooks(env);
  for (const [c, hook] of good) h[c.key] = hook;
  if (good.length) {
    await env.LICENSES.put(HOOKS, JSON.stringify(h));
    await log("디스코드 웹후크", good.map(([c]) => c.key).join(", "));
  }
  return { ok: true, saved: good.length, channels: channels(h), notes };
}

async function send(env, body, log) {
  const c = CHANNELS.find(x => x.key === body.channel);
  if (!c) return fail("없는 채널이에요");
  const hook = (await hooks(env))[c.key];
  if (!hook) return fail(`#${c.name} 채널이 연결되지 않았어요. '디스코드 연결'에서 웹후크 주소를 넣어 주세요`);
  const p = clean(body.payload);
  if (!p.content && !p.embeds.length) return fail("보낼 내용이 없어요");
  const r = await discord(hook, "POST", "?wait=true", p);
  if (r.error) return r;
  const hist = [{ id: r.data.id, channel: c.key, title: titleOf(p), time: Date.now(), payload: p, form: formOf(body) }, ...await history(env)].slice(0, KEEP);
  await env.LICENSES.put(HIST, JSON.stringify(hist));
  await log("디스코드 공지", `#${c.name} · ${titleOf(p)}`);
  return { ok: true, hist };
}

async function edit(env, body, log) {
  const hist = await history(env), it = hist.find(x => x.id === body.id);
  if (!it) return fail("보낸 글 목록에 없어요", 404);
  const hook = (await hooks(env))[it.channel];
  if (!hook) return fail("이 채널이 연결되지 않았어요");
  const p = clean(body.payload);
  if (!p.content && !p.embeds.length) return fail("보낼 내용이 없어요");
  const { username, ...change } = p;
  // Discord keeps a field that is left out, so taking the text above a post away needs an empty one.
  const r = await discord(hook, "PATCH", "/messages/" + it.id, { ...change, content: p.content || "" });
  if (r.error) return r;
  Object.assign(it, { payload: p, title: titleOf(p), edited: Date.now(), form: formOf(body) });
  await env.LICENSES.put(HIST, JSON.stringify(hist));
  await log("디스코드 공지 수정", titleOf(p));
  return { ok: true, hist };
}

async function remove(env, body, log) {
  const hist = await history(env), it = hist.find(x => x.id === body.id);
  if (!it) return fail("보낸 글 목록에 없어요", 404);
  const hook = (await hooks(env))[it.channel];
  if (!hook) return fail("이 채널이 연결되지 않았어요");
  const r = await discord(hook, "DELETE", "/messages/" + it.id);
  if (r.error && r.status !== 404) return r;
  const left = hist.filter(x => x !== it);
  await env.LICENSES.put(HIST, JSON.stringify(left));
  await log("디스코드 공지 삭제", it.title);
  return { ok: true, hist: left, gone: !!r.error };   // gone: already removed, or sent by a webhook made anew since
}

export async function announce(env, route, body, log) {
  switch (route) {
    case "GET /announce": return { channels: channels(await hooks(env)), roles: ROLES, others: OTHERS, hist: await history(env) };
    case "POST /announce/hooks": return saveHooks(env, body, log);
    case "POST /announce/send": return send(env, body, log);
    case "POST /announce/edit": return edit(env, body, log);
    case "POST /announce/delete": return remove(env, body, log);
  }
  return fail("not found", 404);
}
