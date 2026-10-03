<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, reactive, ref, watch } from 'vue'
import { KunButton, KunChip, KunSwitch, KunModal, KunMessageProvider, KunAlertProvider, useKunMessage, useKunAlert } from '@kungal/ui-vue'
import { Library, Compass, LayoutGrid, BarChart3, Settings, Search, Plus, FolderPlus, ArrowUpRight, ChevronRight, Heart, Star, SlidersHorizontal, X, Play, Clock3, BookOpen, Download, Upload, FolderOpen, Link2, RefreshCw, Check, CheckCircle2, ShieldCheck, Monitor, Tags, Sparkles, Square, CircleHelp, HardDrive, ArrowLeft, LoaderCircle } from 'lucide-vue-next'
import { api, isPreview } from './bridge'
import type { AppSnapshot, BulkGamePatch, LibraryGame, PlayStatus, WorkMetadata, ScanCandidate, Theme, MatchReview, InstallationAttachmentScan, NonPortraitCoverDisplay } from './shared/types'
import GameCard from './components/GameCard.vue'
import CoverPicker from './components/CoverPicker.vue'
import DetailCover from './components/DetailCover.vue'
import { gameCoverUrl, workCoverUrl } from './cover-display'
import OrderMark from './components/OrderMark.vue'
import LibraryOverview from './components/LibraryOverview.vue'
import LibraryMaintenance from './components/LibraryMaintenance.vue'
import PlayStatistics from './components/PlayStatistics.vue'
import { filterLibraryGames, folderKey, knownCandidateIds, libraryViewCounts, readLibraryPreferences, saveLibraryPreferences, splitLabels } from './library-workspace'
import type { LibraryLayout, LibrarySort, LibraryView } from './library-workspace'
import { getLaunchInstallation } from './library-insights'

type Page = 'library' | 'discover' | 'categories' | 'stats' | 'settings'
const page = ref<Page>('library')
const navigation = [{ id: 'library' as Page, label: '我的书架', icon: Library }, { id: 'discover' as Page, label: '发现作品', icon: Compass }, { id: 'categories' as Page, label: '分类收藏', icon: LayoutGrid }, { id: 'stats' as Page, label: '游玩统计', icon: BarChart3 }]
const statuses: { value: PlayStatus; label: string }[] = [{ value: 'planned', label: '未开始' }, { value: 'playing', label: '在玩' }, { value: 'completed', label: '已完成' }, { value: 'paused', label: '搁置' }, { value: 'dropped', label: '放弃' }]
const snapshot = ref<AppSnapshot | null>(null)
const loading = ref(true)
const initialError = ref('')
const busy = reactive(new Set<string>())
const libraryQuery = ref('')
const statusFilter = ref<PlayStatus | 'all'>('all')
const favoritesOnly = ref(false)
const categoryFilter = ref('')
const tagFilter = ref('')
const minRating = ref('')
function uiStorage() { try { return typeof window === 'undefined' ? undefined : window.localStorage } catch { return undefined } }
const libraryPreferences = readLibraryPreferences(uiStorage())
const sort = ref<LibrarySort>(libraryPreferences.sort)
const libraryLayout = ref<LibraryLayout>(libraryPreferences.layout)
const overviewVisible = ref(libraryPreferences.overview)
const libraryView = ref<LibraryView>('all')
const selectionMode = ref(false)
const selectedIds = ref<string[]>([])
let selectionGeneration = 0
const batchStatus = ref<PlayStatus | ''>('')
const batchFavorite = ref('')
const batchCategory = ref('')
const batchTag = ref('')
const batchCategoryMode = ref<'add' | 'remove'>('add')
const batchTagMode = ref<'add' | 'remove'>('add')
watch(() => [sort.value, libraryLayout.value, overviewVisible.value], () => saveLibraryPreferences(uiStorage(), { sort: sort.value, layout: libraryLayout.value, overview: overviewVisible.value }))
const filtersOpen = ref(false)
const games = computed(() => snapshot.value?.games ?? [])
const matching = computed(() => snapshot.value?.matching)
const reidentifying = computed(() => matching.value?.mode === 'reidentify')
const matchingRunning = computed(() => matching.value?.status === 'running')
const matchingUnavailable = computed(() => matchingRunning.value || busy.has('matching-start'))
const localGameIds = computed(() => games.value.filter(game => game.installations.length).map(game => game.id))
const selectedLocalIds = computed(() => localGameIds.value.filter(id => selectedIds.value.includes(id)))
const matchReviews = computed(() => {
  const currentIds = new Set(games.value.map(game => game.id))
  return (matching.value?.reviews ?? []).filter(review => currentIds.has(review.gameId))
})
const matchingPercent = computed(() => matching.value?.total ? Math.min(100, Math.max(0, matching.value.processed / matching.value.total * 100)) : 0)
const dismissedMatchingId = ref<string | null>(null)
const matchingVisible = computed(() => !!matching.value && matching.value.status !== 'idle' && (matchingRunning.value || matching.value.id !== dismissedMatchingId.value))
const matchingLabel = computed(() => reidentifying.value
  ? matchingRunning.value ? '正在重新识别本地游戏' : matching.value?.status === 'cancelled' ? '重新识别已停止' : '本次重新识别已完成'
  : matchingRunning.value ? '正在自动补全游戏资料' : matching.value?.status === 'cancelled' ? '资料补全已停止' : '本次资料补全已完成')
const announcedJobs = new Set<string>()
watch(matching, (current, previous) => {
  if (!current?.id || current.status === 'idle' || current.status === 'running') return
  dismissedMatchingId.value = current.id
  if (!previous || announcedJobs.has(current.id) || (previous.id === current.id && previous.status !== 'running')) return
  announcedJobs.add(current.id)
  useKunMessage(current.status === 'cancelled' ? '识别已停止，已完成的结果和待核对项目仍在资料整理中。' : `识别已结束：匹配 ${current.matched} 部，待核对 ${current.reviews.length} 项${current.failed ? `，失败 ${current.failed} 部` : ''}。`, current.failed ? 'info' : 'success')
})
const reviewOpen = ref(false)
const reviewGameId = ref<string | null>(null)
const reviewChoice = ref('')
const currentReview = computed(() => matchReviews.value.find(review => review.gameId === reviewGameId.value) ?? matchReviews.value[0] ?? null)
const reviewGame = computed(() => games.value.find(game => game.id === currentReview.value?.gameId) ?? null)
const maintenanceOpen = ref(false)
const missingGames = computed(() => games.value.filter(game => game.installations.length && (!game.metadata || !game.metadata.coverUrl)))
const unconfiguredGames = computed(() => games.value.filter(game => game.installations.length && !getLaunchInstallation(game)?.executablePath))
watch(() => [currentReview.value?.gameId, currentReview.value?.reviewId, currentReview.value?.decisionFingerprint].join('|'), () => { reviewChoice.value = '' })
watch(() => currentReview.value?.options.map(option => option.work.id).join(','), () => { if (!currentReview.value?.options.some(option => option.work.id === reviewChoice.value)) reviewChoice.value = '' })
const allCategories = computed(() => [...new Set(games.value.flatMap(g => g.categories))].sort())
const allTags = computed(() => [...new Set(games.value.flatMap(g => [...g.customTags, ...(g.metadata?.tags ?? [])]))].sort())
const filteredGames = computed(() => filterLibraryGames(games.value, { query: libraryQuery.value, view: libraryView.value, status: statusFilter.value, favorites: favoritesOnly.value, category: categoryFilter.value, tag: tagFilter.value, minRating: minRating.value }, sort.value))
const viewCounts = computed(() => libraryViewCounts(games.value))
const smartViews: { value: LibraryView; label: string }[] = [{ value: 'all', label: '全部' }, { value: 'local', label: '本地游戏' }, { value: 'wishlist', label: '愿望单' }]
const activeFilterCount = computed(() => Number(statusFilter.value !== 'all') + Number(favoritesOnly.value) + Number(!!categoryFilter.value) + Number(!!tagFilter.value) + Number(minRating.value !== ''))
watch(() => filteredGames.value.map(game => game.id).join('|'), () => { const visible = new Set(filteredGames.value.map(game => game.id)); selectedIds.value = selectedIds.value.filter(id => visible.has(id)) }, { flush: 'sync' })
function leaveSelection() { selectionGeneration++; selectionMode.value = false; selectedIds.value = []; batchStatus.value = ''; batchFavorite.value = ''; batchCategory.value = ''; batchTag.value = '' }
function enterSelection() { selectionGeneration++; selectionMode.value = true; selectedIds.value = [] }
function selectVisible() { if (!busy.has('bulk')) selectedIds.value = filteredGames.value.slice(0, 1000).map(game => game.id) }
function toggleSelection(id: string) { if (busy.has('bulk')) return; selectedIds.value = selectedIds.value.includes(id) ? selectedIds.value.filter(value => value !== id) : [...selectedIds.value, id] }
function applyOverviewFilter(value: string) { page.value = 'library'; resetFilters(); if (value === 'playing') statusFilter.value = 'playing'; else if (['all', 'local', 'wishlist', 'unmatched', 'unconfigured'].includes(value)) libraryView.value = value as LibraryView }
function showMaintenanceFilter(view: 'unmatched' | 'unconfigured') { maintenanceOpen.value = false; applyOverviewFilter(view) }
async function openMaintenanceGame(game: LibraryGame, configure = false) { maintenanceOpen.value = false; if (configure) await configureGame(game); else await showGame(game) }
async function organizeSelected() {
  const ids = [...selectedIds.value], generation = selectionGeneration; if (!ids.length) return
  const patch: BulkGamePatch = {}
  if (batchStatus.value) patch.status = batchStatus.value
  if (batchFavorite.value) patch.favorite = batchFavorite.value === 'yes'
  const categories = splitLabels(batchCategory.value), tags = splitLabels(batchTag.value)
  if (categories.length) patch[batchCategoryMode.value === 'add' ? 'addCategories' : 'removeCategories'] = categories
  if (tags.length) patch[batchTagMode.value === 'add' ? 'addTags' : 'removeTags'] = tags
  if (!Object.keys(patch).length) { useKunMessage('请选择要修改的状态、收藏，或填写分类和标签。', 'info'); return }
  await task('bulk', async () => { await api.updateGames(ids, patch); await reload(); if (generation === selectionGeneration) leaveSelection(); useKunMessage(`已整理 ${ids.length} 部作品。`, 'success') })
}
const totalSeconds = computed(() => games.value.reduce((n, g) => n + g.totalPlaySeconds, 0))
const completed = computed(() => games.value.filter(g => g.status === 'completed').length)
const countStatus = (status: PlayStatus) => games.value.filter(g => g.status === status).length
const hours = (seconds: number) => seconds < 3600 ? `${Math.floor(seconds / 60)} 分钟` : `${(seconds / 3600).toFixed(1)} 小时`
const sourceName = (source: string) => ({ bangumi: 'Bangumi', vndb: 'VNDB', erogamescape: '批评空间', dlsite: 'DLsite', howlongtobeat: 'HowLongToBeat', steam: 'Steam', nextmoe: 'NextMoe' }[source] ?? source)
const releaseDate = (work?: WorkMetadata | null) => !work?.releaseDate ? '发售日期未收录' : work.releaseDatePrecision === 'year' ? work.releaseDate.slice(0, 4) : work.releaseDatePrecision === 'month' ? work.releaseDate.slice(0, 7) : work.releaseDate
function notifyError(error: unknown) { const message = error instanceof Error ? error.message : '操作未完成，请重试。'; useKunMessage(message.replace(/nmk_(?:live|test)_[\w-]+/g, '[已隐藏密钥]'), 'error', 5500) }
async function task<T>(key: string, action: () => Promise<T>): Promise<T | undefined> { if (busy.has(key)) return; busy.add(key); try { return await action() } catch (e) { notifyError(e) } finally { busy.delete(key) } }
let reloadGeneration = 0
async function reload() {
  const generation = ++reloadGeneration
  try {
    const next = await api.getState()
    if (generation === reloadGeneration) snapshot.value = next
  } catch (error) {
    if (generation === reloadGeneration) throw error
  }
}
function navigate(target: Page) { if (target !== 'library') leaveSelection(); page.value = target }
function resetFilters() { libraryQuery.value = ''; statusFilter.value = 'all'; favoritesOnly.value = false; categoryFilter.value = ''; tagFilter.value = ''; minRating.value = ''; libraryView.value = 'all' }
async function favorite(game: LibraryGame) { await task(`favorite-${game.id}`, async () => { await api.updateGame(game.id, { favorite: !game.favorite }); await reload() }) }
async function launch(game: LibraryGame) { await task('launch', async () => { await api.launchGame(game.id); await reload(); useKunMessage('游戏已启动，开始记录游玩时间。', 'success') }) }
async function configureGame(game: LibraryGame) { if (!game.installations.length) { await prepareAttachment(game); return }; await showGame(game); detailTab.value = 'manage' }
async function external(url?: string | null) { if (url) await task('external', () => api.openExternal(url)) }
async function startMatching(gameIds?: string[], mode: 'missing' | 'reidentify' = 'missing'): Promise<boolean> {
  if (matchingUnavailable.value) return false
  const requested = gameIds === undefined ? null : new Set(gameIds)
  const ids = games.value.filter(game => (!requested || requested.has(game.id)) && game.installations.length && (mode === 'reidentify' || !game.metadata || !game.metadata.coverUrl)).map(game => game.id)
  if (!ids.length) { useKunMessage(mode === 'reidentify' ? '没有可重新识别的本地游戏，请先添加本地安装。' : '当前没有需要补全资料的本地游戏。', 'info'); return false }
  if (!snapshot.value?.settings.hasApiKey) {
    useKunMessage(isPreview ? '自动识别需要在桌面版中连接 NextMoe。' : '请先保存 API 密钥，再识别本地游戏。', 'info')
    detailOpen.value = false; scanOpen.value = false; navigate('settings'); return false
  }
  const started = await task('matching-start', async () => {
    dismissedMatchingId.value = null
    // Snapshot plain IDs before crossing the desktop bridge. A late start reply
    // must not overwrite a newer cancellation or job notification.
    const result = await api.startMetadataMatching(ids, mode)
    await reload()
    if (!result.total) { useKunMessage('本次没有可处理的本地游戏。', 'info'); return false }
    const current = matching.value
    if (current?.id !== result.id || current.status === 'cancelled') return false
    return true
  })
  return started === true
}
async function identifySelected() { await startMatching([...selectedLocalIds.value], 'reidentify') }
async function identifyCurrentGame() { const game = selectedGame.value; if (game) await startMatching([game.id], 'reidentify') }
async function cancelMatching() { await task('matching-cancel', async () => { await api.cancelMetadataMatching(); await reload() }) }
function openMatchReviews() { maintenanceOpen.value = false; reviewOpen.value = true; if (!matchReviews.value.some(review => review.gameId === reviewGameId.value)) reviewGameId.value = matchReviews.value[0]?.gameId ?? null }
async function resolveReview(skip = false) {
  const review = currentReview.value; const choice = reviewChoice.value
  if (!review || (!skip && !review.options.some(option => option.work.id === choice))) return
  if (!review.reviewId || !review.decisionFingerprint) { useKunMessage('核对信息正在更新，请重新打开资料整理。', 'info'); return }
  await task('matching-resolve', async () => {
    await api.resolveMetadataMatch(review.gameId, skip ? null : choice, review.reviewId, review.decisionFingerprint)
    await reload()
    if (selectedGameId.value === review.gameId) selectedWork.value = games.value.find(game => game.id === review.gameId)?.metadata ?? selectedWork.value
    useKunMessage(skip ? '本次建议已结束，书架关联和个人记录未改变。' : '已关联你确认的作品资料。', 'success')
  })
}
const reviewDetailOpen = ref(false), reviewDetailWork = ref<WorkMetadata | null>(null), reviewDetailError = ref('')
let reviewDetailGeneration = 0
watch(reviewDetailOpen, open => { if (!open) { reviewDetailGeneration++; busy.delete('review-detail') } })
async function showReviewDetail(work: WorkMetadata) {
  const generation = ++reviewDetailGeneration
  reviewDetailWork.value = work; reviewDetailError.value = ''; reviewDetailOpen.value = true; busy.add('review-detail')
  try { const detail = await api.getWork(work.id); if (generation === reviewDetailGeneration && reviewDetailOpen.value) reviewDetailWork.value = detail }
  catch (error) { if (generation === reviewDetailGeneration) reviewDetailError.value = error instanceof Error ? error.message : '详情读取失败，可以稍后重试。' }
  finally { if (generation === reviewDetailGeneration) busy.delete('review-detail') }
}

