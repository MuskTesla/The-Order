# 01 · Creative Direction — revision 2

Status: **REVISED DESIGN READY FOR USER REVIEW.** Revision 1 (Direction B with a side inspector) was rejected on
2026-10-08 and is kept in `rev1/` for history only.

No approval gate has been satisfied, and broad UI implementation has not started. The only implemented work is the
Test-only material code in the Gallery (05).

- Previews: `.artifacts/claude-design/review.html`.
  - Section 2: WinUI runtime material evidence.
  - Sections 3–4: concept mockups and runtime accent evidence.
- Prototype source: `previews/direction-r2.html`.

## 1. What caused the three mismatches

| Objection | Cause (verified) |
|---|---|
| Glass looked like an image or panel pasted into glass | **Material:** revision 1 joined two optical regimes — a sharp refracting rim and a σ 20 / 80 %-tint body — through a 12-DIP transition. The Gallery added three non-optical XAML layers: a 1-px frame stroke, a highlight border, and an opaque text card. A native measurement makes this concrete: revision 1's rim carries 10× the detail of its body. **Composition:** revision 1's "stage" was a rectangular picture block inside the inspector, so the glass sat in a framed image. |
| Too far from Fluent; fixed green | The runtime accent was already Windows-derived (`SystemAccentColorDark1/Light2`). My **mockups** hardcoded `#2F6F63` as the accent and added custom gray-green neutrals, pill chips, a 72-DIP labelled rail and a custom radius scale. |
| Click showed an inspector | My own decision: click = select, to save launch steps. It replaced the expected click-to-open behaviour. |

## 2. Revised direction — "Fluent library, glass on the work"

- **Fluent foundation.**
  - Mica window with an extended title bar and AutoSuggestBox search.
  - Standard NavigationView with Fluent thresholds.
  - Fluent card grid and table list.
  - Fluent typography ramp, control geometry, elevation and focus visuals.
  - Accent read from WinUI theme resources only.
- **Identity through content.** Covers lead the Library. Each game's Details page opens on an ambient band made from
  its own artwork, with the poster and one Liquid Glass plate.
- **Custom glass in one place.** It appears only where its background is app-owned and stable: the Details hero.
  Everything that floats over the scrolling Library uses system Acrylic.
- **Two approved variants, chosen by what is behind the glass** (03 §4):
  - **完全清透** when the backdrop is the defocused ambient band (the default hero);
  - **连续玻璃** when sharp content sits behind text (narrow layouts where the plate overlaps the poster).
  - C1 清透 and 清透边缘 + 磨砂中心 are history only.
- **Interaction.**
  - A single click on a cover opens Details.
  - Batch work happens in an explicit selection mode (选择 button, Ctrl/Shift+click).
  - Launch stays available through Details, the context menu and Ctrl+Enter.
  - Returning restores search, filters, sort, view, scroll position and focus.

## 3. Decisions awaiting the Product Owner

| ID | Decision | Recommendation |
|---|---|---|
| R1 | Approve revision 2 as the basis for the Shell + Library vertical slice | Approve |
| R2 | Glass placement: Details hero only (完全清透 default, 连续玻璃 when sharp content is behind) | Approve |
| R3 | Hero text polarity follows the artwork's brightness (light text over dark art, dark text over light art) in both app themes (03 §4.3) | Approve |
| R4 | Context-menu "启动游戏" and Ctrl+Enter as launch shortcuts that reuse the existing launch flow and stop at Details whenever confirmation is needed | Approve |
| R5 | Move 退出 The Order into the NavigationView footer; remove the permanent session bar (title-bar chip while a session exists) | Approve |
| R6 | Read-only "last played" projection (list column) | Approve; no schema change |
