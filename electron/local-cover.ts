import { promises as fs } from 'node:fs'
import path from 'node:path'
import type { LocalCover } from '../src/shared/types'

export const MAX_SOURCE_IMAGE_BYTES = 20 * 1024 * 1024
export const MAX_SOURCE_IMAGE_PIXELS = 40_000_000
export const MAX_LOCAL_COVER_BYTES = 128 * 1024
export const MAX_LOCAL_COVER_EDGE = 1600
const PREFIX = 'data:image/jpeg;base64,'
type ImageInfo = { width: number; height: number; orientation: number; format: 'png' | 'jpeg' }
interface RasterImage {
  isEmpty(): boolean
  getSize(): { width: number; height: number }
  resize(options: { width: number; height: number; quality: 'best' }): RasterImage
  toBitmap(): Buffer
  toJPEG(quality: number): Buffer
}
export interface RasterCodec {
  createFromBuffer(buffer: Buffer): RasterImage
  createFromBitmap(buffer: Buffer, options: { width: number; height: number; scaleFactor: number }): RasterImage
}
const invalid = () => new Error('照片不是有效的 PNG/JPG 图片，请重新选择。')
function dimensions(width: number, height: number, maximumEdge = 65535, maximumPixels = MAX_SOURCE_IMAGE_PIXELS): void {
  if (!Number.isSafeInteger(width) || !Number.isSafeInteger(height) || width < 1 || height < 1 || width > maximumEdge || height > maximumEdge || width * height > maximumPixels) throw new Error('照片尺寸过大或无效，请使用不超过 4000 万像素的图片。')
}
function exifOrientation(segment: Buffer): number {
  if (segment.length < 14 || segment.subarray(0, 6).toString('ascii') !== 'Exif\0\0') return 1
  const start = 6, little = segment.toString('ascii', start, start + 2) === 'II'
  if (!little && segment.toString('ascii', start, start + 2) !== 'MM') return 1
  const word = (offset: number) => little ? segment.readUInt16LE(offset) : segment.readUInt16BE(offset)
  const integer = (offset: number) => little ? segment.readUInt32LE(offset) : segment.readUInt32BE(offset)
  if (word(start + 2) !== 42) return 1
  const relative = integer(start + 4)
  if (relative < 8 || relative > segment.length - start - 2) return 1
  const directory = start + relative, count = word(directory)
  if (count > 512 || directory + 2 + count * 12 > segment.length) return 1
  for (let i = 0; i < count; i++) {
    const entry = directory + 2 + i * 12
    if (word(entry) === 0x112 && word(entry + 2) === 3 && integer(entry + 4) === 1) {
      const orientation = word(entry + 8)
      return orientation >= 1 && orientation <= 8 ? orientation : 1
    }
  }
  return 1
}
export function inspectRaster(buffer: Buffer): ImageInfo {
  if (buffer.length >= 33 && buffer.subarray(0, 8).equals(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]))) {
    if (buffer.readUInt32BE(8) !== 13 || buffer.toString('ascii', 12, 16) !== 'IHDR') throw invalid()
    const width = buffer.readUInt32BE(16), height = buffer.readUInt32BE(20)
    dimensions(width, height)
    let position = 8, ended = false, hasData = false
    while (position + 12 <= buffer.length) {
      const length = buffer.readUInt32BE(position), end = position + length + 12
      if (end > buffer.length) throw invalid()
      const kind = buffer.toString('ascii', position + 4, position + 8)
      if (kind === 'acTL') throw new Error('请使用静态 PNG/JPG 照片，不支持动图。')
      if (kind === 'IDAT') hasData = true
      if (kind === 'IEND') { ended = length === 0 && end === buffer.length; break }
      position = end
    }
    if (!ended || !hasData) throw invalid()
    return { width, height, orientation: 1, format: 'png' }
  }
  if (buffer.length < 4 || buffer[0] !== 0xff || buffer[1] !== 0xd8 || buffer[buffer.length - 2] !== 0xff || buffer[buffer.length - 1] !== 0xd9) throw invalid()
  let position = 2, width = 0, height = 0, orientation = 1, scan = false
  while (position + 4 <= buffer.length) {
    if (buffer[position++] !== 0xff) throw invalid()
    while (buffer[position] === 0xff) position++
    const marker = buffer[position++]!
    if (marker === 0xd9) break
    if (marker === 0x00 || marker >= 0xd0 && marker <= 0xd8) throw invalid()
    if (position + 2 > buffer.length) throw invalid()
    const length = buffer.readUInt16BE(position), end = position + length
    if (length < 2 || end > buffer.length) throw invalid()
    if ([0xc0, 0xc1, 0xc2].includes(marker)) {
      if (length < 8) throw invalid()
      height = buffer.readUInt16BE(position + 3); width = buffer.readUInt16BE(position + 5)
      dimensions(width, height)
    }
    if (marker === 0xe1) orientation = exifOrientation(buffer.subarray(position + 2, end))
    if (marker === 0xda) { scan = true; break }
    position = end
  }
  if (!scan || !width || !height) throw invalid()
  return { width, height, orientation, format: 'jpeg' }
}

