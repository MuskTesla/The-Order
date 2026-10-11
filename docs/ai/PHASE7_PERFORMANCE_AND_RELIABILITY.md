# Phase 7.1：Performance / Reliability / Media Pipeline Hardening

**READY FOR PHASE 7.1 INDEPENDENT GATE**。实现、完整回归和两个最终连续两小时负载已完成，自检 **BLOCKER0 / MAJOR0 / MINOR0 / OBSERVATION9**，等待独立Gate。本记录区分实际执行证据与未验证边界；所有生成数据、慢样本、首次失败和二进制回执在 ignored `.artifacts/native-phase7-1/`。

## 基线、源码与边界

起点为 `codex/winui-phase5` / `4292bf1de29d8e2d2d6509c554b26cb1e8679f77`，工作区干净；与 Phase 6 受测 `7a3a739a3a758216419e8d5c0273a74e8e15561c` 的差异只有 BRIEF/DEVELOPMENT。Phase 6 independent Gate 为 **PASS FOR DAILY NATIVE USE / PHASE 7**，见 `.artifacts/native-phase6-2-independent-gate/gate-audit.json`。旧文档的宿主暂停段落是历史记录。

| 内部 checkpoint | 实际内容 |
|---|---|
| `1c7d5bb89ad3506b18f19de3a1ef81ba7a591d55` | JSON 数组校验线性遍历、图片预算/共享工作、DPI sizing、harness |
| `f3e678818e2dac87661255beff56d39de0fa8a82` | 持续实际 XAML fixture、错误路径、二进制回执与资源检查器 |
| `d3e7d2bcf7a3c3d941e859cf2c2bed958a4c547f` | 解码前拒绝不完整 PNG container |
| `e66a08b4d0afd4c1643669a0bdfa45fda54a75d4` | 零布局时立即作废 recycled cover、owned synthetic probe 有界退出 |
| `605addda17aed378e7c99de128ceaa4424b05748` | 新消费者等待 abandoned image work 排空后续接、UI driver 释放 Process owner |
| `d3ed15c32fb444bdde4f943d34c9c26dbe58f3f2` | 最终实际候选；增加 Test XAML failure stage/HResult/stack 诊断 |
| `1126b1f9971c54f534539921dd220928903257bd` | 仅测试：loopback listener shutdown 的 ObjectDisposedException 与 pending accept 回归 |
| `9ff60402236fa0e38efbd4515223edec4d043d30` | 仅harness：补测同一安装原生Session追加，真实SessionEngine+native store，纯合成process adapters |

最后生产行为修复在 605；d3ed 增加 Test-only 诊断；1126 只改测试，生产 C# 与 d3ed 相同。最终验收文档提交单独绑定，不能冒称候选内嵌事后生成的提交号。

本轮**没有访问、枚举、复制或修改真实 native Production / Electron profile**，没有使用真实 credential、启动真实游戏、改变 schema/accounting/matching/launch 语义、替换日用程序、推送或公开发布。正式 root 没有做本轮 hash 对照，因为本轮选择完全不访问。没有 Phase 7.2 / Liquid Glass / SQLite。

生产性能文件：`TheOrder.Application/Images.cs`、`TheOrder.Platform.Windows/NativeProfileStore.cs`、`WindowsRasterCodec.cs`、`TheOrder.Presentation/ThumbnailSizing.cs`、`TheOrder.WinUI/Views/CoverPresenter.cs`（均在 `native/` 下）。实际 WinUI diagnostics 另在 Program/Composition/MainWindow/LibraryPage/PerformanceImageFixture；只在 Diagnostics + Test profile + explicit artifact output 时启用，不替代正式算法。

## 机器、工具、数据与方法

Windows 11 Pro build 26300 / x64，Intel Core Ultra 9 285H，OS 报告 16 logical processors，约 31.6 GiB RAM；固定 .NET SDK 10.0.401 / runtime 10.0.12，Python 3.14 / Pillow 12.3.0。本机有并发验收负载，绝对耗时为机器局部参考，不是跨机器 SLA。

