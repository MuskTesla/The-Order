# 06 · Implementation results: revision 2 (approved R1–R6)

Status: **implemented in the isolated workspace, awaiting GPT review.** Nothing is merged, pushed, deployed or installed.
Evidence lives in `.artifacts/phase7-2b/claude-ui/` (generated, not committed).

## 1. Identities

| Item | Value |
|---|---|
| Worktree / branch | `D:\Projects\The-Order-worktrees\claude-design-phase7` · `claude/design-phase7` |
| Baseline snapshot (GPT tree, **never integrate**) | `7d74e24` on `ad59c6e` |
| Earlier Claude commits | `f18d609`, `09465c2` (Test-only material variants); `1369f99`, `7cea6a3` (design docs) |
| **Implementation for review** | `7cea6a3..695fc99`: `d0748fe` checkpoint 1 (Shell + Library), `6abb8ed` checkpoint 2 (Details glass), `695fc99` fixes. 24 files, +1521 −664 |
| Patches | `.artifacts/phase7-2b/claude-ui/patch/0001–0003` |
| Test candidate | `.artifacts/phase7-2b/claude-ui/candidate-3/win-x64` (Release, `NativeProductionCandidate=true`, Test profile only) |
| Candidate hashes | exe `FBDF48E3…214E` · WinUI.dll `7FC9035C…1A9E` · OpticalMaterial.dll `4CBDA06A…5E7A` · pri `EB74747C…6C92` |
| Launch | `pwsh -File .artifacts/claude-design/try-candidate.ps1` (fresh synthetic Test profile; Ctrl+Shift+D hides the Test strip) |

## 2. What was built (by requirement)

- **R1: Shell + Library (rev2 basis).**
  - **Shell:** Fluent `TitleBar` (back, pane toggle, icon, library search, Session indicator) with Mica and a NavigationView on Auto thresholds (1008/641).
  - **Keyboard:** Ctrl+F, Alt+←, mouse back.
  - **Library header:** 排序 DropDownButton · 筛选 with badge · 网格/列表 · 选择 · 导入 · ⋯ overflow (below 900).
  - **Grid and list:** filter chips; an adaptive grid with stretched columns; a list table with responsive columns and header sorting.
  - **Interaction:** a single click opens Details. Selection mode is entered by 选择, Ctrl/Shift+click or Ctrl+A, with Shift range, Esc and 完成.
  - **Return:** the scroll anchor and focus are restored on return.
  - Fluent tokens alias theme resources; there is no literal hue anywhere.
- **R2: custom glass.** Two placements, both over backgrounds that the app renders itself from the game's decoded cover pixels (stable, event-driven, no capture of dynamic content):
  1. **Details hero: 完全清透** (`mode3 · C1 23/30/30/.8/.55 · tint α .10/.16 · bound .42/.08 · edge .20`) over a σ18 ambient band.
  2. **Library 「继续游玩」 banner: 连续玻璃** (`mode2 · … · σ12 · tint α .22/.30 · …`) over a σ6 band. It shows only for the unfiltered library outside selection mode, scrolls rigidly as the list header, and is detached (renderer released) when hidden.
  - Everything else uses Fluent materials: Mica window, in-app Acrylic flyouts and save bar, solid cards.
  - No glass is drawn over the scrolling grid, the posters or the editor.
  - Rejected variants (C1清透, 清透边缘+磨砂中心) are not used.
- **R3: text follows artwork.** Mean linear luminance > .30 gives Lift (light plate theme, dark text); otherwise Deepen (dark plate theme, white text). This holds in both app themes, and the plate's `RequestedTheme` also styles its buttons. It is computed once per cover.
- **R4: quick launch.** The context menu 启动游戏 and Ctrl+Enter call `LaunchViewModel.OpenAsync`. They launch only when `CanLaunch` (Ready); otherwise they open Details, where the existing confirm, cancel and choice UI lives. Results show in a polite InfoBar.
- **R5: tray lifecycle.**
  - **No in-app Exit control.** × hides to the tray; the tray menu has 打开/退出 The Order.
  - **Tray unavailable:** × takes the same orderly `RequestExitAsync` (cancellable), so the only window is never hidden.
  - **Session indicator:** the title-bar chip, with a flyout holding pause/resume and 打开详情, replaces the session bar.
