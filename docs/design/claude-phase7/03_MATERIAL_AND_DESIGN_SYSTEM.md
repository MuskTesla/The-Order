# 03 · Material and Design System — revision 2

## 1. Principle

Fluent is the foundation — controls, typography, spacing, geometry, elevation, motion, materials (Mica, Acrylic) and
the Windows accent. The Order's identity comes from **content**: the cover-led library and the artwork-led Details
hero. The one custom material — Liquid Glass — lives on that hero.

Its geometry, lighting and tint come from the same theme resources as the surrounding interface, so it does not read
as a separate layer.

## 2. Tokens → Fluent resources

Replace the custom hex palette. The app keys may stay as aliases, but every value resolves to a WinUI theme resource.

| App key (today) | Revision 2 value | Notes |
|---|---|---|
| CanvasBrush (#F2F3EF / #1B2324) | Window: `MicaBackdrop`. Fallback `SolidBackgroundFillColorBaseBrush` | Gray-green removed |
| ContentBrush | `LayerFillColorDefaultBrush` (content surface) | |
| (cards) | `CardBackgroundFillColorDefaultBrush` / `…Secondary` (hover) | |
| StrokeBrush | `CardStrokeColorDefaultBrush`; dividers `DividerStrokeColorDefaultBrush` | |
| TextPrimaryBrush / TextSecondaryBrush | `TextFillColorPrimaryBrush` / `TextFillColorSecondaryBrush` | |
| AccentBrush | Fills: `AccentFillColorDefaultBrush`; text and links: `AccentTextFillColorPrimaryBrush` | Never a literal colour |
| SelectionBrush | Removed — use control-default selection visuals | |
| Status colours | 在玩 = accent fill; 已完成 `SystemFillColorSuccessBrush`; 搁置 `SystemFillColorCautionBrush`; 放弃 `SystemFillColorNeutralBrush`; 未开始 = outline `TextFillColorTertiaryBrush`; errors `SystemFillColorCriticalBrush` | Always dot + label |
| NavigationSurfaceBrush | Removed (Mica shows through the pane) | |
| ToolbarBrush (custom Acrylic) | Flyout/Menu defaults (`AcrylicInAppFillColorDefaultBrush` family) | `MaterialPolicy` keeps owning AlwaysUseFallback |
| BrandBadgeBrush | Keep — part of the approved brand mark | |
| HighContrast dictionary | Keep, mapping to `SystemColor*` | |

[REQ] There must be no hardcoded hue in buttons, selection indicators, focus visuals, links or tokens.

## 3. Windows accent

- **Source.** WinUI theme resources only: `AccentFillColorDefault/Secondary/Tertiary`, `AccentTextFillColorPrimary`,
  `TextOnAccentFillColorPrimary`, `SystemAccentColor*`. They give the theme-correct variants: Dark1 fill / white text
  in Light; Light2 fill / black text in Dark.
  - Runtime evidence: the system accent at startup is `#0078D4`, and the existing UI renders it in Light and Dark.
    See review §4.
- **Live change.**
  - [DEP] Subscribe to `UISettings.ColorValuesChanged` once, from the window owner, and dispatch to the UI thread.
  - If WinUI has not already re-resolved the accent ThemeResources, refresh them with the same theme toggle the Test
    accent seam uses.
  - Verify on a Test build by changing the accent while the app runs. **Not yet verified**; changing the machine
    accent needs separate authorization. A Test-only simulation exists and is labelled as such.
- **Focus.** Fluent focus visuals (`FocusStrokeColorOuter/Inner`), never the accent.
- **Artwork colours** stay inside the hero/glass (ambient band, refraction, tint). They never replace the accent.
- **High contrast.** System colours; the hero shows no artwork behind text and no glass (§6).

## 4. Liquid Glass

### 4.1 Variants

All variants are implemented in the Test Gallery (05).

| Variant | Optical construction | Status |
|---|---|---|
| **完全清透** (clear) | One continuous material: C1 refraction and dispersion of the **sharp** scene; luminance-adaptive tint in linear light (minimum α .10 Light / .16 Dark); edge density 0.20 from the same distance field; C1 specular. No blurred copy. | **Use** |
| **连续玻璃** (continuous) | Same material over an event-driven **blurred** copy (σ 12 DIP); minimum α .22 / .30. | **Use** |
| C1 清透 | Accepted C1 lens + opaque XAML text card + XAML frame stroke | History only |
| 清透边缘 + 磨砂中心 | Sharp rim + separate frosted centre (σ 20, α .8) | History only |

Rules shared by both approved variants:
- no inner card, no XAML stroke and no XAML highlight layer; the shader draws the only edge;
- text and icons sit directly on the material;
- outside the plate, pixels are unchanged (runtime max diff 1/255 over 32 states).

### 4.2 Placement: decided by what is behind the glass

| Surface | Backdrop | Variant | Why |
|---|---|---|---|
| Details hero plate (default) | App-rendered ambient band (already defocused) | **完全清透** | The band is already soft. Clear glass keeps its colour and bends it at the edge; frosting would blur a blur. Cheapest: no blur pass. |
| Details hero plate, narrow layout (plate overlaps the sharp poster) or any future sharp backdrop behind text | Sharp imagery | **连续玻璃** | Runtime shows that 完全清透 over sharp line art leaves the strokes competing with text. |
| Settings appearance preview | Same as the hero | Same as the hero | It shows the effective result. |
| Library, flyouts, menus, selection bar, save bar | Scrolling or dynamic content | System Acrylic (not Liquid Glass) | Dynamic backgrounds have no app-owned stable source; Fluent Acrylic is correct and free. |
| Window frame, navigation | Desktop | Mica | Platform material |

### 4.3 Polarity follows the artwork [R3]

- On each hero scene, compute the mean linear luminance of the band under the plate (CPU, from the already-decoded
  cover pixels downsampled; once per scene).
- If the mean > 0.30: **lift** polarity (Ymin .42), dark text (`#000000` 90 %).
- Otherwise: **deepen** polarity (Ymax .08), light text (`#FFFFFF`).
- This holds in both app themes. The hero is an on-media surface, as in Store/Xbox pages.
- Why: in the runtime boards, a polarity forced against the backdrop turns the glass into a near-opaque slab.
- Guaranteed contrast for primary text in both polarities, over any backdrop (native GPU test, 7 extreme backdrops):
  - lift: background luminance ≥ 0.41 → 9.2:1 against black text;
  - deepen: ≤ 0.082 → 7.9:1 against white text.
- Text safe area: plate inset by 24 DIP, so no text sits in the steepest edge band.

### 4.4 Parameters

These map to `OpticalDrawEx` (28 constants; see 05):

- geometry as tested: radius 23, rim width 30, depth 30 (max shift 16.5), dispersion .8, gloss .55;
- [REVIEW] a radius-20 / rim-24 hero variant may be tested for closer alignment with Fluent's 8-DIP family before
  freezing;
- continuous material: `c[24]=1`, `c[25]` = bound, `c[26]` = ±1, `c[27]=.20`;
- clear: `c[18]=1`;
- tint: Fluent neutral `#FAFAFA` (lift) / `#202020` (deepen) at the minimum alpha.

### 4.5 Lifecycle

- One renderer, alive only while Details is visible. Release before the page leaves the visual tree, on hide, on
  policy denial and on failure (the proven early-release order).
- Scene revisions happen on cover change, theme, DPI, hero size class or polarity change. 连续玻璃 adds one blur pass
  per revision.
- Idle work is zero (runtime: no draws or passes over 24 s).
- While a revision is pending, show the Standard rendering. Never show the previous game's art.

## 5. Typography, geometry, elevation, motion — Fluent

- **Type:** Segoe UI Variable — Caption 12/16, Body 14/20, Body Strong 14/20 SemiBold, Subtitle 20/28, Title 28/36,
  Display 68/92 (unused); Cascadia Mono for timers. Japanese titles get `Language="ja-JP"`.
- **Radii:** controls 4 (`ControlCornerRadius`), cards / flyouts / dialogs 8 (`OverlayCornerRadius`), content inset 8.
  The glass plate is the only larger radius (§4.4).
- **Elevation:** ThemeShadow — cards on hover Z 8, flyouts Z 32.
- **Motion:** Fluent page transitions (EntranceNavigationTransitionInfo / Drill-in into Details), 83/167/250 ms.
  ConnectedAnimation cover→poster is optional. Reduced motion → instant.

## 6. Quality modes (设置 › 外观 › 材质效果)

| Mode | Frame | Transient surfaces | Details hero |
|---|---|---|---|
| 自动 Auto (default) | Mica | Acrylic | 完全清透 / 连续玻璃 per §4.2 when `MaterialPolicy` allows and the GPU path initialised; else Standard |
| 高级 Advanced | Mica | Acrylic | Same as Auto, ignoring Auto's soft heuristics (e.g. battery without saver); never overrides the system rules below |
| 标准 Standard | Mica | Acrylic | XAML ambient image + Acrylic plate (no custom refraction) |
| 关闭透明效果 Off | Solid | Solid fallbacks | Opaque card; poster only, no ambient art |
| High contrast (system) | System colours | System colours | No art, no glass; 2-DIP borders |

System inputs (existing `MaterialPolicy`):
- transparency effects (`UISettings.AdvancedEffectsEnabled`);
- high contrast;
- `CompositionCapabilities`;
- energy saver;
- remote session;
- material fault.

Reduced motion comes from `UISettings.AnimationsEnabled`. No other preference is claimed.

## 7. Accessibility acceptance

- Text ≥ 4.5:1 (body) everywhere; ≥ 7:1 on glass by construction (§4.3).
- Focus visible on every stop; status never conveyed by colour alone.
- Names on all icon buttons and cards; live regions for the result count, draft state, launch message and errors.
- 100/150/200 % DPI and 150 % text scale without clipped primary actions; minimum width 480.
