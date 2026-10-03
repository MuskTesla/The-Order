<script setup lang="ts">
import { computed, ref } from 'vue'
import { ArrowUpRight, BookOpen, Heart, Search, Tags, X } from 'lucide-vue-next'
import type { LibraryGame, NonPortraitCoverDisplay } from '../shared/types'
import { findCollectionTags, findLibraryCollections, getLibraryCollections, type CollectionSort } from '../library-collections'
import { gameCoverDisplay, gameCoverUrl } from '../cover-display'
import DetailCover from './DetailCover.vue'

const props = defineProps<{ games: LibraryGame[]; tags: string[]; includeNsfw: boolean; globalDisplay: NonPortraitCoverDisplay }>()
defineEmits<{ category: [name: string]; favorites: []; tag: [name: string] }>()
const query = ref(''), tagQuery = ref(''), sort = ref<CollectionSort>('name')
const collections = computed(() => getLibraryCollections(props.games))
const visible = computed(() => findLibraryCollections(collections.value.categories, query.value, sort.value))
const visibleTags = computed(() => findCollectionTags(props.tags, tagQuery.value))
const previewGames = (games: LibraryGame[]) => [...games].sort((a, b) => b.addedAt.localeCompare(a.addedAt) || a.id.localeCompare(b.id)).slice(0, 4)
</script>

<template>
  <section class="collection-browser" aria-label="浏览分类收藏">
    <button class="collection-favorites" @click="$emit('favorites')" :aria-label="`查看特别喜欢，${collections.favorites.games.length}部作品`">
      <span class="collection-heart"><Heart :size="24" /></span>
      <span class="favorites-copy"><strong>特别喜欢</strong><small>{{ collections.favorites.games.length ? '让你念念不忘的故事，在这里重逢。' : '在游戏卡片上点亮爱心，留下想珍藏的故事。' }}</small></span>
      <span class="favorites-count">{{ collections.favorites.games.length }} 部 <ArrowUpRight :size="18" /></span>
    </button>

    <div class="collections-toolbar">
      <label class="collection-search"><Search :size="18" /><input v-model="query" aria-label="搜索分类或所含作品" placeholder="搜索分类，或其中的作品…" /><button v-if="query" aria-label="清空分类搜索" @click="query = ''"><X :size="16" /></button></label>
      <label class="collection-sort">排序<select v-model="sort" aria-label="分类排序"><option value="name">名称</option><option value="count">作品最多</option><option value="recent">最近添加作品</option></select></label>
    </div>
    <p class="collections-result" role="status">{{ query ? `找到 ${visible.length} 个分类` : `${collections.categories.length} 个分类` }}<span> · {{ games.length }} 部作品，同一作品可属于多个分类</span></p>

    <div v-if="visible.length" class="collection-grid">
      <button v-for="collection in visible" :key="collection.name" class="collection-card" :aria-label="`打开分类：${collection.name}，${collection.games.length}部作品`" @click="$emit('category', collection.name)">
        <div class="collection-art" aria-hidden="true">
          <DetailCover v-for="game in previewGames(collection.games)" :key="game.id" :url="gameCoverUrl(game, includeNsfw)" :title="game.name" :display="gameCoverDisplay(game, globalDisplay)" />
          <span v-for="slot in 4 - Math.min(collection.games.length, 4)" :key="`empty-${slot}`" class="collection-art-empty"><BookOpen :size="22" /></span>
        </div>
        <div class="collection-copy"><h2>{{ collection.name }}</h2><p>{{ collection.games.slice(0, 2).map(game => game.name).join(' · ') }}</p><div class="collection-progress"><span>{{ collection.games.length }} 部作品</span><span v-if="collection.playing">在玩 {{ collection.playing }}</span><span v-if="collection.completed">已完成 {{ collection.completed }}</span><ArrowUpRight :size="17" /></div></div>
      </button>
    </div>
    <div v-else class="collections-empty"><Tags :size="30" /><h2>{{ query ? '没有找到这个分类' : '给故事留一个自己的位置' }}</h2><p>{{ query ? '试试分类名、作品名或别名；也可以清空搜索。' : games.length ? '在游戏管理中添加分类，或用上方的批量整理，把你的书架整理成喜欢的样子。' : '先添加游戏，再为作品建立分类，开始整理你的书架。' }}</p><button v-if="query" class="text-button" @click="query = ''">清空搜索</button></div>

    <section v-if="tags.length" class="tag-section" aria-label="按标签浏览"><div class="collection-tag-heading"><h2>按标签逛逛 <small>{{ tags.length }}</small></h2><label class="collection-search"><Search :size="16" /><input v-model="tagQuery" aria-label="搜索标签" placeholder="搜索全部标签…" /><button v-if="tagQuery" aria-label="清空标签搜索" @click="tagQuery = ''"><X :size="15" /></button></label></div><div v-if="visibleTags.length" class="tag-cloud"><button v-for="tag in visibleTags" :key="tag" @click="$emit('tag', tag)">{{ tag }}</button></div><p v-else class="collection-tag-empty" role="status">没有匹配的标签，换个关键词试试。</p></section>
  </section>