`native/TheOrder.Performance` 使用真实 NativeProfileStore、TransactionalLibrary、OfflineLibraryClient、WorkspaceViewModel、WindowsRasterCodec、CombinedCoverSources、RemoteCoverSource、IsolatedThumbnailCache。seed 7101；可设置 entry/history counts；只建立独立 Test profiles。输出环境、原始样本、nearest-rank median/p95、min/max、allocations、资源与 dataset/ProfileId/StoreId/文件身份。重跑使用新 artifact 子目录，不覆盖运行的 publish。

1k/5k/10k 条目具有 unique ID、中日文 title、aliases、metadata、安装/cwd/参数、favorite/rating/note/categories/tags、不同安装与愿望状态、0–3 条历史。不是克隆同一 object。输入 payload SHA 与持久化 envelope SHA 分开保存：正常 import 会将 native history 转为 ImportedSnapshot，不能混称同一个哈希。

Pillow 生成 **10,000 个不同 SHA256** 的原创 synthetic covers：3334 JPEG / 3333 PNG / 3333 WebP，约 320–448 × 480–624，encoded 总计 106,228,375 bytes；9 个 edge fixtures 包含 corrupt、truncated、invalid WebP、小图、极端比例、2400×3600、EXIF orientation、Unicode 与长路径，无 decompression bomb。`covers-final/dataset.json` SHA256 为 `54c5af772040af3f576bef0931d76717fa2cf724d95a7ce7f519790e4078a76d`；全部 10k/edge/generator 哈希重核一致，见 `final-input-audit.json`。图片不提交。

主要方法：profile open 1 warmup + 5 measured；7 search / 5 sort / 7 filter 每 case 1 + 9；200 queries、300 details、edit、backup/restore、history/checkpoint 一般 1 + 3；first-query-index 是单次初始化，不赋予统计 p95。full-unique 3 passes，首轮 warmup，后两轮 measured。所有慢样本保留，两样本 p95 就是最大样本。组合查询使用同一 host，不为每次查询重建应用。

实际 published WinUI 每规模 cold-ish 一次 + 五个新进程 warm；未清 OS/filesystem cache，不能称绝对冷启动。StartupClock 从 managed entry 开始，不包含 OS loader；driver 另记 process lifetime。profileOpen marker 包含 instance registration；另有纯 profile open 基准。VM ready、实现正尺寸 viewport 的 first usable、Image.Source 非空且与 active viewport 相交的 first cover 独立记录，不把进程出现当用户可用。

每规模实际 UI 360 轮：总计 1080 长距 scroll/selection、360 次 details 往返、90 次 Grid/List switch、54 次 Hide/Show；控件与 Image surface 最多26，实际测出 virtualization，而非仅看 XAML 声明。100/150/200% 用 DPI sizing + Windows decoder fixture，得到 256/384/512px；没有改变宿主 DPI。真实系统 tray menu 为 **SKIPPED_USER_INTERACTION**，power/reboot/logoff 为 **DEFERRED_HOST_LIFECYCLE**。Hide/Show 不冒称菜单点击。

规模媒体使用显式文件响应 transport；另外的 loopback TCP/HTTP fixture 真实测试 cache miss/hit/revision/corrupt response、60 并发和100次取消。未放宽生产 HTTPS/public DNS/无 credential/redirect/alternate-port 边界，长期测试不依赖外网。

## 已确认热点与修复

