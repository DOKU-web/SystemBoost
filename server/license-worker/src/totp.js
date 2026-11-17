// The 2-step login of member accounts (src/accounts.js): TOTP codes (RFC 6238: HMAC-SHA-1, 6 digits, 30 seconds), the
// secret sealed with AES-GCM for the database, and backup codes. WebCrypto only; nothing here is logged or stored.
//
// The sealing key is made with HKDF (SHA-256, info "totp") from the accounts' pepper, which comes from the signing key:
// the database alone cannot open a secret, and no new Worker secret is needed. The account id is the AES-GCM
// additional data, so a sealed secret copied to another account does not open there.
const enc = new TextEncoder();
const random = n => crypto.getRandomValues(new Uint8Array(n));

export const TOTP = { digits: 6, period: 30, window: 1, secretBytes: 20 };
export const BACKUP = { count: 10, alphabet: "23456789ABCDEFGHJKMNPQRSTUVWXYZ" };   // no 0/O, 1/I/L

const B32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
/** RFC 4648 base32 without padding, as authenticator apps take it. */
export function base32(bytes) {
  let bits = 0, value = 0, out = "";
  for (const b of bytes) {
    value = (value << 8) | b; bits += 8;
    while (bits >= 5) { out += B32[(value >>> (bits - 5)) & 31]; bits -= 5; }
  }
  if (bits > 0) out += B32[(value << (5 - bits)) & 31];
  return out;
}

export const newSecret = () => random(TOTP.secretBytes);
export const stepAt = (ms = Date.now()) => Math.floor(ms / 1000 / TOTP.period);

/** The code for one time step (HOTP, RFC 4226). */
export async function hotp(secret, counter) {
  const key = await crypto.subtle.importKey("raw", secret, { name: "HMAC", hash: "SHA-1" }, false, ["sign"]);
  const msg = new Uint8Array(8);
  let c = BigInt(counter);
  for (let i = 7; i >= 0; i--) { msg[i] = Number(c & 0xffn); c >>= 8n; }
  const mac = new Uint8Array(await crypto.subtle.sign("HMAC", key, msg));
  const o = mac[mac.length - 1] & 15;
  const n = ((mac[o] & 0x7f) << 24) | (mac[o + 1] << 16) | (mac[o + 2] << 8) | mac[o + 3];
  return String(n % 10 ** TOTP.digits).padStart(TOTP.digits, "0");
}

/** The time step a 6-digit code belongs to (one step either side of now), newer than lastStep; 0 when none. */
export async function totpStep(secret, code, lastStep = 0, ms = Date.now()) {
  code = String(code ?? "").replace(/\s+/g, "");
  if (!/^\d{6}$/.test(code)) return 0;
  const now = stepAt(ms);
  let found = 0;
  for (let s = now - TOTP.window; s <= now + TOTP.window; s++) {
    const want = await hotp(secret, s);
    let d = 0;
    for (let i = 0; i < want.length; i++) d |= want.charCodeAt(i) ^ code.charCodeAt(i);
    if (d === 0 && s > lastStep) found = s;   // every step is computed, whatever matches first
  }
  return found;
}

/** otpauth:// link for the QR code. */
export function otpUri(secretB32, label) {
  return "otpauth://totp/" + encodeURIComponent("SystemBoost:" + label) + "?secret=" + secretB32 +
         "&issuer=SystemBoost&digits=" + TOTP.digits + "&period=" + TOTP.period;
}

// ---- the sealed secret: "v1." + base64(iv | ciphertext)
const sealKeys = new Map();   // pepper -> CryptoKey (one per isolate and signing key)
async function sealKey(pepper) {
  let k = sealKeys.get(pepper);
  if (!k) {
    const ikm = await crypto.subtle.importKey("raw", enc.encode(pepper), "HKDF", false, ["deriveKey"]);
    k = await crypto.subtle.deriveKey({ name: "HKDF", hash: "SHA-256", salt: enc.encode("systemboost-account"), info: enc.encode("totp") },
                                      ikm, { name: "AES-GCM", length: 256 }, false, ["encrypt", "decrypt"]);
    if (sealKeys.size > 4) sealKeys.clear();
    sealKeys.set(pepper, k);
  }
  return k;
}
const b64 = bytes => btoa(String.fromCharCode(...bytes));
const unb64 = s => Uint8Array.from(atob(s), c => c.charCodeAt(0));

export async function seal(pepper, secret, accountId) {
  const iv = random(12);
  const ct = new Uint8Array(await crypto.subtle.encrypt({ name: "AES-GCM", iv, additionalData: enc.encode(String(accountId)) }, await sealKey(pepper), secret));
  const all = new Uint8Array(12 + ct.length);
  all.set(iv); all.set(ct, 12);
  return "v1." + b64(all);
}
/** The secret, or null when the text is not one sealed for this account with this pepper. */
export async function unseal(pepper, text, accountId) {
  try {
    if (typeof text !== "string" || !text.startsWith("v1.")) return null;
    const all = unb64(text.slice(3));
    const pt = await crypto.subtle.decrypt({ name: "AES-GCM", iv: all.subarray(0, 12), additionalData: enc.encode(String(accountId)) },
                                           await sealKey(pepper), all.subarray(12));
    return new Uint8Array(pt);
  } catch { return null; }
}

// ---- backup codes: XXXX-XXXX-XXXX from BACKUP.alphabet, every symbol equally likely (bytes past 248 are drawn again)
function symbols(n) {
  const a = BACKUP.alphabet, top = 256 - (256 % a.length);
  let out = "";
  while (out.length < n) for (const x of random(n * 2)) if (x < top && out.length < n) out += a[x % a.length];
  return out;
}
export const newBackupCodes = () => Array.from({ length: BACKUP.count }, () => symbols(12).match(/.{4}/g).join("-"));
/** What a typed backup code is compared as: capitals, letters and digits only. */
export const normBackup = code => String(code ?? "").toUpperCase().replace(/[^0-9A-Z]/g, "");
