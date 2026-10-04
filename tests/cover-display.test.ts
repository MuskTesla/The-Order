import assert from 'node:assert/strict'
import { test } from 'node:test'
import type { LibraryGame, NonPortraitCoverDisplay, WorkCover, WorkMetadata } from '../src/shared/types'
import { gameCoverDisplay, gameCoverUrl, shouldBlurCover, shouldPreserveCover, workCoverUrl } from '../src/cover-display'

const cover = (id: string, sexual: WorkCover['sexual'] = 'safe'): WorkCover => ({ id, url: `https://synthetic-cover.invalid/${id}.png`, width: 800, height: 600, votes: null, sexual })
const metadata = (): WorkMetadata => ({ id: 'work-one', title: 'Synthetic work', originalTitle: '', aliases: [], coverUrl: cover('default').url, bannerUrl: null, description: '', releaseDate: null, releaseDatePrecision: null, developers: [], tags: [], ratings: [], characters: [], references: [], webUrl: null, contentRating: null, fetchedAt: '2026-10-03T00:00:00.000Z' })
const game = (): LibraryGame => ({ id: 'game-one', name: 'Synthetic game', metadata: metadata(), installations: [], preferredInstallationId: null, status: 'planned', personalRating: null, favorite: false, categories: [], customTags: [], notes: '', totalPlaySeconds: 0, playCount: 0, sessions: [], addedAt: '2026-10-03T00:00:00.000Z', lastPlayedAt: null })

test('no personal choice uses the upstream default independently of cover votes', () => {
  const entry = game()
  entry.metadata!.covers = [{ ...cover('popular'), votes: 999 }, { ...cover('default'), votes: 1 }]
  assert.equal(gameCoverUrl(entry), cover('default').url)
  assert.equal(entry.metadata!.coverUrl, cover('default').url)
})

test('local photos take priority regardless of remote classification and do not require metadata', () => {
  const entry = game()
  entry.localCover = { dataUrl: 'data:image/jpeg;base64,c3ludGhldGlj', width: 800, height: 600 }
  entry.coverChoice = { workId: 'work-one', cover: cover('chosen', 'explicit') }
  entry.metadata!.covers = [cover('default', 'explicit')]
  assert.equal(gameCoverUrl(entry, false), entry.localCover.dataUrl)
  entry.metadata = null
  assert.equal(gameCoverUrl(entry, false), entry.localCover.dataUrl)
  delete entry.localCover
  assert.equal(gameCoverUrl(entry, false), null)
})

test('missing and cleared per-game display follow global defaults while existing overrides remain', () => {
  const entry = game()
  assert.equal(gameCoverDisplay(entry), 'blur')
  assert.equal(gameCoverDisplay(entry, 'crop'), 'crop')
  entry.nonPortraitCoverDisplay = 'plain'
  assert.equal(gameCoverDisplay(entry, 'crop'), 'plain')
  entry.nonPortraitCoverDisplay = null
  assert.equal(gameCoverDisplay(entry, 'crop'), 'crop')
  assert.equal(gameCoverDisplay(undefined, 'plain'), 'plain')
})

test('personal choices apply only to their associated work and do not replace its default metadata', () => {
  const entry = game()
  entry.coverChoice = { workId: 'work-one', cover: cover('chosen') }
  assert.equal(gameCoverUrl(entry), cover('chosen').url)
  assert.equal(entry.metadata!.coverUrl, cover('default').url)
  entry.metadata!.id = 'work-two'
  assert.equal(gameCoverUrl(entry), cover('default').url)
  entry.metadata = null
  assert.equal(gameCoverUrl(entry), null)
})

test('hiding explicit artwork falls back from a personal choice to a permitted default', () => {
  const entry = game()
  entry.coverChoice = { workId: 'work-one', cover: cover('chosen', 'explicit') }
  assert.equal(gameCoverUrl(entry, true), cover('chosen').url)
  assert.equal(gameCoverUrl(entry, false), cover('default').url)
  entry.coverChoice.cover.sexual = 'suggestive'
  assert.equal(gameCoverUrl(entry, false), cover('chosen').url)
})