1. **完整 JSON 校验 O(n²)**：数组对照反复使用 JsonElement indexer 从起点遍历。改为等长检查后的两个顺序 enumerator，每个元素仍递归验证字段、长度与内容；最后元素 invalid/duplicate/丢失字段、provenance/stale-write/writer recovery 回归保留。没有跳过校验或修改 Phase 2 事务。
2. **图片重复 decode / 无界工作**：同 key + edge 共用一次工作，每个 waiter 独立取消；最后 waiter 离开才取消共享工作。unique work 与全局 waiter 各256上限，decode并发2；busy返回有界 placeholder。memory hit不创建 Task.Run；有效 source 的 derived cache IO/AccessDenied 故障可fallback，隔离保护保留。
3. **共享工作取消后的新消费者**：确定性 repro 中，旧工作所有 waiter 取消但尚未排空，新消费者加入 canceled work，得到持久 placeholder。605 保持同一 waiter budget，等旧工作完成后走正常 admission；已完成 canceled entry 原子替换，旧 cleanup 仅删除仍归自身的 entry。内部续接不多计 logical Requests，不对 invalid/fault 无限重试。20 concurrent consumers 的1轮/30轮及等待期间再次取消测试通过，原失败 `iteration-2/renewal-reproduction.trx` 保留。
4. **decoded/cache预算**：LRU **64 MiB / 512 items**，derived disk **128 MiB**，只清理拥有的 thumbnail；原 encoded remote **32 MiB /64 items**、source registrations2048、20MiB source、40MP、edge1600保留。10k cover不会全部永久驻留。64MiB约容纳常见256px封面的数百项；两路解码限制瞬时内存与CPU。disk eviction不是删除个人cover。
5. **缩略图/recycled控件**：DPI-aware 64px buckets 64..1600，同 key/bucket避免重复Reset/decode，零布局等实际尺寸；SourceKey变化立即清旧 bitmap/generation，零尺寸也不能保留旧图。取消/generation保护与真实 staleCoverSources=0 回归保留。详情可请求较高edge，未把原图总按完整尺寸decode。
6. **截断 PNG 被 WIC 接受**：实际64-byte PNG只有半个IDAT仍返回pixels。新增有限container检查：首IHDR13、完整chunk length、非空IDAT、结尾IEND0精确到尾；随后仍由WIC解码，不另造decoder。corrupt/truncated/invalid WebP均InvalidDataException；其余edge成功：small2×3、aspect256×7、moderate171×256、orientation256×171、Unicode171×256、long32×48。
7. **资源 ownership**：ImagePipeline Dispose所有调用者等待同一drain task，取消/排空/finally释放；无人等待的fault也被观察，共享CTS完成后释放，codec/stream/bitmap/cache pin有using/finally。UI driver正常/异常finally释放Process，OSsampler每次释放查询handle。Test fixture listener在stopping后处理pending accept的ObjectDisposedException，并始终释放CTS；活动期间非预期异常仍失败。

查询继续在背景 Task.Run +120ms debounce内执行，generation/cancellation保护旧结果；profile解析/持久化/decoder工作沿原async/background路径。新UI诊断用固定timer监测dispatcher gap，没有per-entry timer，StopAsync停止timer/解绑。没有新增无owner的fire-and-forget。单元/实际UI覆盖missing/corrupt/cancel/cache不可访问/busy/dispose/error cleanup；错误没有自动无限重试、progress残留或日志洪峰。

## 最终完成的规模测量

Headless source605，actual UI source d3ed，tests source1126。`gate-code-binding.json` 核对五个共享程序集 **5763** method bodies、locals、stack、exception regions与tracked PDB source一致；生成版本信息不同明确保留。下表使用 `bench-605addd` / `checkpoint-605addd` / `media-605addd` / `ui-gate`，取代此前e66测量；旧结果不删除。

| 条目 | open | edit | backup | restore+reopen | 200 queries | 300 details |
|---:|---:|---:|---:|---:|---:|---:|
| 1000 | 384.6 / 586.5 | 507.8 / 529.1 | 719.1 / 722.1 | 1171.7 / 1182.6 | 40.7 / 47.7 | 0.96 / 1.24 |
| 5000 | 1854.8 / 1986.9 | 3231.7 / 3505.3 | 3942.3 / 4014.2 | 7352.0 / 7768.4 | 306.5 / 332.4 | 0.72 / 0.75 |
| 10000 | 4042.0 / 5602.3 | 6383.7 / 6771.7 | 8247.6 / 8520.0 | 13793.4 / 14479.3 | 895.9 / 945.0 | 0.70 / 1.24 |

