import type { LibraryGame, NonPortraitCoverDisplay, WorkMetadata } from './shared/types'

function permittedUrl(url: string | null | undefined, work: WorkMetadata | null | undefined, includeNsfw: boolean): string | null {
  if (!url) return null
  if (!includeNsfw && work?.covers?.some(cover => cover.url === url && cover.sexual === 'explicit')) return null
  return url
}

/** Unknown legacy cover classifications retain their previous display behavior. */
export function workCoverUrl(work: WorkMetadata | null | undefined, includeNsfw = true): string | null {
  return permittedUrl(work?.coverUrl, work, includeNsfw)
}

/** A personal selection belongs to one work, never to a later reassociation. */
export function gameCoverUrl(game: LibraryGame, includeNsfw = true): string | null {
  const work = game.metadata
  const choice = work && game.coverChoice && game.coverChoice.workId === work.id ? game.coverChoice.cover : null
  if (choice && (includeNsfw || choice.sexual !== 'explicit')) {
    const selected = permittedUrl(choice.url, work, includeNsfw)
    if (selected) return selected
  }
  // A saved explicit selection is also known evidence if the default uses the
  // identical image, even when the older metadata has no covers collection.
  if (!includeNsfw && choice?.sexual === 'explicit' && choice.url === work?.coverUrl) return null
  return workCoverUrl(work, includeNsfw)
}

export function shouldPreserveCover(imageRatio: number, frameRatio: number, display: NonPortraitCoverDisplay = 'blur'): boolean {
  if (!Number.isFinite(imageRatio) || !Number.isFinite(frameRatio) || imageRatio <= 0 || frameRatio <= 0) return false
  if (imageRatio >= 1) return display !== 'crop'
  // Portrait artwork keeps the existing bounded crop policy in small frames.
  return Math.min(imageRatio / frameRatio, frameRatio / imageRatio) < .8
}

export function shouldBlurCover(imageRatio: number, frameRatio: number, display: NonPortraitCoverDisplay = 'blur'): boolean {
  return shouldPreserveCover(imageRatio, frameRatio, display) && !(imageRatio >= 1 && display === 'plain')
}
