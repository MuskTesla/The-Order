import { ORIGIN, CALLBACK, START, POLL, CANCEL, REFRESH, POST_PATHS, SESSION_MS, MAX_SESSIONS, USER_AGENT,
  type Env, type Tokens, ProtocolFailure, configured, credentialsConfigured, reply, browserReply, flatBody, exact,
  validSecret, validToken, same, digest, random, seal, unseal, textLimited } from "./protocol";

type Session = { id: string; state: string; challenge: string; expires: number; status: number; encrypted: string | null };
type Refresh = { hash: string; expires: number; status: number };
type Budget = { key: string; count: number; expires: number };
type Meta = { key: string; value: number };

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    try {
      const url = new URL(request.url);
      if (url.origin !== ORIGIN) return reply(400);
      if (url.pathname === "/health" && request.method === "GET" && !url.search)
        return reply(200, { service: "TheOrder.BangumiAuthWorker", version: 1, ready: configured(env), enabled: env.BANGUMI_AUTH_ENABLED === "true", configuration_ready: credentialsConfigured(env) });
      if (!POST_PATHS.has(url.pathname) && url.pathname !== CALLBACK) return reply(404);
      if (!configured(env)) return reply(503);
      if (request.headers.has("Authorization") || url.pathname !== CALLBACK && (request.headers.has("Cookie")
        || request.headers.has("Origin") && request.headers.get("Origin") !== ORIGIN)) return reply(400);
      if (url.pathname === CALLBACK) {
        if (request.method !== "GET") return reply(405);
        const pairs = [...url.searchParams]; const keys = pairs.map(([key]) => key);
        if (pairs.length < 2 || pairs.length > 3 || new Set(keys).size !== keys.length
          || keys.some(key => !["state", "code", "error", "error_description"].includes(key))
          || !validSecret(url.searchParams.get("state") ?? "")
          || url.searchParams.has("code") === url.searchParams.has("error")
          || url.searchParams.has("error_description") && !url.searchParams.has("error")
          || (url.searchParams.get("error")?.length ?? 0) > 128 || (url.searchParams.get("error_description")?.length ?? 0) > 1024
          || url.searchParams.has("code") && !/^[A-Za-z0-9_-]{8,512}$/.test(url.searchParams.get("code")!)) return browserReply(400);
      } else {
        if (request.method !== "POST") return reply(405);
        if (url.search) return reply(400);
        const body = await flatBody(request.clone());
        if (url.pathname === START && (!exact(body, "challenge") || !validSecret(body.challenge!))
          || [POLL, CANCEL].includes(url.pathname) && (!exact(body, "session_id", "verifier") || !validSecret(body.session_id!) || !validSecret(body.verifier!))
          || url.pathname === REFRESH && (!exact(body, "refresh_token") || !validToken(body.refresh_token!))) return reply(400);
      }
      const forwarded = new Request(request);
      forwarded.headers.delete("Cookie"); // Browser callback cookies never become authorization evidence or outbound headers.
      // The edge supplies CF-Connecting-IP; caller-supplied internal headers are ignored.
      forwarded.headers.delete("X-TheOrder-Rate-Key");
      forwarded.headers.set("X-TheOrder-Rate-Key", await digest(request.headers.get("CF-Connecting-IP") ?? "missing-edge-ip"));
      const object = env.AUTH.get(env.AUTH.idFromName("bangumi-oauth-v1"));
      return await object.fetch(forwarded);
    } catch (error) { return reply(error instanceof ProtocolFailure ? error.status : 503); }
  }
} satisfies ExportedHandler<Env>;

