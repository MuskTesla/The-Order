# 02 · UI Layout Specification — Direction B

All sizes are in DIP. Ranges mean "preferred (min–max)".

Tags used below:

- **[REQ]** design requirement;
- **[FREE]** implementation freedom;
- **[DEP]** technical dependency;
- **[FALLBACK]** acceptable fallback;
- **[REVIEW]** design review required before shipping a deviation.

Reference previews (concept mockups): `previews/direction-b.html?view=grid|list|details|settings|filter|modes`, with
`&theme=dark`, `&selecting=1`, `&filters=1` and window widths 480/560/960/1280.

## 1. Window classes

Classes are based on window width, because the rail and inspector decide the layout.

| Class | Window width | Rail | Title-bar search | Inspector | Grid min tile |
|---|---|---|---|---|---|
| Wide | ≥ 1280 | 72, labelled | centred, 440 max | docked 340 | 148 |
| Standard | 1008–1279 | 72 | centred, clamp(220, W−600, 440) | docked 300 | 148 |
| Medium | 720–1007 | 72 | as above; session chip collapses to dot + timer | overlay sheet 320 from the right, opened by pane button or Enter-preview | 140 |
| Narrow | 480–719 | hidden → hamburger in title bar (NavigationView LeftMinimal) | collapses to a search icon that expands over the title bar | none (Details page instead) | 132 |

- **[REQ]** No horizontal scrolling at any width ≥ 480, at 100–200 % scale and at text scale 150 %.
- **[REQ]** Do not raise the minimum window size to solve layout. The existing resize clamp floor of 480 stays.

## 2. Shell

```
┌ Title bar 48 · Mica ───────────────────────────────────────────────────────────┐
│ [brand 20] The Order        [ 搜索书架 … Ctrl+F ]        (● 星海の約束 0:42:10) ─ □ ✕ │
├ Rail 72 ┬ Content (Canvas, top-left radius 8, 1 px stroke) ────────────────────┤
│ 书架    │ page                                                                   │
│ 发现    │                                                                        │
│ 识别 ③  │                                                                        │
│  …      │                                                                        │
│ 设置    │                                                                        │
│ 退出    │                                                                        │
└─────────┴────────────────────────────────────────────────────────────────────────┘
```

- **Title bar**
  - [REQ] `ExtendsContentIntoTitleBar`, height 48. Drag regions exclude the search box and the session chip.
  - [DEP] `InputNonClientPointerSource` passthrough regions (WinAppSDK 2.5.1, supported).
  - [FALLBACK] If title-bar integration fails review, keep the system caption and place search as the first item of
    the Library command row. This is a documented deviation, not silent drift.
- **Search**
  - [REQ] Ctrl+F or Ctrl+K focuses it. It filters the Library with the same `LibraryQuery.Text` semantics as today.
    Typing on another page navigates to Library with the query applied.
  - Discover keeps its own online query box; remote catalog search is different semantics and must not be merged.
- **Session chip** (title bar, right)
  - [REQ] Visible only when `SessionPresentation` is not the empty state.
  - Content: 24 cover avatar + title (ellipsis, max 200) + status dot + mono timer.
  - Clicking it opens a flyout with the existing session text, 暂停计时 / 恢复计时 (same enablement), and 打开详情.
  - Terminal or incomplete states (已确认游戏退出, 上次会话未能确认完整结束, …) remain in the chip until a new
    session or until the user dismisses the flyout notice. Dismissing is UI-only.
  - `LifecycleNotice` warnings render inside the flyout, and as a persistent InfoBar on the current page when severity
    is Warning.
- **Rail**
  - [REQ] Items are 62×56, icon 20 + label 11/14. Order: 书架, 发现, 识别 (badge = pending reviews when available),
    spacer, 设置, 退出 The Order.
  - The exit item has the full name "退出 The Order" in its tooltip and accessible name. The visible label is 退出,
    and it calls the existing exit command; the tray hint text that references 「退出 The Order」 stays valid.
- **Notices**
  - [REQ] Informational transaction notices float as an InfoBar at top-centre of the content area (max width 560,
    offset 12). They auto-hide after 6 s, pausing while hovered or focused.
  - Warning and Error notices, and the read-only banner, are inline at the top of the content area and persistent.
  - Live region stays Polite. Assertive is used only for Error.
- **Test diagnostics**
  - [REQ] They remain available only in Test + Diagnostics, collapsed into a bottom drawer toggled by Ctrl+Shift+D.
    They must not appear in visual-review captures unless requested.

## 3. Library — command row

Height 52, padding 0 16 0 24:

- **Left:** 书架 (Title 20/28 SemiBold) + count caption (`384 部`, or `3 / 384 部` when filtered).
- **Right:** 排序 ▾ (DropDownButton showing the current sort name; icon-only in Medium/Narrow) · 筛选 (with a
  numeric badge of active conditions) · segmented [网格 | 列表] · 选择 (toggle) · 导入 (secondary button) ·
  inspector pane toggle.
