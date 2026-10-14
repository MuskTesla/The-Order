import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, rm, readFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { resolve, sep, basename } from "node:path";
import { createHash, randomBytes } from "node:crypto";
import { build } from "esbuild";
import { Miniflare, Response as LocalResponse } from "miniflare";

const origin = "https://the-order-bangumi-auth.zyx2251721565.workers.dev";
const prefix = origin + "/v1/bangumi/";
const access = "synthetic_access_token_not_real_123456";
const refresh = "synthetic_refresh_token_not_real_123456";
const secret = "synthetic_app_secret_not_real_123456";
const nonce = () => randomBytes(32).toString("base64url");
const digest = value => createHash("sha256").update(value).digest("base64url");
let fixturePath, fixtureSource;
before(async () => {
  fixturePath = resolve("../../.artifacts/bangumi-worker-20261014/worker-fixture.mjs");
  await build({ entryPoints: ["test/fixture.ts"], outfile: fixturePath, bundle: true, format: "esm", target: "es2022" });
  fixtureSource = await readFile(fixturePath, "utf8");
});

async function fixture(options = {}) {
  const persistent = await mkdtemp(resolve(tmpdir(), "the-order-worker-synthetic-"));
  const removeState = async () => {
    assert(resolve(persistent).startsWith(resolve(tmpdir())+sep) && basename(persistent).startsWith("the-order-worker-synthetic-"));
    await rm(persistent,{recursive:true,force:true});
  };
  let tokenCalls = 0; const requests = []; const current = { ...options };
  // Every outbound request is handled locally; there is no fallback network route.
  const outbound = async request => {
    const url = new URL(request.url);
    if (url.href === "https://bgm.tv/oauth/access_token" && request.method === "POST") {
      tokenCalls++; const body = await request.text(); requests.push({ body, headers:Object.fromEntries(request.headers), method:request.method });
      const fields = new URLSearchParams(body);
      assert.equal(fields.get("client_id"), "synthetic_app_id"); assert.equal(fields.get("client_secret"), secret);
      assert.equal(fields.get("redirect_uri"), prefix + "callback");
      assert.match(request.headers.get("user-agent"), /The.Order/i);
      if(current.timeout)await new Promise(resolve=>setTimeout(resolve,11_000));
      return new LocalResponse(current.tokenBody ?? JSON.stringify({ access_token: access, refresh_token: refresh,
        expires_in: 3600, user_id: 123 }), { status: current.tokenStatus ?? 200, headers: { "content-type": "application/json" } });
    }
    if (url.href === "https://api.bgm.tv/v0/me" && request.method === "GET") {
      assert.equal(request.headers.get("authorization"), "Bearer " + access);
      return new LocalResponse(JSON.stringify({id:current.accountId??123,username:"synthetic",nickname:"合成用户"}),
        {status:current.accountStatus??200,headers:{"content-type":"application/json"}});
    }
    throw new Error("Unexpected outbound destination in synthetic fixture");
  };
  const config = { modules: true, script: fixtureSource, compatibilityDate: "2026-08-01",
    durableObjects: { AUTH: { className: "BangumiAuthCoordinator", useSQLite: true } }, durableObjectsPersist: persistent,
    bindings: { BANGUMI_CLIENT_ID: "synthetic_app_id", BANGUMI_CLIENT_SECRET: secret, BANGUMI_AUTH_ENABLED: options.disabled ? "false" : "true" },
    outboundService: outbound, cf: false };
  let mf = new Miniflare(config);
  try { await mf.ready; } catch (error) { await mf.dispose(); await removeState(); throw error; }
  const post = (path, body, extra = {}) => mf.dispatchFetch(prefix + path, { method: "POST",
    headers: { "content-type": "application/json", "CF-Connecting-IP": "192.0.2.10", ...extra.headers }, body: JSON.stringify(body) });
  const inspect = async path => {
    const namespace = await mf.getDurableObjectNamespace("AUTH");
    return namespace.get(namespace.idFromName("bangumi-oauth-v1")).fetch("https://fixture.invalid/" + path);
  };
  const begin = async () => {
    const verifier = nonce(); const response = await post("login/start", { challenge: digest(verifier) });
    assert.equal(response.status, 200); const value = await response.json();
    return { ...value, verifier, state: new URL(value.authorization_url).searchParams.get("state") };
  };
  const callback = session => mf.dispatchFetch(prefix + "callback?" + new URLSearchParams({ state: session.state, code: "synthetic_code_12345" }));
  const proof = session => ({ session_id: session.session_id, verifier: session.verifier });
  const poll = session => post("login/poll", proof(session));
  return { post, inspect, begin, callback, poll, proof, current, get calls() { return tokenCalls; }, requests,
    request: (...args) => mf.dispatchFetch(...args),
    restart: async () => { await mf.dispose(); mf = new Miniflare(config); await mf.ready; },
    close: async () => { await mf.dispose(); await removeState(); } };
}
async function using(fn, options) { const f = await fixture(options); try { await fn(f); } finally { await f.close(); } }

