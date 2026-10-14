export const ORIGIN = "https://the-order-bangumi-auth.zyx2251721565.workers.dev";
export const CALLBACK = "/v1/bangumi/callback";
export const START = "/v1/bangumi/login/start";
export const POLL = "/v1/bangumi/login/poll";
export const CANCEL = "/v1/bangumi/login/cancel";
export const REFRESH = "/v1/bangumi/refresh";
export const POST_PATHS = new Set([START, POLL, CANCEL, REFRESH]);
export const SESSION_MS = 180_000;
export const MAX_SESSIONS = 128;
export const USER_AGENT = "Ph-Rank/The-Order-BangumiAuth/1.1.0 (https://github.com/Ph-Rank/The-Order)";
export interface Env {
  BANGUMI_CLIENT_ID?: string;
  BANGUMI_CLIENT_SECRET?: string;
  BANGUMI_AUTH_ENABLED?: string;
  AUTH: DurableObjectNamespace;
}
export interface Tokens { access_token: string; refresh_token: string; expires_in: number; user_id: string; issued_at?: number }
export class ProtocolFailure extends Error {
  constructor(readonly status: number) { super("Authorization request failed"); }
}
const headers = {
  "Cache-Control": "no-store",
  "Content-Security-Policy": "default-src 'none'; frame-ancestors 'none'",
  "Referrer-Policy": "no-referrer",
  "X-Content-Type-Options": "nosniff"
};
export function reply(status: number, body?: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status, headers: { ...headers, "Content-Type": "application/json; charset=utf-8", ...(status === 429 ? { "Retry-After": "3" } : {}) }
  });
}
export function browserReply(status: number): Response {
  return new Response(status === 200 ? "授权处理完成。请返回 The Order；此页面可以关闭。" : "授权未完成，请返回 The Order 重试。", {
    status, headers: { ...headers, "Content-Type": "text/plain; charset=utf-8" }
  });
}
export function credentialsConfigured(env: Env): boolean {
  return /^[A-Za-z0-9_-]{1,128}$/.test(env.BANGUMI_CLIENT_ID ?? "")
    && (env.BANGUMI_CLIENT_SECRET?.length ?? 0) >= 8 && (env.BANGUMI_CLIENT_SECRET?.length ?? 0) <= 4096
    && !/[\u0000-\u001f\u007f]/.test(env.BANGUMI_CLIENT_SECRET ?? "");
}
export function configured(env: Env): boolean { return env.BANGUMI_AUTH_ENABLED === "true" && credentialsConfigured(env); }
export function validSecret(value: string): boolean { return /^[A-Za-z0-9_-]{43}$/.test(value); }
export function validToken(value: string): boolean { return value.length >= 8 && value.length <= 4096 && /^[A-Za-z0-9._~+/-]+={0,2}$/.test(value); }
export function same(a: string, b: string): boolean {
  if (a.length !== b.length) return false;
  let difference = 0;
  for (let i = 0; i < a.length; i++) difference |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return difference === 0;
}
export function base64url(bytes: Uint8Array): string {
  return btoa(String.fromCharCode(...bytes)).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/, "");
}
export function random(): string { return base64url(crypto.getRandomValues(new Uint8Array(32))); }
export async function digest(value: string): Promise<string> {
  return base64url(new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(value))));
}
export async function textLimited(response: Response | Request, maximum: number): Promise<string> {
  const advertised = response.headers.get("Content-Length");
  if (advertised && (!/^\d+$/.test(advertised) || Number(advertised) > maximum)) throw new ProtocolFailure(413);
  if (!response.body) return "";
  const reader = response.body.getReader();
  const chunks: Uint8Array[] = []; let total = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      total += value.length;
      if (total > maximum) throw new ProtocolFailure(413);
      chunks.push(value);
    }
    const all = new Uint8Array(total); let offset = 0;
    for (const chunk of chunks) { all.set(chunk, offset); offset += chunk.length; }
    return new TextDecoder("utf-8", { fatal: true, ignoreBOM: false }).decode(all);
  } finally { await reader.cancel().catch(() => {}); reader.releaseLock(); }
}
export async function flatBody(request: Request): Promise<Record<string, string>> {
  if (request.headers.get("Content-Type")?.split(";")[0]?.trim() !== "application/json") throw new ProtocolFailure(400);
  const text = await textLimited(request, 16_384);
  let value: unknown;
  try { value = JSON.parse(text); } catch { throw new ProtocolFailure(400); }
  if (!value || Array.isArray(value) || typeof value !== "object") throw new ProtocolFailure(400);
  const fields = Object.entries(value);
  // Protocol keys are ASCII and never escaped. Reject duplicate/non-canonical keys,
  // nested values and ambiguous JSON before any durable state or upstream request.
  const writtenKeys = [...text.matchAll(/"([a-z_]+)"\s*:/g)].map(match => match[1]!);
  if (fields.some(([key, val]) => !/^[a-z_]+$/.test(key) || typeof val !== "string")
    || writtenKeys.length !== fields.length || new Set(writtenKeys).size !== fields.length) throw new ProtocolFailure(400);
  return value as Record<string, string>;
}
export function exact(value: Record<string, string>, ...keys: string[]): boolean {
  return Object.keys(value).length === keys.length && keys.every(key => Object.hasOwn(value, key));
}
async function encryptionKey(secret: string): Promise<CryptoKey> {
  const bytes = await crypto.subtle.digest("SHA-256", new TextEncoder().encode("TheOrder.BangumiAuth/v1\0" + secret));
  return crypto.subtle.importKey("raw", bytes, { name: "AES-GCM" }, false, ["encrypt", "decrypt"]);
}
export async function seal(tokens: Tokens, secret: string, session: string): Promise<string> {
  const iv = crypto.getRandomValues(new Uint8Array(12));
  const encrypted = new Uint8Array(await crypto.subtle.encrypt({ name: "AES-GCM", iv,
    additionalData: new TextEncoder().encode(session) }, await encryptionKey(secret), new TextEncoder().encode(JSON.stringify(tokens))));
  return base64url(iv) + "." + base64url(encrypted);
}
export async function unseal(value: string, secret: string, session: string): Promise<Tokens> {
  const decode = (part: string) => Uint8Array.from(atob(part.replaceAll("-", "+").replaceAll("_", "/")), c => c.charCodeAt(0));
  const [iv, body] = value.split(".");
  if (!iv || !body) throw new ProtocolFailure(503);
  const plain = await crypto.subtle.decrypt({ name: "AES-GCM", iv: decode(iv), additionalData: new TextEncoder().encode(session) },
    await encryptionKey(secret), decode(body));
  try { return JSON.parse(new TextDecoder().decode(plain)) as Tokens; }
  finally { new Uint8Array(plain).fill(0); }
}