export function validateLocalCover(value: unknown): LocalCover {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw invalid()
  const raw = value as Record<string, unknown>
  if (typeof raw.dataUrl !== 'string' || !raw.dataUrl.startsWith(PREFIX) || raw.dataUrl.length > PREFIX.length + Math.ceil(MAX_LOCAL_COVER_BYTES / 3) * 4) throw invalid()
  const encoded = raw.dataUrl.slice(PREFIX.length)
  if (!encoded || encoded.length % 4 || !/^[A-Za-z0-9+/]+={0,2}$/.test(encoded)) throw invalid()
  const buffer = Buffer.from(encoded, 'base64')
  if (buffer.length > MAX_LOCAL_COVER_BYTES || buffer.toString('base64') !== encoded) throw invalid()
  const info = inspectRaster(buffer)
  if (info.format !== 'jpeg' || info.orientation !== 1 || info.width !== raw.width || info.height !== raw.height) throw invalid()
  dimensions(info.width, info.height, MAX_LOCAL_COVER_EDGE, MAX_LOCAL_COVER_EDGE ** 2)
  return { dataUrl: PREFIX + encoded, width: info.width, height: info.height }
}

// Opening and reading one handle prevents a path replacement from silently
// selecting another file. The fixed buffer also bounds growth after stat().
export async function readPhotoFile(filename: string): Promise<Buffer> {
  if (!path.isAbsolute(filename) || !['.png', '.jpg', '.jpeg'].includes(path.extname(filename).toLowerCase())) throw invalid()
  let handle: Awaited<ReturnType<typeof fs.open>> | undefined
  try {
    handle = await fs.open(filename, 'r')
    const before = await handle.stat()
    if (!before.isFile() || before.size < 1) throw invalid()
    if (before.size > MAX_SOURCE_IMAGE_BYTES) throw new Error('照片超过 20 MB，请选择较小的 PNG/JPG 图片。')
    const buffer = Buffer.allocUnsafe(MAX_SOURCE_IMAGE_BYTES + 1)
    let total = 0
    while (total < buffer.length) {
      const read = await handle.read(buffer, total, Math.min(65536, buffer.length - total), total)
      if (!read.bytesRead) break
      total += read.bytesRead
    }
    if (total > MAX_SOURCE_IMAGE_BYTES) throw new Error('照片超过 20 MB，请选择较小的 PNG/JPG 图片。')
    const after = await handle.stat()
    if (total !== before.size || before.size !== after.size || before.mtimeMs !== after.mtimeMs) throw new Error('照片在读取过程中发生变化，请重新选择。')
    const result = buffer.subarray(0, total)
    const info = inspectRaster(result)
    if (info.format === 'png' && path.extname(filename).toLowerCase() !== '.png' || info.format === 'jpeg' && path.extname(filename).toLowerCase() === '.png') throw invalid()
    return result
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code) throw new Error('无法读取照片文件，请重新选择 PNG/JPG。')
    throw error
  } finally { await handle?.close() }
}