- **R6: last played.** `GameSummary.LastPlayedAt` / `HasPlayHistory` are a read-only projection of the stored values; the display reads 从未游玩 / 时间未知 / dates.
- **Settings:** a 材质效果 card (自动 / 高级优先 / 标准 / 关闭透明效果) plus the effective-state line.
- **Live accent:** `UISettings.ColorValuesChanged` updates the Fluent accent brush instances in the Light and Default theme dictionaries and the app tokens. A labelled Test seam drives the same path.

## 3. Deviations and decisions to confirm

1. **Material mode is session-scoped.** `native-ui.json` decodes with `UnmappedMemberHandling.Disallow`, so a new persisted field would make older builds reject the preferences file. A persistent setting needs a coordinated schema version.
2. **Auto vs 高级优先.** For the current (stable-background) placements, they differ only in failure handling: Auto stays Standard for the session after a renderer failure, while 高级优先 retries on the next change.
3. **Hero band.** It is a rounded card (radius 8) rather than fading into the page over its last 40 % (02 §7). This keeps a Fluent card edge and avoids a second blend pass.
4. **Details secondary actions.** 管理本地安装 / 选择本地封面 / 重新识别 / 刷新在线资料 / 移除愿望条目 are in the plate's ⋯ menu; 管理本地安装 is also in the installs card. AutomationIds are kept (some are now MenuFlyoutItems).
5. **The save bar only appears while dirty** (approved spec). 取消修改 when clean (a reload) is therefore no longer offered.
6. **Library search moved to the title bar** (AutomationId `LibrarySearch` kept). Typing on another page returns to the Library.
7. **The retired v9 Library toolbar experiment was removed:** `LibraryPage.Toolbar.cs`, `ToolbarSnapshot.cs` and the `toolbar` / `toolbarScroll` fixtures.
8. **Recovery-state colour** uses `SystemFillColorNeutral`; Fluent has no `SystemFillColorAttention` colour key, and accent is barred for status by an existing test.

## 4. Verification summary (frozen criteria; details in `claude-ui/criteria-*.md` and `results-*.md`)

- **Builds:**
  - Debug and Release (`dotnet publish`) both build with 0 warnings and 0 errors.
  - The native optical regression (pixel-scene primitives) passes.
- **Unit and full suite:**
  - **1789/1792 pass.** The 3 failures are `SafeStorageExperimentTests`, which need `node_modules/electron` (not installed in the isolated worktree).
  - Profile and recovery tests pass once the pinned SDK is linked at `<worktree>/.artifacts/native-phase1/tools/dotnet`.
  - New: `LastPlayedPresentationTests` (12 cases).
- **Real-input interaction:** UIA + `SendInput`, with a foreground and hit-test guard.
  - **I1–I9 pass:** click → Details; Alt+← / back restores query, sort, view, scroll (Δ 0.0004 px at 40 %) and focus; Ctrl/Shift+click selection; Esc; Ctrl+Enter routes to Details when not Ready (no process); context menu; title search.
  - **Shift range** was added afterwards and is **not re-verified with real input**.
- **Tray and window lifecycle:**
  - **L1–L5 pass:**
    - × hides and the renderer is released;
    - restore via single-instance activation re-renders once;
    - ×4 cycles leave a single shell icon (id 1, probed with `NIM_MODIFY`) and no window growth (the extra window was the × tooltip);
    - orderly exit leaves no ghost icon;
    - with the tray unavailable, × takes the orderly exit.
  - **U1 passes:** the draft survives × and restore; exit shows 保留尚未保存的修改？; 继续编辑 cancels and keeps the app and tray; 放弃草稿 exits cleanly, with the library unchanged throughout.