单位ms，median/p95。7 search、5 sort、7 filter原始每case分布均保存：

| 条目 | search median范围 / 最大case p95 | sort | filter |
|---:|---:|---:|---:|
| 1000 | 0.20–0.48 / 5.06 | 0.13–0.59 / 0.88 | 0.03–0.14 / 0.16 |
| 5000 | 0.31–2.97 / 7.05 | 0.66–2.45 / 6.54 | 0.06–0.62 / 1.01 |
| 10000 | 0.85–3.35 / 31.91 | 1.53–8.26 / 8.38 | 0.18–1.56 / 1.72 |

| 条目 | cold-ish usable/cover，s | warm usable median/p95，s | warm cover median/p95，s | warm profile/VM median，s |
|---:|---:|---:|---:|---:|
| 1000 | 3.930 / 3.932 | 3.881 / 4.138 | 3.884 / 4.140 | 1.747 / 3.776 |
| 5000 | 8.446 / 8.448 | 7.891 / 8.302 | 7.893 / 8.304 | 3.907 / 7.774 |
| 10000 | 12.614 / 12.616 | 11.456 / 12.844 | 11.458 / 12.846 | 5.657 / 11.343 |

| 条目 | 360轮peak/成熟private，MiB | peak WS，MiB | 成熟handles前→后 | realized/surfaces max | UI tick median/p95/max，ms |
|---:|---:|---:|---:|---:|---:|
| 1000 | 473.3 / 413.5 | 604.1 | 1147→1140 | 26/26 | 283.7/314.7/342.4 |
| 5000 | 655.9 / 554.9 | 780.8 | 1156→1151 | 26/26 | 286.2/332.7/401.5 |
| 10000 | 783.5 / 628.8 | 902.4 | 1149→1149 | 26/26 | 283.1/316.2/338.8 |

| history | open median/p95，ms | append-import median，ms | backup median，ms | restore median，ms | checkpoint median/p95，ms |
|---:|---:|---:|---:|---:|---:|
| 1000 | 36.9 / 37.8 | 66.5 | 112.2 | 116.1 | 368.2 / 389.2 |
| 10000 | 574.3 / 611.8 | 725.7 | 1062.5 | 1523.3 | 1578.4 / 1602.4 |
| 50000 | 1744.3 / 1820.0 | 2626.3 | 3577.5 | 4406.7 | 4433.3 / 4442.4 |

50k仍为12.5k native +37.5k legacy；完整50knative仍被32MiB writer安全拒绝。checkpoint相同revision重放，四次推进仅220秒，count不增。append-import不冒称原entry owned Session append。

| full-unique count | median/p95，s | 末轮derived disk bytes |
|---:|---:|---:|
| 1000 | 3.742 / 3.771 | 134020428 |
| 5000 | 19.106 / 19.144 | 134217080 |
| 10000 | 46.195 / 46.918 | 134031692 |

小编辑仍完整替换main+backup：1k/5k/10k envelope bytes为3,033,151 /15,195,067 /30,397,103，logical written约6.07/30.39/60.79MB。checkpoint main907,300 /9,048,304 /18,095,212bytes，logical written约1.815/18.097/36.190MB；临时文件上界按两个envelope保守计，backup大小单个envelope。这不是ETW physical disk bytes。

mixed50k原checkpoint median182,831ms，最终4,433.3ms，约 **41.2×** 改善；`pre-fix-platform-binding.json`证明旧Platform551个method bodies与Phase6受测平台相同。早期whole-harness没有及时冻结完整binding，其他早期耗时仅诊断参考，不声称完整baseline binary已证明。

mixed50k一次checkpoint总分配约4.866GB，GC回收后不等于永久驻留，仍是全JSONvalidation/serialization成本。完整50knative超过原 **32MiB writer contract** 安全拒绝，`baseline/failure.json`保留。10k edit/restore和50k容量/分配构成 **STORAGE REASSESSMENT TRIGGER / STORAGE REASSESSMENT REQUIRED**；未升容量、迁SQLite或改schema。1k/5k/10k已可加载/查询，10k的数秒写/重开成本明确报告，不因规模变慢就自动定MAJOR。

