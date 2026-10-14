// Local workerd fixture only. This entry is NEVER used by wrangler.jsonc.
import Worker, { BangumiAuthCoordinator as ProductionCoordinator } from "../src/index";
export { default } from "../src/index";
export class BangumiAuthCoordinator extends ProductionCoordinator {
  async fetch(request: Request): Promise<Response> {
    if (new URL(request.url).origin !== "https://fixture.invalid") return super.fetch(request);
    const ctx = (this as unknown as { ctx: DurableObjectState }).ctx;
    const path = new URL(request.url).pathname;
    if (path === "/snapshot") return Response.json({
      sessions: ctx.storage.sql.exec("SELECT * FROM sessions").toArray(),
      refreshes: ctx.storage.sql.exec("SELECT * FROM refreshes").toArray(),
      budgets: ctx.storage.sql.exec("SELECT * FROM budgets").toArray()
    });
    if (path === "/expire") { ctx.storage.sql.exec("UPDATE sessions SET expires=0"); ctx.storage.sql.exec("UPDATE refreshes SET expires=0"); await super.alarm(); return new Response(null, { status: 204 }); }
    if (path === "/slots") { ctx.storage.sql.exec("DELETE FROM meta"); return new Response(null, { status: 204 }); }
    if (path === "/daily-limit") { ctx.storage.sql.exec("INSERT OR REPLACE INTO budgets VALUES(?,10000,?)", "day:" + Math.floor(Date.now()/86400000), Date.now()+86400000); return new Response(null, { status: 204 }); }
    return new Response(null, { status: 404 });
  }
}
