import type { AppearanceStyle } from '../src/shared/types'

export const WINDOW_TITLEBAR_HEIGHT = 40

export interface WindowAppearanceInput {
  dark: boolean
  appearanceStyle?: AppearanceStyle
  forcedColors: boolean
  systemBackground?: string
  systemForeground?: string
}

function opaqueSystemColor(value: string | undefined, fallback: string): string {
  // Electron returns system colors as #RRGGBBAA. The application window and
  // caption stay opaque even when Windows supplies an alpha channel.
  return value && /^#[\da-f]{6}(?:[\da-f]{2})?$/i.test(value) ? value.slice(0, 7) : fallback
}

export function getWindowAppearance(input: WindowAppearanceInput) {
  const palette = input.appearanceStyle === 'classic' ? ['#f6f7fb', '#151c2a']
    : input.appearanceStyle === 'glass' ? ['#e9eef5', '#111923'] : ['#f3f3f3', '#202020']
  const backgroundColor = input.forcedColors
    ? opaqueSystemColor(input.systemBackground, input.dark ? '#000000' : '#ffffff')
    : palette[input.dark ? 1 : 0]
  const symbolColor = input.forcedColors
    ? opaqueSystemColor(input.systemForeground, input.dark ? '#ffffff' : '#000000')
    : input.dark ? '#ffffff' : '#1b1b1b'
  return { backgroundColor, titleBarOverlay: { color: backgroundColor, symbolColor, height: WINDOW_TITLEBAR_HEIGHT } }
}

function workDimension(value: number, fallback: number): number {
  return Number.isFinite(value) && value >= 1 ? Math.floor(value) : fallback
}

export function getWindowSizeForWorkArea(workArea: { width: number; height: number }) {
  const availableWidth = workDimension(workArea.width, 1440)
  const availableHeight = workDimension(workArea.height, 940)
  const minWidth = Math.min(500, availableWidth)
  const minHeight = Math.min(400, availableHeight)
  // Leave space around an ordinary window, but never exceed a small work area
  // or assume that a monitor's physical pixels are its effective DPI size.
  const width = Math.min(availableWidth, Math.max(minWidth, Math.min(1440, availableWidth - 48)))
  const height = Math.min(availableHeight, Math.max(minHeight, Math.min(940, availableHeight - 48)))
  return { width, height, minWidth, minHeight }
}
