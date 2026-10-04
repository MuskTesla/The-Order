import { promises as fs } from 'node:fs'
import path from 'node:path'
import type { NonPortraitCoverDisplay, PublicSettings, SettingsInput, Theme } from '../src/shared/types'
import { normalizeBaseUrl } from './nextmoe'

export interface SecretCodec {
  available(): boolean
  encrypt(value: string): Buffer
  decrypt(value: Buffer): string
}
interface StoredSettings { version: 1; baseUrl: string; encryptedKey: string | null; includeNsfw: boolean; theme: Theme; readExecutableMetadata: boolean; nonPortraitCoverDisplay: NonPortraitCoverDisplay }
function coverDisplay(value: unknown): NonPortraitCoverDisplay {
  if (value !== 'blur' && value !== 'plain' && value !== 'crop') throw new Error('封面展示方式不正确。')
  return value
}

export class SettingsStore {
  private value: StoredSettings = { version: 1, baseUrl: 'https://api.nextmoe.dev/v2/', encryptedKey: null, includeNsfw: true, theme: 'system', readExecutableMetadata: true, nonPortraitCoverDisplay: 'blur' }
  private readonly file: string
  private queue: Promise<void> = Promise.resolve()
  constructor(dataDir: string, private readonly codec: SecretCodec) { this.file = path.join(dataDir, 'settings.json') }

  async initialize(): Promise<void> {
    await fs.mkdir(path.dirname(this.file), { recursive: true })
    try {
      const raw = JSON.parse(await fs.readFile(this.file, 'utf8')) as StoredSettings
      if (raw.version !== 1 || typeof raw.encryptedKey !== 'string' && raw.encryptedKey !== null) throw new Error('格式错误')
      this.value = { version: 1, baseUrl: normalizeBaseUrl(raw.baseUrl), encryptedKey: raw.encryptedKey, includeNsfw: Boolean(raw.includeNsfw), theme: ['light', 'dark', 'system'].includes(raw.theme) ? raw.theme : 'light', readExecutableMetadata: raw.readExecutableMetadata === undefined || raw.readExecutableMetadata === true, nonPortraitCoverDisplay: raw.nonPortraitCoverDisplay === undefined ? 'blur' : coverDisplay(raw.nonPortraitCoverDisplay) }
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw new Error('设置文件无法读取，请在数据目录中检查 settings.json。')
    }
  }

  getPublic(): PublicSettings {
    return { baseUrl: this.value.baseUrl, hasApiKey: Boolean(this.value.encryptedKey), includeNsfw: this.value.includeNsfw, theme: this.value.theme, readExecutableMetadata: this.value.readExecutableMetadata, nonPortraitCoverDisplay: this.value.nonPortraitCoverDisplay }
  }

  getApiOptions(): { baseUrl: string; apiKey: string; includeNsfw: boolean } {
    if (!this.value.encryptedKey) throw new Error('请先在设置中填写 NextMoe API 密钥。')
    if (!this.codec.available()) throw new Error('Windows 凭据保护暂不可用。')
    let apiKey: string
    try { apiKey = this.codec.decrypt(Buffer.from(this.value.encryptedKey, 'base64')) }
    catch { throw new Error('此密钥无法在当前 Windows 账户解密，请重新填写。') }
    return { baseUrl: this.value.baseUrl, apiKey, includeNsfw: this.value.includeNsfw }
  }

  async save(input: SettingsInput): Promise<PublicSettings> {
    return (await this.saveWithEffects(input)).settings
  }

  async saveWithEffects(input: SettingsInput): Promise<{ settings: PublicSettings; matchingChanged: boolean }> {
    const run = this.queue.then(async () => {
      if (!input || typeof input !== 'object' || typeof input.includeNsfw !== 'boolean' || !['light', 'dark', 'system'].includes(input.theme) || input.readExecutableMetadata !== undefined && typeof input.readExecutableMetadata !== 'boolean') throw new Error('设置格式不正确。')
      const baseUrl = normalizeBaseUrl(input.baseUrl)
      const nonPortraitCoverDisplay = input.nonPortraitCoverDisplay === undefined ? this.value.nonPortraitCoverDisplay : coverDisplay(input.nonPortraitCoverDisplay)
      let encryptedKey = this.value.encryptedKey
      if (input.clearApiKey) encryptedKey = null
      if (input.apiKey !== undefined && typeof input.apiKey !== 'string') throw new Error('密钥格式不正确。')
      if (input.apiKey?.trim()) {
        const key = input.apiKey.trim()
        if (key.length > 512 || /\s/.test(key)) throw new Error('密钥应为单行文本。')
        if (!this.codec.available()) throw new Error('Windows 凭据保护暂不可用，无法保存密钥。')
        encryptedKey = this.codec.encrypt(key).toString('base64')
      }
      // Changing the destination cannot silently forward the previous credential.
      if (new URL(baseUrl).origin !== new URL(this.value.baseUrl).origin && !input.apiKey?.trim()) encryptedKey = null
      const next: StoredSettings = { version: 1, baseUrl, encryptedKey, includeNsfw: input.includeNsfw, theme: input.theme, readExecutableMetadata: input.readExecutableMetadata ?? this.value.readExecutableMetadata, nonPortraitCoverDisplay }
      // Compare normalized values inside this transaction. A submitted credential
      // replacement counts even if hasApiKey stays true; never decrypt to compare.
      const matchingChanged = next.baseUrl !== this.value.baseUrl || next.includeNsfw !== this.value.includeNsfw ||
        next.readExecutableMetadata !== this.value.readExecutableMetadata || next.encryptedKey !== this.value.encryptedKey || !!input.apiKey?.trim()
      const temp = this.file + '.tmp'
      await fs.writeFile(temp, JSON.stringify(next, null, 2), { encoding: 'utf8', mode: 0o600 })
      await fs.rename(temp, this.file)
      this.value = next
      return { settings: this.getPublic(), matchingChanged }
    })
    this.queue = run.then(() => undefined, () => undefined)
    return run
  }
}