55s checkpoint使用真实Session事务：1warm+3measured，四次推进累计仅220秒，同revision重放不重复累加，count不增；historical floor/exactly-once不变。history-append-import只是新增带历史的entry，不冒称既有owned Session append。active Session端到端另由完整Session/Windows fixtures验证。

收尾另补齐 **同一安装的原生Session追加**，`native-appends-final/native-session-append-results.json`：真实SessionEngine的Prepare/Confirm/admission/start/原子持久化，filesystem/process/clock/tokens均为纯合成适配器，无OS game process。每规模1warmup+3measured；Stop Observing及合成进程结束/正常observer排空在计时之外，下一轮继续原single-owned admission，不绕过reservation。四次追加分别只增一条Session/一次count；total playtime增量0；最终合成观察资源0。源码9ff、`append-harness-binding.json`绑定补测binary；`append-code-binding.json`确认五个共享程序集5763方法与d3ed候选、1126全量一致，tracked source相同，原两个2h结果仍有效。

| 历史 | Native append median / p95，ms | min / max，ms | count / time delta |
|---:|---:|---:|---:|
| 1k | 404.6 / 534.1 | 354.8 / 534.1 | +4 / 0s |
| 10k | 1452.7 / 1461.2 | 1416.0 / 1461.2 | +4 / 0s |
| 50k mixed | 4467.6 / 4807.1 | 4403.5 / 4807.1 | +4 / 0s |

Supplement publish为0warning/error；相关targeted94/0/0（Performance22/SessionCore39/Persistence30/Validation3）。未重跑已完成1763全量：这次只增benchmark/checker，生产Application/Platform/Presentation/WinUI源和method bodies均不变。补测初次clock接口/nullable/percentile类型编译错误，以及把long-poll fixture停止观察当作monitor已排空而被AlreadyRunning拒绝的失败均保留；修正fixture按现有生命周期有界排空，count/time/assertions/admission不放宽。

## 最终连续两小时验收

`soak-605addd`为10k headless Application/Presentation/media混合负载；每4秒paced十次query、十二张image、details/library导航、obsolete/new query，preferences每60轮、backup每225轮（约15–16分钟）。`ui-soak-gate/10000-0`为同一实际WinUI进程的10k负载；每4秒scroll/select、details每3轮、Grid/List每12轮、Hide/Show+SessionCheckpoint每20轮。两项均使用完成初始化后的单调负载时钟，各自连续120分钟，没有把旧partial拼接。

| 长时负载 | 实际时长 / 轮次 | peak WS / private，MiB | 成熟 private / managed heap median，MiB | 成熟 handles median | USER / GDI |
|---|---|---:|---:|---:|---|
| 10k复合查询/媒体 | 7200.205s / 1686轮 / 43901操作 | 721.3 / 674.6 | 276.1 / 176.2 | 533 | 5–7 / 0–0 |
| 实际WinUI10k | 7202.478s / 1727轮 | 726.0 / 588.4 | 535.4 / 未采managedheap | 1146 | OS 45–49 / 16–16 |

检查器 PASS，28682 checks / 0 failed；headless errors 0，actual UI UI_WORKLOAD_COMPLETED，OS PROCESS_EXITED。UI tick gap median/p95/max 269.5/284.9/485.7ms。

成熟区间首/末季度growth（原始预算不变）：

| 检查 | 实际增长 | 上限 |
|---|---:|---:|
| ui-1000/mature-handle-growth | -7.50 | 64.00 |
| ui-1000/mature-private-growth | -19.75MiB | 256.00MiB |
| ui-5000/mature-handle-growth | -5.00 | 64.00 |
| ui-5000/mature-private-growth | -30.90MiB | 256.00MiB |
| ui-10000/mature-handle-growth | 0.00 | 64.00 |
| ui-10000/mature-private-growth | -80.20MiB | 256.00MiB |
| soak/mature-growth/handles | 0.00 | 32.00 |
| soak/mature-growth/user | 0.00 | 4.00 |
| soak/mature-growth/gdi | 0.00 | 4.00 |
| soak/mature-growth/privateBytes | 8.52MiB | 256.00MiB |
| ui-soak/mature-growth/handles | 2.00 | 64.00 |
| ui-soak/mature-growth/privateBytes | 31.74MiB | 256.00MiB |
| ui-os/mature-growth/user | -1.00 | 4.00 |
| ui-os/mature-growth/gdi | 0.00 | 4.00 |

