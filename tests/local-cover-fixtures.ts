import type { LocalCover } from '../src/shared/types'

// Header-only JPEG fixtures for validators/transactions. Native decoding uses
// independent, genuinely encoded rasters in desktop-local-covers.mjs.
export function structuralJpeg(width = 4, height = 3, payload = 1): Buffer {
  const header = Buffer.from([255, 216, 255, 192, 0, 11, 8, height >> 8, height & 255, width >> 8, width & 255, 1, 1, 17, 0, 255, 218, 0, 8, 1, 1, 0, 0, 63, 0])
  return Buffer.concat([header, Buffer.alloc(payload, 1), Buffer.from([255, 217])])
}
export const localCoverFixture = (): LocalCover => ({ dataUrl: 'data:image/jpeg;base64,' + structuralJpeg().toString('base64'), width: 4, height: 3 })
