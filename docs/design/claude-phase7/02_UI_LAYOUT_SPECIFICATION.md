# 02 · UI Layout Specification — revision 2

All sizes are in DIP. Tags:

- **[REQ]** design requirement;
- **[FREE]** implementation freedom;
- **[DEP]** technical dependency;
- **[FALLBACK]** acceptable fallback;
- **[REVIEW]** design review required before deviating.

Mockups: `previews/direction-r2.html?view=grid|list|details|flow|accent|modes|settings&theme=light|dark&accent=blue|purple|orange`.
Accent values in mockups are examples.

## 1. Window and navigation

Use the Fluent NavigationView defaults, not custom breakpoints:

| Window width | NavigationView | Title-bar search |
|---|---|---|
| ≥ 1008 | Left, expanded (OpenPaneLength 240) | AutoSuggestBox centred, width clamp(200, W−640, 420) |
| 641–1007 | LeftCompact (48, icons; tooltips carry names) | same |
| < 641 | LeftMinimal (hamburger in the title bar) | search icon that expands the box over the title |

- **[REQ] Title bar.**
  - `ExtendsContentIntoTitleBar`, height 48, Mica (`MicaBackdrop`).
  - Contents: back button when navigable · app icon (approved Brand.png) · "The Order" (Caption 12) · search · session
    chip · system caption buttons.
  - Drag regions exclude interactive elements [DEP: `InputNonClientPointerSource`].
  - [FALLBACK] System caption with search moved into the Library header. Record this as a deviation.
- **[REQ] Navigation items:** 书架, 发现作品, 识别与核对 (InfoBadge with the pending-review count when available).
  Footer: 设置, 退出 The Order (an action item that calls the existing exit; the tray hint text stays valid).
  The selection indicator is the system accent pill (NavigationView default).
- **[REQ] Content surface:** a layer on Mica (`LayerFillColorDefaultBrush`, 8-DIP top-left radius, 1-DIP
  `CardStrokeColorDefaultBrush`), the standard Windows 11 "content on Mica" composition.
- **[REQ] Session chip.**
  - Shown only while `SessionPresentation` is non-empty.
  - Contents: 24 avatar + title + status dot + mono timer; it collapses to dot + timer below 1100 width.
  - It opens a Flyout with the existing state text, 暂停计时 / 恢复计时 (same enablement) and 打开详情.
  - Lifecycle warnings appear inside it, and as a persistent inline InfoBar.
  - The permanent bottom session bar is removed.
- **[REQ] Notices.**
  - Informational notices: a floating InfoBar, auto-hidden after 6 s (paused while hovered or focused).
  - Warning and Error notices, and the read-only state: inline and persistent.
- Test diagnostics: available only in Test + Diagnostics, collapsed behind Ctrl+Shift+D.

## 2. Library — header

- Row: **书架** (Title 28/36 SemiBold) + caption `384 部作品` (`12 / 384` when filtered).
  - Right side: 排序 DropDownButton (shows the current sort), 筛选 (badge = active count), view toggle
    [网格 | 列表] (two ToggleButtons or `SelectorBar`; no new dependency), 选择 (ToggleButton), 导入 (standard
    Button).
  - Below 900 width, labels collapse to icons and 选择 / 导入 / 重新读取书架 move to ⋯.
  - Padding 20 24 12 32.
- **Active filter row:** chips (one per condition, × to remove) + 清除全部 (HyperlinkButton). It replaces
  `ActiveFilterRegion` and is only present when filters are active.
- **Filter flyout:** Acrylic Flyout. Same fields and values as today (作品范围, 游玩状态, 分类, 个人标签, 个人评分,
  只看收藏, 常用筛选). Conditions apply immediately; footer 保存筛选 · 清除条件 · 完成.

## 3. Library — interaction model

