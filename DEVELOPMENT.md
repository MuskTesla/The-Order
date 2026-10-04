# 开发与维护

## WinUI Phase 3A：隔离离线客户端与验收

当前候选分支 `codex/winui-phase3`，起点为 Phase 2.1 的 `d0702cafe1b6762069d63fe08bcba07b199ca713`。仅 Phase 3A；下面各阶段描述保留为历史证据。Phase 3A checkpoint 后暂停等待 Gate，不能自动进入 Phase 3B。

### 架构与写入边界

| 层 / 位置 | 职责 |
| --- | --- |
| WinUI/Views、MainWindow、Resources/DesignTokens.xaml | 原生三页面、导航/确认框、虚拟化容器、主题资源；无数据访问实现 |
| Presentation/WorkspaceViewModel、PersonalDraft | 草稿、dirty/保存/导航状态、最新请求保护、UI continuation；仅依赖 Application |
| Application/OfflineLibraryClient、OfflineContracts | 统一 capability/result、后台搜索索引、轻量 summary 和按需详情、保存结果解释 |
| Application/PersonalEditing | 既有 transaction kernel 的有限扩展：个人字段 token、最多 1000 ID 的一次批量 mutation |
| Infrastructure/Legacy/LegacyClientTextRules | 使用已验证的 legacy Unicode lower/空白规则；查询/分类标签差分支持 |
| Platform.Windows/IsolatedLibraryFactory、IsolatedUiPreferencesStore | 后台打开已隔离 profile；界面偏好独立存储、目录固定、大小/链接检查、临时文件替换 |

书架仍为 legacy v1 `library.json` / `library.json.bak`，原始大小、schema、完整校验、writer lease、串行事务、原子提交和读取恢复语义不变。主文件已经提交的 warning 不能报告为失败、不能自动重试。个人编辑 token 检查 epoch、条目 generation 和原个人字段，允许无关条目变化，拒绝删除/恢复身份和同条目个人字段过期。批量标签先规范化，再按旧规则移除和追加；不循环执行 N 次整库保存。

`native-ui.json` 是独立的 version 1 偏好 envelope，仅包含 Theme/Layout/Sort/SavedFilters。临时搜索和筛选条件只在内存里，只有“保存筛选”会创建命名项。不得读取真实 Electron settings/credentials/localStorage；localStorage 迁移后置。损坏偏好文件保留，本次外观可用但不覆盖它。偏好队列按顺序写入，关闭等待已经接受的偏好和事务。读取无效库不能当作空库。当前 writer 只支持带 `.the-order-synthetic` 标记并通过原隔离检查的目录。

### 生成与运行

从仓库根目录执行；使用既有锁定 .NET 10.0.401 / Windows App SDK 2.5.1，未增加 NuGet/npm 版本。生成器只接受**尚不存在**的绝对目录，不覆盖已有合成资料。输出不提交。

```powershell
./native/scripts/native.ps1 -Action Restore
./native/scripts/native.ps1 -Action Build -Configuration Debug
./native/scripts/native.ps1 -Action Build -Configuration Release
./native/scripts/native.ps1 -Action Test -Configuration Debug
./native/scripts/native.ps1 -Action Test -Configuration Release

$taskPhase3 = Join-Path (Get-Location) ('.artifacts/phase3-smoke-' + [guid]::NewGuid().ToString('N'))
node native/scripts/generate-offline-library.mjs --out $taskPhase3 --count 10000
$env:THE_ORDER_NATIVE_DIAGNOSTICS = '1'
./native/scripts/native.ps1 -Action Run -Configuration Release -DataDirectory $taskPhase3
Remove-Item Env:THE_ORDER_NATIVE_DIAGNOSTICS
```

分别生成 1000/5000/10000；`--count 0` 为合法空库，`--corrupt` 为损坏输入，`--readonly` 为两条、合计 200k sessions 的合法但不可写资料。路径内的 `Z:\SyntheticOnly` 和 `example.invalid` 都是数据字符串，本阶段不访问它们。诊断开关仅显示 first-container/query 时间和面板容器统计；默认关闭，计数使用 weak table，不保留旧容器。

差分与 Electron 回归：

```powershell
node native/scripts/generate-offline-ui-oracle.mjs
./node_modules/.bin/tsx.cmd --test tests/library.test.ts tests/bulk-library.test.ts tests/metadata-concurrency.test.ts tests/title-matching.test.ts tests/library-cover.test.ts tests/library-local-cover.test.ts tests/library-workspace.test.ts
npm run build
Remove-Item Env:KGM_TEST_EXECUTABLE -ErrorAction SilentlyContinue
node tests/desktop-import.mjs
```

新 oracle 在锁定 Electron 中执行实际 `filterLibraryGames`、`splitLabels`、`LibraryStore.updateGames`，记录四个参考文件 hash；重复生成 SHA-256 相同。旧 transaction/codec oracle 未修改。C# 新测试在 OfflineClientTests / OfflineClientPerformanceTests，包含 51 个 UI differential、草稿、冲突/删除恢复、失败保留、迟到请求、统一只读、损坏偏好、保存/关闭等待、1000 ID 单次持久化、单线程 UI context 和大库查询。性能 collection 不与其他 collection 并行运行。

本轮发布使用独立目录，未替换正式程序：

```powershell
./.artifacts/native-phase1/tools/dotnet/dotnet.exe publish native/TheOrder.WinUI/TheOrder.WinUI.csproj -c Release --no-restore -o .artifacts/native-phase3a/publish/win-x64
```

这是 unpackaged/self-contained 候选目录，必须保留整个目录，不能仅复制 EXE。正常 `native.ps1 -Action Publish` 仍使用既有 `.artifacts/native-phase1/publish/win-x64`；其 `-Published` 也只指向该默认目录。本轮独立发布目录应显式设置 `THE_ORDER_NATIVE_DATA_DIR` 后运行其中 EXE，结束后清除本次环境覆盖。

### 实际结果与证据范围（2026-10-04）

