import { readFile } from "node:fs/promises";
import assert from "node:assert/strict";
const configuration = JSON.parse(await readFile(new URL("../wrangler.jsonc", import.meta.url), "utf8"));
assert.equal(configuration.name, "the-order-bangumi-auth");
assert.equal(configuration.keep_vars, true);
assert.equal(configuration.workers_dev, true);
assert.equal(configuration.preview_urls, false);
assert.equal(configuration.observability.enabled, false);
assert.equal(configuration.vars.BANGUMI_AUTH_ENABLED, "false");
assert.deepEqual(Object.keys(configuration.vars), ["BANGUMI_AUTH_ENABLED"]);
assert.equal(configuration.env, undefined, "Do not create a second named Worker environment");
assert.equal(configuration.routes, undefined, "Do not overwrite unrelated routes");
assert.deepEqual(configuration.durable_objects.bindings, [{ name: "AUTH", class_name: "BangumiAuthCoordinator" }]);
assert.deepEqual(configuration.migrations, [{ tag: "bangumi-auth-sqlite-v1", new_sqlite_classes: ["BangumiAuthCoordinator"] }]);
const source = await readFile(new URL("../src/protocol.ts", import.meta.url), "utf8");
assert(source.includes("https://the-order-bangumi-auth.zyx2251721565.workers.dev"));
assert(!source.includes("console."));
assert(!(await readFile(new URL("../src/index.ts", import.meta.url), "utf8")).includes("console."));
const client = await readFile(new URL("../../../native/TheOrder.Infrastructure/Online/BangumiOAuthProvider.cs", import.meta.url), "utf8");
assert(client.includes('ServiceUrl = "https://the-order-bangumi-auth.zyx2251721565.workers.dev/"'));
console.log(JSON.stringify({ configuration: "PASS", worker: configuration.name, keepDashboardVariables: true,
  writesClientId: false, writesClientSecret: false, authenticationEnabledOnFirstDeployment: false,
  sqliteDurableObjectNamespaces: 1, productionDeploymentExecuted: false, realAccountAuthorizationExecuted: false }));