- **Material:**
  - **M1:** the targeted idle check after an explicit mode-switch completion boundary gives **0 draws, 0 scene revisions and 0 frost passes over 12 s** for both placements, on Debug and on candidate-3. The original rev2 Gallery row stays as recorded.
  - **M2:** hide releases (created = released) and restore re-renders exactly once.
  - **M3:** Standard and Opaque release the renderer, and Opaque / Transparency Off remove Mica.
  - **M4:** 20 re-entries change private bytes by −5.2 MiB and handles by −1, with no renderer leak.
- **Accent:** the system accent `0078D4` is read live. A Test-only purple/green simulation propagates to the nav indicator, toggles, accent buttons, hearts and the InfoBar icon in Dark and Light, and the real event restores it.
- **Workload:** new performance/resource runs took ≈ 3.5 min this phase, ≈ 39.5 of the 45-minute allowance in total. GA1 is not repeated or reinterpreted.

## 5. Defects found and fixed during validation
- **Startup:** `SelectedItems.Clear()` in SelectionMode None broke startup recovery.
- **Grid sizing:** a NaN comparison meant the adaptive item width was never applied.
- **Selection:** Ctrl/Shift+click went undetected; pointer-event modifiers are now used.
- **Resource keys:** two missing Fluent colour keys.
- **Diagnostics:** the snapshot crashed on NaN.
- **Banner:** the plate was clipped.
- **XAML:** custom CLR properties can't be set from XAML; stage configuration moved to code.
- **Shared brush re-resolution:** the plate's polarity theme re-resolved shared app brushes, which turned the Dark cards light after the Opaque fallback.
- **Re-parenting:** the banner stage was detached by the Unloaded-after-Loaded ordering.
- **Live accent:** it missed WinUI's "Default" (dark) theme dictionary.

## 6. Limitations / not verified
- **Not exercised:**
  - **Real Windows accent change:** personalization was not changed; only the labelled Test simulation drives the same handler.
  - **OS shutdown/logoff:** the handlers are unchanged and were not exercised.
  - **HC:** checked through policy evidence only; Windows contrast themes were not switched.
  - **150 %/200 % DPI and 150 % text scale:** not run.
  - **Narrow layouts:** captured only at 620 and 900 DIP.
- **No real game was launched.** The synthetic installations are not Ready, so the `Started` path of quick launch is untested.
- **Pages left unchanged:** Discover, Recognition, Import and Installations are only re-skinned through tokens (slice 3 in 04).
- **Recordings:**
  - The GIFs in `claude-ui/recordings/` are window-capture frame sequences: frames are delivered on content change and played at a fixed 100 ms, so the timing is not real.
  - The storyboard is still frames from the real-input run.

## 7. Prompt for GPT: independent review of the implementation

> 你是 The Order 的独立工程评审。Claude 已在隔离工作区 `D:\Projects\The-Order-worktrees\claude-design-phase7`（分支 `claude/design-phase7`）实现经用户批准的修订 2（R1–R6）。请只读评审 `7cea6a3..695fc99`（不要合入 `7d74e24` 基线快照），并核对 `.artifacts/phase7-2b/claude-ui/` 中的冻结标准与结果。
> 1. **生命周期：** 托盘关闭/恢复/退出、托盘不可用时 × 的有序退出、单实例激活、未保存草稿与会话在隐藏和退出时的保留、关机/注销路径未被改变。
> 2. **材质：** 两处玻璃（详情 完全清透、书架“继续游玩” 连续玻璃）只绘制应用自有、稳定的封面像素背景；交换链在离开可视树前释放；隐藏时释放；空闲零绘制；回退模式（标准/不透明/透明效果关闭/高对比度）。
> 3. **交互与数据：** 单击进详情、选择模式、快速启动遵循既有就绪/确认语义、搜索筛选排序结果与基线一致、返回时滚动与焦点恢复、最近游玩为只读投影。
> 4. **偏差：** 逐条检查第 3 节 8 项；特别是材质模式不持久化的理由、AutomationId 改为菜单项的影响、动态强调色修改共享画刷的风险。
> 5. 指出未覆盖项（第 6 节）中你认为合入前必须补的验证。
>
> 只读：不修改该工作区，不运行会写入其目录的构建；需要验证时在你自己的隔离目录进行。
