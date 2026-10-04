// Synthetic boundary fixtures only; refuse existing destinations and formal data.
import fs from 'node:fs/promises';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
const root = path.resolve(process.argv[2]);
const mode = process.argv[3] ?? 'normal';
if (!['normal', 'corrupt', 'capacity'].includes(mode)) throw Error('Unknown fixture mode');
const formal = path.resolve(process.env.APPDATA, 'KunGalManager').toLowerCase();
if (root.toLowerCase().startsWith(formal) || formal.startsWith(root.toLowerCase())) throw Error('Formal profile boundary');
await fs.mkdir(root);
const base = JSON.parse(await fs.readFile('native/fixtures/normal/library.json', 'utf8')).games[0];
const names = ['星空の航路', '星空の航路 FD', 'Summer Pockets', '星空の航路', '架空未知作品'];
const games = [];
for (let i = 0; i < names.length; i++) {
 const folder = path.join(root, 'games', i === 3 ? 'game' : names[i]); await fs.mkdir(folder, {recursive:true});
 await fs.writeFile(path.join(folder, 'game.exe'), 'synthetic non-executable placeholder');
 if (i === 0) await fs.writeFile(path.join(folder, 'steam_appid.txt'), '123456');
 games.push({...base,id:'boundary-'+i,name:names[i],nameIsCustom:false,
  metadata:i === 3 ? {...base.metadata,id:'200',title:'星空の航路',originalTitle:'星空の航路',coverUrl:null} : null,
  installations:[{id:'install-'+i,folderPath:folder,executablePath:path.join(folder,'game.exe'),arguments:[]}],preferredInstallationId:'install-'+i,
  notes:'Phase 4B.1 personal note preserved',categories:['验收'],customTags:[],sessions:[]});
}
await fs.writeFile(path.join(root,'.the-order-synthetic'),'the-order-native-phase1-synthetic-v1');
await fs.writeFile(path.join(root,'library.json'), JSON.stringify({format:'kun-gal-manager',version:1,exportedAt:'2026-10-05T00:00:00.000Z',games}));
if (mode !== 'normal') {
 const reviews = Array.from({length: mode === 'capacity' ? 10000 : 1}, (_, i) => ({
  id:randomUUID(),gameId:'boundary-4',title:'历史建议 '+i,createdAt:new Date(Date.UTC(2020,0,1)+i*1000).toISOString(),
  status:2,algorithmVersion:'electron-legacy',result:null,historicalCandidates:mode === 'corrupt' ? [null] : [{id:'200',title:'星空の航路',originalTitle:'星空の航路',confidence:.9,reason:'合成历史'}]
 }));
 await fs.writeFile(path.join(root,'native-recognition.json'), JSON.stringify({version:1,reviews}));
}
console.log(JSON.stringify({root,mode,count:games.length,history:mode === 'capacity' ? 10000 : mode === 'corrupt' ? 1 : 0}));
