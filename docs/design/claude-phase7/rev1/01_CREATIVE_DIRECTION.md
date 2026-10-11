> **SUPERSEDED — revision 1, rejected by the Product Owner on 2026-10-08.** Kept for history only. Nothing in this file is an active requirement (side inspector, single-click select, fixed gray-green accent, clear-rim + frosted-centre glass). See ../01–05 (revision 2).

# 01 · Creative Direction — The Order (Phase 7 redesign)

Status: **DESIGN READY FOR USER REVIEW** — recommendation only. Nothing here is a user-approved design or a validated
UI implementation. The only implemented and validated item is the Test-only Liquid Glass frost capability (see 05).

Workspace: `D:\Projects\The-Order-worktrees\claude-design-phase7`, branch `claude/design-phase7`
(baseline snapshot `7d74e24`, engineering `f18d609`). Previews: `previews/` (HTML source) and the review page
`.artifacts/claude-design/review.html` (rendered PNGs + runtime captures).

## 1. Evidence base

Each observation below is labelled with its basis:

- **[RT-now]** — current runtime: my own isolated Test runs of a candidate whose non-Gallery UI is byte-identical
  to the authoritative source (2026-10-08, `.artifacts/phase7-2b/claude-frost/runs/audit-a`, 1280×800 DIP, 200 % DPI,
  384-item synthetic library).
- **[RT-hist]** — historical runtime screenshots: Phase 7.2A Round 3 (`phase7-2a-round3/control/*`) and v9
  (`library-toolbar-integration/visual-v9`).
- **[SRC]** — source inspection of baseline `7d74e24`.
- **[INF]** — inference.

Not re-observed at runtime by me: Details, Import, Installations, ContentDialogs, session-active states.
For those I rely on [RT-hist] Round 3 captures and [SRC].

## 2. Audit — what the current interface does to the user's main tasks

Primary tasks, by expected frequency [INF from product facts]: (1) find a game and launch it, (2) browse and enjoy the
collection, (3) record status, rating and notes after play, (4) import and identify new games, (5) discover online,
(6) settings.

| # | Finding | Basis | Effect on tasks |
|---|---|---|---|
| A1 | Fixed chrome stack: notice InfoBar (~56) + page title block (~90) + bordered toolbar card (~72) + session bar (~64) + Test diagnostics. At 1280×800, covers start at about 33 % of window height and about 1.3 rows are visible. | RT-now `library-grid-light` | Browsing density is low. The collection, the product's emotional core, gets under half the window. |
| A2 | `GridView SelectionMode="Multiple"`: a single click toggles a checkbox. Opening a game needs a double-click (undiscoverable), Enter, or select → scroll to the selection bar → 查看详情. | SRC `LibraryPage.xaml`, `.xaml.cs:184-186` | The most frequent task (open → launch) is the least discoverable. Launch is at least 3 steps away. |
| A3 | Every card shows a permanent checkbox over the cover corner, plus border and padding. The grid does not stretch, leaving a ragged right gutter (≈ 1 empty column at 1280). | RT-now, RT-hist | Visual noise; covers lose prominence. |
| A4 | List view has no column headers. "0 本地安装" repeats a label on every row, long titles wrap to 2 lines, and the row height is about 50 DIP or more. Sorting lives inside the Filter flyout. | RT-now `library-list-light` | The list is not a scannable information view; sort is hidden. |
| A5 | Details: small poster (156×208); launch sits in a second card, disabled until 确认启动位置, next to a full-path ComboBox. Raw paths and parameter arrays dominate the first screen. The save footer is always visible ("当前资料已保存"). | RT-hist `r3-details-installed-200`, SRC | Action priority is unclear; the screen reads like a diagnostics form. |
| A6 | The session bar permanently consumes ~64 DIP even with no session, and hosts a large 退出 The Order button. | RT-now | Wasted space; Exit is visually over-weighted for a tray app. |
| A7 | The empty Recognition queue shows a blank panel with no message. Disabled actions are displayed without a reason. | RT-now `page-Recognition` | Error, empty and "nothing to do" states are indistinguishable (AGENTS rule). |
| A8 | Settings has no material control. The Auto/Advanced/Standard/Off modes exist only in a Test library overflow menu. | SRC | Quality modes are not a product feature yet. |
| A9 | v9 FilterFlyout C1: a 23-DIP glass ring around an opaque panel, over a busy, moving grid. A 450 ms quiet period plus an XAML capture pipeline exist only for this one surface. | RT-hist `visual-v9/dark-on/000.png`, handoff §6 | It reads as a heavy bezel, the stale-fallback window is visible, and the cost is high for little value. |
| A10 | Good foundations: neutral gray-green tokens, Windows-accent interaction colour, high-contrast dictionary, virtualization with inactive `ItemsSource=null`, a proven early-release swapchain fix, and a policy object covering transparency / HC / energy saver / RDP. | SRC, reports | Keep all of these. |