const attachmentOpen = ref(false), attachmentScan = ref<InstallationAttachmentScan | null>(null), attachmentGameId = ref<string | null>(null), attachmentError = ref('')
let attachmentGeneration = 0
watch(attachmentOpen, open => {
  if (!open) {
    attachmentGeneration++
    const requestId = attachmentScan.value?.requestId
    attachmentScan.value = null; attachmentGameId.value = null
    if (requestId) void api.cancelInstallationAttachment(requestId).catch(notifyError)
  }
})
async function prepareAttachment(game: LibraryGame) {
  if (busy.has('attachment-prepare')) return
  const generation = ++attachmentGeneration
  await task('attachment-prepare', async () => {
    const result = await api.prepareInstallationAttachment(game.id)
    if (!result) return
    if (generation !== attachmentGeneration || !games.value.some(item => item.id === game.id)) { await api.cancelInstallationAttachment(result.requestId); return }
    attachmentGameId.value = game.id; attachmentScan.value = result; attachmentError.value = ''; attachmentOpen.value = true
  })
}
async function attachCandidate(candidate: ScanCandidate) {
  const prepared = attachmentScan.value, generation = attachmentGeneration
  if (!prepared || !prepared.candidates.some(item => item.id === candidate.id)) return
  await task('attachment-commit', async () => {
    attachmentError.value = ''
    try {
      const updated = await api.attachLocalInstallation(prepared.requestId, candidate.id)
      if (!updated) return
      await reload()
      if (generation === attachmentGeneration) attachmentOpen.value = false
      useKunMessage('已添加本地安装，作品关联和个人记录已保留。', 'success')
    } catch (error) {
      if (generation === attachmentGeneration) attachmentError.value = error instanceof Error ? error.message : '添加失败，请重新选择。'
      await reload()
    }
  })
}

const searchQuery = ref('')
const submittedQuery = ref('')
const searchResults = ref<WorkMetadata[]>([])
const searchCursor = ref<string | null>(null)
const hasSearched = ref(false)
const searchError = ref('')
const searchUsedCursors = new Set<string>()
let searchGeneration = 0
// Editing invalidates the previous request immediately, including an A → B → A
// input change. Only submitting the form starts another API request.
watch(searchQuery, () => {
  searchGeneration++; busy.delete('search'); searchResults.value = []; searchCursor.value = null
  submittedQuery.value = ''; hasSearched.value = false; searchError.value = ''; searchUsedCursors.clear()
}, { flush: 'sync' })
async function search(more = false) {
  const q = more ? submittedQuery.value : searchQuery.value.trim()
  if (!q || busy.has('search') || (more && (!searchCursor.value || q !== searchQuery.value.trim()))) return
  const cursor = more ? searchCursor.value! : undefined
  if (!more) { submittedQuery.value = q; searchResults.value = []; searchCursor.value = null; hasSearched.value = false; searchUsedCursors.clear() }
  const generation = ++searchGeneration; busy.add('search'); searchError.value = ''
  const current = () => generation === searchGeneration && q === submittedQuery.value && q === searchQuery.value.trim()
  try {
    const result = await api.searchWorks(q, cursor)
    if (!current()) return
    const ids = new Set(searchResults.value.map(work => work.id))
    const additions = result.items.filter(work => { if (ids.has(work.id)) return false; ids.add(work.id); return true })
    searchResults.value = more ? [...searchResults.value, ...additions] : additions
    hasSearched.value = true
    if (cursor) searchUsedCursors.add(cursor)
    if (result.nextCursor && searchUsedCursors.has(result.nextCursor)) {
      searchCursor.value = null; useKunMessage('分页标记出现重复，已停止加载。可重新搜索后再试。', 'info')
    } else {
      searchCursor.value = result.nextCursor
      if (!additions.length && result.nextCursor) useKunMessage('此页没有新增结果，可继续加载。', 'info')
    }
  }
  catch (error) { if (current()) { searchError.value = error instanceof Error ? error.message : '搜索失败'; hasSearched.value = true } }
  finally { if (generation === searchGeneration) busy.delete('search') }
}
const detailOpen = ref(false)
const detailTab = ref<'overview' | 'characters' | 'manage'>('overview')
const selectedGameId = ref<string | null>(null)
const selectedWork = ref<WorkMetadata | null>(null)
const selectedGame = computed(() => games.value.find(g => g.id === selectedGameId.value) ?? games.value.find(g => g.metadata?.id === selectedWork.value?.id) ?? null)
watch(() => selectedGame.value?.metadata, metadata => { if (detailOpen.value && selectedGameId.value && metadata) selectedWork.value = metadata })
const detailTitle = computed(() => selectedGame.value?.name ?? selectedWork.value?.title ?? '游戏资料')
const coverPickerOpen = ref(false)
const coverOptionsWork = ref<WorkMetadata | null>(null)
const coverError = ref('')
let coverLoadGeneration = 0, coverSaveGeneration = 0
const detailCoverUrl = computed(() => selectedGame.value
  ? gameCoverUrl({ ...selectedGame.value, metadata: selectedWork.value ?? selectedGame.value.metadata }, snapshot.value?.settings.includeNsfw ?? true)
  : workCoverUrl(selectedWork.value, snapshot.value?.settings.includeNsfw ?? true))
