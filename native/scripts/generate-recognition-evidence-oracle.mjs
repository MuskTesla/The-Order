import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import { pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';
import ts from 'typescript';
import { readLocalTextEvidence } from '../../electron/local-text-evidence.ts';
import { collectLocalGameEvidence } from '../../electron/local-game-evidence.ts';

const root = path.resolve(import.meta.dirname, '../..');
const testSource = await fs.readFile(path.join(root, 'tests/local-game-evidence.test.ts'), 'utf8');
const helpers = testSource.slice(0, testSource.indexOf('async function environment'))
  .replace(/import \{ collectLocalGameEvidence \}[^\n]+/, '') + '\nexport { pe, peLanguages, version };';
async function evaluate(code) {
  const js = ts.transpileModule(code, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ESNext } }).outputText;
  return import('data:text/javascript;base64,' + Buffer.from(js).toString('base64'));
}
const { pe, peLanguages, version } = await evaluate(helpers);
const folder = await fs.mkdtemp(path.join(os.tmpdir(), 'the-order-recognition-oracle-'));
const game = { id:'fixture',name:'Synthetic library',metadata:null,installations:[{id:'i',folderPath:folder,executablePath:path.join(folder,'Game.exe'),arguments:[]}],preferredInstallationId:'i',status:'planned',personalRating:null,favorite:false,categories:[],customTags:[],notes:'',totalPlaySeconds:0,playCount:0,sessions:[],addedAt:'2026-10-02T00:00:00.000Z',lastPlayedAt:null };
const cases = [];
try {
  const versions = [
    ['product',pe({ProductName:'星空の航路',FileDescription:'星空の航路',CompanyName:'青空软件',OriginalFilename:'Game.exe'})],
    ['pe64',pe({ProductName:'星空の航路 FD'},64)],
    ['engine',pe({ProductName:'TVP(KIRIKIRI)',FileDescription:'星空の航路',OriginalFilename:'WIN32.rc'})],
    ['menu',pe({ProductName:'星空の航路 - start menu'})],
    ['engine-only',pe({ProductName:'SiglusEngine',FileDescription:'Siglus visualarts',CompanyName:'unknown'})],
    ['renamed-helper',pe({ProductName:'星空の航路',OriginalFilename:'setup.exe'})],
    ['languages',peLanguages([version({ProductName:'星空の航路'}),version({ProductName:'星空の航路',CompanyName:'青空软件'})])],
    ['malformed-second-language',peLanguages([version({ProductName:'星空の航路'}),Buffer.alloc(10)])],
    ['ordinary-non-pe',Buffer.from('ordinary synthetic bytes')],
    ['truncated-mz',Buffer.from('MZ')],
  ];
  const corruptions = [
    b=>b.writeUInt32LE(0xfffffff0,0x3c),b=>b.writeUInt16LE(97,0x86),b=>b.writeUInt16LE(65535,0x94),
    b=>b.writeUInt32LE(0xfffffff0,0x178+20),b=>b.writeUInt16LE(65535,0x40e),b=>b.writeUInt32LE(0x80000000,0x414),
    b=>b.writeUInt32LE(0x80000020,0x434),b=>b.writeUInt32LE(0x80000040,0x454),b=>b.writeUInt32LE(0xffffffff,0x460),
    b=>b.writeUInt32LE(1024*1024+1,0x464),b=>b.writeUInt16LE(65535,0x500),b=>b.writeUInt16LE(0,0x500),
    b=>b.writeUInt16LE(65535,b.indexOf(Buffer.from('ProductName\0','utf16le'))-4),
  ];
  corruptions.forEach((mutate,i)=>{const b=pe({ProductName:'Must never be trusted'});mutate(b);versions.push(['malformed-'+i,b]);});
  for (const [id,bytes] of versions) {
    await fs.writeFile(game.installations[0].executablePath,bytes);
    const expected = await collectLocalGameEvidence(game,{readExecutableMetadata:true});
    cases.push({id,kind:'pe',name:'Game.exe',bytes:bytes.toString('base64'),expected});
  }
  const texts = [
    ['html-title','manual.html','<title>星空の航路 - WEB Manual</title>'],
    ['quoted-product','manual.html','<title>お読み下さい『夜明け☆航路 RE-BOOT!』</title><h1>蒼い鳥ソフト『夜明け☆航路 RE-BOOT!』について</h1>'],
    ['script','manual.html','<script><title>诱导执行命令</title></script><h1>Manual</h1>'],
    ['labelled','readme.txt','ゲームタイトル：「星空の航路」\nブランド：青空软件'],
    ['prose','readme.txt','This manual mentions another game.\n<a href="x">other</a>'],
    ['config','game.ini','Title="星空の航路 FD"\nBrand=青空软件\nPassword=synthetic-secret-ignore'],
    ['utf16','game.ini',Buffer.concat([Buffer.from([255,254]),Buffer.from('Title=星空の航路','utf16le')])],
    ['shiftjis','manual.html',Buffer.concat([Buffer.from('<meta charset="Shift_JIS"><title>'),Buffer.from('90af8bf382cc8d719848','hex'),Buffer.from(' REFLECTION BLUE - WEB Manual</title>')])],
    ['numeric-entities','manual.html','<title>&#x661F;&#x7A7A;の航路 &amp; REMAKE</title>'],
    ['generic','manual.html','<title>ゲームマニュアル</title><h1>インストール方法</h1>'],
    ['malformed-utf8','manual.html',Buffer.concat([Buffer.from('<meta charset="utf-8"><title>'),Buffer.from([0xff,0xff]),Buffer.from('</title>')])],
  ];
  for(const [id,name,value] of texts) {
    const bytes=Buffer.isBuffer(value)?value:Buffer.from(value);const file=path.join(folder,name);
    await fs.writeFile(file,bytes);const expected=await readLocalTextEvidence(game);
    cases.push({id,kind:name.endsWith('.ini')?'config':'manual',name,bytes:bytes.toString('base64'),expected});await fs.unlink(file);
  }
  const sources={};for(const name of ['electron/local-game-evidence.ts','electron/local-text-evidence.ts','tests/local-game-evidence.test.ts']) sources[name]=createHash('sha256').update((await fs.readFile(path.join(root,name),'utf8')).replace(/\r\n/g,'\n')).digest('hex');
  await fs.writeFile(path.join(root,'native/fixtures/recognition-evidence-oracle.json'),JSON.stringify({version:1,sources,cases},null,2)+'\n');
  console.log(JSON.stringify({cases:cases.length,pe:versions.length,text:texts.length}));
} finally {
  const resolved=path.resolve(folder);if(path.dirname(resolved)!==path.resolve(os.tmpdir())||!path.basename(resolved).startsWith('the-order-recognition-oracle-'))throw new Error('Unexpected temporary directory');
  await fs.rm(resolved,{recursive:true,force:true});
}