// Electron's Windows bitmap is premultiplied BGRA. Flattening on white avoids
// black transparent margins; writing a fresh JPEG strips EXIF/GPS metadata.
function orientAndFlatten(image: RasterImage, orientation: number, codec: RasterCodec): RasterImage {
  const { width, height } = image.getSize(), swapped = orientation >= 5
  const outputWidth = swapped ? height : width, outputHeight = swapped ? width : height
  const source = image.toBitmap(), output = Buffer.allocUnsafe(width * height * 4)
  if (source.length !== output.length) throw invalid()
  for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
    let dx = x, dy = y
    switch (orientation) {
      case 2: dx = width - 1 - x; break
      case 3: dx = width - 1 - x; dy = height - 1 - y; break
      case 4: dy = height - 1 - y; break
      case 5: dx = y; dy = x; break
      case 6: dx = height - 1 - y; dy = x; break
      case 7: dx = height - 1 - y; dy = width - 1 - x; break
      case 8: dx = y; dy = width - 1 - x; break
    }
    const from = (y * width + x) * 4, to = (dy * outputWidth + dx) * 4, white = 255 - source[from + 3]!
    for (let channel = 0; channel < 3; channel++) output[to + channel] = Math.min(255, source[from + channel]! + white)
    output[to + 3] = 255
  }
  return codec.createFromBitmap(output, { width: outputWidth, height: outputHeight, scaleFactor: 1 })
}
export function normalizePhoto(buffer: Buffer, codec: RasterCodec): LocalCover {
  if (buffer.length > MAX_SOURCE_IMAGE_BYTES) throw new Error('照片超过 20 MB，请选择较小的 PNG/JPG 图片。')
  const info = inspectRaster(buffer)
  let decoded: RasterImage
  try { decoded = codec.createFromBuffer(buffer) } catch { throw invalid() }
  if (decoded.isEmpty()) throw invalid()
  const size = decoded.getSize()
  if (size.width !== info.width || size.height !== info.height) throw invalid()
  const scale = Math.min(1, MAX_LOCAL_COVER_EDGE / Math.max(size.width, size.height))
  const resized = decoded.resize({ width: Math.max(1, Math.round(size.width * scale)), height: Math.max(1, Math.round(size.height * scale)), quality: 'best' })
  let image = orientAndFlatten(resized, info.orientation, codec)
  // Preserve reasonable JPEG quality first, then reduce dimensions; never
  // preserve a large pixel count by crushing the image to an unusable quality.
  for (let attempt = 0; attempt < 7; attempt++) {
    for (const quality of [88, 80, 72]) {
      const jpeg = image.toJPEG(quality)
      if (jpeg.length <= MAX_LOCAL_COVER_BYTES) {
        const { width, height } = image.getSize()
        return validateLocalCover({ dataUrl: PREFIX + jpeg.toString('base64'), width, height })
      }
    }
    const current = image.getSize()
    image = image.resize({ width: Math.max(1, Math.round(current.width * .8)), height: Math.max(1, Math.round(current.height * .8)), quality: 'best' })
  }
  throw new Error('无法将照片转换为适合书架的封面，请选择较小的 PNG/JPG 图片。')
}

export function assertLocalCoverDecodes(value: LocalCover, codec: RasterCodec): void {
  const cover = validateLocalCover(value)
  const image = codec.createFromBuffer(Buffer.from(cover.dataUrl.slice(PREFIX.length), 'base64'))
  if (image.isEmpty() || image.getSize().width !== cover.width || image.getSize().height !== cover.height) throw invalid()
}