- Locked restore、Debug/Release 全 solution build：0 warnings / 0 errors；两配置全量 C# **349 passed / 0 failed / 0 skipped**（原 270 + 新增 79），原 102 differential / 14 fault-injection 测试全部保留通过。Release publish 成功，发布目录实际启动与正常退出。
- Electron 七个相关单元文件 **130 passed / 0 failed / 0 skipped**；build + desktop import 通过：37 条新增，重复 0 新增 / 37 跳过，个人字段和重启一致，游戏文件保留，renderer errors 0。未宣称运行了整个 npm test 或全部 desktop suite。
- 新 UI 的实际自动化操作通过 Computer Use 的 WinUI accessibility / 窗口截图完成，没有浏览器替代。先前锁屏造成的激活失败与壁纸截图均不计入结果；用户解锁后重新执行。发现并修正 DPI 下初始物理窗口过小、空 Tag 的评分选项未选中；初始大小现按 XamlRoot scale 转成物理像素并限制在当前工作区。窗口缩放/最大化也已观察。
- 真实 smoke：1k Grid/List、多选/详情、输入时磁盘不变、保存后名称/NameIsCustom 落盘、退出重启搜索命中、取消笔记、dirty 导航的继续编辑、System/Light 外观；10k 末尾条目详情、搜索、评分排序、收藏筛选 2500/10000、显式保存命名筛选、清除临时条件、切换另一详情、关闭时选择保存并继续，最终两处笔记落盘且进程退出。发布包额外批量更新两条标签，Case/case 分别保留且同大小写重复项去重。
- ValidReadOnly 的两条/200k sessions 主库能查看；批量、名称、状态、评分、收藏、分类、标签、笔记、保存全部禁用。损坏库显示读取错误/尚未加载书架；合法空库显示 0/0 和空状态。
- 已在进行中的事务关闭等待、commit 后 warning/取消、IO failure 等精确时序由生产 persistence probe + 单线程 context 及 ViewModel gate 测试覆盖。实际 UI smoke 覆盖“保存完成后关闭”和“关闭时保存草稿”；不声称人工输入准确落在某个 durable IO 指令之间。没有 UI 自动重试路径。

**最终 Release 发布包观察**（无图片下载/解码；本机一次启动、一次大幅滚动，诊断开启）：

| 合成条目 | 首个容器回调 ms | 首屏 / 滚动后面板容器 | 工作集 MiB | Private MiB |
| --- | --- | --- | --- | --- |
| 1,000 | 937 | 45 / 51 | 306.1 | 203.6 |
| 5,000 | 1563 | 45 / 51 | 335.5 | 236.9 |
| 10,000 | 2126 | 45 / 51 | 353.7 | 256.5 |

时间从 LibraryPage 构造至首个非回收 ContainerContentChanging 回调，**不是完整冷启动或首帧像素呈现测量**。容器值为 ItemsPanelRoot.Children.Count（包含缓冲/池）；有首屏与滚动截图，10k 没有创建 10k 个完整可视元素。先前 Debug smoke 还验证列表 1k/5k/10k 分别约 14–32/32/28 个面板容器，窗口大小不同不能直接横比。累计 containers 计的是“曾创建过”，不同查询/模式切换会增加，不等于同时存活。没有帧时间/FPS/长时间泄漏测试，不把单次 working set 作为内存上限。