- **Narrow:** sort, filter and view stay; 选择, 导入 and the pane toggle move to the ⋯ overflow together with
  重新读取书架.
- [REQ] 导入 is not accent-filled. The accent fill is reserved for 启动游戏 and for the confirm action of the current
  surface.
- [REQ] **Active filter chips** row appears only when conditions are active: one chip per condition with ×,
  plus 清除全部 and the result count. It replaces `ActiveFilterRegion`.

## 4. Library — Grid

- **Tile:** borderless. Cover 3:4, radius 6, 1 px inner stroke (12 % neutral) and elevation shadow.
  - Title: BodyStrong 14/20, max 2 lines, ellipsis, tooltip = full name. The title row has a min height of 40 so
    rows align.
  - Meta line: Caption 12/16 — status dot + text, rating `9 分` if set, ♥ right-aligned if favourite
    (accessible name 收藏).
  - Wishlist (0 installs): a `愿望单` pill at the cover's bottom-right.
- **Geometry:**
  - Column count n = floor((W + 16) / (min + 16)). Item width = (W − 16(n−1)) / n, clamped to [min, 196].
    This removes the ragged gutter.
  - Gaps: 16 horizontal, 20 vertical. Padding 4 24 24.
  - [DEP] Set `ItemsWrapGrid.ItemWidth/ItemHeight` on size change. Keep the default virtualization,
    `ContainerContentChanging` and the CoverPresenter request/recycle contract unchanged.
  - [REQ] Tile height = cover + 8 + 40 + 2 + 16.
- **Interaction:**
  - Click selects (single). Ctrl/Shift extends (`SelectionMode=Extended`). Double-click or Enter opens Details
    (existing `OpenSelected`). Space toggles selection in 选择 mode.
  - **选择 mode** switches to `Multiple` with visible 22 px checkboxes at the cover's top-left.
- **States:**
  - hover — cover lifts 2 and the shadow deepens over 100 ms (none with reduced motion);
  - selected — 2 px accent outline, offset 2;
  - focus — 2 px focus stroke (system focus visual), offset 3, never hidden by selection;
  - pressed — scale 0.98 over 80 ms.
- **First load:** the grid stays empty for 400 ms, then shows a ProgressRing (existing `IsLoading`). [FREE] Skeleton
  tiles may replace the ring if they do not request covers.
- **Empty states:**
  - empty library: glyph + "书架还是空的" + 导入目录 (accent);
  - no results: "没有符合条件的作品" + 清除筛选;
  - load failure: persistent Error InfoBar + 重新读取书架.

  The texts come from the existing `EmptyText` semantics; only the presentation is added.

## 5. Library — List

| Column | Width | Content | Hidden when list width < |
|---|---|---|---|
| (checkbox, 选择 mode only) | 36 | CheckBox | — |
| Cover | 36 | 36×48 thumb, radius 3 | — |
| 名称 | * (min 200) | title (BodyStrong, 1 line, ellipsis, tooltip) / original title or developer · ♥ 收藏 | — |
| 状态 | 92 | dot + text | 440 → merged into the name's 2nd line |
| 评分 | 64 | number or — | 560 |
| 游玩时长 | 96 | mono `h:mm` or — | 560 |
| 最近游玩 | 100 | caption date | 760 [DEP: LastPlayed projection] |
| 安装 | 64 | count, or `愿望单` when 0 | 760 |

- Row height 60. The header row (36) is sticky.
- Clicking a sortable header (名称, 评分, 游玩时长, 最近游玩, and 最近添加 via the sort menu) applies the existing
  sort key. The direction stays as defined by the current sort semantics (no new ascending/descending state).
- [REVIEW] Adding direction toggles is a semantics change requiring approval.

## 6. Inspector (docked 340 / 300, overlay 320)

Top to bottom:

1. **Artwork stage** — 268 tall (232 at 300 width).
   - Background: the selected cover, filling the width, anchored at 62 % vertical focus, ambient blur 6.
   - Glass plate inset 12 at the bottom, height 148: title (Subtitle 18/24 SemiBold, 2 lines), caption (original title
     · developer), action row [▶ 启动游戏 (accent, 40 high, flexible width)] [详情].
   - When the launch state is `PositionReviewRequired`/`StaleInstallation`, the accent button reads 确认启动位置 and
     invokes `ConfirmAsync`; after success it becomes 启动游戏. While launching, 取消启动 replaces 详情. Disabled
     states show their reason in the line below (the existing `Launch.Message`).
2. **本次使用的安装** — compact selector, 36 tall: folder name (not full path; full path in tooltip and accessible
   description) and count. Below it, a status line with a dot (启动位置已确认 / 待确认 / 程序缺失 — from
   `Launch.Message`).
3. **Facts** — `dl` grid 64 | *: 状态, 评分 (+♥), 游玩 (total · count), 最近.
4. **Tags & categories** — chips, max 2 lines, then "+N".
5. **笔记** — 3-line excerpt.
6. **编辑个人资料** — text button that opens Details (editing stays there).

Other states:

