# 05 · Liquid Glass Engineering Report — revision 2

Status: **MATERIAL CHANGES READY FOR INTEGRATION REVIEW** — Test-only, Gallery-scoped. This is not a resource-gate
PASS, not GA1-equivalent, and not integrated into Library or Details.

## 1. Problem and hypotheses

- **Observed (user review + runtime):** revision 1 read as two components — a clear refracting rim around a frosted
  insert. C1 itself needs an opaque XAML text card.
- **Hypothesis A (连续玻璃).** The two-component reading has two sources:
  1. a change of optical regime inside the lens (sharp vs blurred);
  2. non-optical XAML layers (stroke, highlight, text card).

  So: drive every property from one distance field over one source; replace the uniform 80 % tint with a per-pixel,
  luminance-adaptive tint (contrast bound without a backing panel); let the shader draw the only edge.
- **Hypothesis B (完全清透, requested by the Product Owner).** The same material over the sharp refracted scene is
  coherent as clear glass. It suits defocused backdrops; sharp, busy backdrops behind text are a placement issue,
  handled in 03 §4.2.

## 2. Changes

Commits on `claude/design-phase7`:

| Commit | Content |
|---|---|
| `7d74e24` | Baseline snapshot — **never integrate** |
| `f18d609` | Revision 1 frost capability (history; also the base the next commit builds on) |
| `1369f99` | Revision 1 docs (superseded) |
| `09465c2` | **Continuous glass + fully clear** (this report) |
| docs commit | Revision 2 design package |

`09465c2` (8 files, +231/−38):

- **`OpticalRenderer.cpp`**
  - cbuffer 112 B; `material` float4 (enable, luminance bound, polarity, edge density); `raster.z` = clear flag.
  - Continuous branch: blurred copy (or the sharp sample when clear) → C1 refraction and dispersion → linear-light
    adaptive tint `k = max(minAlpha, need)` → edge density `pow(1−t,3)·w` → C1 specular.
  - `OpticalDrawEx` accepts 20/24/28 constants; validates ranges, polarity ±1, clear ∈ {0,1}, clear only with the
    material enabled, and rev1/rev2 exclusivity.
  - A blurred-copy request without a valid copy fails closed (clear needs none).
  - New `OpticalSceneImageBlur` export.
- **`GallerySource.h`**
  - Blurred scene images use one D2D Gaussian effect per draw, held until `EndDraw`.
  - Blurred inputs are created at the scene DPI.
- **`OpticalRenderer.cs`:** P/Invoke and primitive kind 3.
- **`GalleryScene.cs`:** scenes 8/9, the Details-hero composition (ambient cover σ 18 + scrim + sharp poster outside
  the lens).
- **Gallery `.xaml` / `.cs`**
  - Body-mode selector: C1 / rev1 / 连续玻璃 / 完全清透. Test commands `frostOff`, `frostOn`, `continuous`, `clear`,
    `ambientCover`, `ambientReference`.
  - In continuous and clear modes the text panels go transparent, the XAML highlight collapses, and the frame stroke
    becomes transparent **without changing `BorderThickness`**.
  - The theme stroke brush is restored from `DesignTokens` when leaving those modes.
- **`fixtures/optical-source-regression.cpp`:** continuity, regime, contrast, validation and resource tests for both
  variants.

**Unchanged:**
- the C1 shader path (bit-exact) and C1 constants;
- the same-device D2D/DirectWrite/WIC source and the early swapchain release;
- `MaterialPolicy`, Library/Toolbar/v9 code, business code, store, CoverPresenter/cache and virtualization.

## 3. Verification

### Native GPU regression (`native/scripts/test-optical-source.ps1`)

All PASS, log `claude-glass-r2/native-regression.log`:

- existing 132 + 160 exact readbacks;
- rev1 suite;
- 连续玻璃 / 完全清透 off == accepted C1, bit-exact;
- outside the lens unchanged;
- no inner boundary on a uniform field (max adjacent delta 1 ≤ 3);
- rim/body detail ratio — 连续玻璃 2.33 / 1.27 within the 2× limit; rev1 is 10×;
- contrast bound over 7 extreme backdrops × 2 polarities: lift ≥ 0.411 luminance (9.2:1), deepen ≤ 0.082 (7.9:1);
- validation;
- blurred scene effects released at `EndDraw`.

### Builds and managed tests

- WinUI Debug: 0 warnings / 0 errors.
- Targeted tests (LensOptics, MaterialResource, PresentationRemediation, MaterialTreeProbe): 47/47.
- Release publish OK.