| | Normal browsing (default) | Selection mode |
|---|---|---|
| Enter / exit | — | 选择 button, Ctrl+click, Shift+click or Ctrl+A enter. 完成, Esc, or clearing the last selection exits. |
| `GridView.SelectionMode` | `None`, `IsItemClickEnabled=True` | `Multiple` (checkboxes visible, top-right of each card) |
| Click / tap | **Opens Details** (`ItemClick` → existing open command) | Toggles that item |
| Enter / Space | Opens Details | Space toggles; Enter does nothing |
| Shift+click | Enters selection mode, selects the item | Range select |
| Ctrl+click | Enters selection mode, selects the item | Toggles |
| Right-click / Shift+F10 / long press | Menu: 打开详情 · 启动游戏 · 选择 | Menu: 识别所选 · 批量编辑 · 完成选择 |
| Header | Normal header | Selection bar: "已选择 N 项" · 全选 · 识别所选 · 批量编辑 · hint · 完成 |

- [REQ] Batch actions operate only on the visible selected items, as today, and call the existing recognize and
  batch commands.
- [REQ] Context-menu **启动游戏** uses the existing launch flow for the item's preferred installation. If the launch
  state is not `Ready` (confirmation, missing installation, stale), it opens Details with that state visible instead
  of launching. **Ctrl+Enter** on a focused card does the same.
- [REVIEW] No hover "▶" overlay on cards; launch stays deliberate.

## 4. Library — grid

- **Card:** `CardBackgroundFillColorDefaultBrush`, 1-DIP `CardStrokeColorDefaultBrush`, radius 8, padding 6/6/10.
  - Cover 3:4, radius 4.
  - Title: BodyStrong 14/20, 2 lines, ellipsis, tooltip with the full name, min height 40.
  - Meta: Caption 12/16, status dot + label, rating, favourite heart (accent text colour, accessible name 收藏).
  - Wishlist: a 愿望单 pill on the cover.
- **Column count:** n = floor((W + 12) / (156 + 12)), with min 128 below 641 width. Items stretch to fill the row,
  so there is no ragged gutter [DEP: set `ItemsWrapGrid.ItemWidth` on size change; keep virtualization,
  `ContainerContentChanging` and the CoverPresenter contracts].
- **States:**
  - hover — `CardBackgroundFillColorSecondary` + elevation shadow (ThemeShadow, Translation Z = 8);
  - pressed — tertiary fill, scale 0.98 (no scale with reduced motion);
  - focus — Fluent focus visual (2-DIP outer black/white + 1-DIP inner), never accent-coloured;
  - selected (selection mode) — 2-DIP accent border + checked checkbox.
- **Empty, no results, loading and failure** (existing `EmptyText` / `IsLoading` semantics, Fluent presentation): empty library →
  导入目录 button; no results → 清除筛选; first load shows a ProgressRing after 400 ms; failure → persistent Error InfoBar + 重新读取书架.

## 5. Library — list

- Fluent table:
  - header row (36, sticky);
  - rows 56 with `SubtleFillColorSecondary` on hover;
  - 1-DIP divider (`DividerStrokeColorDefault`).
- Columns:

  | Column | Width | Hidden below list width |
  |---|---|---|
  | checkbox | 24, selection mode only | — |
  | cover | 30×40 | — |
  | 名称 | `*`, name + subtitle | — |
  | 状态 | 92 | — |
  | 评分 | 56 | 560 |
  | 游玩时长 | 88, mono | 560 |
  | 最近游玩 | 96 [DEP: LastPlayed projection] | 760 |
  | 安装 | 60 | 760 |

- Clicking a header applies the existing sort key (direction unchanged).
- Click, selection and keyboard behaviour are identical to the grid (§3).

## 6. Returning from Details

- [REQ] Back sources: title-bar back button, BreadcrumbBar "书架", Alt+←, mouse back button, Backspace when focus
  is not in text input.