export class BangumiAuthCoordinator {
  constructor(private readonly ctx: DurableObjectState, private readonly env: Env) {
    ctx.storage.sql.exec("CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY,state TEXT UNIQUE NOT NULL,challenge TEXT NOT NULL,expires INTEGER NOT NULL,status INTEGER NOT NULL,encrypted TEXT)");
    ctx.storage.sql.exec("CREATE INDEX IF NOT EXISTS session_expiry ON sessions(expires)");
    ctx.storage.sql.exec("CREATE TABLE IF NOT EXISTS refreshes(hash TEXT PRIMARY KEY,expires INTEGER NOT NULL,status INTEGER NOT NULL)");
    ctx.storage.sql.exec("CREATE INDEX IF NOT EXISTS refresh_expiry ON refreshes(expires)");
    ctx.storage.sql.exec("CREATE TABLE IF NOT EXISTS budgets(key TEXT PRIMARY KEY,count INTEGER NOT NULL,expires INTEGER NOT NULL)");
    ctx.storage.sql.exec("CREATE INDEX IF NOT EXISTS budget_expiry ON budgets(expires)");
    ctx.storage.sql.exec("CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY,value INTEGER NOT NULL)");
  }
  async fetch(request: Request): Promise<Response> {
    try {
      if (!configured(this.env) || new URL(request.url).origin !== ORIGIN) return reply(503);
      const path = new URL(request.url).pathname;
      this.purge();
      if (!this.allow(request.headers.get("X-TheOrder-Rate-Key") ?? "missing", path)) return reply(429);
      if (path === CALLBACK) return await this.callback(new URL(request.url).searchParams);
      const body = await flatBody(request);
      if (path === START) return await this.begin(body.challenge!);
      if (path === REFRESH) return await this.refresh(body.refresh_token!);
      const hash = await digest(body.verifier!);
      if (path === CANCEL) {
        return this.ctx.storage.transactionSync(() => {
          const row = this.find(body.session_id!);
          if (!row || !same(row.challenge, hash)) return reply(410);
          this.ctx.storage.sql.exec("DELETE FROM sessions WHERE id=?", row.id);
          return reply(204);
        });
      }
      if (path === POLL) {
        const result = this.ctx.storage.transactionSync(() => {
          const row = this.find(body.session_id!);
          if (!row || !same(row.challenge, hash)) return null;
          if (row.status === 202 || row.status === 102) return row;
          // Delete before returning: concurrent pollers cannot both consume credentials.
          this.ctx.storage.sql.exec("DELETE FROM sessions WHERE id=?", row.id);
          return row;
        });
        if (!result) return reply(410);
        if (result.status === 202 || result.status === 102) return reply(202);
        if (result.status !== 200 || !result.encrypted) return reply(result.status);
        const tokens = await unseal(result.encrypted, this.env.BANGUMI_CLIENT_SECRET!, result.id);
        return reply(200, this.remaining(tokens));
      }
      return reply(404);
    } catch (error) { return reply(error instanceof ProtocolFailure ? error.status : 503); }
  }
  private find(id: string): Session | undefined {
    return this.ctx.storage.sql.exec<Session>("SELECT * FROM sessions WHERE id=? AND expires>?", id, Date.now()).toArray()[0];
  }
  private async begin(challenge: string): Promise<Response> {
    const value = this.ctx.storage.transactionSync(() => {
      const count = this.ctx.storage.sql.exec<{ n: number }>("SELECT COUNT(*) AS n FROM sessions").one().n;
      if (count >= MAX_SESSIONS) throw new ProtocolFailure(429);
      const id = random(); const state = random();
      this.ctx.storage.sql.exec("INSERT INTO sessions VALUES(?,?,?,?,202,NULL)", id, state, challenge, Date.now() + SESSION_MS);
      return { id, state };
    });
    await this.schedule();
    const authorization = new URL("https://bgm.tv/oauth/authorize");
    authorization.search = new URLSearchParams({ client_id: this.env.BANGUMI_CLIENT_ID!, response_type: "code",
      redirect_uri: ORIGIN + CALLBACK, state: value.state }).toString();
    return reply(200, { session_id: value.id, challenge, authorization_url: authorization.href, expires_in: 180 });
  }
  private async callback(query: URLSearchParams): Promise<Response> {
    const row = this.ctx.storage.transactionSync(() => {
      const value = this.ctx.storage.sql.exec<Session>("SELECT * FROM sessions WHERE state=? AND expires>?", query.get("state"), Date.now()).toArray()[0];
      if (!value || value.status !== 202) return undefined;
      this.ctx.storage.sql.exec("UPDATE sessions SET status=? WHERE id=?", query.has("error") ? 403 : 102, value.id);
      return value;
    });
    if (!row) return browserReply(410);
    if (query.has("error")) return browserReply(200);
    let status = 503; let encrypted: string | null = null;
    try {
      const tokens = await this.grant("authorization_code", query.get("code")!);
      // Absolute token expiry is sealed too; waiting to poll must not extend its validity.
      encrypted = await seal(tokens, this.env.BANGUMI_CLIENT_SECRET!, row.id);
      status = 200;
    } catch (error) { status = error instanceof ProtocolFailure ? error.status : 503; }
    const committed = this.ctx.storage.transactionSync(() => {
      const current = this.find(row.id);
      if (!current || current.status !== 102) return false;
      this.ctx.storage.sql.exec("UPDATE sessions SET status=?,encrypted=? WHERE id=?", status, encrypted, row.id);
      return true;
    });
    return browserReply(committed ? status : 410);
  }
  private async refresh(token: string): Promise<Response> {
    const hash = await digest(token);
    const claimed = this.ctx.storage.transactionSync(() => {
      const prior = this.ctx.storage.sql.exec<Refresh>("SELECT * FROM refreshes WHERE hash=?", hash).toArray()[0];
      if (prior) return prior.status === 102 ? 429 : prior.status === 200 ? 403 : prior.status;
      if (this.ctx.storage.sql.exec<{ n: number }>("SELECT COUNT(*) AS n FROM refreshes").one().n >= 128) return 429;
      this.ctx.storage.sql.exec("INSERT INTO refreshes VALUES(?,?,102)", hash, Date.now() + SESSION_MS);
      return 200;
    });
    if (claimed !== 200) return reply(claimed);
    await this.schedule();
    try {
      const tokens = await this.grant("refresh_token", token);
      this.ctx.storage.sql.exec("UPDATE refreshes SET status=200 WHERE hash=?", hash);
      return reply(200, this.remaining(tokens));
    } catch (error) {
      const status = error instanceof ProtocolFailure ? error.status : 503;
      this.ctx.storage.sql.exec("UPDATE refreshes SET status=? WHERE hash=?", status, hash);
      return reply(status);
    }
  }
  private remaining(tokens: Tokens): Tokens {
    const issued = tokens.issued_at;
    const seconds = tokens.expires_in - Math.ceil((Date.now() - (issued ?? Date.now())) / 1000);
    if (seconds < 1) throw new ProtocolFailure(410);
    return { access_token: tokens.access_token, refresh_token: tokens.refresh_token, user_id: tokens.user_id, expires_in: seconds };
  }
  private async grant(grant: "authorization_code" | "refresh_token", credential: string): Promise<Tokens> {
    const fields = new URLSearchParams({ grant_type: grant, client_id: this.env.BANGUMI_CLIENT_ID!,
      client_secret: this.env.BANGUMI_CLIENT_SECRET!, redirect_uri: ORIGIN + CALLBACK, [grant === "authorization_code" ? "code" : "refresh_token"]: credential });
    const issued = await this.upstream("https://bgm.tv/oauth/access_token", { method: "POST", body: fields,
      headers: { "Content-Type": "application/x-www-form-urlencoded" } });
    const access = issued.access_token; const refresh = issued.refresh_token; const expires = issued.expires_in;
    if (typeof access !== "string" || !validToken(access) || typeof refresh !== "string" || !validToken(refresh)
      || typeof expires !== "number" || !Number.isInteger(expires) || expires < 60 || expires > 31_536_000) throw new ProtocolFailure(503);
    const issuedAt = Date.now();
    const account = await this.upstream("https://api.bgm.tv/v0/me", { method: "GET", headers: { Authorization: "Bearer " + access } });
    if (typeof account.id !== "number" || !Number.isSafeInteger(account.id) || account.id <= 0
      || issued.user_id !== undefined && issued.user_id !== account.id) throw new ProtocolFailure(503);
    return { access_token: access, refresh_token: refresh, expires_in: expires, user_id: String(account.id), issued_at: issuedAt };
  }
  private async upstream(url: string, init: RequestInit): Promise<Record<string, unknown>> {
    // Reserve global, durable one-second slots. Bound backlog rather than keeping a busy object alive.
    const wait = this.ctx.storage.transactionSync(() => {
      const now = Date.now();
      const next = this.ctx.storage.sql.exec<Meta>("SELECT * FROM meta WHERE key='upstream'").toArray()[0]?.value ?? now;
      const at = Math.max(now, next);
      if (at - now > 3000) throw new ProtocolFailure(429);
      this.ctx.storage.sql.exec("INSERT OR REPLACE INTO meta VALUES('upstream',?)", at + 1000);
      return at - now;
    });
    if (wait) await new Promise(resolve => setTimeout(resolve, wait));
    const headers = new Headers(init.headers); headers.set("User-Agent", USER_AGENT); headers.set("Accept", "application/json");
    const response = await fetch(url, { ...init, headers, redirect: "manual", signal: AbortSignal.timeout(10_000) });
    if (response.status === 400 || response.status === 401 || response.status === 403) { await response.body?.cancel(); throw new ProtocolFailure(401); }
    if (response.status === 429) { await response.body?.cancel(); throw new ProtocolFailure(429); }
    if (!response.ok || response.status !== 200 || !response.headers.get("Content-Type")?.includes("application/json")) {
      await response.body?.cancel(); throw new ProtocolFailure(503);
    }
    try {
      const body: unknown = JSON.parse(await textLimited(response, 65_536));
      if (!body || Array.isArray(body) || typeof body !== "object") throw new ProtocolFailure(503);
      return body as Record<string, unknown>;
    } catch { throw new ProtocolFailure(503); }
  }
  private allow(ip: string, path: string): boolean {
    return this.ctx.storage.transactionSync(() => {
      const now = Date.now();
      const category = path === START ? "start" : path === REFRESH ? "refresh" : "poll";
      const perMinute = category === "start" ? 10 : category === "refresh" ? 12 : 120;
      const minute = "ip:" + ip + ":" + category + ":" + Math.floor(now / 60_000);
      const day = "day:" + Math.floor(now / 86_400_000);
      const count = (key: string) => this.ctx.storage.sql.exec<Budget>("SELECT * FROM budgets WHERE key=?", key).toArray()[0]?.count ?? 0;
      if (count(day) >= 5000) return false;
      // Rejected valid requests count too, bounding work under a distributed abuse burst.
      this.ctx.storage.sql.exec("INSERT INTO budgets VALUES(?,1,?) ON CONFLICT(key) DO UPDATE SET count=count+1", day, (Math.floor(now / 86_400_000) + 1) * 86_400_000);
      const prior = count(minute);
      if (prior >= perMinute) return false;
      if (prior === 0 && this.ctx.storage.sql.exec<{ n: number }>("SELECT COUNT(*) AS n FROM budgets WHERE key LIKE 'ip:%'").one().n >= 256) return false;
      this.ctx.storage.sql.exec("INSERT INTO budgets VALUES(?,1,?) ON CONFLICT(key) DO UPDATE SET count=count+1", minute, now + 120_000);
      return true;
    });
  }
  private purge(): void {
    const now = Date.now();
    this.ctx.storage.transactionSync(() => {
      this.ctx.storage.sql.exec("DELETE FROM sessions WHERE expires<=?", now);
      this.ctx.storage.sql.exec("DELETE FROM refreshes WHERE expires<=?", now);
      this.ctx.storage.sql.exec("DELETE FROM budgets WHERE expires<=?", now);
    });
  }
  private async schedule(): Promise<void> {
    const due = Date.now() + SESSION_MS;
    const prior = await this.ctx.storage.getAlarm();
    if (prior === null || prior > due) await this.ctx.storage.setAlarm(due);
  }
  async alarm(): Promise<void> {
    this.purge();
    // No recurring timer when there are no short-lived credentials or sessions.
    const pending = this.ctx.storage.sql.exec<{ due: number | null }>("SELECT MIN(expires) AS due FROM (SELECT expires FROM sessions UNION ALL SELECT expires FROM refreshes UNION ALL SELECT expires FROM budgets WHERE key LIKE 'ip:%')").one().due;
    if (pending !== null) await this.ctx.storage.setAlarm(Math.max(Date.now() + 1000, pending));
  }
}