## 3. Three concepts (same content, 1280×800, Light)

Renders: `concept-A.png`, `concept-B.png`, `concept-C.png` (concept mockups, headless Edge — not WinUI).

| | A · 安静书架 Quiet Shelf | B · 舞台与检视 Stage & Inspector | C · 放映厅 Cinema Home |
|---|---|---|---|
| Navigation | Expanded left rail (220) | Compact labelled rail (72) + title-bar search | Top tabs in title bar, Home tab first |
| Hierarchy | Poster wall; Details is a page | Poster wall + persistent inspector; Details for deep edits | Hero "continue" banner + horizontal shelves; Library is a tab |
| Command organisation | One page command row | Title bar = global; page row = view commands; inspector = item commands | Hero owns launch; shelves own browsing |
| Density at 1280 | 6 columns, ~2.2 rows | 5 columns + inspector | 1 shelf row |
| Glass | None in product paths | One app-rendered artwork stage (inspector / Details hero) | Large hero plate |
| Open → launch | double-click → Details → 启动 | click → 启动 (inspector) | 1 click for the hero game only |

Evaluation against tasks:

- **A** fixes density (A1, A3) at the lowest risk, but keeps the open/launch cost (A2) and gives the product little
  identity.
- **C** is the most emotive and best for "continue playing", but it duplicates content across shelves and
  multiplies virtualized image lists (a risk to the Phase 7.1 image-lifecycle contracts). It also pushes management of
  hundreds of titles behind a tab. Only one shelf fits at 720–800 DIP height.
- **B** solves A1, A2, A3 and A6 together:
  - selection drives the inspector, so one click shows state and installation, and a second click launches;
  - the glass appears only where its background is the game's own artwork, which is stable, app-owned and
    event-driven (not the scrolling grid);
  - it keeps a single virtualized list.

  Its cost is horizontal space (340 DIP) on medium windows, handled by the responsive rules in 02.

## 4. Recommendation — Direction B, "Stage & Inspector"

The collection is the interface; the selected game is on stage.

1. **Windows-native foundation, own identity.**
   - Windows foundation: Mica frame, title-bar search and window controls, Segoe UI Variable, standard WinUI controls,
     keyboard model and motion timing.
   - The Order's identity:
     - the cover-first poster wall;
     - the gray-green neutral palette;
     - a single material motif — the *lens*: a clear refracting rim around a frosted body, cut from the same corner
       family and lit from the same top-left as every elevated surface.

   It should feel at home next to Explorer and Photos without imitating Windows Settings.
2. **Selection is the hub.** A single click selects and fills the inspector; double-click or Enter opens Details;
   Ctrl/Shift multi-select (or 选择 mode) turns the inspector into the batch panel.
3. **Launch is the only accent-filled action on a screen.** 确认启动位置 takes the primary slot when the existing
   launch state requires it. Semantics are unchanged.
4. **Glass is earned.** Advanced glass is limited to the artwork stage (inspector header and Details hero). Flyouts,
   menus and the selection bar use standard Acrylic over dynamic content, and the frame uses Mica. Filter-flyout C1
   (v9) is retired from the delivery design.
5. **Quiet when idle.** No session means no session bar (a title-bar chip appears only while a session exists).
   Success notices float and auto-hide, while warnings and errors persist.

## 5. Tradeoffs accepted

- Single click changes from "toggle checkbox" to "select". Batch work moves to Ctrl/Shift or the 选择 mode. This is
  a deliberate interaction change; no business action changes.
- At content widths below ~1008 DIP the inspector becomes an overlay. Medium windows lose the always-visible
  inspector, and the floating selection bar supplies 启动/详情 instead.
- The artwork stage needs the selected cover's decoded pixels in the native source. This is a new presentation-layer
  dependency on the image pipeline; see 04 §Dependencies.

## 6. Decisions awaiting the Product Owner

| ID | Decision | My recommendation |
|---|---|---|
| D1 | Approve Direction B as the direction for the Shell + Library vertical slice | Approve |
| D2 | Single click = select (inspector), not toggle multi-select | Approve |
| D3 | Retire the v9 FilterFlyout C1 integration from delivery (keep its code and evidence as history) | Approve |
| D4 | Move 退出 The Order from the session bar to the rail footer (label and behaviour unchanged) | Approve |
| D5 | Advanced glass limited to the artwork stage; frost body (05) as the stage-plate material | Approve after viewing the runtime frost captures |
| D6 | New presentation projections: last played (list column, inspector), status counts | Approve; read-only projections, no schema change |