function resetCoverPicker() {
  coverLoadGeneration++; coverSaveGeneration++
  coverPickerOpen.value = false; coverOptionsWork.value = null; coverError.value = ''
  busy.delete('cover-options'); busy.delete('cover-save')
}
function currentCoverContext(gameId: string | null, workId: string, generation: number) {
  return detailOpen.value && coverPickerOpen.value && detailGeneration === generation
    && (selectedGame.value?.id ?? null) === gameId && selectedWork.value?.id === workId
}
async function loadCoverOptions() {
  const work = selectedWork.value, gameId = selectedGame.value?.id ?? null, detail = detailGeneration
  if (!detailOpen.value || !coverPickerOpen.value || !work) return
  const request = ++coverLoadGeneration
  busy.add('cover-options'); coverError.value = ''
  try {
    const full = await api.getWork(work.id)
    if (request === coverLoadGeneration && currentCoverContext(gameId, work.id, detail)) {
      if (full.id !== work.id) throw new Error('作品资料已合并，请先刷新资料后再选择封面。')
      coverOptionsWork.value = full
    }
  } catch (error) {
    if (request === coverLoadGeneration && currentCoverContext(gameId, work.id, detail)) coverError.value = error instanceof Error ? error.message : '封面列表读取失败，请重试。'
  } finally { if (request === coverLoadGeneration) busy.delete('cover-options') }
}
async function toggleCoverPicker() {
  if (coverPickerOpen.value) { closeCoverPicker(); return }
  coverPickerOpen.value = true; detailTab.value = 'overview'
  await loadCoverOptions()
}
function closeCoverPicker() {
  coverLoadGeneration++; coverSaveGeneration++; coverPickerOpen.value = false
  busy.delete('cover-options'); busy.delete('cover-save')
}
watch(() => [selectedGame.value?.id, selectedWork.value?.id], ([gameId, workId], [oldGameId, oldWorkId]) => {
  if (coverPickerOpen.value && (gameId !== oldGameId || workId !== oldWorkId)) resetCoverPicker()
}, { flush: 'sync' })
async function saveCover(coverId: string | null | undefined, display: NonPortraitCoverDisplay) {
  const game = selectedGame.value, workId = selectedWork.value?.id, detail = detailGeneration
  if (!detailOpen.value || !coverPickerOpen.value || !game || !workId || game.metadata?.id !== workId || busy.has('cover-save')) return
  const request = ++coverSaveGeneration
  busy.add('cover-save'); coverError.value = ''
  try {
    await api.setGameCover(game.id, workId, coverId, display)
    await reload()
    if (request === coverSaveGeneration && currentCoverContext(game.id, workId, detail)) useKunMessage('已保存封面与展示方式，之后仍可更换。', 'success')
  } catch (error) {
    if (request === coverSaveGeneration && currentCoverContext(game.id, workId, detail)) coverError.value = error instanceof Error ? error.message : '封面保存失败，请重试。'
  } finally { if (request === coverSaveGeneration) busy.delete('cover-save') }
}
const editor = reactive({ name: '', status: 'planned' as PlayStatus, personalRating: '', categories: '', tags: '', notes: '', launchArgs: '' })
let editorInitialName = ''
let editorGameId: string | null = null
let editorInstallationId: string | null = null
let nameEditGeneration = 0, savedNameEditGeneration = 0, argumentsEditGeneration = 0
function markNameEdited() { nameEditGeneration++ }
function markArgumentsEdited() { argumentsEditGeneration++ }
function editorInstallation(game: LibraryGame) { return game.installations.find(i => i.id === game.preferredInstallationId) ?? game.installations[0] }
function loadInstallationArguments(game: LibraryGame) {
  const installation = editorInstallation(game)
  editorInstallationId = installation?.id ?? null
  editor.launchArgs = JSON.stringify(installation?.arguments ?? [])
  argumentsEditGeneration++
}
function fillEditor(game: LibraryGame) {
  editorInitialName = game.name; editorGameId = game.id; savedNameEditGeneration = nameEditGeneration
  Object.assign(editor, { name: game.name, status: game.status, personalRating: game.personalRating == null ? '' : String(game.personalRating), categories: game.categories.join('，'), tags: game.customTags.join('，'), notes: game.notes })
  loadInstallationArguments(game)
}
function clearEditor() {
  editorInitialName = ''; editorGameId = null; editorInstallationId = null; savedNameEditGeneration = nameEditGeneration; argumentsEditGeneration++
  Object.assign(editor, { name: '', status: 'planned', personalRating: '', categories: '', tags: '', notes: '', launchArgs: '[]' })
}
let detailGeneration = 0
let detailOrigin: HTMLElement | null = null
function closeDetailWithEscape(event: KeyboardEvent) {
  if (event.key === 'Escape' && detailOpen.value && !bindOpen.value && !reviewOpen.value && !scanOpen.value) { event.preventDefault(); detailOpen.value = false }
}
async function showGame(game: LibraryGame) { detailGeneration++; resetCoverPicker(); busy.delete('detail'); detailOrigin = typeof HTMLElement !== 'undefined' && document.activeElement instanceof HTMLElement ? document.activeElement : null; selectedGameId.value = game.id; selectedWork.value = game.metadata; fillEditor(game); detailTab.value = 'overview'; detailOpen.value = true }
async function showWork(work: WorkMetadata) {
  const generation = ++detailGeneration
  resetCoverPicker()
  detailOrigin = typeof HTMLElement !== 'undefined' && document.activeElement instanceof HTMLElement ? document.activeElement : null
  const game = games.value.find(item => item.metadata?.id === work.id)
  selectedGameId.value = game?.id ?? null; selectedWork.value = work
  if (game) fillEditor(game); else clearEditor()
  detailTab.value = 'overview'; detailOpen.value = true; busy.add('detail')
  try {
    const full = await api.getWork(work.id)
    if (!detailOpen.value || generation !== detailGeneration || selectedWork.value?.id !== work.id) return
    selectedWork.value = full
    // A merged work ID can reveal a library entry only after the detail arrives.
    // Initialize that new target once; metadata refreshes never refill its draft.
    const current = selectedGame.value
    if (current && editorGameId !== current.id) { selectedGameId.value = current.id; fillEditor(current) }
  } catch (error) { if (detailOpen.value && generation === detailGeneration && selectedWork.value?.id === work.id) notifyError(error) }
  finally { if (generation === detailGeneration) busy.delete('detail') }
}
watch(detailOpen, value => { if (!value) { detailGeneration++; resetCoverPicker(); busy.delete('detail'); if (detailOrigin?.isConnected) setTimeout(() => detailOrigin?.focus(), 0) } }, { flush: 'sync' })
const splitTags = (text: string) => [...new Set(text.split(/[,，\n]/).map(v => v.trim()).filter(Boolean))]
function sameDetail(gameId: string, generation: number) { return detailOpen.value && detailGeneration === generation && selectedGame.value?.id === gameId }
// Synchronize the field before rendering a newly selected installation. A later
// IPC/reload callback must not overwrite edits made after that render.
watch(() => selectedGame.value && editorInstallation(selectedGame.value)?.id, () => {
  const game = selectedGame.value
  if (detailOpen.value && game && editorGameId === game.id && editorInstallationId !== (editorInstallation(game)?.id ?? null)) loadInstallationArguments(game)
}, { flush: 'sync' })
function syncInstallationArguments(game: LibraryGame, generation: number, previousInstallation: string | null, edits: number) {
  if (sameDetail(game.id, generation) && editorInstallation(selectedGame.value!)?.id === editorInstallation(game)?.id && editorInstallationId === previousInstallation && argumentsEditGeneration === edits && editorInstallationId !== (editorInstallation(game)?.id ?? null)) loadInstallationArguments(game)
}
async function saveGame() {
  const game = selectedGame.value; if (!detailOpen.value || !game || editorGameId !== game.id) return
  await task('save-game', async () => {
    const rating = editor.personalRating === '' ? null : Number(editor.personalRating)
    if (rating !== null && (!Number.isFinite(rating) || rating < 0 || rating > 10)) throw new Error('个人评分请填写 0 到 10 之间的数字。')
    const generation = detailGeneration, edits = nameEditGeneration
    const name = editor.name.trim() || editorInitialName, renamed = edits !== savedNameEditGeneration
    await api.updateGame(game.id, { ...(renamed ? { name } : {}), status: editor.status, personalRating: rating, categories: splitTags(editor.categories), customTags: splitTags(editor.tags), notes: editor.notes })
    if (renamed && sameDetail(game.id, generation)) { savedNameEditGeneration = edits; editorInitialName = name }
    await reload(); useKunMessage('已保存到你的书架。', 'success')
  })
}
async function addToLibrary() {
  const work = selectedWork.value, generation = detailGeneration
  if (!detailOpen.value || !work || selectedGame.value) return
  await task('add-game', async () => {
    const game = await api.addVirtualGame(work.id); await reload()
    if (detailOpen.value && detailGeneration === generation && selectedWork.value?.id === work.id) {
      selectedGameId.value = game.id; selectedWork.value = game.metadata
      if (editorGameId !== game.id) fillEditor(game)
    }
    useKunMessage(isPreview ? '已加入预览书架，刷新页面后重置。' : '已加入愿望单。', 'success')
  })
}
async function pickExecutable(targetInstallationId?: string) { const game = selectedGame.value; if (!game) return; if (!game.installations.length) { await prepareAttachment(game); return }; const generation = detailGeneration, edits = argumentsEditGeneration, installationId = editorInstallationId; await task('executable', async () => { const updated = await api.pickExecutable(game.id, targetInstallationId); if (updated) { await reload(); syncInstallationArguments(updated, generation, installationId, edits); useKunMessage('已设置游戏启动文件。', 'success') } }) }
async function saveArguments() { const game = selectedGame.value; const installation = game?.installations.find(i => i.id === editorInstallationId); if (!detailOpen.value || !game || editorGameId !== game.id || !installation || installation.id !== editorInstallation(game)?.id) return; await task('args', async () => { let args: unknown; try { args = JSON.parse(editor.launchArgs) } catch { throw new Error('启动参数请使用 JSON 字符串数组，例如 ["-windowed"]。') } if (!Array.isArray(args) || args.some(a => typeof a !== 'string')) throw new Error('启动参数须为字符串数组。'); await api.setLaunchArguments(game.id, installation.id, args); await reload(); useKunMessage('启动参数已保存。', 'success') }) }
async function removeGame() {
  const game = selectedGame.value, generation = detailGeneration
  if (!detailOpen.value || !game) return
  await task('remove', async () => {
    const confirmed = await useKunAlert({ title: '从书架移除？', message: `移除「${game.name}」的书架记录。磁盘中的游戏文件会保留。` })
    if (!confirmed || !sameDetail(game.id, generation)) return
    await api.removeGame(game.id)
    // The library notification can remove the entry before this reply returns.
    if (detailOpen.value && detailGeneration === generation && selectedGameId.value === game.id) detailOpen.value = false
    await reload(); useKunMessage('已从书架移除。', 'success')
  })
}
async function refreshMetadata() { const game = selectedGame.value; if (!game) return; const generation = detailGeneration; await task('refresh', async () => { const updated = await api.refreshGame(game.id); if (sameDetail(game.id, generation)) selectedWork.value = updated.metadata; await reload(); useKunMessage('游戏资料已更新。', 'success') }) }

