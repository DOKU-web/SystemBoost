// Local test of Discord announcements: fake KV, a fake Discord, and the worker's real routes.
//   node test_announce.mjs
// Global fetch is replaced before anything runs, so nothing here can reach Discord.
import assert from "node:assert/strict";
import worker from "./src/index.js";

const HOOK = n => `https://discord.com/api/webhooks/${100 + n}/fake-token-${n}`;
const CH = { ann: "1568551676565258280", log: "1568551691924541491", beta: "1568552111866650634" };

const kv = new Map();
const env = {
  LICENSES: {
    get: async k => kv.get(k) ?? null,
    getWithMetadata: async k => ({ value: kv.get(k) ?? null, metadata: null }),
    put: async (k, v) => { kv.set(k, v); },
    delete: async k => { kv.delete(k); },
    list: async ({ prefix, limit = 1000 }) => ({ keys: [...kv.keys()].filter(k => k.startsWith(prefix)).sort().slice(0, limit).map(name => ({ name })), list_complete: true }),
  },
  ADMIN_TOKEN: "T".repeat(32),
};

// ---- fake Discord
const calls = [], messages = new Map();
let nextId = 9000000, script = [];
const ok = (o, status = 200) => new Response(o === null ? null : JSON.stringify(o), { status });
globalThis.fetch = async (url, init = {}) => {
  url = String(url);
  assert.ok(url.startsWith("https://discord.com/"), "only the fake Discord is called: " + url);
  const body = init.body ? JSON.parse(init.body) : undefined;
  calls.push({ url, method: init.method || "GET", body, ua: init.headers?.["User-Agent"] });
  if (script.length) { const s = script.shift(); if (s === "throw") throw new TypeError("network failure at " + url); return s(); }
  const m = url.match(/\/webhooks\/(\d+)\/[^/?]+(?:\/messages\/(\d+))?/);
  if ((init.method || "GET") === "GET") return ok({ id: m[1], token: "fake", channel_id: { 101: CH.ann, 102: CH.log, 103: "999" }[m[1]] });
  if (init.method === "POST") { const id = String(++nextId); messages.set(id, body); return ok({ id, ...body }); }
  if (!messages.has(m[2])) return ok({ message: "Unknown Message" }, 404);
  if (init.method === "PATCH") { messages.set(m[2], body); return ok({ id: m[2], ...body }); }
  messages.delete(m[2]); return ok(null, 204);
};

const call = (path, init) => worker.fetch(new Request("https://lic.example" + path, init), env);
const adm = async (path, body) => {
  const r = await call("/admin/api" + path, { method: body ? "POST" : "GET", headers: { Authorization: "Bearer " + env.ADMIN_TOKEN, "Content-Type": "application/json" }, body: body ? JSON.stringify(body) : undefined });
  const text = await r.text();
  assert.ok(!text.includes("fake-token"), "a reply carried a webhook address: " + path);
  return { http: r.status, ...JSON.parse(text) };
};
const post = (title, extra = {}) => ({ embeds: [{ title, color: 1 }], ...extra });

// the password guards all of it
for (const [m, p] of [["GET", "/announce"], ["POST", "/announce/hooks"], ["POST", "/announce/send"], ["POST", "/announce/edit"], ["POST", "/announce/delete"]])
  assert.equal((await call("/admin/api" + p, { method: m, body: m === "POST" ? "{}" : undefined })).status, 401, p);
for (const k of [...kv.keys()]) if (k.startsWith("fail:")) kv.delete(k);   // those misses count towards the lockout

// nothing connected yet
let s = await adm("/announce");
assert.equal(s.channels.length, 7);
assert.ok(s.channels.every(c => c.set === false) && s.hist.length === 0 && s.roles.ann.id);
assert.match((await adm("/announce/send", { channel: "ann", payload: post("x") })).error, /연결되지 않았/);
assert.equal(calls.length, 0);