### Runtime (WinUI, isolated Test profiles, 1160×820 DIP, 200 % DPI)

- **C1 mode vs frozen GA1 candidate**, stage region, captured right after rev2 states: mean 0.000, p99 0,
  max ≤ 6 (JPEG noise). `pixel-comparison-r3.json`.
- **连续玻璃 / 完全清透 vs C1 outside the lens** (+16 px margin), 16 states each: max 1/255.
- **Lifecycle** (criteria frozen in `claude-glass-r2/criteria.md` before each run):
  - Revision 2: 16 of 17 rows pass. The **idle-12s row FAILED as defined** (+2 draws). A follow-up with criteria
    written before it ran shows A = B = C over 24 s (no idle work). The +2 was the mode switch settling later than the
    400-ms probe delay.
  - 完全清透: all rows pass — blurred copy retired on switch; no idle work over 24 s; renderer released on hide/leave;
    recovery on show/re-entry.
- **Exits:** every run exited normally with the Test library hash unchanged. Exit codes are unreadable from a handle
  opened after start.

### Evidence

- Boards: `.artifacts/claude-design/r2/runtime/glass-{normal,enlarged}-{Light,Dark}.png`.
- Raw captures: `claude-glass-r2/runs/r3-matrix/capture/*`.

### Attempt history

Kept, not overwritten:

1. Rim/body criterion failed at edge density .35 (2.67 > 2.53). The decomposition showed edge shading, not
   background regime; density reduced to .20 and re-run with the criterion unchanged → PASS.
2. `r2-matrix-attempt1-ambient-dpi-bug`: blurred ambient art covered only ~2/3 of the scene (effect DPI mismatch).
   Fixed: blurred inputs are created at the scene DPI.
3. `r2-matrix-b-attempt2-lens-extent-bug`: after a rev2 visit, C1 differed from GA1 in the ring (p99 13). Cause:
   changing `BorderThickness` resized `LensHost`, and the event-driven draw used stale extents. Fixed by toggling the
   stroke brush only; C1 is GA1-identical again.
4. `r2-matrix-c-attempt3-request-race`: transient file race in my control helper; bounded retry added.

### Measured cost

Not a performance claim:
- 连续玻璃: one blur pass per scene revision (CPU submit 2–6 ms; GPU time not measured) and one texture of
  source size (13.7 MB at the Gallery's 2600×1320; ~1.5–3 MB at hero size).
- 完全清透: no extra texture and no pass.
- Per frame: ≤ 3 extra texture reads in the lens.

New workload used this revision: about 22 minutes (matrix runs including failed attempts, lifecycle and follow-ups,
accent captures). Cumulative for the assignment: about 36 of the 45-minute budget.

## 4. Candidate identity

`.artifacts/phase7-2b/claude-glass-r2/candidate-d/win-x64/`:

- `TheOrder.WinUI.dll` `82962F0A…A928`
- `TheOrder.OpticalMaterial.dll` `DE94C803…C19B`
- `TheOrder.WinUI.exe` `DAF7FF55…1542`

Launch only through `claude-glass-r2/launch.ps1` (fresh Test profile; advanced OFF by default). Then:
`command.ps1 -Action gallery` · `glass candidate1` · `glass on` · `glass continuous|clear` ·
`glass ambientCover|ambientReference|covers|reference|text|grid|plainLight|plainDark`.

## 5. Integration instructions

Apply `0001-…frosted…` and `0003-…continuous-glass…` from `claude-glass-r2/patch/` (SHA256 `47ac4e9f…`,
`a7ec9a93…`) onto the authoritative tree. Do **not** apply the baseline snapshot `7d74e24`. Patch `0002` is
superseded revision-1 docs.

- Revision 1 stays selectable as a Test-only comparison mode, as the Product Owner asked. It may be removed before
  productization with a follow-up patch; the C1 bit-exact tests do not depend on it.
- Pre-check that the authoritative blob ids match the baseline before applying. If they changed, rebase and re-run §3.

## 6. Limitations

- Product integration is not done: the hero stage from decoded cover pixels at hero size, the polarity computation
  (03 §4.3), and the hero lifecycle owner in Details. These belong to slice 2 and need user approval.
- No ETW allocation-retirement evidence for the new paths; ownership is shown by native counters, readback tests and
  lifecycle probes.
- Live Windows accent change at runtime is not verified (03 §3). Changing the machine accent was not authorized; the
  Test seam simulates it.
- Not exercised: real device loss, other GPUs/drivers, 100/150 % DPI, Narrator, live HC switching.
