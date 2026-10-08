# 05 · Liquid Glass Engineering Report — frosted lens body

Status: **LIQUID GLASS CHANGE READY FOR INTEGRATION REVIEW** (Test-only, Gallery-scoped).
This is not a resource-gate PASS, not GA1-equivalent, and not integrated into Library or Details.

## 1. Problem and hypothesis

- **Observed limitation:** C1 can only put text on glass by placing an opaque XAML card inside the lens.
  - Gallery: "文字保持稳定" card. v9 filter: opaque panel inside a 23-DIP ring.
  - Visually this becomes "a frame around a card" (v9 capture; Gallery C1 captures). The ring reads as a bezel, and
    the material does not belong to the content it holds.
- **Capability required by the design:** the artwork-stage plate (01 §4, 03 §4) must carry a title and a launch
  button directly on glass over arbitrary cover art, with guaranteed contrast.
- **Hypothesis:** a second, blurred copy of the same app-owned scene, produced once per scene revision, blended into
  a rounded interior field under a bounded tint, gives:
  - legible text on glass for any artwork, with contrast bounded by tint alpha;
  - C1's clear refracting rim unchanged;
  - no per-frame cost beyond one extra texture read inside the lens;
  - exact C1 output when disabled.

## 2. Change (commit `f18d609`, parent baseline snapshot `7d74e24`)

| File | Change |
|---|---|
| `Materials/Native/OpticalRenderer.cpp` | cbuffer 80 → 96 B: `raster.y` = frost amount, new `frostTint`; `t1` frost texture; rounded body field blend (skipped when amount = 0); `OpticalDrawEx(…, count 20\|24, background)` with range validation; legacy `OpticalDraw`/`Background` copy 20 floats into a zeroed 24 buffer (no over-read); `OpticalSceneFrost(σ)` (σ ≤ 0 retires); `OpticalFrostDiagnostics`; frost invalidated by `SceneBegin` and `Upload`; a frost draw without a valid copy fails closed (`E_UNEXPECTED`). |
| `Materials/Native/GallerySource.h` | Scene target is drawable (`CANNOT_DRAW` removed) so it can be an effect input; one D2D Gaussian effect (border HARD, QUALITY) per device context; frost target wraps the renderer-owned texture; input detached after each pass; `dxguid.lib` pragma. |
| `Materials/OpticalRenderer.cs` | P/Invoke for the new exports; `Draw` accepts 20 or 24 constants; `Frost(σ)`; `FrostEvidence`. |
| `Materials/RefractionGallery.Optical.cs` | 24 constants; frost applied after each scene revision or toggle (never per frame); tint per theme (α .80); evidence fields. |
| `Materials/RefractionGallery.xaml(.cs)` | Test-only `FrostSwitch` (`GalleryFrost`), `frostOn`/`frostOff` test commands; the stable text panel goes transparent only while frost is active and HC is off. |
| `fixtures/optical-source-regression.cpp`, `fixtures/accepted-c1-shader.h` | GPU readback tests (below). The header is a frozen verbatim copy of the accepted C1 shader extracted from `7d74e24`. |

Diff: 8 files, +298 / −25.

Patch for integration: `.artifacts/phase7-2b/claude-frost/patch/0001-feat-material-frosted-stage-plate-body-with-clear-C1.patch`
(SHA256 `52d02ac4…3679e3`).

**Not changed:** C1 constants and profiles, the shader math before the frost branch, the same-device D2D/DirectWrite/WIC
source, the early swapchain release (`ReleaseBeforeLeave`), MaterialPolicy, Library/Toolbar/v9 paths, business,
store, CoverPresenter/cache, virtualization.

## 3. Verification