test('known explicit default or newly classified personal URLs remain hidden', () => {
  const entry = game()
  entry.metadata!.covers = [cover('default', 'explicit'), cover('chosen', 'explicit')]
  assert.equal(gameCoverUrl(entry, false), null)
  assert.equal(workCoverUrl(entry.metadata, false), null)
  entry.coverChoice = { workId: 'work-one', cover: cover('chosen', null) }
  assert.equal(gameCoverUrl(entry, false), null, 'the current known classification overrides an older unknown personal classification')
  assert.equal(gameCoverUrl(entry, true), cover('chosen').url)
})

test('a saved explicit choice cannot reappear as the same default when older metadata lacks a cover collection', () => {
  const entry = game()
  entry.coverChoice = { workId: 'work-one', cover: cover('default', 'explicit') }
  assert.equal(gameCoverUrl(entry, false), null)
})

test('unknown legacy cover classifications retain their URL, and absent covers remain absent', () => {
  const entry = game()
  assert.equal(gameCoverUrl(entry, false), cover('default').url)
  entry.metadata!.covers = [cover('default', null)]
  assert.equal(gameCoverUrl(entry, false), cover('default').url)
  entry.metadata!.coverUrl = null
  assert.equal(gameCoverUrl(entry), null)
  assert.equal(workCoverUrl(null), null)
})

test('landscape and square artwork follows blur, plain, and crop choices even when the frame matches', () => {
  for (const ratio of [1, 4 / 3, 4]) {
    assert.equal(shouldPreserveCover(ratio, ratio, 'blur'), true)
    assert.equal(shouldBlurCover(ratio, ratio, 'blur'), true)
    assert.equal(shouldPreserveCover(ratio, 3 / 4, 'plain'), true)
    assert.equal(shouldBlurCover(ratio, 3 / 4, 'plain'), false)
    assert.equal(shouldPreserveCover(ratio, 3 / 4, 'crop'), false)
    assert.equal(shouldBlurCover(ratio, 3 / 4, 'crop'), false)
  }
})

test('ordinary portrait artwork retains bounded cropping with every display preference', () => {
  for (const display of ['blur', 'plain', 'crop'] as NonPortraitCoverDisplay[]) {
    assert.equal(shouldPreserveCover(2 / 3, 3 / 4, display), false)
    assert.equal(shouldBlurCover(2 / 3, 3 / 4, display), false)
  }
  assert.equal(shouldPreserveCover(.8, 1), false, 'exactly twenty percent loss is still bounded cropping')
  assert.equal(shouldPreserveCover(.79, 1), true)
})

test('crop fills the frame for tall portrait covers and portraits in short frames', () => {
  for (const [imageRatio, frameRatio] of [[1 / 4, 3 / 4], [324 / 563, 3 / 4], [2 / 3, 1.4]]) {
    assert.equal(shouldPreserveCover(imageRatio!, frameRatio!, 'crop'), false)
    assert.equal(shouldBlurCover(imageRatio!, frameRatio!, 'crop'), false)
  }
})

test('special portrait ratios respect complete-image blur and plain backgrounds', () => {
  for (const [imageRatio, frameRatio] of [[1 / 4, 3 / 4], [324 / 563, 3 / 4], [2 / 3, 1.4]]) {
    assert.equal(shouldPreserveCover(imageRatio!, frameRatio!, 'blur'), true)
    assert.equal(shouldBlurCover(imageRatio!, frameRatio!, 'blur'), true)
    assert.equal(shouldPreserveCover(imageRatio!, frameRatio!, 'plain'), true)
    assert.equal(shouldBlurCover(imageRatio!, frameRatio!, 'plain'), false)
  }
})

test('missing or invalid image/frame dimensions never request preservation backgrounds', () => {
  for (const [imageRatio, frameRatio] of [[0, .75], [.75, 0], [-1, .75], [NaN, .75], [.75, Infinity]]) {
    assert.equal(shouldPreserveCover(imageRatio!, frameRatio!), false)
    assert.equal(shouldBlurCover(imageRatio!, frameRatio!), false)
  }
})
