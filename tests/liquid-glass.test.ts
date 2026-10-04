import test from 'node:test'
import assert from 'node:assert/strict'
import { glassDisplacement, glassMap } from '../src/liquid-glass'

test('glass leaves center and exterior unchanged and bends only an inward edge band', () => {
  assert.deepEqual(glassDisplacement(400, 120, 24, 200, 60), [0, 0])
  assert.deepEqual(glassDisplacement(400, 120, 24, -5, 60), [0, 0])
  const left = glassDisplacement(400, 120, 24, 8, 60), right = glassDisplacement(400, 120, 24, 392, 60)
  assert.ok(left[0] > .9 && right[0] < -.9)
  assert.equal(left[0], -right[0]); assert.equal(Math.abs(left[1]), 0)
  const top = glassDisplacement(400, 120, 24, 200, 8), bottom = glassDisplacement(400, 120, 24, 200, 112)
  assert.equal(top[1], -bottom[1]); assert.ok(top[1] > .9)
})

test('corner normals remain isotropic in both wide and tall glass surfaces', () => {
  const a = glassDisplacement(400, 120, 24, 12, 12), b = glassDisplacement(120, 400, 24, 12, 12)
  assert.deepEqual(a, b); assert.ok(a[0] > 0); assert.ok(Math.abs(a[0] - a[1]) < .0001)
})

test('displacement textures are bounded and opaque even for large windows or thin controls', () => {
  for (const [width, height] of [[4000, 3000], [70, 1400], [2000, 80], [1, 1]]) {
    const map = glassMap(width, height, 24)
    assert.ok(map.width <= 512 && map.height <= 512)
    assert.ok(map.width * map.height <= 65536)
    assert.equal(map.pixels.length, map.width * map.height * 4)
    for (let i = 0; i < map.pixels.length; i += 4) { assert.equal(map.pixels[i + 2], 128); assert.equal(map.pixels[i + 3], 255) }
  }
  const map = glassMap(400, 120, 24), offset = (60 * map.width + 200) * 4
  assert.equal(map.pixels[offset], 128); assert.equal(map.pixels[offset + 1], 128)
})

test('non-finite and empty geometry is rejected before allocating a texture', () => {
  for (const values of [[0, 30, 5], [30, -1, 5], [Infinity, 2, 1], [3, 4, NaN], [3, 4, -1]]) assert.throws(() => glassMap(...values as [number, number, number]))
})
