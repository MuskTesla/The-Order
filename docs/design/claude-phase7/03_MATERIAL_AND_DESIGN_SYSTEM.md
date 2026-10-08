# 03 · Material and Design System

## 1. Principle — at home on Windows, unmistakably The Order

| Layer | Windows convention (foundation) | The Order identity (on top) |
|---|---|---|
| Window | Mica frame, extended title bar, system caption buttons, snap behaviour | Gray-green Canvas with an 8 DIP inset corner where content meets the frame |
| Type | Segoe UI Variable Text / Display; CJK fallback Microsoft YaHei UI / Yu Gothic UI | Mono session timer; title-first hierarchy; Japanese titles rendered with the Japanese font (Language tag) |
| Controls | Stock WinUI controls, ControlCornerRadius 4, focus visuals, keyboard model | Accent reserved for the single "play/confirm" act; status always dot + word |
| Motion | WinUI timings (83/167/250 ms), connected entrance | Cover lift on hover; stage cross-fade on selection |
| Material | Mica (frame), Acrylic (transient, over dynamic content) | **The lens**: a clear refracting rim + frosted body, used only on the artwork stage |

The lens shares the system geometry and light:

- plate radius 20 = 5 × control radius 4;
- highlight from the top-left, matching the elevation shadow;
- frost tint taken from the Canvas tokens, so the glass reads as the canvas lifted into glass rather than a separate
  layer.

## 2. Tokens

### Colour

Values are existing tokens unless marked *new*.

| Token | Light | Dark | Use |
|---|---|---|---|
| Canvas | #F2F3EF | #1B2324 | Content background |
| Content | #FAFBF8 | #242D2E | Cards, inspector body |
| Raised *new* | #FFFFFF | #2A3435 | Inputs, secondary buttons |
| TextPrimary | #263230 | #E5EBE7 | |
| TextSecondary | #53635F | #B4C2BC | Captions — 5.7:1 / 8.7:1 on Canvas (computed) |
| Stroke | #D9E1DC | #3C4A48 | 1 px separators |
| Accent | SystemAccentColorDark1 | SystemAccentColorLight2 | Interaction (existing Windows-derived policy) |
| Status · 在玩 | Accent | Accent | dot |
| Status · 已完成 | #256448 (Success) | #9BD1AF | dot |
| Status · 搁置 | #805300 (Warning) | #F0C77B | dot |
| Status · 放弃 | #6B7A76 *new* | #8FA19B *new* | dot only (4.0:1 / 5.9:1 — meets 3:1 for graphics; never used for text) |
| Status · 未开始 | outline TextSecondary | outline | hollow dot |
| FrostTint *new* | #FAFBF8 @ .80 | #1B2324 @ .80 | Lens body |

- The high-contrast dictionary maps every token to system colours (existing). The lens and stage are not shown in HC
  (see §6).

### Typography

| Style | Size / line | Weight | Use |
|---|---|---|---|
| Display | 28/36 | SemiBold | Details title |
| Title | 20/28 | SemiBold | Page titles (down from 26; recovers ~12 DIP) |
| Subtitle | 16/22 · 18/24 (stage) | SemiBold | Section / stage title |
| Body | 14/20 | Regular | Default |
| BodyStrong | 14/20 | SemiBold | Tile titles, list names |
| Caption | 12/16 | Regular | Meta |
| SectionLabel | 12/16 | SemiBold, TextSecondary | In-surface headings |
| Mono | 13/18 (chip 12) | Cascadia Mono, tabular | Timers, durations |

- [REQ] When a title contains kana, set `Language="ja-JP"` on that TextBlock so kanji use Japanese glyph forms
  (e.g. 約, 雪). Detection is a pure presentation helper.
- Text must reflow at 150 % text scale. Fixed heights apply only to grid title rows, which allow 2 lines and then
  ellipsis plus tooltip.

### Spacing, geometry and elevation