const bindOpen = ref(false)
const bindTargetId = ref<string | null>(null)
const bindReviewContext = ref<{ reviewId: string; fingerprint: string } | null>(null)
const bindQuery = ref('')
const bindResults = ref<WorkMetadata[]>([])
const bindCursor = ref<string | null>(null)
const bindSubmittedQuery = ref('')
const bindHasSearched = ref(false)
const bindError = ref('')
const bindNotice = ref('')
const bindRetryMore = ref(false)
const bindUsedCursors = new Set<string>()
let bindGeneration = 0
function resetBindingSearch() {
  bindGeneration++; busy.delete('bind-search'); bindResults.value = []; bindCursor.value = null
  bindSubmittedQuery.value = ''; bindHasSearched.value = false; bindError.value = ''; bindNotice.value = ''; bindUsedCursors.clear()
}
// Editing is local: only submitting the form calls the API. Invalidate immediately
// so a response cannot become valid again after changing the input back.
watch(bindQuery, resetBindingSearch, { flush: 'sync' })
watch(bindTargetId, resetBindingSearch, { flush: 'sync' })
watch(bindOpen, open => { if (!open) resetBindingSearch() }, { flush: 'sync' })
watch(selectedGameId, (current, previous) => { if (current !== previous && bindOpen.value) bindOpen.value = false }, { flush: 'sync' })
watch(() => games.value.some(game => game.id === bindTargetId.value), exists => { if (!exists && bindOpen.value) bindOpen.value = false }, { flush: 'sync' })
async function beginBind() { resetBindingSearch(); bindReviewContext.value = null; bindTargetId.value = selectedGame.value?.id ?? null; bindQuery.value = selectedGame.value?.name ?? ''; bindOpen.value = true }
function searchReviewAgain(review: MatchReview) { resetBindingSearch(); bindReviewContext.value = review.reviewId && review.decisionFingerprint ? { reviewId: review.reviewId, fingerprint: review.decisionFingerprint } : null; bindTargetId.value = review.gameId; bindQuery.value = games.value.find(game => game.id === review.gameId)?.name ?? review.name; reviewOpen.value = false; bindOpen.value = true }
async function searchBinding(more = false) {
  const query = more ? bindSubmittedQuery.value : bindQuery.value.trim(), target = bindTargetId.value
  if (!bindOpen.value || !target || !games.value.some(game => game.id === target) || !query || busy.has('bind-search')) return
  if (more && (!bindCursor.value || query !== bindQuery.value.trim())) return
  const cursor = more ? bindCursor.value! : undefined, generation = ++bindGeneration
  busy.add('bind-search'); bindError.value = ''; bindNotice.value = ''
  if (!more) { bindResults.value = []; bindCursor.value = null; bindHasSearched.value = false; bindSubmittedQuery.value = query; bindUsedCursors.clear() }
  const current = () => bindOpen.value && generation === bindGeneration && target === bindTargetId.value && query === bindQuery.value.trim() && games.value.some(game => game.id === target)
  try {
    const result = await api.searchWorks(query, cursor)
    if (!current()) return
    const ids = new Set(bindResults.value.map(work => work.id))
    const additions = result.items.filter(work => { if (ids.has(work.id)) return false; ids.add(work.id); return true })
    bindResults.value = more ? [...bindResults.value, ...additions] : additions
    bindHasSearched.value = true
    if (cursor) bindUsedCursors.add(cursor)
    if (result.nextCursor && bindUsedCursors.has(result.nextCursor)) {
      bindCursor.value = null; bindNotice.value = '暂时无法继续加载，请重新搜索。'
    } else {
      bindCursor.value = result.nextCursor
      if (!additions.length && result.nextCursor) bindNotice.value = '此页没有新增结果，可继续加载。'
    }
  } catch { if (current()) { bindRetryMore.value = more; bindError.value = more ? '更多结果加载失败，请重试。' : '搜索失败，请重试。' } }
  finally { if (generation === bindGeneration) busy.delete('bind-search') }
}
async function bindWork(work: WorkMetadata) {
  const game = games.value.find(item => item.id === bindTargetId.value)
  if (!game || !bindResults.value.some(item => item.id === work.id)) { useKunMessage('这部游戏或搜索结果已经失效，请重新搜索。', 'info'); return }
  const generation = bindGeneration, target = bindTargetId.value, detail = detailGeneration
  await task('bind', async () => {
    const context = bindReviewContext.value
    const linked = await api.linkGame(game.id, work.id, context?.reviewId, context?.fingerprint); await reload()
    if (detailOpen.value && detailGeneration === detail && selectedGameId.value === game.id) { selectedGameId.value = linked.id; selectedWork.value = linked.metadata }
    if (generation === bindGeneration && target === bindTargetId.value) bindOpen.value = false
    useKunMessage('已关联你选择的作品资料。', 'success')
  })
}

const scanOpen = ref(false)
const candidates = ref<ScanCandidate[]>([])
const chosen = ref<string[]>([])
const autoMatchOnImport = ref(true)
const scanQuery = ref('')
const alreadyInLibrary = computed(() => knownCandidateIds(candidates.value, games.value))
function candidateOwner(candidate: ScanCandidate): LibraryGame | undefined {
  const root = folderKey(candidate.folderPath)
  return games.value.find(game => game.installations.some(installation => { const owned = folderKey(installation.folderPath); return root === owned || root.startsWith(owned + '/') || owned.startsWith(root + '/') }))
}
const conflictingCandidates = computed(() => new Set(candidates.value.filter(candidate => !alreadyInLibrary.value.has(candidate.id) && candidateOwner(candidate)).map(candidate => candidate.id)))
const scannedLibraryIds = computed(() => {
  const folders = new Set(candidates.value.map(candidate => folderKey(candidate.folderPath)))
  return games.value.filter(game => game.installations.some(installation => folders.has(folderKey(installation.folderPath)))).map(game => game.id)
})
const newCandidates = computed(() => candidates.value.filter(candidate => !alreadyInLibrary.value.has(candidate.id) && !conflictingCandidates.value.has(candidate.id)))
const selectedScanIds = computed(() => chosen.value.filter(id => !alreadyInLibrary.value.has(id) && !conflictingCandidates.value.has(id)))
const visibleCandidates = computed(() => { const q = scanQuery.value.trim().toLocaleLowerCase(); return candidates.value.filter(candidate => !q || candidate.name.toLocaleLowerCase().includes(q) || candidate.folderPath.toLocaleLowerCase().includes(q)) })
watch(() => [...alreadyInLibrary.value, ...conflictingCandidates.value].join('|'), () => { chosen.value = [...selectedScanIds.value] }, { flush: 'sync' })
let scanGeneration = 0
watch(scanOpen, value => { if (!value) scanGeneration++ }, { flush: 'sync' })
async function scan() { await task('scan', async () => { const result = await api.scanFolder(); if (!result.length) { useKunMessage('没有找到可导入的游戏程序，或未选择文件夹。', 'info'); return } scanGeneration++; candidates.value = result; chosen.value = newCandidates.value.map(candidate => candidate.id); scanQuery.value = ''; scanOpen.value = true }) }
async function identifyScannedGames() {
  const generation = scanGeneration, ids = [...scannedLibraryIds.value]
  const started = await startMatching(ids, 'reidentify')
  if (started && scanOpen.value && generation === scanGeneration && !newCandidates.value.length) { scanOpen.value = false; page.value = 'library' }
}
async function importSelected() {
  const generation = scanGeneration, ids = chosen.value.filter(id => !alreadyInLibrary.value.has(id) && !conflictingCandidates.value.has(id))
  if (!ids.length) return
  await task('import', async () => {
    const requestedMatch = autoMatchOnImport.value, waitingForMatching = matchingUnavailable.value
    const canMatch = requestedMatch && !!snapshot.value?.settings.hasApiKey && !waitingForMatching
    const result = await api.importLocalGames(ids, canMatch)
    dismissedMatchingId.value = null
    await reload()
    if (generation === scanGeneration) { scanOpen.value = false; page.value = 'library' }
    const nextStep = result.matchingStarted ? '识别进度和结果可在资料整理中查看。'
      : requestedMatch && !snapshot.value?.settings.hasApiKey ? '本地游戏已保留，配置 API 密钥后，可在“资料整理”中点击“只补缺失”。'
      : requestedMatch && waitingForMatching ? '本地游戏已保留，当前识别任务结束后，可在“资料整理”中点击“只补缺失”。' : ''
    useKunMessage(`已添加 ${result.added} 部游戏，跳过 ${result.skipped} 个重复条目。${nextStep}`, 'success', 6000)
  })
}

const settingsForm = reactive({ baseUrl: 'https://api.nextmoe.dev/v2/', apiKey: '', includeNsfw: false, theme: 'light' as Theme, readExecutableMetadata: true })
watch(() => JSON.stringify(snapshot.value?.settings), () => { const settings = snapshot.value?.settings; if (settings) Object.assign(settingsForm, { baseUrl: settings.baseUrl, includeNsfw: settings.includeNsfw, theme: settings.theme, readExecutableMetadata: settings.readExecutableMetadata }); applyTheme(settings?.theme ?? 'light') })
watch(() => JSON.stringify([snapshot.value?.settings.baseUrl, snapshot.value?.settings.includeNsfw, snapshot.value?.settings.hasApiKey]), (_current, previous) => { if (previous !== undefined) { searchGeneration++; searchResults.value = []; searchCursor.value = null; submittedQuery.value = ''; hasSearched.value = false; searchError.value = ''; busy.delete('search') } })
function applyTheme(theme: Theme) { document.documentElement.classList.toggle('kun-dark-mode', theme === 'dark' || (theme === 'system' && matchMedia('(prefers-color-scheme: dark)').matches)) }
const systemThemeChanged = () => { if (snapshot.value?.settings.theme === 'system') applyTheme('system') }
function workspaceShortcut(event: KeyboardEvent) {
  if (detailOpen.value || bindOpen.value || scanOpen.value || reviewOpen.value || maintenanceOpen.value || attachmentOpen.value || reviewDetailOpen.value || event.altKey || event.isComposing) return
  if ((event.ctrlKey || event.metaKey) && ['f', 'k'].includes(event.key.toLowerCase())) {
    event.preventDefault(); navigate('library'); void nextTick(() => { const input = document.querySelector<HTMLInputElement>('input[aria-label="搜索书架"]'); input?.focus(); input?.select() })
  } else if (event.key === 'Escape' && selectionMode.value) { event.preventDefault(); leaveSelection() }
}
async function saveSettings(clear = false) { await task('settings', async () => { await api.saveSettings({ baseUrl: settingsForm.baseUrl.trim(), includeNsfw: settingsForm.includeNsfw, theme: settingsForm.theme, readExecutableMetadata: settingsForm.readExecutableMetadata, ...(settingsForm.apiKey.trim() ? { apiKey: settingsForm.apiKey.trim() } : {}), ...(clear ? { clearApiKey: true } : {}) }); settingsForm.apiKey = ''; await reload(); useKunMessage(isPreview ? '预览偏好已更新；密钥不会保存在浏览器。' : '设置已保存。', 'success') }) }
async function testConnection() { await task('connection', async () => { const result = await api.testConnection(); useKunMessage(result.message, result.ok ? 'success' : 'warn', 6000) }) }
async function importBackup() { await task('backup-import', async () => { const result = await api.importLibrary(); if (result) { await reload(); useKunMessage(`已导入 ${result.added} 条书架记录，跳过 ${result.skipped} 条。`, 'success') } }) }
async function exportBackup() { await task('export', async () => { if (await api.exportLibrary()) useKunMessage('书架已导出。', 'success') }) }
async function openDataFolder() { await task('data', () => api.openDataFolder()) }
async function openGameFolder() { if (selectedGame.value) await task('folder', () => api.openGameFolder(selectedGame.value!.id)) }
async function finishSession() { await task('finish', async () => { await api.finishSession(); await reload(); useKunMessage('本次计时已结束，游戏会继续运行。', 'info') }) }
async function setPreferred(id: string) { const game = selectedGame.value; if (!game) return; const generation = detailGeneration, edits = argumentsEditGeneration, installationId = editorInstallationId; await task('preferred', async () => { const updated = await api.updateGame(game.id, { preferredInstallationId: id }); await reload(); syncInstallationArguments(updated, generation, installationId, edits) }) }
const activeSeconds = computed(() => snapshot.value?.activeSession?.elapsedSeconds ?? 0)
const activeGame = computed(() => games.value.find(g => g.id === snapshot.value?.activeSession?.gameId))
let unsubscribe: (() => void) | undefined
let themeQuery: MediaQueryList | undefined
onMounted(async () => { document.addEventListener('keydown', closeDetailWithEscape); document.addEventListener('keydown', workspaceShortcut); themeQuery = matchMedia('(prefers-color-scheme: dark)'); themeQuery.addEventListener?.('change', systemThemeChanged); try { await reload(); unsubscribe = api.onChanged(() => { reload().catch(notifyError) }) } catch (error) { initialError.value = error instanceof Error ? error.message : '书架读取失败' } finally { loading.value = false } })
onUnmounted(() => { document.removeEventListener('keydown', closeDetailWithEscape); document.removeEventListener('keydown', workspaceShortcut); themeQuery?.removeEventListener?.('change', systemThemeChanged); unsubscribe?.() })
</script>

