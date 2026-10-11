// Original rounded-rectangle displacement field. Inspired by the material
// principles of LiquidGlassWinUI and hicccc77/electron-liquid-glass;
// no copied shader code, native hook or desktop capture.
export function glassDisplacement(width: number, height: number, radius: number, x: number, y: number): [number, number] {
  const r = Math.max(0, Math.min(radius, width / 2, height / 2))
  const px = x - width / 2, py = y - height / 2
  const qx = Math.abs(px) - (width / 2 - r), qy = Math.abs(py) - (height / 2 - r)
  const ox = Math.max(qx, 0), oy = Math.max(qy, 0), length = Math.hypot(ox, oy)
  const distance = length + Math.min(Math.max(qx, qy), 0) - r
  const band = Math.min(16, width / 4, height / 4)
  if (band <= 0 || distance >= 0 || distance <= -band) return [0, 0]
  // An inward, smooth edge band with an undisturbed interior. A sharp edge
  // sample would read outside the backdrop and produce dark clipping seams.
  const strength = Math.sin(Math.PI * -distance / band) ** 2
  const nx = length > 0 ? ox / length : qx > qy ? 1 : 0
  const ny = length > 0 ? oy / length : qx > qy ? 0 : 1
  return [-Math.sign(px) * nx * strength, -Math.sign(py) * ny * strength]
}

export function glassMap(width: number, height: number, radius: number) {
  if (![width, height, radius].every(Number.isFinite) || width <= 0 || height <= 0 || radius < 0) throw new Error('Invalid glass geometry')
  const ratio = Math.min(1, 512 / Math.max(width, height), Math.sqrt(65536 / (width * height)))
  const mapWidth = Math.max(1, Math.floor(width * ratio)), mapHeight = Math.max(1, Math.floor(height * ratio))
  const pixels = new Uint8ClampedArray(mapWidth * mapHeight * 4)
  for (let y = 0; y < mapHeight; y++) for (let x = 0; x < mapWidth; x++) {
    const [dx, dy] = glassDisplacement(width, height, radius, (x + .5) * width / mapWidth, (y + .5) * height / mapHeight)
    const index = (y * mapWidth + x) * 4
    pixels[index] = 128 + dx * 127; pixels[index + 1] = 128 + dy * 127
    pixels[index + 2] = 128; pixels[index + 3] = 255
  }
  return { width: mapWidth, height: mapHeight, pixels }
}