- [REQ] Restore:
  - **query state:** search text, filters, sort and view. These already live in `WorkspaceViewModel` and must not be
    reset by navigation.
  - **scroll position:** before navigating, capture the relative scroll position with the item id as key
    [DEP: `ListViewPersistenceHelper.GetRelativeScrollPosition` / `SetRelativeScrollPositionAsync`].
    [FALLBACK] `ScrollIntoView(item, Leading)` plus the stored offset.
  - **focus:** focus the card of the game just opened, using `FocusState.Keyboard` if Details was opened by keyboard
    and `Programmatic` otherwise.
  - **selection mode:** it is not active while navigating, because selection mode does not navigate.
- Restoration happens after the existing inactive-`ItemsSource=null` contract re-binds, with no extra cover requests
  beyond the visible range.
- [FREE] ConnectedAnimation from cover to poster (disabled with reduced motion).

## 7. Details

- **Breadcrumb row** (BreadcrumbBar): `书架 › <title>`.
- **Hero band**, full content width, height 300 (412 when content width < 720):
  - Background: ambient artwork from the game's cover — app-rendered blur (σ ≈ 18–26 DIP) with a 15 % dark scrim —
    fading into the page layer over its last 40 %.
  - Poster 180×240 (120×160 narrow), radius 8, elevation; existing CoverPresenter, MinimumEdge 512.
  - Glass plate (03 §4): x 236, y 40, w min(660, W−268), h 220. In narrow layouts it sits below the poster at full
    width (h 200).
  - Plate contents:
    - title (Title 28/36, 2 lines);
    - meta caption;
    - fact row (status · rating · ♥ · total time);
    - actions: **启动游戏** (AccentButton, 36), installation DropDownButton (folder name; full path in tooltip and
      accessible description), ⋯ (管理本地安装, 选择本地封面, 重新识别, 刷新在线资料, 移除愿望条目).
  - When the existing launch state requires confirmation, the accent button reads 确认启动位置 and calls the existing
    confirm. 取消启动 appears while launching. Disabled states show `Launch.Message` under the action row.
- **Body:** two columns — 个人记录 card (form) | 300 right column (游玩记录, 本地安装, 资料来源 cards). It is a
  single column below 900.
  - Fields and their parsing are unchanged.
  - `DraftState` text sits in the card header (Polite live region).
  - Dirty fields show the standard focused-input accent bottom line.
- **Save bar:** visible only while dirty. It is a bottom-docked Acrylic bar inside the content (16 inset): status dot
  + "有 N 项未保存的修改" · 取消修改 · 保存修改 (AccentButton). The page reserves 80 bottom padding. All leave
  guards are unchanged.
- **No installation:** the accent slot reads 添加或管理本地安装, with the existing reason text.

## 8. Other pages (same system, later slices)

- **Discover, Recognition, Import, Installations:** Fluent page header + cards. Recognition is queue | comparison |
  sticky decision bar; the empty queue gets an explanatory empty state.
- **Settings:**
  - Appearance card: theme ComboBox; a note that the accent follows Windows, with a link to Windows colour settings
    (`ms-settings:colors`, optional); 材质效果 radio group; effective-state line; live hero preview.
  - Then the 在线目录 and 数据 cards.
- **Dialogs:** ContentDialog defaults (radius 8, Fluent smoke). **Flyouts and menus:** Acrylic defaults.

## 9. Keyboard and accessibility

- **Tab order:** title-bar back → search → session chip → navigation → page header → filter chips → grid/list (one
  stop; arrow keys move) → hero actions → body.
- **Keys:** F6 cycles regions; Ctrl+F search; Ctrl+A select all (enters selection mode); Esc leaves selection mode
  or clears search.
- **Cards:** `AutomationProperties.Name` = title; HelpText = status · rating · 收藏 · 愿望单; selection state is
  exposed in selection mode.
- **Scaling:** 100–200 % DPI and 150 % text scale without clipping primary actions. Long CJK titles use 2 lines plus
  a tooltip.
- **Minimum width:** 480, as today. Do not raise it.