<template>
  <div class="app-shell" :class="{ 'library-page': page === 'library' }">
    <aside class="sidebar">
      <button class="brand" @click="navigate('library')"><span class="brand-mark"><OrderMark /></span><span class="brand-copy"><strong>The Order</strong><small>GALGAME LIBRARY</small></span></button>
      <div class="sidebar-section-label">你的游戏时光</div>
      <nav><button v-for="item in navigation" :key="item.id" :class="['nav-item', { active: page === item.id }]" :aria-current="page === item.id ? 'page' : undefined" @click="navigate(item.id)"><component :is="item.icon" :size="19" /><span>{{ item.label }}</span><span v-if="item.id === 'library'" class="nav-count">{{ games.length }}</span></button><button class="nav-item maintenance-entry" @click="maintenanceOpen = true"><Sparkles :size="19" /><span>资料整理</span><span v-if="matchReviews.length" class="nav-count">{{ matchReviews.length }}</span></button></nav>
      <div class="sidebar-divider"></div>
      <div class="sidebar-section-label">快捷收藏 <span>{{ allCategories.length }}</span></div>
      <button class="collection-link" @click="page = 'library'; resetFilters(); favoritesOnly = true"><Heart :size="16" /><span>特别喜欢</span><small>{{ games.filter(g => g.favorite).length }}</small></button>
      <button v-for="category in allCategories.slice(0, 6)" :key="category" class="collection-link" @click="page = 'library'; resetFilters(); categoryFilter = category"><span class="category-dot"></span><span>{{ category }}</span><small>{{ games.filter(g => g.categories.includes(category)).length }}</small></button>
      <p v-if="!allCategories.length" class="sidebar-hint">在游戏详情中创建分类，<br />收好每一种心动。</p>
      <div class="sidebar-bottom"><div class="source-badge"><span class="source-dot"></span><span>数据来自 NextMoe · 鲲</span><button aria-label="打开开发者平台" @click="external('https://developer.nextmoe.dev/')"><ArrowUpRight :size="14" /></button></div><button :class="['nav-item', { active: page === 'settings' }]" @click="navigate('settings')"><Settings :size="18" /><span>偏好设置</span><span class="version">v{{ snapshot?.version ?? '0.7.1' }}</span></button></div>
    </aside>

    <div :class="['workspace', { 'library-workspace': page === 'library', 'library-busy': matchingRunning || !!snapshot?.activeSession }]">
      <div v-if="isPreview" class="preview-notice"><Monitor :size="14" /><span>界面预览 · 示例封面来自真实资料，书架操作仅在内存中生效。打开桌面版使用本地功能。</span></div>
      <header class="topbar" :class="{ 'compact-library-topbar': page === 'library' }"><div v-if="page === 'library'" class="library-heading"><h1>我的书架 <span class="heading-count">{{ games.length }}</span></h1></div><div v-else class="breadcrumb">The Order <ChevronRight :size="13" /> {{ page === 'settings' ? '偏好设置' : navigation.find(n => n.id === page)?.label }}</div>
        <div v-if="page === 'library'" class="library-tools"><label class="search-field"><Search :size="16" /><input v-model="libraryQuery" placeholder="搜索名称、别名、标签…" aria-label="搜索书架" /><button v-if="libraryQuery" aria-label="清空搜索" @click="libraryQuery = ''"><X :size="14" /></button></label><select v-model="sort" aria-label="游戏排序" class="sort-select"><option value="added">最近添加</option><option value="recent">最近游玩</option><option value="name">名称排序</option><option value="rating">我的评分</option><option value="playtime">游玩时长</option></select><div class="library-view-actions"><button aria-label="封面视图" :aria-pressed="libraryLayout === 'grid'" :class="{ active: libraryLayout === 'grid' }" @click="libraryLayout = 'grid'"><LayoutGrid :size="17" /></button><button aria-label="列表视图" :aria-pressed="libraryLayout === 'list'" :class="{ active: libraryLayout === 'list' }" @click="libraryLayout = 'list'"><Library :size="17" /></button></div></div>
        <div class="topbar-actions"><button v-if="page === 'library' && !selectionMode && games.length" class="filter-button" @click="enterSelection"><CheckCircle2 :size="16" /> 选择整理</button><span v-if="page !== 'library'" class="local-badge"><ShieldCheck :size="14" /> 本地书架</span><KunButton color="primary" variant="flat" size="sm" :loading="busy.has('scan')" @click="scan"><Plus :size="16" /> 添加游戏</KunButton></div></header>
      <section v-if="matchingVisible && matching" class="matching-banner" :class="{ running: matchingRunning }" :aria-label="reidentifying ? '游戏重新识别进度' : '游戏资料补全进度'">
        <span class="matching-icon"><LoaderCircle v-if="matchingRunning" :size="20" class="spin" /><CheckCircle2 v-else-if="matching.status === 'completed'" :size="20" /><Square v-else :size="17" /></span>
        <div class="matching-banner-main"><div class="matching-heading"><strong>{{ matchingLabel }}</strong><span>{{ matching.processed }} / {{ matching.total }}</span></div><p v-if="matchingRunning" role="status" aria-live="polite">{{ matching.currentName ? `正在识别：${matching.currentName}` : '正在准备下一部作品…' }}</p><p v-else>{{ reidentifying ? '已核实' : '已补全' }} {{ matching.matched }} 部 · 未自动匹配 {{ matching.unmatched }} 部<span v-if="matching.failed"> · 失败 {{ matching.failed }} 部</span>。已完成的结果已保留。</p><div class="matching-progress" role="progressbar" :aria-valuemin="0" :aria-valuemax="matching.total || 1" :aria-valuenow="matching.processed" :aria-label="matchingLabel"><i :style="{ width: `${matchingPercent}%` }"></i></div></div>
        <div class="matching-banner-actions"><KunButton v-if="matchReviews.length" variant="flat" color="primary" size="sm" @click="openMatchReviews">待核对 {{ matchReviews.length }} 项 <ChevronRight :size="14" /></KunButton><KunButton v-if="matchingRunning" variant="light" size="sm" :loading="busy.has('matching-cancel')" @click="cancelMatching">{{ reidentifying ? '取消识别' : '取消补全' }}</KunButton><button v-else class="matching-dismiss" :aria-label="reidentifying ? '收起重新识别结果' : '收起资料补全结果'" @click="dismissedMatchingId = matching.id"><X :size="16" /></button></div>
      </section>
      <main :class="{ 'library-main': page === 'library' }">
        <div v-if="loading" class="empty-state"><LoaderCircle class="spin" :size="32" /><h2>正在打开你的书架</h2></div>
        <div v-else-if="initialError" class="empty-state"><CircleHelp :size="32" /><h2>书架暂时无法打开</h2><p>{{ initialError }}</p><KunButton @click="task('retry', async () => { await reload(); initialError = '' })">重新读取</KunButton></div>
        <template v-else-if="page === 'library'">
          <section class="library-controls">
            <div class="library-filter-line">
              <div class="library-smart-views" role="group" aria-label="书架快捷视图"><button v-for="view in smartViews" :key="view.value" :class="{ active: libraryView === view.value }" :aria-pressed="libraryView === view.value" @click="libraryView = view.value">{{ view.label }}</button></div>
              <div class="status-tabs" role="group" aria-label="游玩状态"><button :class="{ active: statusFilter === 'all' }" :aria-pressed="statusFilter === 'all'" @click="statusFilter = 'all'">全部进度</button><button v-for="status in statuses" :key="status.value" :class="{ active: statusFilter === status.value }" :aria-pressed="statusFilter === status.value" @click="statusFilter = status.value">{{ status.label }}</button></div>
              <button :class="['filter-button', { selected: filtersOpen || activeFilterCount > 0 }]" :aria-expanded="filtersOpen" aria-controls="library-expanded-filters" @click="filtersOpen = !filtersOpen"><SlidersHorizontal :size="16" /> 筛选 <small v-if="activeFilterCount">{{ activeFilterCount }}</small></button>
            </div>
            <div v-if="filtersOpen" class="expanded-filters" id="library-expanded-filters"><label>分类<select v-model="categoryFilter"><option value="">全部分类</option><option v-for="c in allCategories" :key="c">{{ c }}</option></select></label><label>标签<select v-model="tagFilter"><option value="">全部标签</option><option v-for="t in allTags" :key="t">{{ t }}</option></select></label><label>我的评分<select v-model="minRating"><option value="">不限</option><option value="8">8 分及以上</option><option value="6">6 分及以上</option><option value="0">已评分</option></select></label><label class="filter-check"><input v-model="favoritesOnly" type="checkbox" /> 只看收藏</label><label class="filter-check"><input v-model="overviewVisible" type="checkbox" /> 显示继续游玩</label><button class="text-button" @click="resetFilters">重置筛选</button></div>
            <div v-if="activeFilterCount || libraryQuery" class="active-filter-chips" aria-label="当前筛选"><button v-if="libraryQuery" @click="libraryQuery = ''">搜索：{{ libraryQuery }} <X :size="12" /></button><button v-if="categoryFilter" aria-label="移除分类筛选" @click="categoryFilter = ''">分类：{{ categoryFilter }} <X :size="12" /></button><button v-if="tagFilter" aria-label="移除标签筛选" @click="tagFilter = ''">标签：{{ tagFilter }} <X :size="12" /></button><button v-if="favoritesOnly" @click="favoritesOnly = false">特别喜欢 <X :size="12" /></button><button v-if="minRating !== ''" @click="minRating = ''">{{ minRating === '0' ? '已评分' : `评分 ≥ ${minRating}` }} <X :size="12" /></button><button v-if="statusFilter !== 'all'" @click="statusFilter = 'all'">{{ statuses.find(status => status.value === statusFilter)?.label }} <X :size="12" /></button></div>
          </section>
          <LibraryOverview v-if="games.length && overviewVisible && !snapshot?.activeSession && !selectionMode && libraryView === 'all' && !libraryQuery && statusFilter === 'all' && !favoritesOnly && !categoryFilter && !tagFilter && minRating === ''" :games="games" :include-nsfw="snapshot?.settings.includeNsfw ?? true" @open="showGame" @play="launch" @filter="applyOverviewFilter" />
          <div v-if="libraryQuery || libraryView !== 'all' || statusFilter !== 'all' || favoritesOnly || categoryFilter || tagFilter || minRating !== ''" class="library-result-bar"><span>{{ filteredGames.length }} 部作品<span v-if="libraryView === 'unmatched' || libraryView === 'unconfigured'"> · {{ libraryView === 'unmatched' ? '缺资料' : '待设启动程序' }}</span></span><button v-if="libraryView === 'unmatched' || libraryView === 'unconfigured'" class="text-button" @click="resetFilters">查看全部书架</button></div>
          <section v-if="selectionMode" class="selection-toolbar" aria-label="批量整理"><div><strong>已选择 {{ selectedIds.length }} 部</strong><small>只整理当前结果中的勾选项</small></div><button :disabled="busy.has('bulk')" @click="selectVisible">全选当前结果{{ filteredGames.length > 1000 ? '（前1000部）' : '' }}</button><button :disabled="matchingUnavailable || !selectedLocalIds.length" @click="identifySelected">识别所选</button><button @click="leaveSelection">取消整理</button></section>
          <section v-if="selectionMode" class="batch-form" aria-label="整理选中的游戏">
            <label>游玩状态<select v-model="batchStatus" aria-label="批量游玩状态" :disabled="busy.has('bulk')"><option value="">保持原状态</option><option v-for="status in statuses" :key="status.value" :value="status.value">{{ status.label }}</option></select></label>
            <label>收藏<select v-model="batchFavorite" aria-label="批量收藏" :disabled="busy.has('bulk')"><option value="">保持原收藏</option><option value="yes">标记为喜欢</option><option value="no">取消喜欢</option></select></label>
            <label class="batch-label-field">分类<div><select v-model="batchCategoryMode" aria-label="分类操作" :disabled="busy.has('bulk')"><option value="add">添加</option><option value="remove">移除</option></select><input v-model="batchCategory" aria-label="批量分类" placeholder="分类名，逗号分隔" :disabled="busy.has('bulk')" /></div></label>
            <label class="batch-label-field">个人标签<div><select v-model="batchTagMode" aria-label="标签操作" :disabled="busy.has('bulk')"><option value="add">添加</option><option value="remove">移除</option></select><input v-model="batchTag" aria-label="批量个人标签" placeholder="标签，逗号分隔" :disabled="busy.has('bulk')" /></div></label>
            <KunButton color="primary" :loading="busy.has('bulk')" :disabled="!selectedIds.length" @click="organizeSelected">应用到 {{ selectedIds.length }} 部</KunButton>
          </section>
          <section v-if="snapshot?.activeSession" class="library-session" role="status"><Play :size="18" /><div><strong>{{ activeGame?.name || '正在游玩' }}</strong><span>计时中 · {{ hours(activeSeconds) }}</span></div><KunButton size="sm" variant="bordered" :loading="busy.has('finish')" @click="finishSession">结束计时</KunButton></section>
          <div v-if="filteredGames.length" :class="['game-grid', 'library-grid', { 'game-list': libraryLayout === 'list' }]"><GameCard v-for="game in filteredGames" :key="game.id" :game="game" :include-nsfw="snapshot?.settings.includeNsfw ?? true" :layout="libraryLayout" :selection-mode="selectionMode" :selected="selectedIds.includes(game.id)" :launch-disabled="!!snapshot?.activeSession || busy.has('launch')" @open="showGame(game)" @favorite="favorite(game)" @play="launch(game)" @configure="configureGame(game)" @toggle-selected="toggleSelection(game.id)" /></div>
          <section v-else class="empty-state"><span class="empty-brand"><OrderMark /></span><h2>{{ games.length ? '这里暂时还没有游戏' : '让第一段故事，住进书架' }}</h2><p>{{ games.length ? '换一个关键词，或调整筛选条件试试看。' : '导入电脑里的游戏，或从鲲的数据源发现下一部想玩的作品。' }}</p><div class="button-row"><KunButton v-if="games.length" variant="flat" @click="resetFilters">清空筛选</KunButton><template v-else><KunButton color="primary" :loading="busy.has('scan')" @click="scan"><FolderPlus :size="17" /> 导入本地游戏</KunButton><KunButton variant="bordered" @click="navigate('discover')"><Compass :size="17" /> 发现作品</KunButton></template></div><p v-if="!games.length && !snapshot?.settings.hasApiKey" class="empty-tip"><button @click="navigate('settings')">先配置 NextMoe API 密钥 <ArrowUpRight :size="12" /></button></p></section>
          <footer class="content-footer"><span>Ctrl + K 搜索书架 · 排序和视图自动记住</span><span>KunUI × NextMoe</span></footer>
        </template>

        <template v-else-if="page === 'discover'">
          <section class="page-heading"><div><div class="eyebrow">FIND YOUR NEXT STORY</div><h1>发现作品</h1><p>从鲲的多源资料中，找到下一次心动。</p></div><span class="connection-pill" :class="{ ready: snapshot?.settings.hasApiKey }"><span></span>{{ isPreview ? '预览资料' : snapshot?.settings.hasApiKey ? 'NextMoe 已配置' : '等待配置密钥' }}</span></section>
          <form class="discovery-search" @submit.prevent="search()"><Search :size="23" /><input v-model="searchQuery" placeholder="输入中文名、日文名或游戏别名…" aria-label="搜索作品名称" /><KunButton type="submit" color="primary" :loading="busy.has('search')" :disabled="!searchQuery.trim()">搜索作品 <ArrowUpRight :size="16" /></KunButton></form>
          <div class="discovery-note"><span>游戏资料来自 NextMoe · 鲲 Galgame</span><span>{{ settingsForm.includeNsfw ? '包含成人分级作品' : '全年龄内容优先' }}</span></div>
          <div v-if="searchError" class="inline-error"><CircleHelp :size="18" /><span>{{ searchError }}</span><button @click="page = 'settings'">检查设置</button></div>
          <div v-if="busy.has('search') && !searchResults.length" class="loading-grid"><div v-for="i in 6" :key="i" class="skeleton-card"></div></div>
          <div v-else-if="searchResults.length" class="game-grid"><GameCard v-for="work in searchResults" :key="work.id" :work="work" :include-nsfw="snapshot?.settings.includeNsfw ?? true" @open="showWork(work)" /></div>
          <div v-else-if="hasSearched && !searchError" class="empty-state"><Search :size="36" /><h2>{{ searchCursor ? '此页暂时没有可见作品' : '没有找到匹配的作品' }}</h2><p>{{ searchCursor ? '仍有下一页，可以继续加载更多作品。' : '试试完整原名或其他别名；分级设置也可能影响结果。' }}</p></div>
          <div v-else-if="!searchError" class="discover-welcome"><div class="orbit-mark"><OrderMark /></div><h2>很多故事，只差一次相遇</h2><p>搜索名称即可查看封面、角色、制作公司和多源评分。<br />喜欢的作品可以先加入愿望单。</p><div class="discovery-features"><span><BookOpen :size="16" /> 作品与角色资料</span><span><Star :size="16" /> 多源评分</span><span><Heart :size="16" /> 你的愿望清单</span></div><KunButton v-if="!snapshot?.settings.hasApiKey && !isPreview" variant="flat" color="primary" @click="page = 'settings'">配置 API 密钥 <ChevronRight :size="15" /></KunButton></div>
          <div v-if="searchCursor" class="load-more"><KunButton variant="bordered" :loading="busy.has('search')" @click="search(true)">加载更多作品</KunButton></div>
        </template>

        <template v-else-if="page === 'categories'">
          <section class="page-heading"><div><div class="eyebrow">KEEP WHAT YOU LOVE</div><h1>分类收藏</h1><p>用自己的方式，整理值得珍藏的故事。</p></div><KunButton v-if="games.length" variant="bordered" @click="applyOverviewFilter('all'); enterSelection()"><Tags :size="16" /> 批量整理书架</KunButton></section>
          <div class="collection-grid"><button class="collection-card favorites" @click="page = 'library'; resetFilters(); favoritesOnly = true"><Heart :size="27" /><h2>特别喜欢</h2><p>那些让你念念不忘的故事</p><span>{{ games.filter(g => g.favorite).length }} 部作品 <ArrowUpRight :size="17" /></span></button><button v-for="category in allCategories" :key="category" class="collection-card" @click="page = 'library'; resetFilters(); categoryFilter = category"><LayoutGrid :size="27" /><h2>{{ category }}</h2><p>{{ games.filter(g => g.categories.includes(category)).slice(0, 2).map(g => g.name).join(' · ') }}</p><span>{{ games.filter(g => g.categories.includes(category)).length }} 部作品 <ArrowUpRight :size="17" /></span></button></div><div class="soft-info"><Tags :size="18" /><span>打开任意游戏的「管理」页，可以添加多个分类和个人标签。</span></div><section v-if="allTags.length" class="tag-section"><h2>按标签逛逛</h2><div class="tag-cloud"><button v-for="tag in allTags.slice(0, 80)" :key="tag" @click="page = 'library'; resetFilters(); tagFilter = tag">{{ tag }}</button></div></section>
        </template>

        <template v-else-if="page === 'stats'">
          <section class="page-heading"><div><div class="eyebrow">YOUR TIME, YOUR STORIES</div><h1>游玩统计</h1><p>看见最近的游玩节奏，也记住一路读过的故事。</p></div></section>
          <PlayStatistics :games="games" :include-nsfw="snapshot?.settings.includeNsfw ?? true" :active-session="snapshot?.activeSession" @open="showGame" />
        </template>

        <template v-else-if="page === 'settings'">
          <section class="page-heading"><div><div class="eyebrow">MAKE YOURSELF AT HOME</div><h1>偏好设置</h1><p>连上喜欢的数据源，保留自己的使用习惯。</p></div></section>
          <div class="settings-layout"><div><section class="panel settings-panel"><div class="panel-heading"><span class="panel-icon"><Link2 :size="20" /></span><div><h2>NextMoe · 鲲数据源</h2><p>游戏资料与多源评分</p></div><span class="connection-pill" :class="{ ready: snapshot?.settings.hasApiKey }"><span></span>{{ snapshot?.settings.hasApiKey ? '已保存密钥' : '未配置' }}</span></div><label class="field-label">API Base URL<input v-model="settingsForm.baseUrl" spellcheck="false" placeholder="https://api.nextmoe.dev/v2/" /></label><label class="field-label">API 密钥<input v-model="settingsForm.apiKey" type="password" autocomplete="new-password" spellcheck="false" :placeholder="snapshot?.settings.hasApiKey ? '已安全保存；留空保留现有密钥' : '填写你的 nmk_live_… 密钥'" /></label><p class="field-help">密钥加密保存在本机，保存后不再显示。目录读取需要 catalog:read 权限。</p><div class="button-row"><KunButton color="primary" :loading="busy.has('settings')" @click="saveSettings()">保存设置</KunButton><KunButton variant="bordered" :loading="busy.has('connection')" @click="testConnection">测试已保存的连接</KunButton><button v-if="snapshot?.settings.hasApiKey" class="text-button danger" @click="saveSettings(true)">移除密钥</button></div><div class="settings-divider"></div><div class="setting-line"><div><strong>显示成人分级作品</strong><p>同时应用于搜索与资料获取；保存设置后生效。</p></div><KunSwitch v-model="settingsForm.includeNsfw" label="" aria-label="显示成人分级作品" /></div></section>
          <section class="panel settings-panel"><div class="panel-heading"><span class="panel-icon"><Monitor :size="20" /></span><div><h2>界面与数据</h2><p>你的收藏，由你掌握</p></div></div><label class="field-label">外观主题<select v-model="settingsForm.theme"><option value="light">明亮</option><option value="dark">深色</option><option value="system">跟随系统</option></select></label><div class="button-row"><KunButton variant="flat" color="primary" @click="saveSettings()">应用外观</KunButton></div><div class="settings-divider"></div><div class="setting-line"><div><strong>读取程序内的游戏名称</strong><p>新用户默认开启，保留已保存的关闭选择。仅读取程序中有大小和范围限制的版本资源，提取名称等线索，不会运行程序。读取仍可能触发 Windows 安全扫描及隔离；仅对可信游戏启用。更改后点击“保存设置”。</p></div><KunSwitch v-model="settingsForm.readExecutableMetadata" label="" aria-label="读取程序内的游戏名称" /></div><div class="settings-divider"></div><div class="setting-line"><div><strong>书架备份</strong><p>导出你的收藏、分类、笔记与游玩记录。</p></div></div><div class="button-row"><KunButton variant="bordered" @click="exportBackup"><Upload :size="16" /> 导出书架</KunButton><KunButton variant="bordered" :loading="busy.has('backup-import')" @click="importBackup"><Download :size="16" /> 导入备份</KunButton><KunButton variant="light" @click="openDataFolder"><FolderOpen :size="16" /> 数据文件夹</KunButton></div><p class="data-path">{{ snapshot?.dataPath }}</p></section></div>
          <aside class="settings-aside"><OrderMark /><h3>欢迎使用 The Order</h3><p>一个使用 KunUI 构建的小小书架，和 NextMoe 一起收好你的 Galgame。</p><button @click="external('https://developer.nextmoe.dev/')">获取 API 密钥 <ArrowUpRight :size="15" /></button><button @click="external('https://developer.nextmoe.dev/docs/native-app')">NextMoe 接口文档 <ArrowUpRight :size="15" /></button><button @click="external('https://ui.kungal.com/')">了解 KunUI <ArrowUpRight :size="15" /></button><div class="aside-note"><ShieldCheck :size="18" /><p>游戏文件留在原来的位置。书架只保存你选择的路径与游戏资料。</p></div></aside></div>
        </template>
      </main>
      <div v-if="snapshot?.activeSession && page !== 'library'" class="session-player"><span class="playing-pulse"></span><div><strong>{{ activeGame?.name ?? '游戏运行中' }}</strong><small>正在计时 · {{ hours(activeSeconds) }} · 关闭窗口后在托盘继续</small></div><KunButton variant="flat" color="primary" size="sm" @click="finishSession"><Square :size="13" /> 结束计时（游戏继续）</KunButton></div>
    </div>
  </div>

  <KunModal v-model="detailOpen" :title="detailTitle" size="xl" inner-class-name="game-detail-modal" scroll-behavior="inside">
    <div class="detail-layout"><aside class="detail-aside"><DetailCover :url="detailCoverUrl" :title="detailTitle" :display="selectedGame?.nonPortraitCoverDisplay ?? 'blur'" /><button v-if="selectedWork" type="button" class="detail-cover-settings" :aria-expanded="coverPickerOpen" aria-controls="detail-cover-picker" @click="toggleCoverPicker">封面与展示</button><KunButton v-if="selectedGame && getLaunchInstallation(selectedGame)?.executablePath" color="primary" full-width :loading="busy.has('launch')" :disabled="!!snapshot?.activeSession" @click="launch(selectedGame)"><Play :size="17" fill="currentColor" /> 开始游戏</KunButton><KunButton v-else-if="selectedGame" color="primary" full-width :loading="busy.has('executable')" @click="pickExecutable()"><FolderPlus :size="17" /> {{ selectedGame.installations.length ? '设置启动程序' : '添加本地游戏' }}</KunButton><KunButton v-else color="primary" full-width :loading="busy.has('add-game')" @click="addToLibrary"><Plus :size="17" /> 加入愿望单</KunButton><button v-if="selectedGame" :class="['detail-favorite', { active: selectedGame.favorite }]" @click="favorite(selectedGame)"><Heart :size="16" :fill="selectedGame.favorite ? 'currentColor' : 'none'" />{{ selectedGame.favorite ? '特别喜欢' : '标记为特别喜欢' }}</button><div v-if="selectedGame" class="detail-time"><Clock3 :size="15" /><span>{{ hours(selectedGame.totalPlaySeconds) }}</span><small>游玩 {{ selectedGame.playCount }} 次</small></div><div class="detail-facts"><span>发行日期</span><strong>{{ releaseDate(selectedWork) }}</strong><span>制作公司</span><strong>{{ selectedWork?.developers.join(' / ') || '暂未收录' }}</strong></div></aside>
      <div class="detail-main"><p class="original-title">{{ selectedWork?.originalTitle }}</p><div class="detail-tabs"><button :class="{ active: detailTab === 'overview' }" @click="detailTab = 'overview'">作品资料</button><button :class="{ active: detailTab === 'characters' }" @click="detailTab = 'characters'">角色 <small>{{ selectedWork?.characters.length ?? 0 }}</small></button><button v-if="selectedGame" :class="{ active: detailTab === 'manage' }" @click="detailTab = 'manage'">管理</button><LoaderCircle v-if="busy.has('detail')" :size="17" class="spin" /></div>
      <template v-if="detailTab === 'overview'"><CoverPicker v-if="coverPickerOpen" id="detail-cover-picker" :work="coverOptionsWork?.id === selectedWork?.id ? coverOptionsWork : selectedWork" :game="selectedGame" :loading="busy.has('cover-options')" :saving="busy.has('cover-save')" :error="coverError" :include-nsfw="snapshot?.settings.includeNsfw ?? true" @save="saveCover" @retry="loadCoverOptions" @close="closeCoverPicker" /><div v-if="selectedWork?.ratings.length" class="ratings-row"><div v-for="rating in selectedWork.ratings" :key="rating.source"><small>{{ sourceName(rating.source) }}</small><strong><Star :size="14" />{{ rating.score.toFixed(1) }}<em>/ {{ rating.scale }}</em></strong><span>{{ rating.votes != null ? `${rating.votes.toLocaleString()} 人评价` : '来源评分' }}</span></div></div><h3 class="detail-section-title">关于这段故事</h3><p class="description-text">{{ selectedWork?.description || '还没有关联简介。你可以在管理页搜索并关联准确的作品资料。' }}</p><div v-if="selectedWork?.tags.length" class="tag-cloud detail-tags"><button v-for="tag in selectedWork.tags" :key="tag" @click="detailOpen = false; page = 'library'; resetFilters(); tagFilter = tag">{{ tag }}</button></div><div v-if="selectedWork?.references.length || selectedWork?.webUrl" class="source-links"><h3 class="detail-section-title">资料出处</h3><button v-if="selectedWork.webUrl" @click="external(selectedWork.webUrl)">鲲 Galgame <ArrowUpRight :size="13" /></button><button v-for="reference in selectedWork?.references.filter(r => r.url)" :key="`${reference.source}-${reference.id}`" @click="external(reference.url)">{{ sourceName(reference.source) }} <ArrowUpRight :size="13" /></button></div></template>
      <template v-else-if="detailTab === 'characters'"><div v-if="selectedWork?.characters.length" class="character-grid"><article v-for="character in selectedWork.characters" :key="character.id"><img v-if="character.imageUrl" :src="character.imageUrl" :alt="character.name" loading="lazy" referrerpolicy="no-referrer" /><div v-else class="character-placeholder"><BookOpen :size="22" /></div><strong>{{ character.name }}</strong><small>{{ character.voiceActors.length ? `CV · ${character.voiceActors.join(' / ')}` : character.originalName }}</small></article></div><div v-else class="small-empty"><BookOpen :size="28" /><p>该作品暂未收录角色资料。</p></div></template>
      <template v-else-if="selectedGame"><div class="manage-form"><label class="field-label">书架名称<input v-model="editor.name" @input="markNameEdited" /></label><div class="form-columns"><label class="field-label">游玩状态<select v-model="editor.status"><option v-for="status in statuses" :key="status.value" :value="status.value">{{ status.label }}</option></select></label><label class="field-label">我的评分 <small>0–10，可留空</small><input v-model="editor.personalRating" type="number" min="0" max="10" step="0.1" placeholder="还未评分" /></label></div><label class="field-label">分类<input v-model="editor.categories" placeholder="例如：视觉小说、待补票（逗号分隔）" /></label><label class="field-label">个人标签<input v-model="editor.tags" placeholder="例如：周末想玩、收藏版（逗号分隔）" /></label><label class="field-label">游玩笔记<textarea v-model="editor.notes" rows="4" placeholder="记下感想，或下一次想接着看的地方…"></textarea></label><KunButton color="primary" :loading="busy.has('save-game')" @click="saveGame"><Check :size="16" /> 保存书架信息</KunButton><div class="settings-divider"></div><h3>本地安装与资料</h3><div v-for="installation in selectedGame.installations" :key="installation.id" class="installation-row"><HardDrive :size="16" /><div><strong>{{ installation.executablePath ?? '尚未选择启动文件' }}</strong><small>{{ installation.folderPath }}</small></div><button class="text-button" :disabled="busy.has('executable')" @click="pickExecutable(installation.id)">设置程序</button><button v-if="selectedGame.installations.length > 1" class="text-button" @click="setPreferred(installation.id)">{{ selectedGame.preferredInstallationId === installation.id ? '默认启动' : '设为默认' }}</button></div><div class="button-row"><KunButton variant="bordered" size="sm" @click="pickExecutable()"><FolderPlus :size="14" /> {{ selectedGame.installations.length ? '设置启动程序' : '添加本地游戏' }}</KunButton><KunButton v-if="selectedGame.installations.length" variant="bordered" size="sm" @click="openGameFolder"><FolderOpen :size="14" /> 打开目录</KunButton><KunButton variant="bordered" size="sm" @click="beginBind"><Link2 :size="14" /> 关联作品</KunButton><KunButton v-if="selectedGame.installations.length" variant="bordered" size="sm" :disabled="matchingUnavailable" @click="identifyCurrentGame"><Sparkles :size="14" /> 重新识别此游戏</KunButton><KunButton v-if="selectedGame.metadata" variant="light" size="sm" :loading="busy.has('refresh')" @click="refreshMetadata"><RefreshCw :size="14" /> 刷新资料</KunButton></div><details v-if="selectedGame.installations.length" class="advanced-options"><summary>高级启动参数</summary><label class="field-label">参数数组<input v-model="editor.launchArgs" @input="markArgumentsEdited" placeholder='["-windowed"]' /></label><KunButton variant="flat" size="sm" @click="saveArguments">保存参数</KunButton></details><button class="remove-button" @click="removeGame">从书架移除这部游戏</button></div></template>
      </div></div>
  </KunModal>

  <KunModal v-model="bindOpen" title="选择要关联的作品" size="lg"><p class="modal-help">请核对名称与封面后选择，避免关联到同系列的其他作品。</p><form class="modal-search" @submit.prevent="searchBinding()"><input v-model="bindQuery" aria-label="关联作品搜索" placeholder="输入游戏名" /><KunButton type="submit" color="primary" :loading="busy.has('bind-search')">搜索</KunButton></form><div class="bind-results" :aria-busy="busy.has('bind-search')"><button class="binding-result" v-for="work in bindResults" :key="work.id" :disabled="busy.has('bind')" @click="bindWork(work)"><img v-if="workCoverUrl(work, snapshot?.settings.includeNsfw ?? true)" :src="workCoverUrl(work, snapshot?.settings.includeNsfw ?? true)!" :alt="work.title" /><div><strong>{{ work.title }}</strong><small>{{ work.originalTitle }}</small><span>{{ work.developers.join(' · ') }} · {{ releaseDate(work) }}</span></div><Link2 :size="17" /></button><div class="binding-pagination"><p v-if="bindError" class="binding-error" role="alert">{{ bindError }}</p><p v-else-if="bindNotice" role="status">{{ bindNotice }}</p><p v-else-if="busy.has('bind-search')" role="status">正在加载作品…</p><p v-else-if="bindHasSearched && !bindResults.length">没有找到作品，请试试其他名称。</p><p v-else-if="bindHasSearched && !bindCursor" role="status">已显示全部结果</p><KunButton v-if="bindError" size="sm" variant="bordered" :loading="busy.has('bind-search')" @click="searchBinding(bindRetryMore)">重试</KunButton><KunButton v-else-if="bindCursor" size="sm" variant="bordered" :loading="busy.has('bind-search')" :disabled="busy.has('bind-search')" @click="searchBinding(true)">{{ busy.has('bind-search') ? '加载中…' : '加载更多' }}</KunButton></div></div></KunModal>
  <KunModal v-model="scanOpen" title="确认导入本地游戏" size="lg">
    <p class="modal-help">扫描发现 {{ candidates.length }} 个游戏目录。先导入本地记录，再自动补全封面与资料；无法确定的作品集中核对。</p>
    <div class="scanning-summary"><span><strong>{{ newCandidates.length }}</strong> 部可导入</span><span><CheckCircle2 :size="16" /><strong>{{ alreadyInLibrary.size }}</strong> 部已在书架</span></div>
    <div class="import-match-option"><div><strong><Sparkles :size="16" /> 导入后自动补全封面和资料</strong><p v-if="!snapshot?.settings.hasApiKey">未配置密钥也能先导入。保存密钥后，在“资料整理”中点击“只补缺失”。</p><p v-else-if="matchingUnavailable">当前识别任务结束后，可继续补全新导入的作品。</p><p v-else>结合目录、名称与本地线索识别；证据有冲突时保留候选供你确认。</p></div><KunSwitch v-model="autoMatchOnImport" label="" aria-label="导入后自动补全封面和资料" :disabled="!snapshot?.settings.hasApiKey || matchingUnavailable || busy.has('import')" /></div>
    <label class="scan-search"><Search :size="17" /><input v-model="scanQuery" aria-label="筛选扫描结果" placeholder="筛选名称或目录…" /></label>
    <div class="scan-selection"><button class="text-button" :disabled="busy.has('import')" @click="chosen = newCandidates.map(candidate => candidate.id)">全选新游戏</button><button class="text-button" :disabled="busy.has('import')" @click="chosen = []">清空选择</button><span>已选择 {{ selectedScanIds.length }} 项</span></div>
    <div class="scan-list"><label v-for="candidate in visibleCandidates" :key="candidate.id" :class="{ 'scan-known': alreadyInLibrary.has(candidate.id), 'scan-conflict': conflictingCandidates.has(candidate.id) }"><input v-model="chosen" type="checkbox" :value="candidate.id" :disabled="alreadyInLibrary.has(candidate.id) || conflictingCandidates.has(candidate.id) || busy.has('import')" /><FolderOpen :size="22" /><div><strong>{{ candidate.name }} <small v-if="alreadyInLibrary.has(candidate.id)">已在书架</small></strong><small>{{ candidate.folderPath }}</small><small v-if="conflictingCandidates.has(candidate.id)">与「{{ candidateOwner(candidate)?.name }}」的安装目录重叠，请先核对已有条目。</small><span>{{ candidate.executables.length === 1 ? '发现 1 个启动程序' : `发现 ${candidate.executables.length} 个程序，导入后可选择默认启动` }}</span></div></label><p v-if="!visibleCandidates.length" class="modal-help">没有符合筛选的目录。</p></div>
    <div class="modal-footer"><KunButton v-if="scannedLibraryIds.length" variant="bordered" :disabled="matchingUnavailable || busy.has('import')" :title="`重新识别扫描范围内已入库的 ${scannedLibraryIds.length} 部游戏，无需重复导入`" @click="identifyScannedGames">识别已入库游戏</KunButton><KunButton variant="light" :disabled="busy.has('import')" @click="scanOpen = false">取消</KunButton><KunButton color="primary" :disabled="!selectedScanIds.length" :loading="busy.has('import')" @click="importSelected">确认导入 {{ selectedScanIds.length }} 部</KunButton></div>
  </KunModal>
  <KunModal v-model="reviewOpen" title="核对游戏资料" size="xl" inner-class-name="matching-review-modal" scroll-behavior="inside">
    <p class="modal-help">先对照当前作品和本地安装，再核对候选。标题相符并不代表章节或版本相同。</p>
    <div v-if="currentReview" class="review-layout">
      <aside class="review-queue"><span class="review-queue-heading">待核对 · {{ matchReviews.length }}</span><button v-for="review in matchReviews" :key="review.gameId" :class="{ active: currentReview.gameId === review.gameId }" :disabled="busy.has('matching-resolve')" @click="reviewGameId = review.gameId"><BookOpen :size="14" /><span>{{ review.name }}</span><ChevronRight :size="13" /></button></aside>
      <section class="review-content"><div class="review-local-game"><span>本地游戏</span><h3>{{ reviewGame?.name || currentReview.name }}</h3><span>{{ currentReview.source ? '首选识别目录' : '旧建议记录的目录' }}</span><p><FolderOpen :size="13" />{{ currentReview.source?.folderPath || currentReview.folderPath }}</p><small v-if="currentReview.source?.executablePath">程序：{{ currentReview.source.executablePath }}</small><details v-if="(currentReview.source?.installations?.length || 0) > 1"><summary>本次输入 {{ currentReview.source?.installations?.length }} 个安装</summary><p v-for="source in currentReview.source?.installations" :key="source.installationId">{{ source.folderPath }}</p></details></div>
        <div v-if="reviewGame?.metadata" class="review-current-work" aria-label="当前关联"><img v-if="gameCoverUrl(reviewGame, snapshot?.settings.includeNsfw ?? true)" :src="gameCoverUrl(reviewGame, snapshot?.settings.includeNsfw ?? true)!" alt="" referrerpolicy="no-referrer" /><div><small>当前关联 · 保留至你明确确认</small><strong>{{ reviewGame.metadata.title }}</strong><span>{{ releaseDate(reviewGame.metadata) }} · {{ reviewGame.metadata.developers.join(' / ') || '厂商暂未收录' }}</span></div></div>
        <div class="review-reason"><CircleHelp :size="15" /><p>{{ currentReview.sourceStatus === 'stale' ? '安装、名称或关联已改变，这条建议已过时。请手动搜索，或结束本次建议。' : currentReview.reason }}</p></div><p v-if="currentReview.sourceStatus === 'legacy'" class="review-source-note">旧队列未记录完整来源。这里显示当前关联，请核对后再决定；目录信息不能证明候选已验证。</p>
        <div v-if="currentReview.options.length" class="review-options"><div v-for="option in currentReview.options" :key="option.work.id" class="review-option-row"><button :class="['review-option', { selected: reviewChoice === option.work.id }]" :aria-pressed="reviewChoice === option.work.id" :disabled="busy.has('matching-resolve') || currentReview.sourceStatus === 'stale'" @click="reviewChoice = option.work.id"><img v-if="workCoverUrl(option.work, snapshot?.settings.includeNsfw ?? true)" :src="workCoverUrl(option.work, snapshot?.settings.includeNsfw ?? true)!" :alt="option.work.title" loading="lazy" referrerpolicy="no-referrer" /><span v-else class="review-cover-placeholder"><BookOpen :size="24" /></span><span class="review-option-copy"><strong>{{ option.work.title }}</strong><small v-if="option.work.originalTitle !== option.work.title">{{ option.work.originalTitle }}</small><span>{{ releaseDate(option.work) }} · {{ option.work.developers.join(' / ') || '厂商暂未收录' }}</span><em>{{ option.reason }}</em><small>此处是匹配线索，整体关联仍待你核对。</small></span><span class="review-option-check"><Check v-if="reviewChoice === option.work.id" :size="13" /></span></button><button class="text-button review-detail-link" @click="showReviewDetail(option.work)">查看详情</button></div></div>
        <div v-else class="review-no-options"><Search :size="25" /><p>暂时没有合适的候选。</p><span>可以换一个完整名称或原名，重新搜索。</span></div>
        <div class="review-footer"><button class="text-button" :disabled="busy.has('matching-resolve')" @click="searchReviewAgain(currentReview)"><Search :size="14" /> 手动搜索</button><div><KunButton variant="light" size="sm" :disabled="busy.has('matching-resolve')" @click="reviewOpen = false">稍后处理</KunButton><KunButton variant="bordered" size="sm" :disabled="busy.has('matching-resolve')" @click="resolveReview(true)">{{ reviewGame?.metadata ? '保留当前关联' : '本次不关联' }}</KunButton><KunButton color="primary" size="sm" :disabled="!reviewChoice || currentReview.sourceStatus === 'stale'" :loading="busy.has('matching-resolve')" @click="resolveReview()"><Check :size="14" /> 确认关联</KunButton></div></div>
      </section>
    </div>
    <div v-else class="small-empty review-finished"><CheckCircle2 :size="35" /><h3>{{ matchingRunning ? '目前没有待核对的项目' : '待核对项目已处理完毕' }}</h3><p>{{ matchingRunning ? '后台仍在识别其他游戏，有新的候选时会显示在这里。' : '可以返回书架查看补全的封面与资料。' }}</p><KunButton variant="flat" color="primary" @click="reviewOpen = false">返回书架</KunButton></div>
  </KunModal>
  <LibraryMaintenance v-model="maintenanceOpen" :matching="matching" :missing="missingGames" :unconfigured="unconfiguredGames" :unavailable="matchingUnavailable" :has-local="!!localGameIds.length" @start="startMatching(undefined, $event)" @cancel="cancelMatching" @review="openMatchReviews" @open="openMaintenanceGame" @configure="openMaintenanceGame($event, true)" @filter="showMaintenanceFilter" />
  <KunModal v-model="attachmentOpen" title="添加本地游戏" size="lg" scroll-behavior="inside" inner-class-name="attachment-modal"><p class="modal-help">为「{{ games.find(game => game.id === attachmentGameId)?.name }}」附加安装。原作品资料和个人记录会保留。</p><p class="attachment-root"><strong>所选根目录</strong>{{ attachmentScan?.rootPath }}</p><p v-if="attachmentError" class="inline-error" role="alert">{{ attachmentError }}</p><p v-if="!attachmentScan?.candidates.length" class="modal-help">没有发现可用的程序目录。请确认选择的是游戏目录，再重新选择。</p><div v-for="candidate in attachmentScan?.candidates" :key="candidate.id" class="attachment-candidate"><div><strong>{{ candidate.name }}</strong><span>{{ candidate.folderPath }}</span><small v-if="candidateOwner(candidate)">与「{{ candidateOwner(candidate)?.name }}」的安装目录重叠，请先核对已有条目。</small><small v-else>选择这个根目录中的程序；取消选择不会添加安装。</small></div><KunButton size="sm" :disabled="!!candidateOwner(candidate)" :loading="busy.has('attachment-commit')" @click="attachCandidate(candidate)">选择程序并添加</KunButton></div><div class="modal-footer"><KunButton variant="light" @click="attachmentOpen = false">取消</KunButton><KunButton variant="bordered" :disabled="busy.has('attachment-commit')" @click="attachmentOpen = false; scan()">返回普通导入</KunButton></div></KunModal>
  <KunModal v-model="reviewDetailOpen" title="候选作品详情" size="lg" scroll-behavior="inside" inner-class-name="review-detail-modal"><div v-if="reviewDetailWork" class="review-detail-body"><img v-if="workCoverUrl(reviewDetailWork, snapshot?.settings.includeNsfw ?? true)" :src="workCoverUrl(reviewDetailWork, snapshot?.settings.includeNsfw ?? true)!" :alt="reviewDetailWork.title" referrerpolicy="no-referrer" /><section><h3>{{ reviewDetailWork.title }}</h3><p v-if="reviewDetailWork.originalTitle !== reviewDetailWork.title">{{ reviewDetailWork.originalTitle }}</p><p>{{ releaseDate(reviewDetailWork) }} · {{ reviewDetailWork.developers.join(' / ') }}</p><p v-if="busy.has('review-detail')" role="status">正在读取完整资料…</p><p v-if="reviewDetailError" class="inline-error" role="alert">{{ reviewDetailError }}<button @click="showReviewDetail(reviewDetailWork)">重试</button></p><p class="description-text">{{ reviewDetailWork.description || '暂未收录简介。' }}</p><p>{{ reviewDetailWork.tags.join(' · ') }}</p></section></div></KunModal>
  <KunMessageProvider /><KunAlertProvider />
</template>

<style scoped>
.detail-cover-settings{width:100%;margin:-3px 0 12px;padding:9px 10px;border:1px solid var(--line);border-radius:8px;font-size:12px;color:var(--blue);background:var(--surface)}
.detail-cover-settings:hover{background:var(--blue-soft)}
.detail-cover-settings:focus-visible{outline:2px solid var(--blue);outline-offset:2px}
</style>