const svgNS = 'http://www.w3.org/2000/svg'
const targets = '.sidebar.compact-sidebar,.compact-library-topbar,.expanded-filters,.kun-modal-panel,.detail-aside-controls,.settings-save-bar'
let serial = 0
export function installLiquidGlass(): () => void {
  const create = (name: string, attributes: Record<string, string>) => {
    const node = document.createElementNS(svgNS, name)
    for (const [key, value] of Object.entries(attributes)) node.setAttribute(key, value)
    return node
  }
  const svg = create('svg', { width: '0', height: '0', 'aria-hidden': 'true', 'data-liquid-definitions': '' })
  Object.assign(svg.style, { position: 'fixed', pointerEvents: 'none', overflow: 'hidden' })
  const defs = create('defs', {}); svg.append(defs); document.body.append(svg)
  const surfaces = new Map<HTMLElement, { filter: SVGElement; image: SVGElement; geometry: string }>()
  const queries = ['(forced-colors: active)', '(prefers-reduced-transparency: reduce)', '(prefers-contrast: more)'].map(query => matchMedia(query))
  let frame = 0, disposed = false
  const remove = (element: HTMLElement) => {
    surfaces.get(element)?.filter.remove(); surfaces.delete(element)
    element.style.removeProperty('--liquid-optics'); element.removeAttribute('data-liquid-surface'); resize.unobserve(element)
  }
  const sync = () => {
    frame = 0
    if (disposed) return
    const allowed = !document.hidden && !queries.some(query => query.matches)
    const visible = allowed ? Array.from(document.querySelectorAll<HTMLElement>(targets)).filter(element => element.getClientRects().length > 0).slice(0, 12) : []
    for (const element of surfaces.keys()) if (!visible.includes(element)) remove(element)
    for (const element of visible) {
      // Filter coordinates are local CSS pixels. An opening dialog can be
      // transformed by an ancestor; its visual bounding rect is then smaller,
      // and the end of that transform does not trigger ResizeObserver.
      const width = element.offsetWidth, height = element.offsetHeight
      if (width < 4 || height < 4) continue
      const radius = Math.max(0, parseFloat(getComputedStyle(element).borderTopLeftRadius) || 0)
      const geometry = `${width}:${height}:${radius}`
      let surface = surfaces.get(element)
      if (surface?.geometry === geometry) continue
      const map = glassMap(width, height, radius), canvas = document.createElement('canvas')
      canvas.width = map.width; canvas.height = map.height
      const context = canvas.getContext('2d'); if (!context) continue
      context.putImageData(new ImageData(map.pixels, map.width, map.height), 0, 0)
      if (!surface) {
        const id = `order-liquid-${++serial}`
        const filter = create('filter', { id, x: '0', y: '0', width: String(width), height: String(height), filterUnits: 'userSpaceOnUse', primitiveUnits: 'userSpaceOnUse', 'color-interpolation-filters': 'sRGB' })
        const image = create('feImage', { result: 'edgeMap', preserveAspectRatio: 'none' })
        // SVG maps are encoded in sRGB. Only the backdrop enters this filter;
        // labels, children and hit areas are rendered normally on top.
        filter.append(image)
        // A subpixel difference between wavelengths adds a restrained coloured
        // rim only where the displacement field bends the live backdrop.
        for (const [channel, scale] of [['r', '14.8'], ['g', '14'], ['b', '13.2']]) {
          filter.append(create('feDisplacementMap', { in: 'SourceGraphic', in2: 'edgeMap', scale, xChannelSelector: 'R', yChannelSelector: 'G', result: `bend-${channel}` }))
          const values = [channel === 'r' ? '1 0 0 0 0' : '0 0 0 0 0', channel === 'g' ? '0 1 0 0 0' : '0 0 0 0 0', channel === 'b' ? '0 0 1 0 0' : '0 0 0 0 0', '0 0 0 1 0'].join(' ')
          filter.append(create('feColorMatrix', { in: `bend-${channel}`, type: 'matrix', values, result: channel }))
        }
        filter.append(create('feBlend', { in: 'r', in2: 'g', mode: 'screen', result: 'rg' }), create('feBlend', { in: 'rg', in2: 'b', mode: 'screen' }))
        defs.append(filter); surface = { filter, image, geometry: '' }; surfaces.set(element, surface)
        element.style.setProperty('--liquid-optics', `url("#${id}") blur(1.2px) saturate(1.35)`)
        element.setAttribute('data-liquid-surface', id); resize.observe(element)
      }
      surface.filter.setAttribute('width', String(width)); surface.filter.setAttribute('height', String(height))
      surface.image.setAttribute('width', String(width)); surface.image.setAttribute('height', String(height))
      surface.image.setAttribute('href', canvas.toDataURL('image/png')); surface.geometry = geometry
    }
  }
  const schedule = () => { if (!disposed && !frame) frame = requestAnimationFrame(sync) }
  const resize = new ResizeObserver(schedule)
  // Observe page/modal insertions only. Ignore our own SVG descendants so
  // adding/updating a filter cannot create a self-sustaining observer loop.
  const mutations = new MutationObserver(records => {
    if (records.some(record => !svg.contains(record.target) && record.target !== svg)) schedule()
  })
  mutations.observe(document.body, { childList: true, subtree: true })
  queries.forEach(query => query.addEventListener('change', schedule))
  document.addEventListener('visibilitychange', schedule)
  schedule()
  return () => {
    disposed = true; cancelAnimationFrame(frame); mutations.disconnect(); resize.disconnect()
    queries.forEach(query => query.removeEventListener('change', schedule)); document.removeEventListener('visibilitychange', schedule)
    for (const element of surfaces.keys()) remove(element)
    svg.remove()
  }
}