headless总分配 95.224GB，GC0/1/2累计 7208/1321/394；这表示持续工作中的累计临时分配，不是常驻内存。15分钟统计分桶与每轮原始资源在JSON中保留，共8个统计窗口。实际UI共1727轮scroll/selection、576次details往返、144次layout switch、87次Hide/Show+checkpoint。两个driver正常exit0，OSwatcher记录PROCESS_EXITED；final work/waiter/decode/error/backlog全0，staleCoverSources全0。长期realized控件与image surfaces最多31，保持有界。

OSwatcher在UI初始化后约2分钟开始，不包括最早启动阶段。长期UI没有active game，checkpoint为空活动Session noop；不冒称两小时游戏Session。真正activeSession资源/语义由完整Windowsfixtures及独立historycheckpoint覆盖。headless不冒称XAMLvirtualization，actualWinUI才提供真实控件证据。旧1c/f3/d3/e66局部负载因修复停止、保留现场，不能计入最终120min。

采样区分materialization、GCsawtooth、peak和成熟区间。handles/threads不等于完整file-handle或task类别枚举；使用ThreadPool pending/图片work-waiter-decode/控件surface与静态owner审计组合，不声称枚举所有CLRtimer、GPU资源或ETW逐filehandle。SessionEngine worker/polling/PeriodicTimer沿既有shutdown owner，无新增条目timer。

## 阈值、测试、发布身份

`check-performance-results.py`已在全部完成后运行并PASS：硬预算decoded64MiB/512、decode2、work/waiter256、deriveddisk128MiB、errors/stale/finalbacklog0；actualrealized/surfaces≤256；2hminimum；maturehandles增长headless≤32/UI≤64、private增长≤256MiB、USER/GDI≤4；OSwatcher正常PROCESS_EXITED。原检查28682项；补入native追加accounting后 **28697 checks /0 failures**。108项latency参考为本机measuredp95×2+合理floor（100–5000ms），保留全部outlier。`acceptance-append/thresholds.json`供未来独立复测，原`acceptance`不覆盖；初次生成不是对旧latencybaseline的独立证明。

locked restore、Debug/Release solution build成功，两配置各0warnings/errors；publish成功。最终 **Debug1763/0/0（3m47s）**，**Release完整confirmation1763/0/0（3m36s）**。原1734项逐名称保留、新增29（PerformanceHardening22、MediaNetworkStress3、ProfileValidationPerformance3、ProfileWriterRecovery1）。TRX见`iteration-4/debug-final.trx` /`release-confirmation.trx`；`test-name-preservation-final.json`、冻结`test-binaries-final4-*`及`*-final4-binding.json`保留。targeted首次失败不删除。

完整suite包含Presentation/media/cache/profile/PathGuard321/transactions/provenance/writer、SessionCore39/Persistence30/Hardening23/Windows9、RecoveredOccupancy18+Windows1、Lifecycle24+Windows4+Retry22、RecognitionCore222/Remediation44等。Electron与共享import/schema未改，未跑Electron全量。29新增及已有回归同时覆盖正确性/错误/取消/有界排空，不修改旧assertions来迎合实现。