// saving addresses
assert.match((await adm("/announce/hooks", { hooks: {} })).error, /넣은 주소/);
assert.match((await adm("/announce/hooks", { hooks: { nope: HOOK(1) } })).error, /넣은 주소/);
let r = await adm("/announce/hooks", { hooks: { ann: "https://evil.example/api/webhooks/1/abc", log: "http://discord.com/api/webhooks/1/abc", beta: "https://discord.com/api/webhooks/1/a b" } });
assert.equal(r.saved, 0);
assert.match(r.notes.ann, /주소가 아니에요/);
assert.equal(calls.length, 0, "a bad address is never called");
assert.ok(!kv.has("config:discord_hooks"));

r = await adm("/announce/hooks", { hooks: { ann: HOOK(1), log: " " + HOOK(2) + " " } });
assert.equal(r.saved, 2);
assert.deepEqual(r.notes, {});
assert.deepEqual(calls.map(c => c.method), ["GET", "GET"], "checking a webhook posts nothing");
assert.deepEqual(r.channels.filter(c => c.set).map(c => c.key), ["ann", "log"]);
assert.ok((await adm("/announce")).channels.find(c => c.key === "ann").set);
assert.ok(!(await adm("/log")).log.some(l => JSON.stringify(l).includes("webhooks")));

// a webhook of another channel, one that is gone, and a check that could not run
r = await adm("/announce/hooks", { hooks: { faq: HOOK(3) } });
assert.equal(r.saved, 1);
assert.match(r.notes.faq, /다른 채널/);
script = [() => ok({ message: "Unknown Webhook" }, 404)];
r = await adm("/announce/hooks", { hooks: { rules: HOOK(4) } });
assert.equal(r.saved, 0);
assert.match(r.notes.rules, /없는 웹후크/);
assert.ok(!r.channels.find(c => c.key === "rules").set);
script = [() => new Response("<html>error code: 1015</html>", { status: 429 })];
r = await adm("/announce/hooks", { hooks: { dl: HOOK(5) } });
assert.equal(r.saved, 1, "being refused says nothing about the address");
assert.match(r.notes.dl, /확인하지 못했어요.*막았어요/);