</template>

<style scoped>
.collection-browser{min-width:0}.collection-favorites{display:flex;align-items:center;gap:16px;width:100%;min-width:0;padding:20px 22px;background:var(--surface);border:1px solid var(--line);border-radius:14px;text-align:left}.collection-heart{flex:none;display:grid;place-items:center;width:48px;height:48px;border-radius:14px;background:#c15d8510;color:#b6567d}.favorites-copy{min-width:0;flex:1;display:flex;flex-direction:column;gap:5px}.favorites-copy strong{font-size:17px;color:var(--ink)}.favorites-copy small{font-size:13px;line-height:1.6;color:var(--muted);overflow-wrap:anywhere}.favorites-count{display:flex;align-items:center;gap:8px;flex:none;font-size:14px;color:var(--muted)}
.collections-toolbar{display:flex;align-items:center;gap:16px;flex-wrap:wrap;margin:24px 0 12px}.collection-search{display:flex;align-items:center;gap:9px;flex:1;min-width:0;border:1px solid var(--line);border-radius:9px;background:var(--surface);padding:10px 12px;color:var(--muted)}.collection-search input{min-width:0;flex:1;width:100%;font-size:14px;color:var(--ink);background:transparent;outline:none}.collection-search button{flex:none;display:grid;place-items:center}.collection-sort{display:flex;align-items:center;gap:10px;font-size:13px;color:var(--muted)}.collection-sort select{padding:10px 12px;border:1px solid var(--line);border-radius:9px;background:var(--surface);color:var(--ink);font-size:13px;max-width:100%}.collections-result{font-size:13px;line-height:1.6;color:var(--ink);margin:0 0 14px;overflow-wrap:anywhere}.collections-result span{color:var(--muted)}
.collection-browser .collection-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(min(100%,270px),1fr));gap:18px}.collection-browser .collection-card{display:flex;flex-direction:column;justify-content:flex-start;min-width:0;width:100%;padding:0;overflow:hidden;background:var(--surface);border:1px solid var(--line);border-radius:14px;text-align:left;transition:border-color .15s,box-shadow .15s}.collection-card:hover,.collection-favorites:hover{border-color:var(--blue);box-shadow:0 4px 18px #354b7210}.collection-art{flex:none;width:100%;display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:3px;height:108px;background:var(--hover);overflow:hidden;pointer-events:none}.collection-art :deep(.detail-artwork){grid-row:auto;width:100%;height:100%;min-width:0;margin:0;border:0;border-radius:0}.collection-art :deep(.detail-art-placeholder){gap:4px}.collection-art :deep(.detail-art-placeholder small){display:none}.collection-art :deep(.detail-art-placeholder>svg){width:20px;height:20px}.collection-art-empty{display:grid;place-items:center;color:var(--muted);opacity:.4}.collection-copy{display:flex;flex:1;flex-direction:column;width:100%;padding:18px}.collection-copy h2{margin:0;font-size:17px;line-height:1.6;color:var(--ink);overflow-wrap:anywhere}.collection-browser .collection-copy p{margin:6px 0 16px;font-size:13px;line-height:1.6;color:var(--muted);white-space:normal;overflow:hidden;display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;overflow-wrap:anywhere}.collection-progress{margin-top:auto;display:flex;align-items:center;gap:8px;flex-wrap:wrap;font-size:12px;color:var(--muted)}.collection-progress>span:first-child{margin-right:auto;color:var(--ink)}.collection-progress>svg{flex:none;color:var(--blue)}
.collections-empty{display:flex;flex-direction:column;align-items:center;gap:10px;padding:42px 20px;background:var(--surface);border:1px dashed var(--line);border-radius:14px;text-align:center;color:var(--muted)}.collections-empty h2{font-size:18px;color:var(--ink)}.collections-empty p{font-size:14px;line-height:1.8;max-width:440px}.collection-tag-heading{display:flex;align-items:center;justify-content:space-between;gap:16px;flex-wrap:wrap;margin-bottom:16px}.collection-tag-heading h2{font-size:17px;color:var(--ink)}.collection-tag-heading small{font-size:12px;font-weight:400;color:var(--muted);margin-left:5px}.collection-tag-heading .collection-search{max-width:280px;min-width:min(100%,180px)}.collection-browser .tag-cloud button{max-width:100%;font-size:13px;white-space:normal;overflow-wrap:anywhere;text-align:left}.collection-tag-empty{font-size:13px;color:var(--muted)}
.collection-browser button:focus-visible,.collection-sort select:focus-visible{outline:2px solid var(--blue);outline-offset:3px}.collection-search:focus-within{outline:2px solid var(--blue);outline-offset:2px}
@media(max-width:700px){.collection-favorites{padding:16px;gap:12px;flex-wrap:wrap}.favorites-count{margin-left:auto}.collection-search{flex-basis:100%}.collection-sort{margin-left:auto}.collections-result span{display:block}.collection-copy{padding:16px}.collection-tag-heading .collection-search{max-width:none}.collection-art{height:100px}}
</style>
