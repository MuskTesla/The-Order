// Fictional local installs only. Never reads or overwrites a user profile.
import fs from 'node:fs/promises';
import path from 'node:path';
const root = path.resolve(process.argv[2] ?? '.artifacts/native-phase4b/ui-profile');
const formal = path.resolve(process.env.APPDATA, 'KunGalManager').toLowerCase();
if (root.toLowerCase().startsWith(formal) || formal.startsWith(root.toLowerCase())) throw Error('Formal profile boundary');
await fs.mkdir(root); // Refuse existing destination.
const base = JSON.parse(await fs.readFile('native/fixtures/normal/library.json', 'utf8')).games[0];
const names = ['星空の航路','双子の庭','航路 第2章','架空未知作品','離線の庭','遅延の庭','Summer Pockets'];
const games=[];
for (let i=0;i<names.length;i++) {
 const folder=path.join(root,'games',names[i]); await fs.mkdir(folder,{recursive:true});
 await fs.writeFile(path.join(folder,'game.exe'),'synthetic non-executable placeholder');
 if(i===0) await fs.writeFile(path.join(folder,'steam_appid.txt'),'123456');
 games.push({...base,id:'recognition-'+i,name:names[i],nameIsCustom:false,metadata:null,
  installations:[{id:'install-'+i,folderPath:folder,executablePath:path.join(folder,'game.exe'),arguments:[]}],preferredInstallationId:'install-'+i,
  notes:'Phase 4B personal note preserved',categories:['验收'],customTags:[],sessions:[]});
}
await fs.writeFile(path.join(root,'.the-order-synthetic'),'the-order-native-phase1-synthetic-v1');
await fs.writeFile(path.join(root,'library.json'),JSON.stringify({format:'kun-gal-manager',version:1,exportedAt:'2026-10-05T00:00:00.000Z',games}));
console.log(JSON.stringify({root,count:games.length,names}));