| Check | Result | Evidence |
|---|---|---|
| Native GPU regression (existing 132 + 160 readbacks) | PASS | `claude-frost/native-regression/` (rebuilt and run by `native/scripts/test-optical-source.ps1`) |
| Frost off = accepted C1, bit-exact (256×128 offscreen, C1 reference profile), with and without the frost copy bound | PASS | same |
| Clear rim pixels identical to C1 when frost on; body = blur·.2 + tint·.8 within 2.5/255; blur is mean-preserving (≤ 3 %) and variance < 50 % | PASS | same |
| 17 passes reuse 1 texture; Begin / Upload / resize invalidate; stale frost draw fails closed; bad count / range / σ rejected; σ = 0 retires all frost resources; re-entry; balanced owner incl. failed-detach retry | PASS | same |
| WinUI Debug build | 0 warnings / 0 errors | `claude-frost/build-debug.log` |
| Targeted managed tests (LensOptics, MaterialResource, PresentationRemediation, MaterialTreeProbe — same filter as TC1) | 47/47 | `claude-frost/tests/targeted.trx` |
| Release publish (`NativeProductionCandidate=true`, same command as prior candidates) | OK | `claude-frost/publish-release.log` |
| Runtime: frost-off vs **frozen GA1 candidate** (accepted C1, Optical DLL `A116AA7C…`), same states (Dark/Light × synthetic covers / authorised reference / plain), stage region | mean 0.000, p99 0, max ≤ 6 (JPEG noise) in all 6 states | `claude-frost/pixel-comparison.json` |
| Runtime: frost-on vs frost-off outside the lens (+16 px margin) | max 0 in all 6 states | same |
| Runtime text contrast on frosted body | 7.98–15.5:1 measured; analytic floor 7.59:1 for any art | `claude-frost/contrast.json` |
| Lifecycle (13 rows, criteria frozen first in `criteria.md`): idle, scene revision, disable, policy solid → restore, resize, leave/re-enter Gallery, hide/show, frost off | all rows meet criteria | `runs/frost-b/lifecycle.jsonl` |
| Exits | normal exit requested; process gone; Test library hash unchanged (frost-a, frost-b, baseline-ga1, audit-a) | `runs/*/exit.json` |
| Frozen GA1 candidate directory untouched by the baseline run | identical file list / size / mtime before and after | `ga1-dir-before.txt`, `ga1-dir-after.txt` |

Measured costs, which are not a performance claim:

- One frost texture equal to the scene source size. The Gallery's 2600×1320 test scene is 13.7 MB.
- Frost pass CPU submission time 2.0–5.6 ms per scene revision (GPU execution time not measured).
- Per frame: +1 texture read inside the body.
- Idle: no draws and no passes over 12 s.

Workload time used: about 14 of 45 minutes (four short runs). No ETW, GA1 repeat or long run.

Iteration recorded: run `frost-a` showed a square-cornered frosted rectangle inside the rounded plate. Cause: bevel
(30) > corner radius (23), so the plate's inner iso-line is square. The body therefore received its own rounded field,
the native tests were re-run and the candidate republished. `frost-a` captures are kept as the counter-example.

## 4. Candidate identity

`.artifacts/phase7-2b/claude-frost/candidate/win-x64/` (my worktree):

- `TheOrder.WinUI.dll` `12E05738…87A3`
- `TheOrder.OpticalMaterial.dll` `D76807BC…23F1`
- `TheOrder.WinUI.exe` `DAA442FE…267F`

Launch only via `claude-frost/launch.ps1` (fresh Test profile via the existing helper; Advanced OFF; frost OFF by
default). Then use `command.ps1 -Action gallery`, `glass candidate1`, `glass on`, `glass frostOn`.

## 5. Integration instructions — exclude the baseline snapshot

- Apply only commit `f18d609` (or the `.patch`) on top of the authoritative working tree. Its parent `7d74e24` is a byte
  copy of that tree and must not be imported.
- Pre-check: the 7 touched files' baseline blob ids must match the authoritative files.
  - `OpticalRenderer.cpp` `354e97c0`
  - `GallerySource.h` `d9d6fecd`
  - `OpticalRenderer.cs` `a8ada049`
  - `RefractionGallery.Optical.cs` `6c5ad09a`
  - `RefractionGallery.xaml` `0d04db30`
  - `RefractionGallery.xaml.cs` `51532606`
  - `optical-source-regression.cpp` `a87af9b0`

  If the authoritative files changed, rebase the patch and re-run §3.

## 6. Not done / limitations

- No ETW allocation-retirement evidence for the frost texture; ownership is shown only by native counters and
  readback tests.
- The stage application (ambient cover scene from decoded cover pixels at stage size, plus an ambient blur step) is
  **not implemented**. Today the Gallery draws images by file path at 1300×660 DIP.
  - Required for Library/Details: `OpticalSceneImageBgra(handle, pixels, w, h, x, y, w, h, fill)` fed from the
    existing image pipeline, and an ambient-blur option.
  - Scope this with the vertical slice; it is a presentation-layer dependency, not a business change.
- Real device loss, other GPUs/drivers, 100/150 % DPI, Narrator and HC switching at runtime were not exercised in this
  round. The policy and fault paths are the existing ones.
- Exit codes were not readable (process handle opened after start); normal exit was requested and the process was
  confirmed gone.
