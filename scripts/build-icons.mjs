// Run with Electron: electron scripts/build-icons.mjs
// Convert the transparent PNG into the Windows multi-resolution ICO container.
import { app, nativeImage } from 'electron'
import { promises as fs } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

try {
  const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
  const source = nativeImage.createFromPath(path.join(root, 'public/the-order.png'))
  if (source.isEmpty()) throw new Error('The Order PNG is missing or invalid')
  const sizes = [16, 24, 32, 48, 64, 128, 256]
  const images = sizes.map(size => source.resize({ width: size, height: size, quality: 'best' }).toPNG())
  const header = Buffer.alloc(6 + images.length * 16)
  header.writeUInt16LE(1, 2)
  header.writeUInt16LE(images.length, 4)
  let offset = header.length
  images.forEach((png, i) => {
    const entry = 6 + i * 16
    header[entry] = sizes[i] === 256 ? 0 : sizes[i]
    header[entry + 1] = sizes[i] === 256 ? 0 : sizes[i]
    header.writeUInt16LE(1, entry + 4)
    header.writeUInt16LE(32, entry + 6)
    header.writeUInt32LE(png.length, entry + 8)
    header.writeUInt32LE(offset, entry + 12)
    offset += png.length
  })
  await fs.writeFile(path.join(root, 'public/icon.ico'), Buffer.concat([header, ...images]))
  console.log(JSON.stringify({ source: source.getSize(), icoSizes: sizes }))
  app.quit()
} catch (error) {
  console.error(error.message)
  app.exit(1)
}