// sending
calls.length = 0;
r = await adm("/announce/send", {
  channel: "ann",
  payload: { username: "someone else", avatar_url: "https://x", tts: true, content: "<@&1> hi", allowed_mentions: { parse: [], roles: ["1"] }, embeds: [{ title: "첫 글", color: 5 }, { title: "extra" }] },
  form: { title: "첫 글" },
});
assert.equal(r.http, 200);
let c = calls[0];
assert.equal(c.url, HOOK(1) + "?wait=true");
assert.equal(c.method, "POST");
assert.match(c.ua, /^DiscordBot \(/);
assert.deepEqual(Object.keys(c.body).sort(), ["allowed_mentions", "content", "embeds", "username"], "only the known fields go out");
assert.equal(c.body.username, "SystemBoost");
assert.equal(c.body.embeds.length, 1);
assert.equal(r.hist.length, 1);
const id = r.hist[0].id;
assert.equal(r.hist[0].title, "첫 글");
assert.deepEqual(r.hist[0].form, { title: "첫 글" });
assert.ok((await adm("/log")).log.some(l => l.action === "디스코드 공지" && l.detail.includes("첫 글")));
assert.equal((await adm("/announce")).hist.length, 1, "the list survives a reload");

// a post without mentions cannot ping
await adm("/announce/send", { channel: "log", payload: { content: "@everyone" } });
assert.deepEqual(calls.at(-1).body.allowed_mentions, { parse: [] });

// refused sends
assert.match((await adm("/announce/send", { channel: "nope", payload: post("x") })).error, /없는 채널/);
assert.match((await adm("/announce/send", { channel: "ann", payload: { embeds: [] } })).error, /보낼 내용/);
assert.match((await adm("/announce/send", { channel: "ann" })).error, /보낼 내용/);
const before = (await adm("/announce")).hist.length;

// Discord's own limit: waits and tries again, and the post goes out once
script = [() => ok({ retry_after: 0.01 }, 429)];
assert.equal((await adm("/announce/send", { channel: "ann", payload: post("retry") })).hist.length, before + 1);
// over the wait, refused address, gives up, rejected, down: nothing is recorded, nothing logs the page out, no address leaks
script = [() => ok({ retry_after: 60 }, 429)];
r = await adm("/announce/send", { channel: "ann", payload: post("long") });
assert.equal(r.http, 503); assert.match(r.error, /너무 많아요/);
script = [() => new Response("<html>1015</html>", { status: 429 })];
r = await adm("/announce/send", { channel: "ann", payload: post("blocked") });
assert.equal(r.http, 503); assert.match(r.error, /막았어요/);
script = [0, 1, 2].map(() => () => ok({ retry_after: 0 }, 429));
r = await adm("/announce/send", { channel: "ann", payload: post("thrice") });
assert.equal(r.http, 503);
script = [() => ok({ code: 50035, message: "Invalid Form Body" }, 400)];
r = await adm("/announce/send", { channel: "ann", payload: post("bad") });
assert.equal(r.http, 502); assert.match(r.error, /받지 않았어요/);
script = [() => ok({ message: "Invalid Webhook Token" }, 401)];
r = await adm("/announce/send", { channel: "ann", payload: post("token") });
assert.equal(r.http, 502, "Discord's 401 must not read as a wrong dashboard password");
script = ["throw"];
r = await adm("/announce/send", { channel: "ann", payload: post("down") });
assert.equal(r.http, 502); assert.match(r.error, /연결하지 못했어요/);
assert.equal((await adm("/announce")).hist.length, before + 1, "a failed send is not listed");

// editing
calls.length = 0;
r = await adm("/announce/edit", { id, payload: post("고친 글", { allowed_mentions: { parse: [] } }), form: { title: "고친 글" } });
assert.equal(r.http, 200);
c = calls[0];
assert.equal(c.url, HOOK(1) + "/messages/" + id);
assert.equal(c.method, "PATCH");
assert.ok(!("username" in c.body), "a webhook edit cannot take a name");
assert.equal(c.body.content, "", "the text above the post goes away when removed");
const edited = r.hist.find(x => x.id === id);
assert.equal(edited.title, "고친 글");
assert.ok(edited.edited);
assert.deepEqual(edited.form, { title: "고친 글" });

assert.equal((await adm("/announce/edit", { id: "123456", payload: post("x") })).http, 404);
assert.equal((await adm("/announce/edit", { id: "../../x", payload: post("x") })).http, 404);
assert.match((await adm("/announce/edit", { id, payload: { embeds: [] } })).error, /보낼 내용/);
script = [() => ok({ message: "boom" }, 500)];
r = await adm("/announce/edit", { id, payload: post("실패") });
assert.equal(r.http, 502);
assert.equal((await adm("/announce")).hist.find(x => x.id === id).title, "고친 글", "a failed edit changes nothing");

// deleting: a failure keeps the row, a post already gone drops it and says so
script = [() => ok({ message: "boom" }, 500)];
assert.equal((await adm("/announce/delete", { id })).http, 502);
assert.ok((await adm("/announce")).hist.some(x => x.id === id));
calls.length = 0;
r = await adm("/announce/delete", { id });
assert.equal(calls[0].method, "DELETE");
assert.equal(calls[0].url, HOOK(1) + "/messages/" + id);
assert.equal(r.gone, false);
assert.ok(!r.hist.some(x => x.id === id));
assert.ok(!messages.has(id));
const other = (await adm("/announce")).hist[0].id;
messages.delete(other);
r = await adm("/announce/delete", { id: other });
assert.equal(r.gone, true);
assert.equal((await adm("/announce/delete", { id: other })).http, 404);

// the list stops at 50, oversized form text is dropped but the post is kept
for (let i = 0; i < 55; i++) await adm("/announce/send", { channel: "log", payload: post("n" + i), form: i === 54 ? { desc: "x".repeat(30000) } : {} });
s = await adm("/announce");
assert.equal(s.hist.length, 50);
assert.equal(s.hist[0].title, "n54");
assert.equal(s.hist[0].form, null);

// the address itself is only ever in its own KV entry, never in the list or the log
for (const [k, v] of kv) if (k !== "config:discord_hooks") assert.ok(!v.includes("fake-token"), k);
console.log("announce tests passed");
