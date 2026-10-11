import { spawn } from 'node:child_process'
import { createServer } from 'vite'
import { build } from 'esbuild'
import electron from 'electron'
const server = await createServer()
await server.listen()
await build({ entryPoints: ['electron/main.ts'], outfile: 'dist-electron/main.cjs', bundle: true, platform: 'node', format: 'cjs', external: ['electron'] })
await build({ entryPoints: ['electron/preload.ts'], outfile: 'dist-electron/preload.cjs', bundle: true, platform: 'node', format: 'cjs', external: ['electron'] })
const child = spawn(electron, ['.'], { stdio: 'inherit', env: { ...process.env, KGM_DEV_URL: 'http://127.0.0.1:5173' } })
child.on('exit', async code => { await server.close(); process.exit(code ?? 0) })
process.on('SIGINT', () => child.kill())