- 4-pt grid. Page padding 24 (Narrow 16). Section gap 24, inner gap 12–16. Control heights 32 (default) and 40
  (primary in stage and hero).
- Radii: controls 4, covers 6, cards and flyouts 8, content inset 8, lens plate 20, chips 11–14 (pill).
- Elevation: covers `0 1 2 / 8 %` + `0 6 18 / 8 %` (Light); flyouts 8/32 shadow; the lens adds an inset top
  highlight. There are no other drop shadows.

### Motion

| Event | Duration | Curve | Reduced motion |
|---|---|---|---|
| Page navigation | 167 ms entrance | WinUI default | Instant |
| Inspector content swap | 120 ms cross-fade (stage + text) | Decelerate | Instant |
| Tile hover lift | 100 ms | Standard | None |
| Save bar show/hide | 140 ms slide 12 | Decelerate | Fade only |
| Floating notice | 167 ms | | Instant |

The lens never animates its optics continuously. Its refraction changes only when its background changes.

## 3. Surface roles

| Surface | Material (Auto) | Content behind | How it changes | Rationale |
|---|---|---|---|---|
| Window frame (title bar + rail) | Mica | Desktop wallpaper (system-managed) | Rarely | Platform belonging at zero app cost |
| Content area | Opaque Canvas | — | — | Cover colour fidelity; text contrast |
| Cards, inspector body, list | Opaque Content | — | — | Reading surfaces |
| Flyouts, menus, selection bar, save bar, floating notice | Acrylic (`ToolbarBrush` family) | Scrolling grid / page (dynamic) | Continuously | Real-time system backdrop is free and correct for moving content; custom refraction is not justified here |
| Artwork stage (inspector header, Details hero) | **Lens (C1 rim + frost body)** over an app-rendered ambient cover | The selected game's own cover, rendered by the app | Only on selection, cover, theme, DPI or size change | Stable, app-owned, event-driven background → C1 is visible, cheap and never stale |
| ContentDialog | Solid Content + system smoke | — | — | Decisions need maximum clarity |

**Classification rule:** classify the background, not the position. The inspector is stationary, but its background is
discrete (it changes per selection). The command row is stationary over a background that scrolls continuously, so it
gets no custom glass.

## 4. The lens — optical specification

Parameter mapping uses the 24-float constants of `OpticalDrawEx` (05).

| Property | Stage (inspector, plate ≤ 340 wide) | Hero (Details) | Constant |
|---|---|---|---|
| Shape | Rounded rect, radius 20 | radius 23 (C1) | c[8] |
| Bevel (rim) width | 20 | 30 (C1) | c[9] |
| Optical depth (max shift = .55 × depth) | 16 → 8.8 DIP | 30 → 16.5 DIP (C1) | c[10] |
| Dispersion | .55 | .8 (C1) | c[11] |
| Gloss (neutral highlight) | .45 | .55 (C1) | c[12] |
| Frost amount | 1 | 1 | c[17] |
| Body field | inset .6 × bevel; body radius max(.6 × corner, 8); 12 DIP feather | same | shader constants |
| Body blur σ | 20 DIP | 20 DIP | `OpticalSceneFrost(σ)` |
| Tint | FrostTint (theme) α .80 | same | c[20..23] |
| Ambient (stage background) blur | 6 DIP | 7 DIP | **missing** — see 05 §Not done |
| Clear rim | outer 0.6 × bevel − 6 DIP is pixel-identical to C1 | same | verified |

- **C1 reference:** `OpticalProfile.Reference` (30/30/.8/.55) and radius 23 remain recoverable bit-exactly with frost
  amount 0 or the legacy 20-float draw (05).
- The stage values are the existing `OpticalProfile.Daily` family (20/16) with slightly more dispersion. They are a
  starting point; compare against C1 at runtime before freezing. [REVIEW]

### Text contrast is set by tint alpha, not by blur

- Worst case over any artwork (analytic, pure black or white art under α .80):
  - Light: dark text 9.87:1;
  - Dark: light text 7.59:1.