布局按 [Microsoft GridView/ListView 虚拟化指导](https://learn.microsoft.com/en-us/windows/apps/develop/performance/optimize-gridview-and-listview)：有限 viewport、保留默认 ItemsWrapGrid/ItemsStackPanel、无外层整页 ScrollViewer，inactive view 的 ItemsSource 为 null。窗口尺寸换算参考 [Microsoft 窗口管理](https://learn.microsoft.com/zh-cn/windows/apps/develop/ui/manage-app-windows)。

**Application 微基准**使用另一组更小的、无 metadata 的合成 entry，每项预热 1 次 + 测量 3 次；仅计后台查询、不计 120 ms 防抖、XAML 渲染或图片。它与真实 UI 表是不同指标。

| 条目 | 打开库 ms（单次） | 建索引 ms（单次） | 搜索 median ms | 评分排序 median ms | 组合筛选 median ms |
| --- | --- | --- | --- | --- | --- |
| 1,000 | 21.9 | 3.2 | 0.21 | 0.42 | 0.13 |
| 5,000 | 87.5 | 22.4 | 0.79 | 2.81 | 0.40 |
| 10,000 | 179.7 | 41.9 | 0.50 | 4.94 | 0.41 |

证据目录 `.artifacts/native-phase3a/`（不提交）：`restore.log`、`debug-build.log`、`release-build.log`、`debug-full.trx`、`release-full.trx`、`publish.log`、`electron-unit.log`、`electron-build.log`、`electron-import.log`、`oracle-repeat.json`；`ui-*.txt/png` 为真实交互、只读/损坏/空库；`published-{1000,5000,10000}-{initial,scroll}.txt/png` 与 `published-memory-*.json` 为最终发布包数据；`ui-close-persistence.json`、`published-batch.json` 为合成文件回读。所有测试窗口已正常关闭。复验应从新生成目录开始，不把本次编辑过的 profile 当原始基准。

**Gate 剩余边界**：Phase 3A 尚待独立 review，不自行宣告 Phase 3B GO。长时间运行、全 Unicode/其他 ICU/NLS 版本比较、全屏幕尺寸与无障碍、真实用户库、断电/网络盘、干净机器安装/签名/升级未验收。图片仍是占位，数据中的本地 cover 未当作缓存删除；安装、文件系统 identity/preflight、扫描/导入、ImageService/ThumbnailService 都属于 Phase 3B。通过 Gate 前不实现，不运行真实游戏，不接入 API/凭据、匹配、SQLite 或 Liquid Glass。

## WinUI Phase 2.1：Gate 整改与验证边界

本轮从 `codex/winui-phase2` / `f495d06c6b3b6d8dc2b6125405490876b5d7fab1` 整改，不新增架构层或依赖，不允许正式 profile / 凭据，也没有 Phase 3 授权。以下规则替代旧 Phase 2 中相应的输入预处理和同步释放描述。

| Gate 项 | 生产位置 | 专项回归 |
| --- | --- | --- |
| B1 | Application/Transactions.cs 的 LibraryAccess、Open、Mutate；Infrastructure/LegacyLibraryCodec.cs 的 Inspect/Decode；LegacyLibraryReader.ParseCanonical | ValidLargeMainRemainsReadOnlyNeverFallsBackOrWrites（旧有效、损坏、无备份三种）；ValidWritableAndInvalidInputAreDistinct（两种） |
| B2 | TransactionalLibrary.Open 默认 Now：TimeProvider → UTC → CultureInfo.InvariantCulture；Legacy codec/validation 日期解析均显式 invariant | DefaultProductionTimestampIsUtcInvariantAcrossCalendars（5 种 culture，含 Buddhist / UmAlQura） |
| M1 | TransactionalLibrary.Enqueue / ShutdownAsync / DisposeAsync；IsolatedLibraryPersistence 内部 await；WinUI App.Closing / NativeHost.DisposeAsync | SingleThreadUiCanCloseWhileKernelDrains（7 种状态）；CancellationDuringCandidateThenShutdownDoesNotPersist；UiEntryPointsDoNotNormalizeOrDecodeOnCaller（3 个入口） |
| M2 | TransactionalLibrary.LinkCoreAsync 的 sessionIndices | EightyThousandSessionMergePreservesOrderTotalsAndLimits（6 种）；真实 Electron 新增 partial / reverse / equal-seconds whole-source / duplicate-input 差分 |
| 异步 token | CommitLinkAsync 必填 RequestToken；FinishRequestAsync / 取消登记和退休清理 | AsyncCommitRequiresTokenAndRejectsDeletedThenRestoredId；CancelledRequestEventuallyClearsPendingWithoutCancellingNewerRequest（3 种） |

**输入有效性与可写能力**：外部输入始终先检查 32 MiB，再做完整 schema 校验。只有 codec 自身生成的规范化 JSON 可走 internal ParseCanonical（完整校验仍保留），输出字节上限单独由 Encode 执行。InvalidInput 才属于损坏输入；ValidReadOnly 不读旧备份，构造不再 Encode，Snapshot.Access/ReadOnlyReason 可供未来 UI 展示。只读 mutation 在调用 operation 前拒绝，主/备份字节保留。旧 unknown-field 白名单没有为大小边界作特例，未删除/压缩 session 或改写用户内容来绕过限制。真实 Electron validateLibraryBackup 已验证同一形状的 200k sessions 紧凑输入合法；具体字节记录在 oracle 的 largeInputReference。

**线程与关闭协议**：UI 只发起操作并 await。admission lock 内同步登记 SemaphoreSlim.WaitAsync 保持接单顺序，随后 Task.Run 执行业务和 IO，不在 UI 线程创建 candidate、规范化 metadata 或解析导入。导入 ReadOnlyMemory 的调用方必须在任务结束前保持缓冲区内容稳定，工作线程先复制完整输入再解析/验证。不要把 cancellation 当作 stale token，也不要从 UI 同步等待 transaction。

| 关闭时状态 | 行为 |
| --- | --- |
| 空闲 | 异步释放资源；重复关闭幂等 |
| 已接收但排队 | 停止新增接单，已有工作继续按序；已取消的排队工作跳过 |
| 正在构造/校验 candidate | 允许完成验证，在进入持久化前最后检查取消；未持久化则无内存/磁盘提交 |
| 备份或主文件持久化中 | 不把关闭/取消传入 durable IO；完成最小安全提交或按原 IO 故障语义失败 |
| 主文件已替换 | 必须发布准备好的内存 snapshot；不能报告回滚 |
| 提交后检查警告 | 返回 committed-with-warning，再完成 drain/release |

生产窗口在 AppWindow.Closing 中 Cancel=true，await provider.DisposeAsync 后通过 DispatcherQueue 发起真正关闭，保持关闭等待期间 dispatcher 存活。容器尚未注册 writer；“保存中关闭”证据来自实际生产内核/文件适配器置于真实单线程 SynchronizationContext 的测试，不等于已迁移完整 WinUI 保存命令。同步 Dispose 仅为无事务的旧调用保留，忙时抛明确错误，不能用来等待。

**异步结果**：即时明确点击采用 LinkAsync；远程响应/后台识别/延迟结果采用 CommitLinkAsync（无 token 默认值），token 携带 epoch/generation/request/revision。BeginRequestAsync 可用请求 cancellation 安排 Pending 清理；显式 FinishRequestAsync 的 cleanup 不受已取消 token 阻止。CommitLinkAsync 在完成、失败、取消后清理同一 token，较新请求不会被旧清理取消。关闭中的剩余登记在排空后清除。

**性能方法**：WarmedMultiRunOrdinaryTransactionAndMergeGrowth 独占测试 collection，10k 普通条目及 20k/40k/80k 每侧 session 合并，各 1 轮预热 + 3 轮测量。计时覆盖发起事务、候选、完整验证、备份/主文件 IO 和提交，不包括打开库。报告 median/min/max；三个样本不足以宣称稳定 P95。仅检查字典循环结构并观察增长，不设置机器相关绝对毫秒门槛；不借此优化普通整库事务。

复验使用下方历史 Phase 2 的同一组命令。当前日志和两配置 TRX 保存到 `.artifacts/native-phase2.1/`（不提交）；测试工具仍默认覆盖 `.artifacts/native-phase1/test-results/compatibility.trx`，每次结束后复制为本阶段 `debug-tests.trx` / `release-tests.trx`。不升级 NuGet/npm 依赖，也不替换正式程序。

### Phase 2.1 实际结果（2026-10-04）

- Locked restore 成功；Debug / Release 全 solution build 均 **0 warnings / 0 errors**。完整 C# suite 两配置均 **270 passed / 0 failed / 0 skipped**，较 Phase 2 的 234 新增 **36**：32 个专项实例 + 4 个真实 Electron 小型差分。
- 原 98 个 differential 全保留并通过，现共 **102**（54 mutation + 5 lifecycle + 43 codec）；原 **14** 个 FaultInjection 标记用例也全部通过。Electron oracle 重新生成且哈希与测试时 fixture 一致：54 mutation（41 接受 / 13 预期拒绝）全部验证旧端重启，另有 5 lifecycle / 5 recovery / 43 codec 参考。大主库参考为 **20,978,558 bytes compact / 34,178,884 bytes pretty**，Electron 校验合法；C# 三种备份组合均加载当前 2 条/200k sessions，拒绝修改且主/备份逐字节不变。
- 默认时间实测 en-US / zh-CN / ja-JP（Gregorian）、th-TH（ThaiBuddhist）、ar-SA（UmAlQura）均为 `2026-10-04T00:00:00.000Z`。单线程 UI context 专项同时断言 codec/probe 在非 UI 线程、SynchronizationContext=null；覆盖空闲、排队、candidate、取消、持久化、已替换 warning，关闭后 writer lease 释放、重开成功、disk/snapshot 一致。
- Electron 6 个相关单元文件 **120/0/0**；Electron build 与真实 desktop-import 通过：37 新增、重复 0 新增/37 跳过、个人字段/重启一致、游戏文件保留、renderer errors 0。原生 Debug/Release EXE 均实际启动至窗口，通过正常窗口关闭消息退出（exit 0），合成资料 SHA-256 未变。此 smoke 只验证启动/关闭，不代表完整 UI 功能或发布包验收。

| 完整事务（每项 warmup 1 + measured 3） | Debug median [min, max] ms | Release median [min, max] ms |
| --- | --- | --- |
| 10k 普通条目修改 | 228.8 [224.9, 236.2] | 182.1 [173.0, 183.6] |
| 20k + 20k 逆序完全重叠合并 | 200.4 [198.2, 228.8] | 160.1 [149.8, 211.1] |
| 40k + 40k 逆序完全重叠合并 | 391.3 [378.2, 412.6] | 294.3 [290.1, 332.4] |
| 80k + 80k 逆序完全重叠合并 | 717.0 [704.3, 748.9] | 604.7 [562.4, 620.5] |

本机完整事务增长与线性 session 索引一致；这些是合成、无并行测试干扰的有限样本，不能当作所有机器/真实库的 SLA，也不宜直接与历史单次并行套件数字比较。80k+80k 不相交的 160k 候选按原 100k 上限整笔拒绝；部分重叠合并到 100k 成功，逆序、重复 ID、startedAt 冲突和历史累计保持契约。大库整库解析/重建仍有 CPU 与内存成本，ValidReadOnly 不承诺可编辑。

证据：`restore.log`、`debug-build.log`、`release-build.log`、`debug-test.log`、`release-test.log`、两配置 TRX、`oracle.log`、`electron-unit.log`、`electron-build.log`、`electron-import.json`、`native-close.json`，均在 `.artifacts/native-phase2.1/`。B1/B2/M1/M2 自检有生产位置、专项回归和实际通过结果，结论 **PASS FOR PHASE 3（仅表示可交接下一阶段；没有实施授权）**。仍未验证正式用户资料、真实凭据、断电/非 NTFS/网络盘、完整 WinUI 写入集成和干净机器发布；本轮没有 SQLite、完整 UI 功能或用户迁移。checkpoint 后暂停。

## WinUI Phase 2：事务内核约束与复验（历史 checkpoint）

Phase 2 在 `codex/winui-phase2`，基于 Phase 1.1 checkpoint `d988d2b`。它没有将 WinUI 页面连接到 writer。禁止把 `TransactionalLibrary` 的合成验证解释成真实用户迁移已经获准；正式 `%APPDATA%\KunGalManager`、真实 settings/密钥/游戏目录仍不可作为输入。Phase 3 必须另获用户明确指令。

### 实际旧版 oracle 与分层

- `electron/library.ts` 的真实 `mutate`：queue → structuredClone → operation → parseBackup(envelope) → stringify + 32 MiB 检查 → beforeCommit → atomicWrite(backup,lastGood) → atomicWrite(main,candidate) → 生命周期更新 → games/lastGood → clone(operation result)。没有数据库，也没有写后通知总线。
- 旧 atomicWrite 是同目录唯一 `wx` 临时文件，writeFile、sync、close、rename，finally 尝试删本次 temp。initialize 在主文件缺失/损坏且备份有效时会恢复主文件；主文件存在时还先复制 corrupt 文件；两者缺失会写空库；两者不可读拒绝启动。
- 新 Application 层只依赖 `ILibraryCodec` / `ILibraryPersistence`，不依赖 XAML、JSON、Windows API。Domain 仍是 immutable records；Legacy codec 和命名保护在 Infrastructure；文件句柄、隔离与原子替换在 Platform.Windows。`AddInstallationAsync` 是已授权安装**值**的提交入口，不是任意文件路径访问授权，未来目录选择/扫描必须另建受限 filesystem service。
- `native/scripts/generate-transaction-oracle.mjs` 在真实锁定 Electron 的 Node 模式调用上述原类；临时合成数据全部自行创建/清理，不传入 KGM_DATA_DIR 或真实 profile。固定时钟和 UUID 是测试依赖控制；C# add 的 ID 工厂注入 oracle 输出 ID，其余保留/跳过/合并 ID 均直接比较。真实生成的 GUID 唯一性由平台提供，不以两种实现碰巧生成同一个随机数为验收标准。
- JSON 比较检查属性存在性、精确 UTF-16 字符串（包括孤立 surrogate）、数字、数组顺序、操作结果、主/备份和重启结果；不依赖属性排列和空白。write codec 保持 legacy v1 字段，已知嵌套属性按旧白名单映射；未知属性首次显式写入时按旧版丢弃，没有扩展保存框架。不得声称这能无损降级未来客户端的新 schema。

### 事务和保存保证

1. 一个实例内所有写操作和请求意图注册共用串行队列，排队任务执行时才取最新 authoritative snapshot，不能在队列外先基于旧值构造“全库替换”。
2. 业务只改 immutable candidate；先验证完整候选（包括全局路径/ID、首选安装、时长下限、条数和字节上限），再做任何保存。输入备份在入队前完整解析并冻结；失败不能只导入半份。
3. `IsolatedLibraryPersistence` 只允许显式绝对目录+合成标记，复用既有正式目录及父子目录/重解析/硬链接拒绝规则。固定目录祖先句柄，保留 marker 句柄，并持有 `.the-order-writer.lock` 的独占句柄；没有自动创建父目录。锁文件是空的协调文件，打开后保留，不能当成用户数据。
4. 先把 canonical lastGood 保存到 `.bak`，再保存新主文件。每个目标使用本目录独占创建的 GUID temp，写入、`FileStream.Flush(true)`、关闭，然后调用 `MoveFileExW(REPLACE_EXISTING | WRITE_THROUGH)`。没有先删除/截断目标，也不使用跨卷 copy/delete。只清理本次 temp；遗留的其他 temp 不作为恢复来源，也不自动删除。
5. 主替换前任何失败：authoritative 内存仍是旧版，主文件未变，备份最多推进到 lastGood。主替换已成功后的检查异常：发布新 snapshot，返回 warning；不能抛“保存失败”却让磁盘保存了新版。备份替换后的检查 warning 也保留在返回结果中。
6. 这保证了本机测试覆盖的正常退出和可预期 I/O 失败一致性，不宣称所有文件系统或断电绝对原子。微软的 [MoveFileExW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw) 和 [Flush(true)](https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream.flush?view=net-10.0) 文档不能被解释成目录项/硬件缓存的全面断电保证。未测试 SMB、云同步目录、不同 ACL 继承策略或硬件故障。Electron 不遵循 native writer lock，禁止双写。

### 恢复矩阵：选库一致，打开时不自愈

| 主文件 | 备份 | Native 打开结果 | 文件动作 |
| --- | --- | --- | --- |
| 有效（包括真空库） | 有效/损坏 | Main，选主库 | 不修改主/备份 |
| 损坏 | 有效 | Backup，明确报告恢复来源 | 不修改主/备份 |
| 缺失 | 有效 | Backup | 不创建主文件 |
| 损坏 | 损坏/缺失 | 错误 | 不当作空库 |
| 缺失 | 损坏 | 错误 | 不当作空库 |
| 缺失 | 缺失 | New，真正新库 | 显式 mutation 前不创建主/备份 |

这是明确偏离旧 initialize 自动写回的设计，以满足本阶段“读取不偷偷自愈”的要求。恢复后的第一次显式 mutation 会以已恢复的有效内容更新 backup，再替换主文件；当前不另外生成旧 Electron 的 `library.corrupt-*` 副本，所以需要保留损坏原件的诊断工作必须先在调用者授权范围内另行保存。没有 UI 自动保存恢复状态。

### 合并字段表（source 主动关联到 target 的作品 ID）

| 字段 | 与旧版一致的规则 |
| --- | --- |
| 游戏 ID / 加入时间 / 顺序 | 保留 source ID、addedAt；删除 target，其他条目相对顺序不变 |
| installations | source 后接 target，不悄悄去重；重复安装 ID 或全局目录导致全事务拒绝 |
| preferredInstallationId | source 非 null 优先，否则 target；完整校验必须指向现存安装 |
| categories / customTags | source 顺序后追加 target 未出现项；区分大小写 |
| name / nameIsCustom | 缺省来源标记视为可能个人名；显式版本/章节名、与原 metadata.title 不同的名称受保护；否则采用新资料标题；preserveName/preserveMergedName 分别遵循旧优先级 |
| status | 仅 source 为 planned 时采用 target |
| personalRating / favorite | source rating 非 null 优先，0 不能视为缺失；favorite 取 OR |
| notes | target 非空且不同才以两个换行追加；相同不重复 |
| sessions | source 顺序，追加新 ID；同 ID 不同 startedAt 拒绝；同开始时间保留 seconds 较大的完整 session，等长保留 source |
| totalPlaySeconds | 两侧累计之和减重叠 session 的较小 seconds；不从 session 重新计算历史累计 |
| playCount | 两侧累计之和减两侧 seconds 都大于 0 的重复 session 数 |
| lastPlayedAt | 取较新的非 null ISO 时间 |
| metadata | 使用本次规范化资料，只按明确相同 work ID 合并，名称不是身份 |
| cover | 删 source 不再对应 work ID 的资料封面；source 无个人封面时才继承 target localCover 或对应作品 coverChoice |
| cover display | source 缺省时才继承；source 显式 null 不等于缺省 |

### 取消、生命周期与尚未移植的边界

- cancellation 只决定是否继续排队/进入提交，token 决定结果是否仍有权限提交，两者不得混为一谈。进入备份持久化前最后检查取消；之后完成提交并用 `CancellationAfterCommit` 回报。主替换前真实 IO 失败仍是失败，即使同时收到取消。
- `BeginRequestAsync` 在同一队列中登记最新意图。token 带 epoch、generation、request、revision；旧结果、跨实例 token、rename/metadata/installations 改变前 token、delete→restore 同 ID 前 token 全部拒绝。个人 notes/rating/favorite 等不改变资料证据身份。每次 link 即使内容相同也消费原 generation。
- 合并目标的 pending request 或晚于 source token 的 revision 阻止合并；较新请求即使失败/finish，其意图 revision 仍有效。当前只迁移代表性的单一显式请求通道，**没有**完整移植 automatic-batch sibling exception、refresh/association/cover 多通道；后续迁移 matching 时必须以原 `metadata-concurrency.test.ts` 的相应场景补齐，不能宣称本内核已经覆盖完整识别协议。
- append/restore 保留旧 ID、顺序、重复 ID/work ID/路径跳过和个人数据；目录键为 legacy Windows lexical identity。本阶段不访问安装指向的文件系统，因此不等价于旧 importBackup 对真实 junction/目录别名的 `realpath` 解析。未来正式导入需要独立受限预检。新增 remove-installation 只删记录，移除首选时置 null，不自动猜选另一个安装，不删游戏文件。
- 命名保护仅移植旧 predicate 所需规则，没有引入完整匹配引擎。任意手工非标准 Date.parse/WHATWG URL 输入、完整图像解码/EXIF 和所有未来 Unicode normalization 边界未证明全等；当前固定导出数据与列出的 oracle 才是已验证范围。

### 实际复验命令与证据

```powershell
# 从仓库根目录运行。已装锁定 npm 依赖与 portable SDK，无需升级包。
node native/scripts/generate-transaction-oracle.mjs
& ./native/scripts/native.ps1 -Action Restore
& ./native/scripts/native.ps1 -Action Build -Configuration Debug
& ./native/scripts/native.ps1 -Action Build -Configuration Release
& ./native/scripts/native.ps1 -Action Test -Configuration Debug
& ./native/scripts/native.ps1 -Action Test -Configuration Release
& ./node_modules/.bin/tsx.cmd --test tests/library.test.ts tests/bulk-library.test.ts tests/metadata-concurrency.test.ts tests/title-matching.test.ts tests/library-cover.test.ts tests/library-local-cover.test.ts
Remove-Item Env:KGM_TEST_EXECUTABLE -ErrorAction SilentlyContinue
npm run build
node tests/desktop-import.mjs
```

`native.ps1 Test` 默认覆盖 `.artifacts/native-phase1/test-results/compatibility.trx`，每种配置完成后需复制到自己的证据目录；日志输出路径请使用绝对路径，避免脚本内部 Push-Location 导致相对路径变化。Phase 2 已保存 `.artifacts/native-phase2/{restore.log,debug-build.log,release-build.log,debug-tests.trx,release-tests.trx,electron-unit.log,electron-build.log,electron-import.json}`。上述 native 两配置各 234/0/0；比 Phase 1.1 新增 150，含 98 个差分、14 个标记故障注入测试；Electron 单元 120/0/0，desktop import 37 条与重启校验全部通过。生成 fixture 是可提交的虚构参考；TRX、bin/obj、SDK、临时凭据均不是维护源码。

性能测试有 10k 简化 entries 和单条 10k sessions 两种形状。前者 JSON 5,019,006 字节，本机 Debug/Release 完整套件内事务约 577/703 ms；后者 1,759,515 字节，约 299/334 ms。进程累计分配观测约 198/185 MB，受同进程并行测试影响，只用于量级观察。当前候选共享不可变记录，但验证会重建整个库，约三次序列化/两次 parse，成本随总 JSON/session 历史增长；32 MiB 是硬上限。这是后台事务和未来 SQLite 评估依据，不是大库 UI 性能验收，也没有以优化为理由削弱验证或迁入 SQLite。

## 项目边界

这是 The Order 的长期源码仓库。Windows 正式工作目录为 `D:\Projects\The-Order`；Claude Code、终端和编辑器都从这里开始工作。

维护 `src/`、`electron/`、`public/`、`data/`、`scripts/`、`tests/`、项目配置、锁文件及许可证。`node_modules/`、`dist/`、`dist-electron/`、`release/` 和 `.artifacts/` 均为本机生成内容，不提交 Git。

历史发布 ZIP、界面截图、验证报告、旧交付说明和候选源码保留在原工作区中，不作为本仓库的开发输入。

## 技术栈与资源

- Electron 桌面主进程与 preload：`electron/`，TypeScript，esbuild 生成 CommonJS 文件。
- Vue 3、KunUI、Tailwind CSS 与 Vite：`src/`，浏览器入口为 `index.html`。
- `package-lock.json` 固定依赖版本，使用 Node.js 24 与 npm 安装。
- `public/` 保存应用图标及浏览器预览资料，Vite 将其复制到 `dist/`。
- `data/title-index.json.gz` 是运行时所需的公开标题索引，连同授权说明一起版本管理。
- Python 仅用于手动重建标题索引，普通构建和运行不需要 Python。重建命令为 `python scripts/build-title-index.py <vn_mapper.db>`；外部数据库不提交。

## 安装、启动与构建

在仓库根目录执行：

```powershell
npm ci
npm run dev
```

`npm run dev` 同时启动 Vite 与 Electron；`npm run dev:ui` 仅启动浏览器预览。

```powershell
npm test
npm run build
npm start
npm run package
```

`npm start` 使用已经构建的 Electron 与前端文件，首次执行前需运行 `npm run build`。`npm run package` 包含类型检查和构建，生成 `release\win-unpacked\The Order.exe`；运行时保留完整 `win-unpacked` 目录。

## 应用数据与隔离验证

正式应用继续使用 `%APPDATA%\KunGalManager` 保存书架、设置及游玩记录。搬迁源码不会改变这一路径。API 密钥由 Windows 当前用户加密保存，不属于项目源码。

日常 `npm run dev`、`npm start` 和正式 EXE 默认会使用正式应用数据。验证、调试或尝试修改时，先选择独立目录：

```powershell
$env:KGM_DATA_DIR = Join-Path $env:TEMP 'the-order-development-profile'
npm run dev
# 完成并退出程序后恢复默认数据目录：
Remove-Item Env:KGM_DATA_DIR
```

不要把真实 API 密钥写入源码、命令参数或提交。需要连接服务时，在应用设置中填写。`.env`、加密凭据、个人 profile 和本地备份已由 `.gitignore` 排除。

第六轮三套界面候选使用独立的六条虚构书架：

```powershell
& 'D:\Projects\The-Order\scripts\start-iteration-candidate.ps1' -Iteration 06
```

程序为`.artifacts/iteration-06/candidate/win-unpacked/The Order.exe`，数据固定为`.artifacts/iteration-06/trial-profile`。在「偏好设置 → 界面风格」选择经典书架、Windows或Liquid Glass并保存；明暗主题独立。没有预置密钥，不复制正式或旧候选资料，重复启动保留自己的试用修改。第四、第五轮对应`-Iteration 04`、`-Iteration 05`，继续保留独立程序与数据。

本轮重建先`npm run build`，再`npm exec --offline -- electron-builder --win dir --config.directories.output=.artifacts/iteration-06/candidate`；不要用会覆盖原`release/`的默认打包命令。实际生产界面回归为`node tests/desktop-appearance-styles.mjs --executable '.artifacts/iteration-06/candidate/win-unpacked/The Order.exe'`，真实像素折射检查为`node tests/desktop-glass-optics.mjs --executable '.artifacts/iteration-06/candidate/win-unpacked/The Order.exe'`。两者各自创建临时合成profile、阻断HTTP/S，不需要密钥；按顺序运行，避免原生窗口焦点互相影响。后者用测试图案暂时替换工具栏背后的内容，是光学验证用例，不是产品外观截图。准确版本、图像索引及限制见[第六轮记录](docs/ai/ITERATION.md#第六轮三套可切换界面2026-10-04)。

夜间最终候选使用独立打包目录及六条虚构书架，启动命令为：

```powershell
& 'D:\Projects\The-Order\scripts\start-overnight-candidate.ps1'
```

程序位于`.artifacts/overnight-2026-10-04/candidate/win-unpacked/The Order.exe`，资料固定为`.artifacts/overnight-2026-10-04/trial-profile`。入口保持试用修改，未知或不完整目录拒绝写入；首次生成依赖本仓库已有Node/Electron，使用真实codec编码原创PNG/JPEG。未预置密钥，游玩记录明确为模拟，没有游戏EXE。不要直接双击候选EXE来做隔离试用。候选提交、正常入口检查及全部夜间交付见[OVERNIGHT](docs/ai/OVERNIGHT.md#晨间交付)。

显式隔离打包命令为`node node_modules/electron-builder/cli.js --win dir --config.directories.output=.artifacts/overnight-2026-10-04/candidate`，应先构建；默认`npm run package`仍会写原`release/`。夜间候选已打包，无需试用前再执行。生产包烟测为`node tests/packaged-overnight.mjs`，需先取得上述夜间writer guard并将其token设入`KGM_OVERNIGHT_GUARD_TOKEN`；它只用临时合成profile，不应在用户试用实例运行时同时执行。

睡前第三轮候选继续保留原程序和独立虚构书架，启动命令为：

```powershell
& 'D:\Projects\The-Order\scripts\start-iteration-candidate.ps1' -Iteration 03
```

程序位于 `.artifacts/iteration-03/candidate/win-unpacked/The Order.exe`，资料固定为 `.artifacts/iteration-03/trial-profile`。启动脚本保留已有试用记录，不复制正式书架或上一轮试用资料。`synthetic-photos/` 内有原创合成 PNG 可用于本地封面选择。具体提交、截图和验证见 [迭代记录](docs/ai/ITERATION.md#第三轮全局封面偏好本地图片与栏目装饰2026-10-03)。

本地封面仅在桌面版通过文件选择器读取静态 PNG/JPG；保存有界 JPEG 副本，不引用原文件路径，也不上传。照片副本随书架 JSON 备份保存，全局展示偏好属于设置，不在书架备份中。新程序兼容旧 version 1 备份；旧候选可能不认识新增照片或“跟随全局”字段，回退应使用旧版本对应的备份，不能视作无损逆向迁移。

## 回归验证

```powershell
npm test
npm run test:desktop
npm run package
npm run test:packaged
```

桌面回归使用临时独立 profile、合成游戏和模拟 API 响应。清除旧的 `KGM_TEST_EXECUTABLE` 环境变量，确保测试启动当前项目构建的程序。界面截图与测试报告写入 `.artifacts/`。

`tests/private-library-compat.mjs` 是可选的个人书架兼容检查工具，不属于默认测试链。必须显式提供 `--allow-private-profile --source-data-dir <绝对数据目录>` 才会读取所选数据，清除复制设置中的密钥，并在独立临时目录中只读验证。普通迁移与开发验证不需要这个工具，也不会运行它。

## WinUI 3 并行候选（Phase 1）

`native/TheOrder.slnx` 包含六个项目，不替代现有 npm 工程。Presentation 在 WinUI 项目内部；Domain 无平台/序列化依赖，Application 定义读取与设置能力，Infrastructure 做旧 DTO/校验/映射，Windows 适配器负责安全打开文件，WinUI 组合 DI、环境配置与 Debug 日志。无 Service Locator、数据库、API 客户端、游戏扫描/启动或高级效果实现。主题仅本次运行有效。

工具链：.NET SDK 10.0.401（`native/global.json` 禁止自动换 SDK）、Windows App SDK 2.5.1、WinUI 3 子包 2.3.9；所有 NuGet 项目有提交的锁文件。目标 x64 / `net10.0-windows10.0.26100.0`，最低 Windows 11 build 22000。本机 Build Tools 18.4 与 Windows SDK 10.0.26100 已存在；实际 C# CLI 构建使用 SDK/NuGet 工具，无需额外安装 VS/MAUI 工作负载。

从仓库根执行（安装 SDK 步骤只在缺少时使用）：

```powershell
# 官方精确版本 ZIP，SHA-512 校验，解压到 .artifacts/native-phase1/tools/dotnet；不修改系统 PATH。
& ./native/scripts/native.ps1 -Action Restore -InstallSdk
# 已有本地 SDK 时：
& ./native/scripts/native.ps1 -Action Restore
& ./native/scripts/native.ps1 -Action Build -Configuration Debug
& ./native/scripts/native.ps1 -Action Test -Configuration Debug
& ./native/scripts/native.ps1 -Action Run
& ./native/scripts/native.ps1 -Action Build -Configuration Release
& ./native/scripts/native.ps1 -Action Publish
& ./native/scripts/native.ps1 -Action Run -Published
```

构建/发布前先关闭相应候选实例，否则 Windows 会锁住 EXE；脚本不会杀掉用户窗口。也可传 `-DotnetPath <官方 SDK 的 dotnet.exe>`。运行脚本默认选 `native/fixtures/normal`；直接双击 EXE 时若未显式设置 `THE_ORDER_NATIVE_DATA_DIR`，界面会报告未指定目录，绝不使用 `KGM_DATA_DIR` 或正式 `%APPDATA%\KunGalManager` 回退。

候选只接受绝对资料目录，且必须含 `.the-order-synthetic`，内容精确为 `the-order-native-phase1-synthetic-v1`（允许末尾换行）。标记仅用于合成资料。不能给真实库加标记来绕过本阶段边界。拒绝正式目录及其父子目录、路径重解析和多硬链接，按已验证文件句柄有界读取；不存在或损坏时不写空库、不读取 `.bak` 自动恢复、不创建配置文件。

错误/空库实际窗口验证：

```powershell
& ./native/scripts/native.ps1 -Action Run -Published -DataDirectory (Join-Path $PWD 'native/fixtures/corrupt')
& ./native/scripts/native.ps1 -Action Run -Published -DataDirectory (Join-Path $PWD 'native/fixtures/empty')
```

关闭前一个窗口再运行下一项。正常库应显示 3 条记录；损坏库必须显示错误且不能显示成功空库；空库必须明确已成功读取。导航到设置切换浅色/深色后返回，窗口右上角关闭或 Alt+F4 应退出进程。测试前后核对 fixture 哈希，无写回。`node native/scripts/generate-fixtures.mjs` 可重建确定性虚构样本及指纹向量；它仅使用仓库源码/合成输入，不接受私库路径。图像样本是用于结构校验的 header-only JPEG，不代表原生 codec 验证。

测试含正常/空/多安装/自定义名称/0 与 null/缺项与 false/大字符串 ID/历史累计时长/Base64/格式版本错误/损坏截断、目录隔离、文件占用和硬链接等。safeStorage 实验只在运行时生成虚构随机秘密，在临时 profile 执行当前 Electron；密文与摘要通过管道传递，无明文落盘或日志。临时合成 profile 保留在 `%TEMP%\the-order-secret-probe-*`。实验表明密文依赖旧 profile `Local State` 的封装主密钥，不能只复制 settings.json；未来生产迁移需要独立适配和重新确认，缺少所需环境时重新填写凭据。官方实现参考 [Electron 44.5.1 safeStorage](https://github.com/electron/electron/blob/v44.5.1/shell/browser/api/electron_api_safe_storage.cc) 与 [同步加密后端补丁](https://github.com/electron/electron/blob/v44.5.1/patches/chromium/revert_oscrypt_remove_sync_backend.patch)。

发布目录 `.artifacts/native-phase1/publish/win-x64/` 必须整体保留，包括 EXE、.NET 10.0.12、Windows App Runtime 2.5.1、WinUI、本应用 `.pri` / `.xbf` 及 Views 子目录。项目显式声明生成资源的发布输入，遗漏 PRI 会使发布失败。当前不裁剪、不 AOT、不打单文件、不签名、不注册 MSIX，不替换 `release/win-unpacked`。本机真实运行确认 CLR / WinUI / App Runtime 从候选目录加载；`MSVCP140.dll`、`VCRUNTIME140.dll`、`VCRUNTIME140_1.dll` 仍使用系统已安装的 VC++ runtime，因此不能宣称可在完全干净的 Windows 上零前置运行。

验证证据：`.artifacts/native-phase1/test-results/compatibility.trx`、`loaded-runtime-modules.json`、`fixture-hashes.json`、`ui-observations.json`。Debug 与 Release 构建通过，39 项测试通过；原生导航/主题/错误/退出及发布目录启动已实测。干净 VM、ARM64、高对比/读屏完整验收、大型库帧时间、完整 JPEG/EXIF、真实用户凭据和资料迁移未验证。

Electron 保持原入口：本轮 `npm run build` 与 `node tests/packaged-b-smoke.mjs` 通过。`node tests/desktop-import.mjs` 在第76行等待 compact grid 中不存在的启动按钮超时，之前导入37条的断言已通过；属于本次观察到的旧回归脚本/界面不一致，未作范围外修复。

### Phase 1.1 兼容整改与复验

Phase 1.1 在 `775450e` 上修复 Gate 的两个 MAJOR 和三个 MINOR。旧 JSON 的字符串/整数例外仅存在于 `LegacyJson`；验证与 DTO 使用同一规则，保留孤立 UTF-16 单元。安装原始路径继续映射原文，身份键单独按旧 Windows/JavaScript 规则计算；root-relative 旧记录与 Electron 一样依赖运行进程的当前驱动器，绝不能把身份键作为扫描/打开路径的授权。`IsolatedLibrarySource` 的合成目录与文件句柄检查保持不变。

`generate-compatibility-vectors.mjs` 自动在已安装的锁定 Electron 的 Node 模式下加载当前 `validateLibraryBackup`，提取真实 `pathKey`；不启动 Chromium/UI 或打开用户 profile。它生成 7 组字符串、29 组路径、7 组数值正反参考。路径大小写不能使用 .NET 简单小写替代 JavaScript 全量小写：`LegacyUnicodeData.json` 是 Electron 44.5.1 内置 Node 24.21.0 / Unicode 17.0 的参考快照，只有 Legacy 路径比较使用，并随程序集嵌入。生成器同时生成 case-property 范围；C# 处理 contextual final sigma，保持单独 surrogate。参考 hash、Electron 版本、向量数量与内嵌快照 hash 在测试中检查。参考生成需先按 npm 锁文件安装现有依赖；若旧源码或参考引擎升级，先审查行为差异，再显式重建，不能只为消除 freshness 失败而无审查更新。

```powershell
# 仅显式维护参考时运行；正常 C# 测试消费提交的静态向量。
node native/scripts/generate-fixtures.mjs
node native/scripts/generate-compatibility-vectors.mjs
# 发布后，每次只开一个窗口，观察正常合成书架，再正常关闭。
& ./native/scripts/check-published-runner.ps1
& ./native/scripts/check-published-runner.ps1 -WithoutSdkLookup
```

`Run`（含 `Run -Published`）不再寻找或安装 SDK；其他动作继续按精确版本构建。`check-published-runner.ps1` 实际调用 `native.ps1 -Action Run -Published`，返回启动 PID/窗口标题并保留窗口供检查。`-WithoutSdkLookup` 只在本次脚本进程收窄 PATH，并以失败探针拦截 `dotnet` 和本地 SDK 查找，不改系统 PATH、不移动 SDK。该验证不能代替无开发工具/VC++ 的干净机器 Release Gate。

本次锁定 restore、Debug/Release build（0 warnings/errors）、两配置完整测试均通过，**84 passed / 0 failed / 0 skipped**，比 Phase 1 新增45项。生产链测试在自己的临时合成目录通过实际 `IsolatedLibrarySource` 加载，逐 UTF-16 单元断言字符串并核对原文件字节。Electron build 和完整 import regression 已通过：改用详情中的既有启动可用状态/收藏操作，未启动游戏；重复导入、个人资料保留、重启持久化断言全部抵达。没有修改 Electron 产品源码。两种 published runner 的真实窗口均显示3条合成记录并正常退出。

本次证据在 `.artifacts/native-phase1.1/`：`debug-tests.trx`、`release-tests.trx`、构建/发布记录、`runner-normal.json`、`runner-no-sdk.json`、`native-ui.json`。原 `.artifacts/native-phase1/` 的已编译候选目录被正常重建，正式 `release/win-unpacked` 未更新；早期 UI 回执只代表其当时构建。Gate 为 **PASS FOR PHASE 2**，不代表已经启动 Phase 2，也不包含真实资料、凭据或正式发布授权。

## Git checkpoint

初始基线记录迁移后的可构建源码。运行前后的构建、截图、日志和本机凭据不会进入基线。使用个人提交身份时，仅在此仓库配置：

```powershell
git config user.name "你的姓名"
git config user.email "你的提交邮箱"
git status
```

每次提交前检查暂存内容；不要提交正式应用数据，也不要把发布目录作为新的源码目录。
