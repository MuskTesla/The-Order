import { build } from 'esbuild'
await build({ entryPoints: ['electron/main.ts'], outfile: 'dist-electron/main.cjs', bundle: true, platform: 'node', target: 'node22', format: 'cjs', external: ['electron'], sourcemap: false })
await build({ entryPoints: ['electron/preload.ts'], outfile: 'dist-electron/preload.cjs', bundle: true, platform: 'node', target: 'node22', format: 'cjs', external: ['electron'], sourcemap: false })
