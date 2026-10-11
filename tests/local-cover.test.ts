import assert from 'node:assert/strict'
import { test } from 'node:test'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { inspectRaster, MAX_LOCAL_COVER_BYTES, MAX_SOURCE_IMAGE_BYTES, normalizePhoto, readPhotoFile, validateLocalCover, type RasterCodec } from '../electron/local-cover'
import { structuralJpeg } from './local-cover-fixtures'

// Structural JPEG fixtures exercise the pre-decode boundary. Real raster
// decoding/normalization is independently checked by desktop-local-covers.mjs.
const cover = () => ({ dataUrl: 'data:image/jpeg;base64,' + structuralJpeg().toString('base64'), width: 4, height: 3 })
function exif(orientation: number): Buffer {
  const body = Buffer.from([69, 120, 105, 102, 0, 0, 73, 73, 42, 0, 8, 0, 0, 0, 1, 0, 18, 1, 3, 0, 1, 0, 0, 0, orientation, 0, 0, 0, 0, 0, 0, 0])
  const marker = Buffer.alloc(4); marker[0] = 255; marker[1] = 225; marker.writeUInt16BE(body.length + 2, 2)
  return Buffer.concat([marker, body])
}
test('embedded photos accept only canonical bounded JPEG data with matching dimensions', () => {
  assert.deepEqual(validateLocalCover(cover()), cover())
  for (const invalid of [null, { ...cover(), dataUrl: 'data:text/html;base64,PHNjcmlwdD4=' }, { ...cover(), dataUrl: 'data:image/svg+xml;base64,PHN2Zz4=' }, { ...cover(), dataUrl: 'file:///private.png' }, { ...cover(), width: 99 }, { ...cover(), height: null }, { ...cover(), dataUrl: cover().dataUrl + '\n' }, { ...cover(), dataUrl: 'data:image/jpeg;base64,' + Buffer.alloc(MAX_LOCAL_COVER_BYTES + 1).toString('base64') }, { dataUrl: 'data:image/jpeg;base64,' + structuralJpeg(2000, 20).toString('base64'), width: 2000, height: 20 }]) assert.throws(() => validateLocalCover(invalid))
})
test('format inspection rejects active images, truncation and oversized pixels before invoking a decoder', () => {
  assert.deepEqual(inspectRaster(structuralJpeg()), { width: 4, height: 3, orientation: 1, format: 'jpeg' })
  for (const buffer of [Buffer.from('<svg onload="alert(1)"></svg>'), structuralJpeg().subarray(0, 20), structuralJpeg(10000, 10000)]) {
    let invoked = false
    assert.throws(() => normalizePhoto(buffer, { createFromBuffer() { invoked = true; throw Error('decoder must not run') } } as any))
    assert.equal(invoked, false)
  }
})
test('all EXIF orientation values are bounded and normalized before JPEG persistence', () => {
  for (let orientation = 1; orientation <= 8; orientation++) {
    const raw = structuralJpeg(), tagged = Buffer.concat([raw.subarray(0, 2), exif(orientation), raw.subarray(2)])
    assert.equal(inspectRaster(tagged).orientation, orientation)
    const created: Array<{ bitmap: Buffer; width: number; height: number }> = []
    const fake = (width: number, height: number): ReturnType<RasterCodec['createFromBuffer']> => ({ isEmpty: () => false, getSize: () => ({ width, height }), resize: input => fake(input.width, input.height), toBitmap: () => Buffer.alloc(width * height * 4, 0), toJPEG: () => structuralJpeg(width, height) })
    const codec: RasterCodec = { createFromBuffer: () => fake(4, 3), createFromBitmap(bitmap, input) { created.push({ bitmap, ...input }); return fake(input.width, input.height) } }
    const normalized = normalizePhoto(tagged, codec), swapped = orientation >= 5
    assert.equal(normalized.width, swapped ? 3 : 4); assert.equal(normalized.height, swapped ? 4 : 3)
    assert.equal(inspectRaster(Buffer.from(normalized.dataUrl.split(',')[1]!, 'base64')).orientation, 1)
    assert.equal(created[0]!.bitmap.every(value => value === 255), true, 'transparent BGRA pixels become opaque white')
  }
})
test('JPEG normalization prioritizes usable quality and downsizes when all acceptable encodings exceed the cap', () => {
  const encoded: Array<[number, number, number]> = []
  const fake = (width: number, height: number): ReturnType<RasterCodec['createFromBuffer']> => ({ isEmpty: () => false, getSize: () => ({ width, height }), resize: input => fake(input.width, input.height), toBitmap: () => Buffer.alloc(width * height * 4, 255), toJPEG(quality) { encoded.push([width, height, quality]); return structuralJpeg(width, height, width >= 1000 ? MAX_LOCAL_COVER_BYTES : 100) } })
  const codec: RasterCodec = { createFromBuffer: () => fake(1600, 900), createFromBitmap: (_, input) => fake(input.width, input.height) }
  const normalized = normalizePhoto(structuralJpeg(1600, 900), codec)
  assert.equal(encoded[0]![0], 1600, 'a within-cap source is tried at its original resolution first')
  assert.equal(encoded.every(([, , quality]) => quality >= 72), true, 'pixel reduction must not be avoided by crushing JPEG quality')
  assert.equal(normalized.width < 1000 && normalized.width >= 700, true, 'reduce dimensions enough to fit while retaining a useful image')
  assert.equal(encoded.at(-1)![2] >= encoded[0]![2], true, 'a smaller image restarts at the preferred quality')
})
test('source selection uses an absolute allowed extension and bounded regular-file reading', async t => {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'the-order-photo-reader-')); t.after(() => fs.rm(directory, { recursive: true, force: true }))
  const filename = path.join(directory, 'synthetic.jpg'), bytes = structuralJpeg()
  await fs.writeFile(filename, bytes)
  assert.deepEqual(await readPhotoFile(filename), bytes)
  await assert.rejects(readPhotoFile('relative.jpg'))
  await assert.rejects(readPhotoFile(path.join(directory, 'private.svg')))
  await fs.writeFile(path.join(directory, 'wrong.png'), bytes)
  await assert.rejects(readPhotoFile(path.join(directory, 'wrong.png')))
  await fs.writeFile(filename, Buffer.from('<svg/>'))
  await assert.rejects(readPhotoFile(filename))
  const handle = await fs.open(filename, 'w'); await handle.truncate(MAX_SOURCE_IMAGE_BYTES + 1); await handle.close()
  await assert.rejects(readPhotoFile(filename), /20 MB/)
  await assert.rejects(readPhotoFile(path.join(directory, 'missing.jpg')), error => error instanceof Error && !error.message.includes(directory))
})
test('source growth after stat is rejected by the fixed read limit without an unbounded allocation', async t => {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'the-order-photo-growth-')); t.after(() => fs.rm(directory, { recursive: true, force: true }))
  const filename = path.join(directory, 'growing.jpg'); await fs.writeFile(filename, structuralJpeg())
  const original = fs.open
  fs.open = (async (...args: Parameters<typeof fs.open>) => {
    const handle = await original(...args), stat = handle.stat.bind(handle)
    let first = true
    handle.stat = (async () => {
      const before = await stat()
      if (first && args[0] === filename && args[1] === 'r') { first = false; const writer = await original(filename, 'r+'); await writer.truncate(MAX_SOURCE_IMAGE_BYTES + 1); await writer.close() }
      return before
    }) as typeof handle.stat
    return handle
  }) as typeof fs.open
  try { await assert.rejects(readPhotoFile(filename), /20 MB/) } finally { fs.open = original }
})