最终candidate `.artifacts/native-phase7-1/publish-gate/win-x64/TheOrder.WinUI.exe`，source **d3ed15c32fb444bdde4f943d34c9c26dbe58f3f2**；EXE SHA256 **523695D52987E740A70AD09E1C5CBD319332A2C55D4659A096619ABE4EEFD7A1**；WinUI DLL SHA256 **B8CB56E91BC71888A766920723AC27C51171FB888721B39B5B771A9DF86EFEA2**，MVID **f9246607-7b67-4fe7-a317-2b1c1793319b**。`candidate-gate-binding.json`保存EXE/DLL/PDB/source；`harness-final-binding.json`绑定605headless，`gate-code-binding.json`证明与1126全量共享代码相同。environment分别保存binary version/MVID/SHA和runtimeHEAD，不混称。该candidate只待Gate，未替换Phase6候选或用户程序。

## 失败纪律、剩余观察与隐私

- 最终1126 Release第一次1762/1/0：RecognitionRemediationTests.CapacityRefusalKeepsActiveReviewAndIgnoreThenAdmissionWorks空集合Assert.Single，原`iteration-4/release-final.trx`保留；固定两次整个RecognitionRemediation分组各44/0/0，随后一次完整confirmation1763/0/0。未改识别规则/assertion，根因仍未证明；WhenIdle与投影先后仅源码疑点。
- 605 Release原1761/1/0（总1762）：handoffhandles654…662触发原+6限制，固定两次isolated各1/0/0，随后一次完整1762/0/0。更早DebugRunningLimited失败同样保留。没有以绿结果抹去resource/WindowsSession历史Observation。
- 一次Debugsuite ownedProfileProbe child约21min未退出：parent asyncstack为WaitForExitAsync，childmanagedthreads为Suspended，存在Bdwp winhook64.dll；没有因果证明。保存dump/stack/process证据，只停止已核对ownedsyntheticchild，原ExitCode失败保留；新30sdeadline只让测试有界失败/cleanup，不声称修复宿主原因，没有关闭或绕过系统保护。
- 首个605实际UIsoak在67samples后COMException、空Message，约06:38:11保存`ui-soak-605addd/10000-0/ui-failure.json`并正常exit0。旧catch无stack，不能推断根因。d3ed只加stage/HResult/stack；同profile12min721.866s/173round正常结束、未复现；最终新profile连续两小时正常完成，未再复现；仍没有COM根因证明。无证据证明产品崩溃/资料损坏，也不称COM已修复。
- 本轮原Phase6七项observations继续保留：componentbackup非跨component原子、JSON规模、原短时resource证据、recognition、WindowsSession、handoff、旧phaseUI文案。宿主childSuspended与一次COM另列两项；最终2h补齐所选workload资源证据，不推广到未测边界；自检分级B0/M0/m0/OBS9。

早期fixture错误（PNGprefix/HTTP自动重发、UIself-await、发布gateflag、lockedbuildoutput）、renewal测试初次过早释放seconddecoder、listenerstop失败均保留。新同步fixture等待20消费者真正合并后才释放decoder，没有放宽decode/count断言；listener只在stopping时接受shutdown异常，50轮立即pending-accept释放回归通过。生产缺陷修复后重新做最终workload，没有拼接旧partial。

日志不按frame/scrollitem/cachehit输出；显式Testfixture按aggregateprogress/JSON记录，关键异常保留可诊断stack/stage。普通生产路径未新增完整用户payload/token/remotecredential日志，测试没有真实credential/用户数据。dump可能含unit-test假凭据，不声称artifact无任何credentialstring；本轮不是完整SecurityGate。没有Production mutation，真实库字节未作新hash核验。宿主DPI、真实traymenu、sleep/reboot、真实游戏两小时、跨机器/断电/ETWfilehandles/GPU资源为未验证边界。

`validation-audit.json`绑定原完整资源/回归证据；`append-validation-audit.json`补充追加binary/结果/94targeted/检查器和108项阈值的SHA256。最终验收文档commit由 `checkpoint-receipt.json`绑定；相对candidate d3ed，native差异只有1126测试fixture修复、9ff补测harness与Python checker，五个生产共享程序集和WinUI源码未变。受测app仍为d3ed、shared行为605。工作区收尾提交后干净，不推送。**READY FOR PHASE 7.1 INDEPENDENT GATE**；暂停，未进入Phase7.2。
