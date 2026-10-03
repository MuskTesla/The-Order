export type PlayStatus = 'planned' | 'playing' | 'completed' | 'paused' | 'dropped'
export type Theme = 'light' | 'dark' | 'system'
export type NonPortraitCoverDisplay = 'blur' | 'plain' | 'crop'
export interface LocalCover { dataUrl: string; width: number; height: number }
export interface WorkCover { id: string; url: string; width: number | null; height: number | null; votes: number | null; sexual: 'safe' | 'suggestive' | 'explicit' | null }

export interface SourceRating { source: string; score: number; scale: number; votes: number | null }
export interface WorkReference { source: string; id: string; url?: string }
export interface ReleaseIdentity { id: string; workId: string; title: string; originalTitle: string; references: WorkReference[]; kind: string; date: string | null }
export interface WorkCharacter { id: string; name: string; originalName: string; imageUrl: string | null; role: string; voiceActors: string[] }
export interface WorkMetadata {
  id: string
  title: string
  originalTitle: string
  aliases: string[]
  titleEvidence?: Array<{ text: string; kind: string; isMachine: boolean; language: string | null }>
  coverUrl: string | null
  covers?: WorkCover[]
  bannerUrl: string | null
  description: string
  releaseDate: string | null
  releaseDatePrecision: string | null
  developers: string[]
  tags: string[]
  ratings: SourceRating[]
  characters: WorkCharacter[]
  references: WorkReference[]
  webUrl: string | null
  contentRating: string | null
  fetchedAt: string
}
export interface GameInstallation {
  id: string
  folderPath: string
  executablePath: string | null
  arguments: string[]
}
export interface PlaySession { id: string; startedAt: string; endedAt: string; seconds: number }
export interface LibraryGame {
  id: string
  name: string
  nameIsCustom?: boolean
  metadata: WorkMetadata | null
  coverChoice?: { workId: string; cover: WorkCover }
  localCover?: LocalCover
  nonPortraitCoverDisplay?: NonPortraitCoverDisplay | null
  installations: GameInstallation[]
  preferredInstallationId: string | null
  status: PlayStatus
  personalRating: number | null
  favorite: boolean
  categories: string[]
  customTags: string[]
  notes: string
  totalPlaySeconds: number
  playCount: number
  sessions: PlaySession[]
  addedAt: string
  lastPlayedAt: string | null
}
export interface ActiveSession { gameId: string; installationId: string; startedAt: string; elapsedSeconds: number }
export interface PublicSettings { baseUrl: string; hasApiKey: boolean; includeNsfw: boolean; theme: Theme; readExecutableMetadata: boolean; nonPortraitCoverDisplay?: NonPortraitCoverDisplay }
export interface SettingsInput { baseUrl: string; apiKey?: string; clearApiKey?: boolean; includeNsfw: boolean; theme: Theme; readExecutableMetadata?: boolean; nonPortraitCoverDisplay?: NonPortraitCoverDisplay }
export interface MatchOption { work: WorkMetadata; confidence: number; reason: string }
export interface MatchReviewSource {
  installationId: string; folderPath: string; executablePath: string | null; fingerprint: string
  installations?: Array<{ installationId: string; folderPath: string; executablePath: string | null }>
}
export interface MatchReview {
  gameId: string; name: string; folderPath: string; reason: string; options: MatchOption[]
  reviewId?: string; source?: MatchReviewSource; sourceStatus?: 'current' | 'stale' | 'legacy'; decisionFingerprint?: string
}
export type MetadataMatchingMode = 'missing' | 'reidentify'
export interface MetadataMatchingState { id: string | null; status: 'idle' | 'running' | 'completed' | 'cancelled'; total: number; processed: number; matched: number; unmatched: number; failed: number; currentName: string | null; reviews: MatchReview[]; mode?: MetadataMatchingMode }
export interface AppSnapshot { games: LibraryGame[]; settings: PublicSettings; activeSession: ActiveSession | null; dataPath: string; version: string; matching?: MetadataMatchingState }
export interface ScanCandidate { id: string; name: string; folderPath: string; executables: string[] }
export interface InstallationAttachmentScan { requestId: string; rootPath: string; candidates: ScanCandidate[] }
export interface SearchPage { items: WorkMetadata[]; nextCursor: string | null }
export interface GamePatch { name?: string; status?: PlayStatus; personalRating?: number | null; favorite?: boolean; categories?: string[]; customTags?: string[]; notes?: string; preferredInstallationId?: string | null }
export interface BulkGamePatch { status?: PlayStatus; favorite?: boolean; personalRating?: number | null; addCategories?: string[]; removeCategories?: string[]; addTags?: string[]; removeTags?: string[] }
export interface ConnectionResult { ok: boolean; message: string }
export interface ImportResult { added: number; skipped: number; matchingStarted?: boolean }
export interface KunManagerAPI {
  getState(): Promise<AppSnapshot>
  saveSettings(input: SettingsInput): Promise<PublicSettings>
  testConnection(): Promise<ConnectionResult>
  searchWorks(query: string, cursor?: string): Promise<SearchPage>
  getWork(id: string): Promise<WorkMetadata>
  addVirtualGame(workId: string): Promise<LibraryGame>
  scanFolder(): Promise<ScanCandidate[]>
  prepareInstallationAttachment(gameId: string): Promise<InstallationAttachmentScan | null>
  attachLocalInstallation(requestId: string, candidateId: string): Promise<LibraryGame | null>
  cancelInstallationAttachment(requestId: string): Promise<void>
  importLocalGames(candidateIds: string[], autoMatch?: boolean): Promise<ImportResult>
  startMetadataMatching(gameIds?: string[], mode?: MetadataMatchingMode): Promise<MetadataMatchingState>
  cancelMetadataMatching(): Promise<MetadataMatchingState>
  resolveMetadataMatch(gameId: string, workId: string | null, reviewId?: string, decisionFingerprint?: string): Promise<MetadataMatchingState>
  linkGame(gameId: string, workId: string, reviewId?: string, decisionFingerprint?: string): Promise<LibraryGame>
  refreshGame(gameId: string): Promise<LibraryGame>
  setGameCover(gameId: string, expectedWorkId: string | null, coverId: string | null | undefined, display?: NonPortraitCoverDisplay | null): Promise<LibraryGame>
  pickLocalCover(gameId: string, expectedWorkId: string | null, display?: NonPortraitCoverDisplay | null): Promise<LibraryGame | null>
  updateGame(gameId: string, patch: GamePatch): Promise<LibraryGame>
  updateGames(gameIds: string[], patch: BulkGamePatch): Promise<LibraryGame[]>
  pickExecutable(gameId: string, installationId?: string): Promise<LibraryGame | null>
  setLaunchArguments(gameId: string, installationId: string, args: string[]): Promise<LibraryGame>
  launchGame(gameId: string): Promise<void>
  finishSession(): Promise<void>
  removeGame(gameId: string): Promise<void>
  exportLibrary(): Promise<boolean>
  importLibrary(): Promise<ImportResult | null>
  openExternal(url: string): Promise<void>
  openGameFolder(gameId: string): Promise<void>
  openDataFolder(): Promise<void>
  onChanged(callback: () => void): () => void
}

declare global { interface Window { kunManager?: KunManagerAPI } }
