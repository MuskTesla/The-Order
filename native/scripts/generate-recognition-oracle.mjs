import fs from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';
import ts from 'typescript';

// Executes the actual legacy regression corpus with recording wrappers around
// production pure functions. Existing assertions still run; no expected scores
// are invented by the generator or copied from the C# implementation.
const root = path.resolve(import.meta.dirname, '../..');
const cases = [];
for (const [testFile, moduleFile, names] of [
  ['tests/title-matching.test.ts', 'electron/title-matching.ts', ['normalizeGameTitle', 'compareGameTitles', 'gameTitleVariants', 'hasExplicitGameVersion']],
  ['tests/evidence-matching.test.ts', 'electron/evidence-matching.ts', ['rankEvidenceMatches']],
]) {
  const original = await fs.readFile(path.join(root, testFile), 'utf8');
  const target = pathToFileURL(path.join(root, moduleFile)).href;
  let code = original.replace(/import \{ test \} from 'node:test'/, 'let label = ""; function test(name, run) { label = name; run(); }');
  code = code.replace(new RegExp(`import \\{[^}]*\\} from '../${moduleFile.slice(0, -3)}'`),
    `import * as actual from '${target}';\nconst recorded = [];\n${names.map(name => `function ${name}(...args) { const input = structuredClone(args); const output = actual.${name}(...args); recorded.push({group:label, operation:'${name}', input, expected:structuredClone(output)}); return output; }`).join('\n')}`);
  code += '\nexport default recorded;';
  const js = ts.transpileModule(code, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ESNext } }).outputText;
  const captured = (await import('data:text/javascript;base64,' + Buffer.from(js).toString('base64'))).default;
  cases.push(...captured);
}
const sources = {};
for (const file of ['electron/title-matching.ts','electron/evidence-matching.ts','electron/game-identification.ts','electron/local-game-evidence.ts','electron/local-text-evidence.ts','electron/metadata-matching.ts','tests/title-matching.test.ts','tests/evidence-matching.test.ts'])
  sources[file] = createHash('sha256').update(await fs.readFile(path.join(root,file))).digest('hex');
const unique = [...new Map(cases.map(c => [JSON.stringify([c.operation,c.input]), c])).values()];
await fs.writeFile(path.join(root,'native/fixtures/recognition-oracle.json'), JSON.stringify({version:1,sources,cases:unique},null,2)+'\n');
function ranges(regex) {
  const result = []; let start = -1;
  for (let cp=0; cp<=0x110000; cp++) {
    const matches = cp<0x110000 && regex.test(String.fromCodePoint(cp));
    if (matches && start<0) start=cp;
    if (!matches && start>=0) { result.push([start,cp-1]); start=-1; }
  }
  return result;
}
await fs.writeFile(path.join(root,'native/TheOrder.Infrastructure/Recognition/RecognitionUnicode.json'),JSON.stringify({latin:ranges(/\p{Script=Latin}/u),lettersNumbersMarks:ranges(/[\p{L}\p{N}\p{M}]/u)})+'\n');
console.log(JSON.stringify({cases:unique.length,operations:Object.fromEntries([...new Set(unique.map(c=>c.operation))].map(op=>[op,unique.filter(c=>c.operation===op).length]))}));
