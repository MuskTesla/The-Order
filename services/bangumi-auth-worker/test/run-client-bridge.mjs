// Opt-in local contract smoke: real C# client + Windows DPAPI + workerd SQLite,
// with all upstream requests served by synthetic local responses. No Cloudflare API.
import { Miniflare, Response as LocalResponse } from "miniflare";
import { build } from "esbuild";
import { createServer } from "node:http";
import { spawn } from "node:child_process";
import { readFile, mkdir } from "node:fs/promises";
import { resolve } from "node:path";
import assert from "node:assert/strict";
const repo = resolve("../..");
const root = resolve(repo, ".artifacts/bangumi-worker-20261014");
const source = resolve(root, "bridge-worker.mjs");
await build({entryPoints:["src/index.ts"],outfile:source,bundle:true,format:"esm",target:"es2022"});
const origin="https://the-order-bangumi-auth.zyx2251721565.workers.dev";
let exchanges=0,refreshes=0,directMetadata=0;
const access="synthetic_bridge_access_123456789";
const account=()=>JSON.stringify({id:123,username:"bridge-fixture",nickname:"本地闭环账号"});
const upstream=async request=>{
  if(request.url==="https://bgm.tv/oauth/access_token"&&request.method==="POST"){
    const fields=new URLSearchParams(await request.text());
    assert.equal(fields.get("client_id"),"synthetic_app_id");assert.equal(fields.get("client_secret"),"synthetic_client_secret_not_real");
    assert.equal(fields.get("redirect_uri"),origin+"/v1/bangumi/callback");
    if(fields.get("grant_type")==="refresh_token")refreshes++; else exchanges++;
    return new LocalResponse(JSON.stringify({access_token:access,refresh_token:"synthetic_bridge_refresh_"+(exchanges+refreshes)+"_123456789",expires_in:3600,user_id:123}),{headers:{"content-type":"application/json"}});
  }
  if(request.url==="https://api.bgm.tv/v0/me"&&request.method==="GET"){
    assert.equal(request.headers.get("authorization"),"Bearer "+access);
    return new LocalResponse(account(),{headers:{"content-type":"application/json"}});
  }
  throw new Error("Synthetic upstream rejects every other destination");
};
const mf=new Miniflare({modules:true,script:await readFile(source,"utf8"),compatibilityDate:"2026-08-01",
  durableObjects:{AUTH:{className:"BangumiAuthCoordinator",useSQLite:true}},cf:false,outboundService:upstream,
  bindings:{BANGUMI_CLIENT_ID:"synthetic_app_id",BANGUMI_CLIENT_SECRET:"synthetic_client_secret_not_real",BANGUMI_AUTH_ENABLED:"true"}});
await mf.ready;
const server=createServer(async(req,res)=>{
  try{
    const url=new URL(req.url,"http://127.0.0.1");
    if(url.pathname==="/__fixture/me"){
      if(req.headers.authorization!=="Bearer "+access){res.writeHead(401);res.end();return;}
      res.writeHead(200,{"content-type":"application/json"});res.end(account());return;
    }
    if(url.pathname==="/__fixture/subject/13"){
      directMetadata++;const authorized=req.headers.authorization==="Bearer "+access;
      res.writeHead(authorized?200:404,{"content-type":"application/json"});
      res.end(authorized?JSON.stringify({id:13,type:4,nsfw:true,name:"合成受限作品",name_cn:"",summary:"只用于本地授权闭环",date:"2004-08-13",infobox:[]}):"{}");return;
    }
    if(url.pathname==="/__fixture/stats"){res.writeHead(200,{"content-type":"application/json"});res.end(JSON.stringify({exchanges,refreshes,directMetadata}));return;}
    if(!url.pathname.startsWith("/v1/bangumi/")){res.writeHead(404);res.end();return;}
    const chunks=[];let size=0;for await(const chunk of req){size+=chunk.length;if(size>16384)throw new Error("Fixture request too large");chunks.push(chunk);}
    const headers=new Headers();for(const [name,value]of Object.entries(req.headers))if(value&&!["host","connection","transfer-encoding"].includes(name))headers.set(name,Array.isArray(value)?value.join(","):value);
    headers.set("CF-Connecting-IP","192.0.2.20");
    const response=await mf.dispatchFetch(origin+req.url,{method:req.method,headers,body:["POST","PUT"].includes(req.method)?Buffer.concat(chunks):undefined});
    res.writeHead(response.status,Object.fromEntries(response.headers));res.end(Buffer.from(await response.arrayBuffer()));
  }catch{res.writeHead(503);res.end();}
});
await new Promise(resolve=>server.listen(0,"127.0.0.1",resolve));
const address="http://127.0.0.1:"+server.address().port+"/";
const dotnet=process.env.THE_ORDER_TEST_DOTNET;
assert(dotnet,"Set THE_ORDER_TEST_DOTNET to the existing pinned SDK executable");
await mkdir(resolve(root,"tests"),{recursive:true});
const runName="worker-client-bridge-"+Date.now();
try{
  const child=spawn(dotnet,["test",resolve(repo,"native/TheOrder.Tests/TheOrder.Tests.csproj"),"-c","Release","--no-restore","-p:Platform=x64","-p:UseSharedCompilation=true",
    "--filter","FullyQualifiedName~WorkerBrokerIntegrationTests","--logger","trx;LogFileName="+runName+".trx","--results-directory",resolve(root,"tests")],
    {cwd:repo,stdio:"inherit",windowsHide:true,env:{...process.env,THE_ORDER_WORKER_FIXTURE_URL:address}});
  const code=await new Promise((resolve,reject)=>{child.on("exit",resolve);child.on("error",reject);});
  assert.equal(code,0,"C# / Worker contract smoke failed");
  console.log(JSON.stringify({localContract:"PASS",syntheticUpstream:true,exchanges,refreshes,directMetadata,productionProfileAccess:false,cloudflareDeployment:false,realAccountAuthorization:false}));
}finally{await new Promise(resolve=>server.close(resolve));await mf.dispose();}
