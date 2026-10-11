// Calls actual locked Electron production launch implementation with controlled spawn.
import fs from 'node:fs/promises'; import path from 'node:path'; import os from 'node:os'; import vm from 'node:vm';
import { createRequire } from 'node:module'; import { fileURLToPath } from 'node:url'; import { spawnSync } from 'node:child_process';
import { EventEmitter } from 'node:events'; import { createHash } from 'node:crypto'; import { buildSync } from '../../node_modules/esbuild/lib/main.js';
const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
if (!process.versions.electron) { const r = spawnSync(path.join(repo,'node_modules/electron/dist/electron.exe'),[fileURLToPath(import.meta.url)], {env:{...process.env,ELECTRON_RUN_AS_NODE:'1'},windowsHide:true,stdio:'inherit'}); if(r.error) throw r.error; process.exit(r.status ?? 1); }
const electron = JSON.parse(await fs.readFile(path.join(repo,'package.json'))).devDependencies.electron;
if (process.versions.electron !== electron) throw Error('Locked Electron required');
const require = createRequire(import.meta.url);
function actual(file) { const module={exports:{}}; vm.runInNewContext(buildSync({entryPoints:[path.join(repo,file)],bundle:true,write:false,platform:'node',format:'cjs'}).outputFiles[0].text,{module,exports:module.exports,require,process,console,Buffer,URL,setInterval,clearInterval,setTimeout,clearTimeout,structuredClone}); return module.exports; }
const {LibraryStore,scanFolder} = actual('electron/library.ts'); const {PlaytimeTracker} = actual('electron/playtime.ts');
const root=await fs.mkdtemp(path.join(os.tmpdir(),'the-order-launch-oracle-')); const cases=[],continuations=[];
const args=['','two words','日本語','中文','"quote"','a"b','C:\\path\\','$(literal)'];
try {
 for (const spec of [{name:'single',count:1,preferred:0},{name:'single-no-preferred',count:1,preferred:null},{name:'two-no-preferred',count:2,preferred:null},{name:'first',count:2,preferred:0},{name:'second',count:2,preferred:1}]) {
  const dir=path.join(root,spec.name); await fs.mkdir(dir); const folders=[];
  for(let i=0;i<spec.count;i++){const folder=path.join(dir,'game'+i);await fs.mkdir(folder);await fs.writeFile(path.join(folder,'game.exe'),'non-executable synthetic');folders.push(folder);}
  const store=new LibraryStore(path.join(dir,'profile'));await store.initialize();await store.importCandidates(await scanFolder(folders[0]));let game=store.getGames()[0];
  if(spec.count===2) game=await store.attachInstallation(game.id,(await scanFolder(folders[1]))[0]);
  await store.updateGame(game.id,{preferredInstallationId:spec.preferred===null?null:game.installations[spec.preferred].id});
  for(const install of game.installations) await store.setLaunchArguments(game.id,install.id,args);
  const child=new EventEmitter();child.pid=123;child.killed=false;child.kill=()=>{child.killed=true;return true;};const calls=[];
  const tracker=new PlaytimeTracker(store,()=>{}, {spawnProcess:(file,a,cwd)=>{calls.push({index:folders.findIndex(f=>path.dirname(file)===f),args:a,cwdIsExecutableDirectory:cwd===path.dirname(file)});queueMicrotask(()=>child.emit('spawn'));return child;},findProcesses:async()=>[],tickIntervalMs:100000,launcherGraceMs:0});
  let accepted=true,duplicateRejected=false;try{await tracker.launch(game.id);}catch{accepted=false;}
  if(accepted){try{await tracker.launch(game.id);}catch{duplicateRejected=true;}}
  await tracker.shutdown();cases.push({...spec,accepted,call:calls[0]??null,duplicateRejected,noKill:!child.killed});
 }
 for(const name of ['unique-child','same-directory-unrelated','multiple','known-pid','outside','too-old','missing-creation']) {
  const dir=path.join(root,'continuation-'+name);await fs.mkdir(dir);const exe=path.join(dir,'game.exe');await fs.writeFile(exe,'non-executable synthetic');
  const store=new LibraryStore(path.join(dir,'profile'));await store.initialize();await store.importCandidates(await scanFolder(dir));const game=store.getGames()[0];
  const wall=Date.parse('2026-10-05T00:00:00Z'); const candidates=[{pid:name==='known-pid'?500:700,executablePath:name==='outside'?path.join(dir+'-outside','game.exe'):exe,...(name==='missing-creation'?{}:{createdAt:wall+(name==='too-old'?-5000:10)})}];
  if(name==='multiple')candidates.push({pid:701,executablePath:exe,createdAt:wall+20});
  const child=new EventEmitter();child.pid=100;let queries=0;
  const tracker=new PlaytimeTracker(store,()=>{}, {spawnProcess:()=>{queueMicrotask(()=>child.emit('spawn'));return child;},findProcesses:async()=>++queries===1?[{pid:500,executablePath:exe,createdAt:wall-10000}]:candidates,wallNow:()=>wall,tickIntervalMs:100000,launcherGraceMs:10});
  const keepAlive=setInterval(()=>{},1000);
  try {await tracker.launch(game.id);await tracker.afterExit(tracker.active);continuations.push({name,selectedPid:tracker.active?.replacementPid??null,nativeExpected:name==='unique-child'?'handoff':'limited',intentionalImprovement:['same-directory-unrelated','multiple','missing-creation'].includes(name)});}
  finally {await tracker.shutdown();clearInterval(keepAlive);}
 }
 const hashes={};for(const file of ['electron/playtime.ts','electron/library.ts','native/scripts/generate-launch-oracle.mjs']) hashes[file]=createHash('sha256').update((await fs.readFile(path.join(repo,file),'utf8')).replace(/\r\n/g,'\n')).digest('hex');
 await fs.writeFile(path.join(repo,'native/fixtures/launch-oracle.json'),JSON.stringify({electron,hashes,cases,continuations},null,2)+'\n');console.log(JSON.stringify({cases:cases.length,continuations:continuations.length}));
}finally{if(path.dirname(root)!==os.tmpdir()||!path.basename(root).startsWith('the-order-launch-oracle-'))throw Error('Unsafe cleanup');await fs.rm(root,{recursive:true,force:true});}
