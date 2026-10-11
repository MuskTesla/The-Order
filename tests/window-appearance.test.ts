import test from 'node:test'
import assert from 'node:assert/strict'
import { getWindowAppearance, getWindowSizeForWorkArea } from '../electron/window-appearance'

test('light and dark captions share the loading background and keep readable controls', () => {
  const light = getWindowAppearance({ dark: false, forcedColors: false })
  const dark = getWindowAppearance({ dark: true, forcedColors: false })
  assert.equal(light.backgroundColor, '#f3f3f3')
  assert.equal(light.titleBarOverlay.color, light.backgroundColor)
  assert.equal(light.titleBarOverlay.symbolColor, '#1b1b1b')
  assert.equal(dark.backgroundColor, '#202020')
  assert.equal(dark.titleBarOverlay.color, dark.backgroundColor)
  assert.equal(dark.titleBarOverlay.symbolColor, '#ffffff')
  assert.equal(light.titleBarOverlay.height, 40)
  assert.equal(dark.titleBarOverlay.height, 40)
})

test('forced colors preserve the Windows palette instead of the app theme', () => {
  for (const dark of [false, true]) {
    const appearance = getWindowAppearance({ dark, forcedColors: true, systemBackground: '#123456ff', systemForeground: '#fedcba80' })
    assert.equal(appearance.backgroundColor, '#123456')
    assert.equal(appearance.titleBarOverlay.color, '#123456')
    assert.equal(appearance.titleBarOverlay.symbolColor, '#fedcba')
  }
})

test('each style keeps native captions opaque and matched to its canvas, with system contrast taking priority', () => {
  for (const [appearanceStyle, light, dark] of [['classic', '#f6f7fb', '#151c2a'], ['windows', '#f3f3f3', '#202020'], ['glass', '#e9eef5', '#111923']] as const) {
    for (const isDark of [false, true]) {
      const appearance = getWindowAppearance({ appearanceStyle, dark: isDark, forcedColors: false })
      assert.equal(appearance.backgroundColor, isDark ? dark : light)
      assert.equal(appearance.titleBarOverlay.color, appearance.backgroundColor)
      assert.equal(appearance.titleBarOverlay.height, 40)
      const accessible = getWindowAppearance({ appearanceStyle, dark: isDark, forcedColors: true, systemBackground: '#102030ff', systemForeground: '#eeeeeeff' })
      assert.equal(accessible.backgroundColor, '#102030')
      assert.equal(accessible.titleBarOverlay.symbolColor, '#eeeeee')
    }
  }
})

test('forced colors remain opaque and contrasting when a system color is unavailable', () => {
  assert.deepEqual(getWindowAppearance({ dark: true, forcedColors: true, systemBackground: 'transparent', systemForeground: '#00' }), {
    backgroundColor: '#000000', titleBarOverlay: { color: '#000000', symbolColor: '#ffffff', height: 40 }
  })
  assert.deepEqual(getWindowAppearance({ dark: false, forcedColors: true }), {
    backgroundColor: '#ffffff', titleBarOverlay: { color: '#ffffff', symbolColor: '#000000', height: 40 }
  })
})

test('ordinary monitor work areas retain the desired window size and permit narrow resizing', () => {
  assert.deepEqual(getWindowSizeForWorkArea({ width: 1920, height: 1040 }), { width: 1440, height: 940, minWidth: 500, minHeight: 400 })
})

test('high DPI and small work areas contain the window without changing system scaling', () => {
  for (const workArea of [{ width: 960, height: 520 }, { width: 640, height: 480 }, { width: 400, height: 300 }, { width: 1, height: 1 }]) {
    const size = getWindowSizeForWorkArea(workArea)
    assert.ok(size.width <= workArea.width && size.height <= workArea.height)
    assert.ok(size.width >= size.minWidth && size.height >= size.minHeight)
    assert.ok(size.minWidth <= workArea.width && size.minHeight <= workArea.height)
  }
  assert.deepEqual(getWindowSizeForWorkArea({ width: 960, height: 520 }), { width: 912, height: 472, minWidth: 500, minHeight: 400 })
})

test('fractional or unavailable work dimensions always produce usable integer bounds', () => {
  assert.deepEqual(getWindowSizeForWorkArea({ width: 640.9, height: 480.9 }), { width: 592, height: 432, minWidth: 500, minHeight: 400 })
  const size = getWindowSizeForWorkArea({ width: Number.NaN, height: 0 })
  assert.ok(Object.values(size).every(value => Number.isInteger(value) && value > 0))
  assert.ok(size.width >= size.minWidth && size.height >= size.minHeight)
})