- **Multi-selection:** fanned stack of up to 3 covers + "已选 N 部作品" + 识别所选, 批量编辑, 取消选择.
- **Nothing selected:** [DEP] "继续游玩" card for the most recent session game. [FALLBACK] Status summary counts from
  the current query result.
- [REQ] The inspector reads only existing view-model state. It does not create drafts, and quick edits are out of
  scope.

## 7. Details

- **Breadcrumb row** (40): back button + `书架 › <title>`.
- **Hero stage** — full content width, height 312 (260 when the window is under 760 tall).
  - Ambient cover background at focus 55 %.
  - Poster 168×224 at left 32, top 40 (existing CoverPresenter, MinimumEdge 512).
  - Glass plate at x 224, y 50, w min(680, W−256), h 212:
    - title (Display 28/36, 2 lines);
    - meta caption;
    - fact row (status · rating · ♥ · total time);
    - action row: [▶ 启动游戏 (lg)] [installation selector 250] [⋯ menu: 管理本地安装, 选择本地封面, 重新识别,
      刷新在线资料, 移除愿望条目].
- **Body** — padding 20 32, two columns: 个人记录 card (flex, min 420) | right column 320.
  - Right column: 游玩记录 card (big mono total, count, last), 本地安装 card (rows: status dot, folder name, 首选 /
    待确认位置; 管理本地安装 link), 资料来源 card (relationship, cover description, actions).
- **Below 900 content width:** single column; the right cards move under 个人记录.
- **个人记录 card**
  - Fields, unchanged semantics: 个人名称 · 游玩状态 | 个人评分 · 0–10，可留空 | 收藏 · 分类 · 个人标签 · 笔记
    (min 120, max 320).
  - Header status text `已保存` / `● N 项未保存` (`DraftState`, Polite live region).
  - Dirty fields show a 2 px accent bottom stroke.
- **Save bar**
  - Floating Acrylic bar, bottom-centre, visible only when `IsDirty` (slides 12 up over 140 ms).
  - Contents: "有 N 项未保存的修改" · 取消修改 · 保存修改 (accent). The page reserves 72 bottom padding while the
    bar is visible.
  - [REQ] All leave-guards (navigation confirm, save-in-progress lock, reload-after-commit) are unchanged.
- **No installation:** the action row shows [添加或管理本地安装] in the accent slot with the reason "还没有本地安装，
  暂时无法启动。" (existing text).

## 8. Other workflows (same system, lower priority)

- **Discover (发现)**
  - Same command-row pattern: page title + online query box (max 520) + 搜索 / 取消.
  - Results: Wide → list 420 | detail; Narrow → stacked.
  - The detail uses the hero layout without the glass stage (poster + title + 加入愿望单 accent).
  - Restricted state (no key): an inline card with a link to 设置 › 在线目录.
- **Recognition (识别与核对)**
  - Three regions: queue 300 | comparison (local evidence vs. selected candidate side by side) | sticky decision
    footer (确认应用所选候选 accent · 忽略本次建议 · 打开本地详情 · 手动关联…).
  - Toolbar: 只补缺失资料, 取消本轮识别, toggles.
  - Empty queue: "目前没有需要核对的建议" + an explanation of how items enter the queue.
  - Progress line keeps its existing text. Conflicts use the warning colour plus a ⚠ icon — never colour alone.
- **Settings**
  - Appearance section: choices on the left (theme segmented, 材质效果 radio list, 书架默认视图) and a live
    inspector-stage preview (340) on the right.
  - Then the 在线目录 and 数据 groups, each one surface with hairline rows.
  - "当前生效" status line explains the effective material and why (policy reason) — see 03 §6.
- **Import / Installations:** keep the flows. Adopt the command-row + card patterns, and a sticky confirm footer
  (导入所选候选 / 保存安装).
- **Dialogs:** ContentDialog, solid ContentBrush, radius 8 (platform default). Primary action is accent only for a
  constructive confirm. Destructive confirms use a neutral primary + warning text.
- **Flyouts / menus:** Acrylic (standard material), radius 8, 1 px stroke.
  - Filter flyout content: pills for 作品范围 / 游玩状态 / 个人评分 (single-select, same values as the current
    ComboBoxes), 分类 and 个人标签 text boxes, 只看收藏 toggle, 常用筛选, footer 保存筛选 · 清除条件 · 完成.
  - Conditions apply immediately, as today.

## 9. Keyboard and focus

- **Tab order:** title-bar search → session chip → rail → page command row → active chips → grid/list (one tab stop;
  arrows move) → inspector (stage actions → installation → edit) → save bar when visible.
- **Shortcuts:**
  - F6 cycles regions (title bar, rail, content, inspector).
  - Ctrl+F search; Enter open Details; Ctrl+Enter launch the selected game (same command as the inspector button,
    same enablement).
  - Esc clears the search when it is focused, otherwise clears selection.
  - Ctrl+Shift+D Test drawer (Test only).
- **Focus:** the system focus visual (2 px outer, 1 px inner) on every interactive element. It is never clipped by
  rounded containers (use `FocusVisualMargin`).