- Measured on runtime captures: 7.98–15.5:1.
- Required: ≥ 4.5:1 for body text and ≥ 3:1 for the accent button boundary against the body.
- **Text safe area** = plate inset by the bevel (fully frosted region). No text in the transition band.

## 5. Stage lifecycle and ownership

- One renderer per visible stage. Only one is alive at a time: the inspector stage when Library is visible, the hero
  stage when Details is visible.
- Release follows the proven early-release order: release before the page leaves the visual tree, on hide, on policy
  denial, and on theme/device failure.
- **Invalidation:** selection, cover image change (ImageOpened/Failed of the selected item), theme, DPI, stage size
  class change. Coalesce rapid keyboard selection to the last item.
  - [FREE] Up to 120 ms quiet period.
  - While a new scene is pending, show the Standard stage (XAML cover + Acrylic plate) immediately. Never show the
    previous game's art under the new title.
- **Per selection:** 1 scene render (ambient) + 1 frost pass, then draws only on geometry change. Idle work is zero
  (verified in the Gallery: no draws or passes over 12 s idle).
- **Memory:**
  - source + frost textures = 2 × (stage px). The inspector at 340×268 DIP, 200 % → 2 × 1.46 MB.
  - The Gallery's 2600×1320 test scene is 13.7 MB each.
  - The ambient source should be rendered at stage size, not Gallery size.

## 6. Quality modes

Setting: 设置 › 外观 › 材质效果. The value is stored in UI preferences [DEP: new preference field]. The effective state
is shown in a "当前生效" line with its reason.

| Mode | Frame | Transient surfaces | Artwork stage | When the system disallows effects* |
|---|---|---|---|---|
| 自动 Auto (default) | Mica | Acrylic | Lens if the policy allows and the GPU path initialised; else Standard stage | All solid |
| 高级 Advanced | Mica | Acrylic | Lens whenever the policy allows (ignores Auto's soft heuristics, e.g. battery without saver) | All solid — never bypassed |
| 标准 Standard | Mica | Acrylic | Standard stage: XAML ambient cover + Acrylic plate | All solid |
| 关闭透明效果 Off | Solid Canvas | Solid (Acrylic fallback colours) | Opaque stage: poster thumbnail + title on Raised, no ambient art | — |
| High contrast (system) | System colours | System colours | No stage art; title + actions on Window colour; 2 px borders | — |

\*Existing `MaterialPolicy` inputs:

- `UISettings.AdvancedEffectsEnabled` (Windows transparency effects);
- `AccessibilitySettings.HighContrast`;
- `CompositionCapabilities.AreEffectsSupported`;
- `PowerManager.EnergySaverStatus`;
- `GetSystemMetrics(SM_REMOTESESSION)`;
- material fault.

`UISettings.AnimationsEnabled` drives reduced motion only. No other system preference is claimed. Device loss or
renderer failure → the stage falls back to Standard for the rest of the page visit and records the failure (existing
fail-closed pattern).

## 7. Accessibility acceptance

- **Contrast:** text ≥ 4.5:1 (≥ 3:1 for ≥ 18.66 DIP SemiBold); UI boundaries and focus ≥ 3:1; status never by colour
  alone.
- **Names:**
  - every icon button has an accessible name (e.g. "网格视图", "隐藏检视面板", "更多书架操作");
  - tiles expose `Name = title`, `HelpText = 状态 · 评分 · 收藏 · 愿望单`;
  - the inspector is a landmark-like group named 作品检视.
- **Live regions:** search result count (Polite), draft state (Polite), launch message (Polite), errors (Assertive).
- **Scaling:** 100/150/200 % DPI and 150 % text scale without clipping of primary actions. Long CJK titles wrap
  (2 lines in grid, 2 in stage, 2 in hero) and then ellipsis + tooltip.
- **Opaque alternatives:** every glass surface has an opaque rendering (Off / HC) with the same layout and actions.