test("production deployment configuration preserves dashboard ID/secret and starts disabled", async () => {
  const { execFileSync } = await import("node:child_process");
  const result = JSON.parse(execFileSync(process.execPath, ["scripts/check-deployment.mjs"], { encoding: "utf8" }));
  assert.equal(result.writesClientId, false); assert.equal(result.writesClientSecret, false);
  assert.equal(result.productionDeploymentExecuted, false);
});
test("health distinguishes disabled deployment; no authentication endpoints open", () => using(async f => {
  const health = await (await f.request(origin + "/health")).json(); assert.equal(health.ready, false);
  assert.equal((await f.post("login/start", { challenge: digest(nonce()) })).status, 503); assert.equal(f.calls, 0);
}, { disabled: true }));
test("authorization URL contains only official origin, registered callback, own ID and random state", () => using(async f => {
  const s = await f.begin(); const url = new URL(s.authorization_url);
  assert.equal(url.origin, "https://bgm.tv"); assert.equal(url.pathname, "/oauth/authorize");
  assert.equal(url.searchParams.get("redirect_uri"), prefix + "callback");
  assert.equal(url.searchParams.get("client_id"), "synthetic_app_id");
  assert.equal(url.searchParams.get("response_type"), "code"); assert.equal(url.searchParams.size, 4);
  assert.equal(s.expires_in, 180); assert(!s.authorization_url.includes(secret));
  assert.equal((await f.poll(s)).status, 202);
}));
test("first callback verifies /me, encrypts temporary credentials, and allows exactly one proof-bound delivery", () => using(async f => {
  const s = await f.begin(); const callback = await f.callback(s);
  assert.equal(callback.status, 200);
  assert(!(await callback.text()).includes(access));
  const snapshot = await (await f.inspect("snapshot")).json();
  for (const value of [access, refresh, secret]) assert(!JSON.stringify(snapshot).includes(value));
  assert.equal(snapshot.sessions.length, 1); assert.equal(snapshot.sessions[0].status, 200);
  assert.equal((await f.post("login/poll", { session_id: s.session_id, verifier: nonce() })).status, 410);
  const replies = await Promise.all([f.poll(s), f.poll(s), f.poll(s)]);
  assert.deepEqual(replies.map(r => r.status).sort(), [200, 410, 410]);
  const tokens = await replies.find(r => r.status === 200).json();
  assert.equal(tokens.access_token, access); assert.equal(tokens.refresh_token, refresh); assert.equal(tokens.user_id, "123");
  assert(tokens.expires_in <= 3600 && tokens.expires_in >= 3500); assert.equal(tokens.issued_at, undefined);
  assert.equal((await f.callback(s)).status, 410); assert.equal(f.calls, 1);
  assert.equal((await (await f.inspect("snapshot")).json()).sessions.length, 0);
}));
test("pending and completed sessions survive workerd process restart", () => using(async f => {
  const s = await f.begin(); await f.restart(); assert.equal((await f.poll(s)).status, 202);
  assert.equal((await f.callback(s)).status, 200); await f.restart();
  assert.equal((await f.poll(s)).status, 200); assert.equal((await f.poll(s)).status, 410);
}));
test("simultaneous callbacks exchange the authorization code once", () => using(async f => {
  const s = await f.begin(); const results = await Promise.all([f.callback(s), f.callback(s)]);
  assert.deepEqual(results.map(r => r.status).sort(), [200, 410]); assert.equal(f.calls, 1);
}));
test("unknown state, duplicate query and mixed success/error never reach Bangumi", () => using(async f => {
  const s = await f.begin();
  assert.equal((await f.callback({ ...s, state: nonce() })).status, 410);
  for (const query of ["state="+s.state+"&state="+s.state+"&code=synthetic_code", "state="+s.state+"&code=synthetic_code&error=denied", "state="+s.state+"&error_description=oops"]) {
    assert.equal((await f.request(prefix+"callback?"+query)).status, 400);
  }
  assert.equal(f.calls, 0); assert.equal((await f.poll(s)).status, 202);
}));
test("user denial is a terminal private result without replacing any account", () => using(async f => {
  const s = await f.begin(); assert.equal((await f.request(prefix+"callback?"+new URLSearchParams({state:s.state,error:"access_denied"}))).status, 200);
  assert.equal((await f.poll(s)).status, 403); assert.equal((await f.poll(s)).status, 410); assert.equal(f.calls, 0);
}));
test("cancellation needs the original proof and rejects late callbacks", () => using(async f => {
  const s = await f.begin(); assert.equal((await f.post("login/cancel", {session_id:s.session_id,verifier:nonce()})).status,410);
  assert.equal((await f.post("login/cancel", f.proof(s))).status,204);
  assert.equal((await f.callback(s)).status,410); assert.equal((await f.poll(s)).status,410); assert.equal(f.calls,0);
}));
test("cancellation while upstream validation runs prevents late credential commit", () => using(async f => {
  const s = await f.begin(); const callback = f.callback(s);
  for (let i=0;i<40;i++) { if ((await (await f.inspect("snapshot")).json()).sessions[0]?.status===102) break; await new Promise(r=>setTimeout(r,5)); }
  assert.equal((await f.post("login/cancel", f.proof(s))).status,204);
  assert.equal((await callback).status,410); assert.equal((await f.poll(s)).status,410);
}));
test("expiry and alarm cleanup prevent late callback or token delivery without waiting three minutes", () => using(async f => {
  const s = await f.begin(); await f.inspect("expire");
  assert.equal((await f.poll(s)).status,410); assert.equal((await f.callback(s)).status,410);
  assert.equal((await (await f.inspect("snapshot")).json()).sessions.length,0);
}));
test("refresh validates official account and rejects concurrent or already-consumed rotating tokens", () => using(async f => {
  const values = await Promise.all([f.post("refresh",{refresh_token:refresh}),f.post("refresh",{refresh_token:refresh})]);
  assert.deepEqual(values.map(r=>r.status).sort(),[200,429]);
  const response = await values.find(r=>r.status===200).json(); assert.equal(response.user_id,"123");
  assert.equal((await f.post("refresh",{refresh_token:refresh})).status,403); assert.equal(f.calls,1);
  const snapshot = await (await f.inspect("snapshot")).json();
  assert(!JSON.stringify(snapshot).includes(refresh)); assert(!JSON.stringify(snapshot).includes(access));
}));
for (const [label,options,expected] of [
  ["invalid refresh token",{tokenStatus:400},401], ["upstream unavailable",{tokenStatus:503},503],
  ["upstream rate limit",{tokenStatus:429},429], ["redirect is never followed",{tokenStatus:302},503],
  ["wrong account identity",{accountId:999},503], ["malformed token response",{tokenBody:'{"access_token":"not-a-token"}'},503]
]) test(label+" returns sanitized failure and no reusable public result",()=>using(async f=>{
  const response = await f.post("refresh",{refresh_token:refresh}); assert.equal(response.status,expected);
  const text=await response.text(); for(const secretValue of [secret,access,refresh]) assert(!text.includes(secretValue));
  assert.equal((await f.post("refresh",{refresh_token:refresh})).status,expected); assert.equal(f.calls,1);
},options));
test("protocol rejects unexpected origins, cookies, tokens in URLs, duplicate JSON keys and oversized bodies",()=>using(async f=>{
  const challenge=digest(nonce());
  assert.equal((await f.request("https://other.example/health")).status,400);
  assert.equal((await f.post("login/start",{challenge},{headers:{Cookie:"foo=bar"}})).status,400);
  assert.equal((await f.post("login/start",{challenge},{headers:{Origin:"https://evil.example"}})).status,400);
  assert.equal((await f.request(prefix+"login/start?access_token="+access,{method:"POST",headers:{"content-type":"application/json"},body:JSON.stringify({challenge})})).status,400);
  for(const body of ['{"challenge":"'+challenge+'","challenge":"'+challenge+'"}', '{"challen\\u0067e":"'+challenge+'"}', '{"challenge":"'+challenge+'","redirect_uri":"https://evil.example"}'])
    assert.equal((await f.request(prefix+"login/start",{method:"POST",headers:{"content-type":"application/json"},body})).status,400);
  assert.equal((await f.request(prefix+"login/start",{method:"POST",headers:{"content-type":"application/json"},body:'{"challenge":"'+"x".repeat(17000)+'"}'})).status,413);
  assert.equal(f.calls,0);
}));
test("start abuse budget and daily cap fail closed without upstream access",()=>using(async f=>{
  for(let i=0;i<10;i++) assert.equal((await f.post("login/start",{challenge:digest(nonce())})).status,200);
  assert.equal((await f.post("login/start",{challenge:digest(nonce())})).status,429);
  await f.inspect("daily-limit"); assert.equal((await f.post("refresh",{refresh_token:refresh})).status,429);
  assert.equal(f.calls,0);
}));
test("browser cookies on official callback are ignored and never used as identity",()=>using(async f=>{
  const s=await f.begin(); const result=await f.request(prefix+"callback?"+new URLSearchParams({state:s.state,code:"synthetic_code_12345"}),{headers:{Cookie:"synthetic_browser_cookie=ignored",Origin:"https://bgm.tv"}});
  assert.equal(result.status,200);assert.equal((await f.poll(s)).status,200);
  assert(f.requests.every(r=>!Object.hasOwn(r.headers,"cookie")));
}));
test("unknown API cannot become a metadata proxy or expose credentials",()=>using(async f=>{
  assert.equal((await f.request(origin+"/v0/subjects/13")).status,404);
  assert.equal((await f.request(origin+"/v1/bangumi/token?access_token="+access)).status,404);
  assert.equal(f.calls,0);
}));
test("upstream timeout is bounded and returns no credential",()=>using(async f=>{
  const started=Date.now();const result=await f.post("refresh",{refresh_token:refresh});
  assert.equal(result.status,503);assert(Date.now()-started<13_000);
  assert.equal((await f.post("refresh",{refresh_token:refresh})).status,503);assert.equal(f.calls,1);
}, {timeout:true}));
test("invalid or expired authorization code is distinguished from user denial",()=>using(async f=>{
  const s=await f.begin();assert.equal((await f.callback(s)).status,401);
  assert.equal((await f.poll(s)).status,401);assert.equal((await f.poll(s)).status,410);
}, {tokenStatus:400}));
