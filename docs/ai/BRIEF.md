# The Order：当前实现与评审交接

## WinUI Phase 6.2：Production Readiness（进行中）

- Phase 6.1 已在 `ba37df62dbda23020dd1b85684b42953f999b430` 独立 **PASS FOR PHASE 6.2**，完整证据 `.artifacts/native-phase6-1-provenance-regate/gate-audit.json`；BLOCKER/MAJOR/MINOR 0，OBSERVATION 6。Gate 实跑 Release 1724/1/0，handoff 单次 RunningLimited 的定向复跑通过，原始失败不抹去。
- 当前用户明确授权首次真实 native Production bootstrap、最小真实数据和受控恢复验收；仍禁止任何正式 Electron profile 访问/导入，禁止 Phase 7、批量日用、发布/推送。初始 Production preflight absent。正式 profile 创建后不得重建/reset；保留 safety snapshots，完成后等待独立 Gate。
- 先补齐当前候选缺少的显式入口：`--allow-production-profile` / runner `-AllowProductionProfile -ProfileKind Production`，默认仍拒绝正式根；不修改 guard/schema/Session/accounting，设置页只纠正旧开发资料措辞。候选需新 checkpoint、完整 Debug/Release 与 publish 绑定后才能接触正式数据。
- 本节记录进行中工作，真实 Production 验收尚未完成；启动、边界和后续证据见 [DEVELOPMENT](../../DEVELOPMENT.md) 与 `.artifacts/native-phase6-2/`。

## WinUI Phase 6.1：Read Provenance / Stale-Write Remediation（等待 targeted re-Gate）

- 基线 `codex/winui-phase5` / `a0befa5895dd05181047be609d4545041f054a64`，最新 targeted re-Gate 为 **MAJOR 1 / OBSERVATION 5**；已通过的 ProfilePathGuard 和原 ownership exception safety 保留，scope reset 不变。
- 唯一剩余缺陷是新 main baseline 与旧 backup editable state 混用。现在 native writer 的加载 payload、recovery 来源和 stale token 绑定同一份已验证 authoritative main envelope；再次访问遇到 sharing/AccessDenied 明确失败，不 fallback。main generation 变化拒绝 Open/Save，解锁后重试读取当前 main。
- 确认 malformed/truncated/structural/missing main 才允许在 native borrow 前恢复兼容 backup → main，readback 后新建 baseline。已借出 writer 不能另选 backup；超限 main 继续 `LibraryRecoveryRequired → 显式 Restore → borrow`。Phase 2 通用 Open 本身不写盘，事务和业务 mutation 未改；Session accounting/schema/guard/Electron 未改。
- 新增 25 项回归和实际 child-process 原 Gate probe 复验，最终测试/构建/资源数值见 [DEVELOPMENT](../../DEVELOPMENT.md)。证据位于 ignored `.artifacts/native-phase6-1-provenance/validation-audit.json` 与提交后 `checkpoint-receipt.json`。旧测试和阈值未改，五项 observations 保留；不把本轮自检当作独立 Gate 通过。
- 最终 provenance targeted **25/0/0**、相关 **708/0/0**，完整 Debug/Release 各 **1725/0/0**，原 1700 项逐名称保留；recognition **44/0/0**、Windows Session **9/0/0**。两配置构建 0 warnings/errors、locked restore 通过；三轮独立 child-race 均无回退，新增资源循环 Debug **671→671** / Release **676→676**。自检 **BLOCKER 0 / MAJOR 0 / MINOR 0 / OBSERVATION 5**，**READY FOR PHASE 6.1 TARGETED RE-GATE**。
- 正式 native/Electron profile、凭据未访问；无真实游戏、重新发布、正式替换、推送或 6.2 工作。完成本次独立整改 checkpoint 后暂停，等待 targeted re-Gate。

## WinUI Phase 6.1：Guard + Writer Exception-Safety Remediation（等待 targeted re-Gate）

- 本轮从 `codex/winui-phase5` / `f797809983111e3f0f6becfc484dc13a803b203f` 的干净基线开始，核对 independent Gate audit/probes。该 Gate **REMEDIATION REQUIRED / MAJOR 2 / OBSERVATION 5** 优先于下方旧自检结论。仅整改 M1/M2，未改变 scope reset `62da4b46829602e3f4852d094489bbf15d10113b` 或进入 6.2。
- M1：新增共享纯 `ProfilePathGuard`，正常 profile 与 raw/fixture 均先规范 ordinary/extended DOS alias 并判断正式根 overlap/授权，再进入既有 filesystem identity/reparse/pin 检查。不支持的 UNC/device/NT namespace 在 I/O 前拒绝；raw 不能碰默认 Production/Development/Electron roots。Program 两条入口、library/credential/preferences/Session/persistence 与 backup/restore/open-existing 均经共享边界；ProfileProbe 也先 guard 再读 marker。
- M2：原先先置 ownership、constructor 再读 main，异常形成 phantom borrower。现在在同一 owner gate 内先验证 main/允许的 backup 和有界 baseline，无 I/O constructor 成功后才交付 ownership；异常不改变 flag，取消/后续 open 失败沿既有 dispose 清理。锁冲突可重试；有界 malformed/truncated main 保留 Phase 2 fallback；外层超 48 MiB 明确 `LibraryRecoveryRequired`，允许显式 Restore 后 borrow。没有自动覆盖、放宽 stale-write 或 backup identity/version/compatibility 门禁。
- 新增 `ProfilePathGuardTests` **321** 项与 `ProfileWriterRecoveryTests` **16** 项；相关 targeted **753/0/0**，含旧 profile 85 与 Session/persistence/transaction 331。547 次 alias/namespace/capability 拒绝断言均为零文件访问边界；synthetic-equivalent known roots 与正向 I/O 对照，不访问真实 Production 验证。跨进程 main 锁、第二 owner、十轮双 borrower、取消、三类损坏、容量边界、invalid/future/foreign backup、恢复后 stale-write 与 cwd/accounting 组合回归通过。
- Debug/Release 完整各 **1700/0/0**，原有 1363 项逐名称保留，最终全量均一次通过；locked restore、两配置 solution build（0 warnings/errors）通过。完整验证见 [DEVELOPMENT](../../DEVELOPMENT.md) 与 ignored `.artifacts/native-phase6-1-remediation/validation-audit.json` / 提交后 `checkpoint-receipt.json`。失败路径短轮次资源 targeted 472→472、Debug 全量中 672→672；Release 650→653 后稳定 21 个采样，原阈值通过；temp 0，owner/borrower 均可释放。旧测试和阈值未改；初期新测试的编译/有效输入/既有脱敏异常契约错误保留日志。
- 未修改 Electron、Phase 2 transaction/atomic primitive、Phase 5A/5B identity/accounting 或 durable schema；未访问正式 native/Electron profiles、凭据，未启动真实游戏、重新 publish、替换程序或推送。原五项 observation 保留（跨组件非原子备份、性能、短轮次资源、历史 recognition/Windows Session 波动）；不把回归绿灯当作历史根因解决或长期保证。独立 targeted re-Gate 尚待执行。
- **READY FOR PHASE 6.1 TARGETED RE-GATE**。整改自检 BLOCKER 0 / MAJOR 0 / MINOR 0 / OBSERVATION 5；不代替独立复审。独立整改 checkpoint 后暂停，不进入 6.2。

## WinUI Phase 6.1：Production Store Bootstrap（自检完成，等待独立 Gate）

- 基线 `codex/winui-phase5` / `b2a360a005aecd8bea1b18571eb78b75259c115b`，Phase 5B 已通过最终独立 Gate（`3d9a71ad920b5c20fe38772ef3e5c1698dd5010d`）。
- 产品事实更新：没有要求保留的 Electron 正式资料。旧 [PHASE6_MIGRATION_AND_STORE.md](PHASE6_MIGRATION_AND_STORE.md) 已标记 **SUPERSEDED**，仅保留历史，不再是实施计划。
- 当前规范为 [PHASE6_PRODUCTION_PROFILE_BOOTSTRAP.md](PHASE6_PRODUCTION_PROFILE_BOOTSTRAP.md)。6.1 只在隔离 synthetic production-like profile 实现新库初始化、物理隔离、身份与版本门禁、单 writer、备份恢复和原生凭据/偏好；不建立正式 profile，不自动导入 legacy。
- 6.2 为 **Production Readiness / First Real Profile**，以后另获明确授权才创建第一份真实 native production profile。旧 Electron 归档可另行授权；无无损迁移、回滚到 Electron、双 writer 协调或 SQLite 要求。
- scope-reset 文档 checkpoint：`62da4b46829602e3f4852d094489bbf15d10113b`。已完成 native profile owner、稳定身份、严格版本/能力门禁、崩溃续作、跨进程单 writer、Phase 2 共用原子写与实际 restore。新增 manifest/envelope v1，库内部、Session accounting、cwd、cover 和 DPAPI model 保留；备份来源传回原 SessionEngine，不能恢复活动进程的接管资格。
- 最终 Debug/Release 全量各 **1363/0/0**，原有 1278 项逐名称保留，新增 85 项；locked restore、两配置 build（0 warnings/errors）与隔离 publish 通过。最终 Windows Session 分组 **9/0/0**，recognition **44/0/0**，独立性能/资源 **4/0/0**。实际 WinUI 已验证空库、偏好重开、发布默认 Production 与正常退出（exit code 0）；全部指向隔离 production-like 根。
- **READY FOR PHASE 6.1 INDEPENDENT GATE**。自检未发现新的 BLOCKER / MAJOR / MINOR；保留两项既有测试间歇失败观察，分组与最终完整复验均通过但根因尚未确定，未改原测试期望。完整数字、限制及源码/二进制/checkpoint 绑定见 [DEVELOPMENT](../../DEVELOPMENT.md) 与 ignored `.artifacts/native-phase6-1/validation-audit.json`、`checkpoint-receipt.json`。正式 Electron/native profile 和凭据未访问，无真实游戏、Electron 改动或推送；暂停等待独立 Gate，不进入 6.2。下方旧阶段状态为历史记录。

## WinUI Phase 5B.2：Persistence Retry Remediation（等待 targeted re-Gate）

- 本轮从 `codex/winui-phase5` / `9f1aef2e944f8ad07c31a5b43f6f7c84b97d678d` 干净基线开始，独立 Final Gate 的 **REMEDIATION REQUIRED / MAJOR 2** 取代下方历史自检结论。只修两项 persistence retry，不进入 Phase 6。
- `AppSessionHost` 分离一次 engine/writer 创建与可重试 recovery attempt；失败/取消后复用同一 engine，Initialize/Explicit Exit 并发共享 attempt，成功幂等。清理先封闭 retry、等待在途工作，不让旧完成回调复活 host。
- `SessionEngine` Exit 不再跳过未 durable 的 terminal；复用现有 Record/Durable 与 transaction receipt，固定终态候选的 cutoff/EndedAt/final delta，补写成功后重复 Exit/checkpoint no-op。没有重新打开 terminal、补离线时间、增加 count 或新增 durable schema。
- 新增 22 项，新增测试 **22/0/0**、相关 targeted **192/0/0**；Debug/Release 最终完整 suite 各 **1278/0/0**，原 1256 项逐名称保留；两配置 build 0 warnings/errors、locked restore 通过。首次 Release 有 1 项既有 handoff 资源阈值失败，保留日志，不改阈值，单项及随后完整复验通过，纳入资源观察项。未修改原 Gate probe 源码，引用本次 DLL 复验后原三条失败路径均已修复。证据与源码/二进制身份详见 [DEVELOPMENT](../../DEVELOPMENT.md) 和 `.artifacts/native-phase5b2-remediation/`。
- 未改 Phase 5A/Phase 2 contract、共享 codec/Electron、recovery identity、occupancy、Tray/Single Instance/Presentation；没有重新启动真实游戏、访问正式资料、重新发布或推送。旧 publish-final 仍对应 a11704b，不能混作本次修复候选。原 3 项 OBSERVATION 保留，独立 targeted re-Gate 尚未通过，不能宣称 Phase 5B 完成。
- **READY FOR PHASE 5B.2 TARGETED RE-GATE**。整改自检 BLOCKER 0 / MAJOR 0 / MINOR 0；提交后暂停，等待独立复审。

## WinUI Phase 5B.2：App Lifecycle / Tray / Single Instance（自检完成，等待独立 Gate）

- 开始于 `codex/winui-phase5` / `5f57ad8e0a071c2f3c4aed673f546fcf9c2ab792`，核对干净工作区。该基线已通过 5B.1 targeted re-Gate；本轮授权正式接入 5B.2，尚不代表 5B.2 独立 Gate 通过。
- 已实现 AppInstance 在 writer/engine 前分流、application-owned `AppSessionHost`、恢复完成后开放操作、同 writer 刷新、关闭到托盘、显示/暂停/恢复/明确退出、保存失败取消退出及 bounded shutdown/logoff。窗口隐藏不 dispose engine；Explicit Exit 使用 RecoveryPending，不使用 Stop Observing；重启沿用既有身份恢复并诚实显示 CurrentProcessOnly。
- Session UI 直接读取已确认 accounting，不增加全局 stopwatch。Paused、Limited、Unknown、Incomplete 和真实 Completed 分开；未改核心计次、awake 时间、5A facts、Phase 2 transaction 或 recovered terminal occupancy 的当前进程边界。新增正常 UI 不暴露普通“停止观察”。
- 新增 `AppLifecycleTests` / `AppLifecycleWindowsTests` 共 28 项，完整 Debug/Release 各 **1256/0/0**、原 1228 项保留，两配置 build 0 warnings/errors、locked restore、Electron 665/0/0、artifact 32/0/0 通过。真实 WinUI 已验证 simultaneous startup、暂停关窗/唤回、Explicit Exit/no-kill、并发重启只恢复一次、Limited 继续、约 55 秒离线不补时及 Tray failure 可访问；真实托盘 Resume 菜单由用户操作并确认，随后运行时/JSON 复核。43 次 secondary 激活全部退出，预热后 20 次 handles 1077→1077；Windows shell icon/subclass 30 轮、Close/Show 策略 50 轮通过。
- 发布源码 checkpoint **a11704b902d0c643385efa2209713f3f7fff8c01**；唯一候选 `.artifacts/native-phase5b2/publish-final/win-x64`，Release WinUI MVID **058e7e46-b091-40ab-ad95-3d2682ff50f7**，使用 `acceptance-profile`。`published-identity.json` 与最终 `checkpoint-receipt.json` 绑定源码/二进制/验证，Published 入口要求显式绝对 `-PublishedPath`。8 组 Debug Windows UI 与 9 组最终包 UI manifest 分开，最终文档提交不冒称二进制内嵌 SHA。
- 本轮真实 Game-A **2 次**：第一次暂停 55.656 秒累计 ticks 不变，关窗同 Session 继续，正常退出 Completed 164 秒；第二次 Explicit Exit 游戏存活，约 138 秒离线后同 Session/install/count 恢复，ticks 精确不变且 CurrentProcessOnly，随后正常退出为 Incomplete 312 秒，A 总计 476 秒/2 次/2 条记录。Game-B **1 次**：44140→45024 handoff，正常退出 Completed 45 秒/1 次/1 条。仅主菜单与退出；全部验收进程正常退出，A/B 目录元数据及 EXE hash 不变。一次缺少 native profile 参数的启动被隔离保护拒绝，成功恢复证据另列。Game-C 未重跑，旧 cwd 证据明确标为历史。
- **READY FOR PHASE 5B.2 INDEPENDENT GATE**。自检 BLOCKER 0 / MAJOR 0 / MINOR 0，OBSERVATION 3：真实 host 生命周期待人工验收；终态 occupancy 当前 engine 生命周期边界不变；短轮次本机资源与用户托盘操作证据不代表长期/跨机器保证。未改 5B.0/5B.1 核心规范，未访问正式资料、未推送；完成最终 checkpoint 后暂停，不进入 Phase 6。完整分层证据、发布 hash 和后续人工步骤见 [DEVELOPMENT](../../DEVELOPMENT.md)。

## WinUI Phase 5B.1：Recovery Occupancy Remediation（2026-10-05）

- 本轮从 `codex/winui-phase5` / `3cb1984d2bf2e4186b6fc95aca36f436d72f2b80` 干净工作区开始，只修 independent Gate 的唯一 MAJOR：恢复 A 后停止观察，A 尚存活时 B 被错误放行。该 Gate 结论为 **REMEDIATION REQUIRED**，优先于下方历史实现自检声明。
- `SessionEngine` 在成功恢复时保留独立内存 occupancy，复用完整 `SessionRecoveryIdentity`（PID/creation/image/file identity、game/install 与原 folder/exe/cwd binding）。停止观察仍终结为 Incomplete、停止 timing、释放 observer、清除 durable recovery token，但不释放此占用。普通 Session/personal transaction 不改占用；没有新增 durable schema。
- 后续启动在原 `launching` gate 内有限复核：完整身份与安装仍有效为 Active；查询失败或不能确认属 Unknown；二者均 AlreadyRunning，不创建进程/Session、不计次/计时。只有明确退出或 PID creation generation 不同为 Inactive，才释放后调用既有 5A admission；并发请求不能穿过复核与释放之间。
- engine 拥有该无句柄记录；每次复核临时查询 handle 由 `using` 释放，最终 Inactive 或 engine dispose 清除记录。未修改 5A Process Fact / identity、Phase 2 transaction、Session accounting、共享 codec 或 Electron compatibility 层。当前进程外的 terminal reservation 持久化、正式 cutover、UI/Tray/Single Instance 均不在本轮。
- **READY FOR PHASE 5B.1 TARGETED RE-GATE**（整改自检，不代替独立复审）。新增 19 项；Session/recovery 定向 **128/0/0**，Debug/Release 全量各 **1228/0/0**，原有 1209 项逐名称全部保留；两配置最终 solution build 各 0 warnings/errors。真实 Windows 23 次拒绝后的 handles Debug **727→727**、Release **677→677**，A 正常退出后 B 各只创建一次；原 Gate probe 复验 A PID20360 仍活、B AlreadyRunning/无进程/无 Session/count 不变。证据与 hash audit 存于 `.artifacts/native-phase5b1-remediation/`，详见 [DEVELOPMENT](../../DEVELOPMENT.md)。未发现新的 BLOCKER/MAJOR/MINOR；完成 checkpoint 后暂停，不进入 5B.2，不推送。

## WinUI Phase 5B.1：Session / Playtime Core & Lifecycle Engine（2026-10-05）

- **READY FOR PHASE 5B.1 INDEPENDENT GATE**（实现自检，不代替独立 Gate）。Debug/Release 全量各 **1209/0/0**，原有 1108 项逐名称全部保留，新增 101 项；两配置 build 均 0 warnings/errors、locked restore 通过。Electron **665/0/0**、build/import、artifact **32/0/0** 通过。checkpoint 后暂停，不进入 5B.2、不推送。
- 从纯设计 checkpoint `17b91810fa0db9d967a56fc426892608bb6294c1`、分支 `codex/winui-phase5` 开始。用户本轮授权将原计划的 Domain、accounting、Process Fact integration、Windows awake clock、suspend 与 recovery core 合并为 5B.1；原 5B.0 的分阶段编号为历史计划。尚未接入 WinUI、Tray、Single Instance 或正式 profile，不代表桌面产品已经启用计时。
- 已实现 `SessionEngine` 单消费者有界事件队列、独立只读进程采样、v1 native history 扩展、串行原子差额记账与恢复资格。实际 launch operation 是 SessionId；创建成功但身份未知也形成零秒 invocation；首次 durable 保存计次一次，handoff/pause/recovery/replay 不再计次。真实退出事实与保存 cutoff 分开；未知、睡眠、离线、写盘等待不补时。
- Windows 时钟使用 precise unbiased interrupt API，双时钟断点与 power callback 冻结区间；DPAPI token 绑定物理 profile、BootId、PID/creation/image identity、原安装路径身份。重开仅恢复同代进程为 CurrentProcessOnly；backup/import/多候选/身份失效全部拒绝自动接管。未改 5A Process Fact / identity contract 或 Phase 2 原子替换保证。
- legacy 四字段、ID、count、aggregate surplus 保留；未知新 schema 只读，不退回备份覆盖。可移植导入/导出清除恢复资格。旧 Electron 不能与 native 共用可写 profile，也不保证它保留 native 扩展。
- 本轮验证清单与准确结果见 [DEVELOPMENT](../../DEVELOPMENT.md) 的 Phase 5B.1 节；证据位于 ignored `.artifacts/native-phase5b1/`。Windows 合成 EXE 证明直接/多级接续、真实退出时间与管理器释放后进程仍活着；休眠/重启采用语义及身份故障注入，没有实际让宿主休眠/重启，没有本轮真实游戏验证。下方各阶段为其当时的历史记录。

## WinUI Phase 5B.0：Session / Playtime 语义设计 Gate（2026-10-05）

- **READY FOR PHASE 5B IMPLEMENTATION**。规范见 [PHASE5B_SESSION_SEMANTICS.md](PHASE5B_SESSION_SEMANTICS.md)，包含状态机、真实 Process Fact 映射、计次/时长/恢复/兼容契约及分层测试矩阵。这里只完成设计，Phase 5B production implementation 尚未开始；纯文档 checkpoint 后暂停。
- 开始时核对 `codex/winui-phase5` / `041ddca804a134aa146ee7b884d6eff596f451d0`，工作区干净。此前独立 targeted Gate 已通过 Phase 5A.1 且没有修改代码；该 SHA 仍是实现基线，下方“整改自检完成”是历史证据，不能把本次文档提交当作新的实现或二进制 checkpoint。
- 本设计规定：新 Session 首次 durable 创建计次，awake 时长排除睡眠；同 Session handoff、legacy total 保底、原子差额记账；重启同身份恢复不补离线时间且降为 CurrentProcessOnly / Limited；保留全局单 owned launch。关窗默认托盘和零秒成功启动计次是相对旧 Electron 的明确行为变化。均待后续实施验证，未操作正式数据、凭据、真实游戏或发布包。

## WinUI Phase 5A.1：Process Fact Remediation（整改自检完成）

- 可信起点 `codex/winui-phase5` / `9b9b15983124aaf3622056475037b665dd68a2e4`。独立5A Gate为NO-GO；本轮仅修M1–M4与external AlreadyRunning占用，保留分层，不进入5B。
- M1：Windows创建时原子关联非kill Job，补充连续的后代存活事实；可见且被证明是唯一存活者的当前进程形成authoritative checkpoint。最终退出要求保留句柄的真实exit、scope为空、checkpoint后无新出生及无未解决歧义。漏掉helper不能把空快照当作结束。Limited之后有界复核scope，空才释放admission，不补造exit。
- M2：snapshot capture开始时间、同一候选句柄的真实parent PID、父代identity/已确认退出区间共同约束；后续Attach仍校验完整identity。M3：cwd普通路径重新观察并比较目录file ID/final path，非等价明确拒绝。M4：Started/Exited发生时间与ObservedAtUtc分开，最终ExitOccurredAtUtc不包含6秒接续等待。
- external AlreadyRunning只保存外部身份用于后续复核，不产生owned session；同代存活/Unknown拒绝重复启动，旧代退出或PID换代后可重新走正常启动链。不写playtime、次数、history，不访问正式资料/credential，不改真实游戏文件。
- Job accounting→membership→accounting前后必须一致，6项确定性回归防跨查询混合状态；整个整改新增31项、Launch及整改96项。最终Debug/Release各**1108/0/0**，build各0warnings/errors，locked restore、Electron665/0/0、build/import、artifact32/0/0、Release publish均通过。最终10轮handle Debug653→640、Release675→654稳定；一次早期654→661阈值失败保留，未改门槛，单独及最终完整复测通过。
- 本轮A直启/退出、B39256→14568接续/主菜单/退出、C显式cwd/主菜单/正常退出通过；A/B真实OS退出时间与Application精确一致。C独立watcher的0FILETIME作废，不当作精确时间交叉验证。最后跨查询加固后，最终包额外复验B35468→31244、正常退出时间08:45:42.0073080Z一致，manager46636 exit0。A/C证据属于加固前候选，最终fixture完整覆盖，不冒充重跑。
- 隔离library逐字节不变，不写时长/次数/history；A/B游戏元数据不变，C仅既有启动日志，无新增/删除。8组UI manifest/12截图按实际两批build绑定并核查。最终包 `.artifacts/native-phase5a1/publish-final/win-x64`，MVID f5bffdd1-7798-4d10-a408-35b583972075；内嵌父SHA9b9，source/bundle hashes+receipt绑定本轮checkpoint。具体契约/入口与broker、breakaway、UNC未验证边界见DEVELOPMENT。
- **PASS FOR PHASE 5B（本轮整改自检）**；当前阶段提交即Phase5A.1 checkpoint，精确SHA见Git及本机receipt。完成后暂停，未进入5B，不替代独立Gate；下方旧5A本机通过不代表其独立Gate通过。

## WinUI Phase 5A：Game Launch & Process Continuity（本机 Gate 通过）

- 从可信Phase4 checkpoint `7965bee95c49750e26b85302da037582b96a54fc` 创建 `codex/winui-phase5`；Phase5A本机Gate通过，当前阶段提交即Phase5A checkpoint，精确SHA见Git及本机checkpoint receipt。**提交后暂停，未进入5B**。
- LaunchViewModel→GameLaunchService/serialized library admission→Windows process平台。不可变snapshot、独立launch lifecycle、最终folder/exe/cwd物理身份复核、structured ArgumentList、明确cwd、首选/本次显式安装；位置首次核对后保存在隔离机器DPAPI，非legacy/session数据。保留single-active，分开Started与TrackingLimited，PID+creation+image+retained handle防复用，有限parent/child唯一窗口接续，不选同目录最新进程。
- cancel/shutdown不kill，不写时长、次数、历史、session persistence或tray session。native候选窗口关闭真正退出，旧Electron活动窗口隐藏托盘语义留待5B；正式资料及正式程序没有接管。
- 自有fixture新增65项production-path测试通过。真实验收暴露并修复两处通用问题：关闭后迟到的Details.Unloaded访问已释放CTS（2项回归先失败）；扩展EXE路径影响旧引擎/按路径兼容行为（2项读取实际Windows命令行的回归先失败）。前者增加disposed保护；后者仅对再次验证为同一物理文件的短路径使用普通Win32写法，身份、固定句柄、structured args及安全边界不变。未修改GPU设置，无游戏名特例。
- Game-A x86直接启动、实际cwd、AlreadyRunning、正常退出检测通过。最终候选PID7904 exit0，Game-A PID44384/creation134356586222243292保持运行且窗口可用，随后游戏正常退出；此前用户同时关闭所有进程不算证据，早期manager崩溃的no-kill也不算clean shutdown PASS。
- Game-B按用户纠正使用真正游戏入口，原扩展路径只有无窗口子进程；普通路径对照成功，最终WinUI也成功：入口31608→同目录子进程28316，父子/创建时间/唯一窗口接续、主菜单、cwd、正常退出检测全部通过。GPU偏好注册项只读确认仍存在，没有直接测量最终GPU选择；不能把路径对照结果夸大为驱动根因证明。旧错误launcher与无窗口证据已supersede。
- Game-C x64资源在已有`files`子目录。仅修改隔离样本的explicit workingDirectory并重新核对位置，最终PID2572正常主菜单/实际cwd/退出检测通过，未移动资源或改游戏配置。备用Game-D只作有界选择，因B已提供真实handoff，没有执行。
- 证据`.artifacts/native-phase5a/`：26组manifest/27截图，requested/runtime、长度及SHA256已核验，无重复截图hash；`win32-b-real-menu`像素被管理器遮挡，明确INVALID，由`win32-b-real-menu-foreground`替代。最终包`win32-fixed/win-x64`，WinUI MVID db300925-99ba-455e-bc36-93e2ee5d55b3；全层bundle hashes与最终commit receipt绑定，不把内嵌父SHA7965当成本阶段commit。
- A/B目录元数据无变化，C仅既有启动日志变化；无新增/删除文件，无The Order/代理写入游戏目录的操作，不冒称内核级IO审计。隔离4条library SHA256逐字节不变，时长/次数/history未写入。未访问正式profile、API凭据、修改图形设置或替换正式Electron。
- 最终Debug/Release完整C#各1077/0/0；65启动专项、Electron665/0/0、build/import、artifact32/0/0、locked restore、两配置0warnings/errors、Release publish通过。未进入5B。UAC、DRM、broker、未观察到的快速中间进程、UNC、需要扩展语法的长路径及跨重启恢复没有扩大承诺。完整旧Electron行为表、证据版本与baseline见[DEVELOPMENT](../../DEVELOPMENT.md)。

## WinUI Phase 4R：Real API / CDN Compatibility Validation（2026-10-05 完成）

- 从已通过 targeted re-Gate 的 `codex/winui-phase4` / `c077fd257c5195e790a540f77c571269375a7f0d` 继续。**REAL API SMOKE PASS；PHASE 4 COMPLETE — READY FOR PHASE 5**。只完成本阶段并暂停，没有进入 Phase5。以下旧节中的“未验证真实 API/CDN”仍描述其历史 checkpoint，本节补充当前真实服务证据，不代表正式部署或全部真实游戏兼容。
- 只使用用户提供的专用 test credential、`.artifacts/native-phase4r/profile` 的 DPAPI 和3条合成库。未访问正式 profile/凭据/真实游戏，未替换 Electron、迁移 SQLite 或推送远程。测试凭据按授权保留于隔离 DPAPI，明文全仓审计0匹配。
- 用户修正测试 DNS 后，`image.kungal.iloveren.link` 返回真实公网 IPv4/IPv6，原生产 IsPublic 检查通过；Fake-IP、私网、DNS/IP校验、TLS、无任意跳转规则均不变。API默认代理策略和图片无代理路径在当前网络均成功；本轮没有更改系统网络设置。
- 真实 CDN 暴露静态 **WebP** 兼容缺口。获授权进入局部4R.1：仅 RemoteCoverSource 增加 `image/webp`，WindowsRasterCodec 增加有界 RIFF/WebP 静态容器校验并使用原 Windows WIC 解码。动画/坏长度/超大画布拒绝，20MiB/4000万像素/目标边长/并发边界保留。新增10项生产路径回归，先3失败7通过，再全部通过；无依赖升级、识别规则或事务修改。
- 真实鉴权、日文/中文/零结果搜索、分页20→40、详情133/15/919的请求=响应=canonical ID、3张真实WebP、缩略图与A→B→A缓存通过。无效ID为HTTP400/INVALID_PARAMETER→InvalidRequest。实际可选null/缺失cursor等被原DTO正确处理，无schema修改；canonical merge、429、其他CDN格式未自然遇到，不主动枚举或制造限流。
- 最终发布包真实WinUI：连接/搜索/分页/详情/封面/识别/Review/Apply/Manual/重启通过。千恋万花15明确候选0.985、3独立来源一致；Summer Pockets4候选，版本不确定/编号冲突正确阻止Apply。隔离Apply15、明确Manual4成功，错误Manual零库写入；重启个人名称、笔记、评分、分类/标签、安装和游玩数据保留，UTC时间戳仅格式规范化；原合成库没有个人封面，不冒称非空个人封面实测。
- 真实响应受控延迟的生产VM harness验证快速换查询、取消、旧分页、移除/替换同一凭据；旧上下文不接纳，UI也验证远程候选撤销而本地证据保留。最终包活动关闭另使用仅候选进程的透明TLS延迟通道，未解密/更改响应或TLS校验，真实连接处于Loading时关闭、exit0，通道已停止；普通综合smoke仍用正常网络。早期两个关窗观察有采样间隔，不能单独证明活动关闭。
- locked restore、Debug/Release build各0 warnings/errors，完整C#各 **1012/0/0**；102 original、13 filesystem/cover、32 online、254 recognition差分及14fault保留。Electron完整 **665/0/0**、build/import回归、Release publish通过。
- `.artifacts/native-phase4r/` 保存 **13组最终截图/tree/manifest**，requested==runtime、SHA256及构建MVID核对；`real-service-harness.json`只有脱敏schema/状态摘要，不保存原始API响应。旧Fake-IP blocker及封面失败证据由本节/最终证据supersede，保留为历史。最终包嵌入父SHA c077，使用build-identity/提交后receipt绑定本次checkpoint，不能声称嵌入最终commit。
- 剩余边界：自然merge/429未遇到、离线以确定性回归为准；WIC/WebP在当前Windows验证，干净机codec/发布签名升级仍需后续验证。此前UNC/reparse、10k unique images及正式库写入前workingDirectory门禁继续保留。详细证据见 [DEVELOPMENT](../../DEVELOPMENT.md)。

## WinUI Phase 4B.1.1：Final Recognition Gate Remediation（整改自检完成）

- 基线 `codex/winui-phase4` / `41fa1e0a573d83f232e3d5e8d2d2011bf884acc6`。Phase4B.1 targeted re-Gate实际判定 **NO-GO**，本轮只修剩余M1/M5；本节 supersede 下方旧M1/M5完成及放行声明，其余已通过整改保持不变。没有改评分/veto或Review UI，没有进入Phase5。
- **M1** native PE资源选择、标题去重/排序均在有界截断之前；每程序16标题/8公司、总40标题和IO上限不扩张。`pe-evidence-v2`使用实际返回的有效有序输入，`native-local-evidence-v3`还绑定实际总40条输入；algorithm身份`electron-recognition-v1-native-4`。旧Electron raw PE fingerprint及`electron-review-identity-v1`不变。39目录+chapter1/2原反例现在重排后digest/input/ranking/veto一致；真正有效标题变化拒绝旧Apply。
- **M5** 明确Uninitialized/Initializing/Ready/Failed。初始化前live结果保留在原有有界queue中，不发布到Review字典；完成读取后同一gate取最新generation状态，与历史合并并统一retirement/capacity/结构检查，最后一次性发布。容量失败保留合法基线并显式ReviewCapacity；取消可重试，读取损坏降级内存且原文件保留。决定入口等待初始化，Flush不会写入初始化期间的空快照。原active保护及256历史退役策略不变。
- 新增**14**项production-path/integration回归，覆盖PE排列/资源上限/实际40条截断、旧Gate反例与Apply，及暂停near-16MiB读取、live接纳、10000历史、多generation/双初始化、容量拒绝、初始化失败/取消、随后Ignore/Apply。完整Debug/Release C#各 **1002/0/0**，保留既有988及102/13/32/254差分组与14fault；两配置build各0 warnings/errors，locked restore、Electron **665/0/0**、build/import regression、Release publish全部通过。
- 证据 `.artifacts/native-phase4b11/`：完整TRX/日志、`validation-audit.json`、`build-identity.json`及提交后的receipt；发布程序仍嵌入父SHA41fa1e0a，需hash+receipt绑定最终checkpoint。没有新增桌面smoke；真实Windows有界reader及production集成测试证明本轮反例，旧UI artifacts只证明旧场景。详细契约与边界见 [DEVELOPMENT](../../DEVELOPMENT.md)。
- **PASS FOR REAL API SMOKE（仅本轮self-check）**。没有开始真实API smoke、使用真实credential/正式profile/真实游戏；不代替独立re-Gate，也不代表真实catalog/CDN或干净机发布验证。建立checkpoint后暂停，不进入Phase5。

## WinUI Phase 4B.1：Recognition / Review Boundary Remediation（整改自检完成）

- 从 `codex/winui-phase4` / `7da6eebab3c79b66af65e628daad8ae1788644d4` 整改独立Gate的M1–M5及两个MINOR。保持分支，未进入Phase5。本节 supersede 下方旧Phase4B关于证据完整性、候选详情绑定、provider自动名称、历史校验与预算已经充分通过的判断；下方同名“4B.1内部checkpoint”是旧开发子步骤，不是本轮整改。
- **M1** 保留Electron兼容PE fingerprint和`electron-review-identity-v1`；新增`pe-evidence-v1`（消费的四字段、presence/effective output、Ordinal排序、SHA256、原资源边界），纳入`native-local-evidence-v2`。Apply在原事务beforeCommit重新读取证据，PE原地变更即Stale/zero writes。LegalCopyright等未消费metadata不参与新摘要；资源顺序/culture不影响相同有效证据。algorithm为`electron-recognition-v1-native-3`。
- **M2/M3** 候选选择独立generation，切换即取消并清空详情；接纳绑定review/candidate/selection/request/auth。Reidentify只把NameIsCustom=true的个人名作为名称证据；有旧metadata的false/null自动名不进入scoring，原authoritative Input.Entry仍保留。reader继续提供目录、程序、配置/说明和索引来源。
- **M4/M5** historicalCandidates即使Result=null也验证非null/编号/有界文本/有限0–1分数/数量，live result校验证据和veto状态。坏Review保留原文件且持续显示明确告警，不覆盖、不冒充空队列，Library可用。接纳前对拟定状态精确有界序列化：10000总条目/16MiB/5候选；保护NeedsReview，Stale/NeedsRecheck按CreatedAt再Guid最旧先退役，历史最多256条且必要时再缩减。空间仍不足则显式ReviewCapacity且旧状态不变；启动、失效说明及Manual清理同样先预算，已提交后清理错误仍是CommittedWithWarning。
- **两个MINOR** VersionNeedsConfirmation与confirmed conflicts分开，但均阻止不安全直接Apply；凭据epoch变更只撤销remote enrichment，保留current local identity/candidates/evidence，recognition generation/lifecycle变更仍使全结果过期。
- 新增**44**项production-path整改回归；locked restore成功，最终Debug/Release build各0 warnings/errors，C#各**988/0/0**。保留102 original、13 filesystem/cover组、32 online、254 recognition差分及14 fault；artifact tests **32/0/0**；Electron完整**665/0/0**、build/import回归通过，Release publish成功。`test-audit.json`核对两份TRX；未升级依赖或修改Electron。
- 真WinUI隔离验收：200已加载切201、延迟200切201后迟到丢弃、Apply精确保存201、FD显示“版本信息不足，需要确认”、重新识别旧provider标题排除、模拟凭据变更保留Summer Pockets本地索引、Ignore和Manual均完成。损坏历史原SHA256不变且新识别5项完成；容量10000历史→256→接纳5项261→Apply260→Ignore259→Manual258（256历史+2待核对），个人名称/笔记保留。最终两个实例exit0。PE zero-writes/ABA/最终publish前auth时点等由确定性集成测试证明，不冒称真实游戏/API操作。
- `.artifacts/native-phase4b1/`保存**11组manifest/UI tree/JPEG，11个不同截图hash**，requested==runtime、树状态、长度/SHA256已独立核对，并目视复核关键文件。8组早期publish证据覆盖候选/版本/名称/auth/普通决定，3组publish-final证据覆盖损坏提示及容量/决定最终修复；范围和全层DLL/EXE hash见`build-smoke1.json`、`build-final.json`及DEVELOPMENT。最终WinUI MVID `7d69294c-61ee-411f-baad-1df14773ac6e`；二进制内checkpoint仍是父SHA，不能当作最终commit身份。旧Phase4B证据不证明本轮整改，旧Phase3A作废证据继续作废。
- **PASS FOR PHASE 5（仅Phase4B.1整改自检）**。独立targeted re-Gate和其后的真实API smoke尚未执行，需按既定顺序继续；本轮未访问正式profile、真实credential或游戏目录/执行。合成HTTP不代表真实catalog/CDN、长期压力、全部真实游戏或干净机发布；旧UNC/图片/workingDirectory门禁仍保留。完成checkpoint后暂停，**不进入Phase5**。设计、production位置、回归及精确artifact范围见 [DEVELOPMENT](../../DEVELOPMENT.md)。

## WinUI Phase 4B：Metadata Recognition & Review Queue（历史本机验收；独立 Gate NO-GO，整改见上）

- 用户已授权 Phase4B，保持 `codex/winui-phase4`，起点 `0de0b342623c0e6a047fd354843903779e564e2e`；不得进入 Phase5。下方 Phase4A.1 的“无 Phase4B”仅描述该历史 checkpoint。
- 已重新审计实际 Electron recognition/local evidence/review/manual/merge/lifecycle 源码，行为地图留在 DEVELOPMENT.md。先完成 4B.1 内部 checkpoint `7f858ad54b6d90e2668ffd24dfdf2bcffc3fd0a2`，再实现 4B.2；本节 supersede 其“仅核心、未接入 Apply/UI”的中间状态。
- 分层为 Windows 有界证据读取 → Infrastructure 纯识别 → Application 只读双 worker 队列 → 持久化 Review → 显式决定服务 → 原事务内核。RecognitionEngine 不持 writable repository，Presentation 不直接读文件/JSON/HTTP。WinUI 提供单条、所选、只补缺失、重新识别、取消、核对、Apply、Ignore、Manual Link，所有高分仍需确认。
- 最终算法 `electron-recognition-v1-native-2`。原标题/证据 helper 保持 Electron oracle；独立 ReviewSafeRanking 对 FD/remake 等显式版本与无版本目标的歧义增加 veto，不改变原分数。索引沿用 56,064 records / 97,038 titles 及许可，惰性有界加载；128候选饱和拒绝唯一身份推断，结果最多5项。源指纹 `electron-review-identity-v1` 保持 JS JSON UTF-16 契约；证据指纹 `native-local-evidence-v1` 覆盖所读字节和物理身份，两者均不是提交权限。
- `native-recognition.json` 保存有版本的建议，旧 `metadata-matching.json` 仅作为历史输入；重启后 NeedsRecheck，坏文件保留并禁止覆盖。Apply 重验 generation/lifecycle/auth、canonical detail、发行/证据与最终本地 fingerprint，再进入原串行原子事务；个人字段保留，已持久提交后的附加错误不会伪装未保存。Ignore 只删除该 review ID；Manual 仍走 canonical+token；重新识别保留旧关联，旧建议失效。
- 最终 locked restore、Debug/Release solution build 各0 warnings/errors、C#各 **944/0/0**（原609+新增335）。新增包含254实际Electron差分、六指纹、三例完整 golden 流程、状态/持久化/Apply/UI回归；保留原102 transaction differential、13 filesystem/cover、32 online、14 fault。Artifact tests **32/0/0**；Electron完整 **665/0/0**、build/import regression通过；Release publish成功。性能工具默认不跑，另以显式开关实际完成1/100/1000本地与合成HTTP基线，详见 DEVELOPMENT，不是SLA。
- 真实 WinUI 隔离合成 smoke 完成：单条/批量、offline/local index、多候选、veto、Apply/Ignore/Manual错误ID与成功、重新识别、stale、restart、活动取消/迟到响应、活动关闭。实测修正卡片反复重建导致选择高亮不同步、详情残留、快照误标“当前关联”和 stale进度计数；最终包重新核验含键盘选择。活动关闭PID42136 exit0，约0.52秒；再次正常关闭PID31524 exit0。
- `.artifacts/native-phase4b/` 为 ignored 本机证据：**23组manifest/UI tree/JPEG，23个不同截图hash**；9组早期publish、14组最终publish-final。requested/runtime、tree、字节数/SHA256逐项校验。早期`multiple-candidates`的高亮问题由`final-multiple-candidates` supersede，早期Apply/Manual截图不能证明最终清理/计数修复；最终场景另有明确证据。最终MVID与各层DLL hash见build-bundles及manifest，程序集checkpoint仍指向构建前4B.1，不冒称最终提交已嵌入二进制。
- 正式资料、真实 credential、真实游戏目录/执行均未访问。真实 API **SKIPPED — no safe development credential**。合成HTTP不证明真实catalog/CDN、全部真实游戏识别或长时间稳定；干净机器发布/签名/升级及既有UNC/filter环境缺口仍在。**可进入独立 Phase 4B Gate Review；完成本阶段checkpoint后暂停，不进入 Phase5。**

## WinUI Phase 4A.1：Online Result Acceptance Remediation（整改自检完成）

- 起点 `f737bf7c4174cdabe66d25ffa60629d939ca69dd`，保持 `codex/winui-phase4`。Phase4A Gate 为 NO-GO；本节 supersede 下方历史阶段关于 credential lifecycle、detail identity 和结果接纳完整性的判断。仅修 M1–M3、corrupt-vs-missing 和 DNS callback 测试覆盖，无 Phase4B、正式数据/凭据操作或识别功能。
- M1：`AuthenticatedCredentials` 按调用次序执行 save/clear，每次用户意图立即推进共享进程内 auth epoch 并取消旧上下文；请求等待已排队 credential mutation 完成。失败的旧保存不丢弃新保存。Presentation 分离连接测试和凭据操作；旧测试即使忽略取消后完成也不能覆盖新状态。PasswordBox 仅在当前保存成功且输入未再变化时清空，失败保留输入，删除失败不宣称已移除。
- auth generation 由 composition root 的共享 adapter 提供；connection/search/details/cache 均通过 transport 捕获、取消和接纳检查，不要求每个 caller 手动 ClearCache。详情缓存带 epoch；匿名图片不无意义清空，秘密或其 hash 不作为认证缓存身份。Phase4B 新调用者必须复用该 adapter/transport，并保留自身用户意图和事务 token 检查。
- M2：最终 `work.Id == current`，`current` 仅由合法 ENTITY_MERGED 修改。直接86→work90及多跳终点不符拒绝为 InvalidResponse，无错误缓存、alias 或库写入；这是对 Electron 盲点的明确加强，不退化为旧行为。合法3跳/防环保留。
- M3：EOF、parse、map、cache admission、final return 检查取消/45秒预算；统一最终接纳在 auth 锁中完成，失败回滚本次 provisional cache entry；缓存读取也使用该锁，不暴露尚未最终接纳的条目。保留 DTO/identity、cache generation、auth generation 检查、8MiB/严格UTF-8/最多2次重试。
- credential 只有文件不存在表示缺失；空、超大、坏DPAPI或坏格式为 CredentialStorageCorrupt，检查不自动删除/覆盖。当前仍为 **raw DPAPI blob，无应用层版本 envelope**；Phase6/migration 前评估 versioned envelope + legacy raw-DPAPI compatibility。图片生产 DNS/IP/无代理/无自动跳转策略不变，仅增加 resolver/connector seam，测试真正穿过 HttpClient 的生产 ConnectCallback，不连接内网。
- 最终 locked restore 成功；Debug/Release build 各 **0 warnings / 0 errors**；完整 C# 各 **609/0/0**，新增52项 OnlineAcceptance 回归。原102 transaction differential、13 filesystem/cover differential、32 online differential、14 fault injection 全通过。Artifact **24/0/0**；Electron 完整 **665/0/0**（含 relevant），build/import 通过：37新增、重复0新增/37跳过，个人字段/重启/游戏文件保留，renderer errors0。Release publish 成功。
- 真WinUI使用合成 HTTP + **实际隔离 DPAPI store**，验证损坏提示、保存失败保留输入、慢连接时删除/替换、超过旧响应窗口仍不回退、错误ID、取消后重新发HTTP、正常搜索2→分页3/详情/合成远程封面、愿望单3→4且重复仍4、活动请求关闭exit0。EOF精确取消、deadline、失败clear及不合作迟到回调由生产路径/Presentation确定性回归证明，不冒充真实服务桌面测试。
- `.artifacts/native-phase4a1/` 保存 ignored 证据：**19组 JPEG/UI tree/manifest**，requested/runtime、tree、文件长度及SHA-256一致，19个不同截图hash。初批11场景在 `publish`，最终8场景在 `publish-final/win-x64`；最终复核clear、正常流程、错误ID、取消/重新请求与关闭。最终 WinUI MVID `4f81d976-2eb5-43be-8ec1-67ed3d5c6d87`；两批各层hash及范围在 `build-bundles.json`，不能声称全部截图来自最终包。旧4A的26组为历史功能证据，不证明本轮整改；旧3A作废证据仍作废。
- **PASS FOR PHASE 4B（Phase4A.1整改自检）**，不是Phase4B实施授权。真实API：**SKIPPED — no safe development credential**；真实API/CDN/代理/WebP兼容、长时间故障压力、干净机安装仍属后续Gate。Phase3的UNC/generic reparse/10k unique images和正式库写入前workingDirectory降级门禁仍保留。完成checkpoint后暂停。详细位置、回归与证据见 [DEVELOPMENT](../../DEVELOPMENT.md)。

## WinUI Phase 4A：Online Data / API / Remote Assets（历史本机验收；后续 Gate 与整改见上）

- 从可信 Phase 3B.1 `9ae2199aba6aedbb8e8012ab38e4a3ec8eb1a1a2` 新建 `codex/winui-phase4`。本节为当前实现；下方历史阶段的“无 Phase 4 / 远程图片尚不下载”不再描述当前候选。没有进入 Phase 4B、读取正式资料或真实 credential、扫描或执行真实游戏。
- 已实际审计 `electron/nextmoe.ts`、settings、library 与 Vue 在线入口，并以当前 Electron 生成32个在线 oracle。Application 定义在线/credential/cover 接口；Infrastructure 分离 transport、DTO mapping、catalog/cache、remote source；Platform.Windows 提供隔离 DPAPI；Presentation 管理请求 generation/取消/显式状态；WinUI 只负责输入和展示。保留事务、filesystem、图片管线和七项目依赖方向，无依赖升级。
- 固定正常 API endpoint，长寿命连接、45秒共享 operation deadline（搜索两步、重试和合并均消耗同一预算）、最多2次重试，仅502/503/504与429 `RATE_LIMITED`。8MiB流式上限，严格UTF-8/JSON；状态分类、响应/stream释放、有限 Retry-After。HTTP跳转不跟随；作品 `ENTITY_MERGED` 最多3跳且防环，返回canonical string ID。
- 原生发现页提供搜索/取消/重试/分页/详情；去重、查询与详情generation防旧结果覆盖新意图。愿望条目显式加入，经旧事务内核保存；重复ID不增条目，无安装；资料刷新保留个人字段并检查请求token；删除只移除无安装的书架关系。移除/刷新/并发个人编辑的完整语义由生产服务集成测试验证，不冒充全部桌面点击覆盖。
- credential仅隔离目录 `native-online-key.dpapi`、当前Windows用户DPAPI、原子写入、受限读取；不读取旧safeStorage或真实密钥，不写明文，UI只暴露保存状态。连接测试必须同时通过鉴权和目录响应校验。日志结构只允许method/host/status/elapsed/retry/requestId；错误不带响应体、完整URL或密钥。
- 远程图只接受已注册API/库来源的HTTPS公开DNS目标；无IP/本地或内部地址、userinfo、非标准端口、cookie/auth或自动跳转。DNS解析检查后直接连接已检查IP，绕过系统代理以免绕开目的地校验。20秒/20MiB，PNG/JPEG复用现有WIC像素上限与stale-image保护；WebP等仍占位，未声称全格式支持。个人封面优先；URL+revision哈希与decode bucket区分缓存，不把URL写文件名。详情缓存32项/8MiB/5分钟，远程原图64项/32MiB/10分钟，派生缓存沿用64MiB内存/128MiB磁盘，缓存丢弃不删除用户资料。
- 最终 Debug/Release build各0 warnings/errors，C#各 **557/0/0**（原472+85）；保留102 differential、14 fault injection、13 filesystem/cover differential；新增在线 differential **32**。Artifact **24/0/0**，Electron relevant扩展到 **252/0/0**，Electron build/import通过：37新增、重复0新增/37跳过、个人字段/重启/文件保留、renderer errors0。Release publish成功。
- 真WinUI在隔离合成库验证：启动、搜索2项、分页去重到3项、详情和远程封面、坏图占位、空/401/429/离线、加入愿望单及重复、重启保留4项、取消、A→AB及超过旧响应窗口后不回退、活动请求关闭exit0。关闭动作至退出约0.36秒，非性能SLA。正常装配（ONLINE_FIXTURE关闭）无凭据重启，缓存封面和书架可用；连接测试明确提示缺少密钥。真实API smoke因没有安全可用开发凭据明确跳过。
- `.artifacts/native-phase4a/` 为ignored证据：**26组 JPEG/UI tree/manifest**逐项requested/runtime/hash核对，26个不同截图SHA-256。`search-active-before-cancel`仅早期诊断，不证明取消通过；最终 `checkpoint-*` 六场景复核启动、加载、改词Idle、取消与活动关闭exit0。最终包 `publish-checkpoint/win-x64`；WinUI MVID `f0c3cc82-958e-4e2f-9bcb-66f778c83ee0`，Presentation SHA-256 `1884A3CC532FB41D5D15F25E3F7F0E62BEAD87DE089CB7C6D0539538B1C6ED6A`。最终两处状态重置修复有新增回归，三批构建差异/各层hash见DEVELOPMENT及 `build-bundles.json`，不能仅用WinUI MVID判断整个Solution相同。此前Phase3A错误artifacts仍作废。
- **Phase 4A 本机验收自检通过，可进入独立 Gate Review；不是正式接管或 Phase 4B 实施授权。** 剩余边界：真实API/CDN/代理环境与WebP兼容、长时间网络故障压力、干净机安装未验证；既有UNC/generic reparse/10k unique images边界保留。`workingDirectory`降级写入门禁最迟Phase6且必须先于正式库写入，不能用备份提醒代替。完成checkpoint后暂停。

## WinUI Phase 3B.1：Filesystem Boundary Remediation（整改自检完成）

- 基线 `e75663c42fff9b0eea194cbca7d2706cbae45412`，保持 `codex/winui-phase3`。Phase3B独立Gate曾判NO-GO；本节supersede下方历史阶段关于扫描边界/未知identity/候选连续性的完成判断。只修复M1–M3、Depth、Navigation，无Phase4或正式数据操作。
- M1：root observation固定，queue携带子目录observation；dequeue/枚举边界检查根当前identity、卷、组件范围和reparse链。Windows枚举在lexical/final祖先链短期句柄保护后重验；根变化整次失败，子级替换/额外reparse跳过提示，root alias仍支持。不是绝对文件系统sandbox。
- M2：ConfirmedUnique / ConfirmedDuplicate / Indeterminate / NotChecked明确分开。不同字符串的不可比较历史安装阻止自动导入，保留记录并展示路径/状态；恢复后“重新检查当前目录”。无人工强行放行，比Electron更保守；同路径仍保守判重复。
- M3：PreparedInstallation保留原ReviewedCandidate，预处理与final precommit确认它仍有效，不能把A的核对依据替换成B。变化返回CandidateStale；99 stable + 1 stale整批零写入。原路径拼写、一次原子事务与窄TOCTOU边界保留。
- 深度6且仍有未入队目录时ScanResult明确IsPartial / DepthLimit，保留有效候选，零候选不假装完整扫描。InstallationNavigation generation防止晚加载夺回Library/Settings，连续请求最新者生效。
- 新增32项：FilesystemBoundary27 + Presentation5；真实临时目录/junction/symlink/rename/删除/断链测试，生产OfflineFilesClient/事务＋计数persistence验证零写入。Navigation为生产Presentation集成验证，没有新桌面截图。Debug/Release各472/0/0、build各0 warnings/errors；保留102 differential、14 fault、13 filesystem/cover differential；Artifact18、Electron relevant183、Electron build/import（37新增、重复0/37）通过；Release publish成功。
- `.artifacts/native-phase3b1/`保存本轮ignored证据。旧38组Phase3B UI evidence不证明本轮竞态；未重采图片baseline或修改图片架构/decoder/cache预算。UNC/generic reparse真实环境仍未验证；10k unique images留到Phase7。
- **workingDirectory降级保护最迟Phase6，且先于首次写正式库**：必须有独立迁移库/版本或写入门禁，阻止旧Electron无提示回写丢字段；备份提醒不能替代机制。本轮不做migration。
- 五项整改自检通过：**PASS FOR PHASE 4（整改自检）**。建立Phase3B.1 checkpoint后暂停；不开始Phase4，不访问正式资料/真实凭据，不执行或修改真实游戏。用例及验证限制见 [DEVELOPMENT](../../DEVELOPMENT.md)。

## WinUI Phase 3B：Filesystem / Installations / Image Pipeline（历史本机验收；后续 Gate 与整改见上）

- 用户在 Phase 3A.1 Gate 后正式授权本阶段；沿用 `codex/winui-phase3`，起点 `55069ac4fddd1491e3122fd4c69a24487a3b2d86`。本阶段 checkpoint 后暂停，不进入 Phase 4。下面旧阶段的“尚未进入 Phase 3B”是历史停点；当前实现和限制以本节及 DEVELOPMENT 的 Phase 3B 节为准。
- 七项目分层未推翻，无 NuGet/npm 升级。Application 增加 filesystem/preflight、bounded scanner、安装和个人封面事务、image/source/codec/cache 接口；Platform.Windows 实现 Win32 handle identity、顶层枚举、WIC decode/normalize、隔离派生缓存；Presentation 管理扫描/草稿/受跟踪 mutation 和请求 generation；WinUI 使用标准 picker 与复用安全的 CoverPresenter。IO/扫描/identity/解码均在后台，最终像素上传在 UI。
- stored user path、display path、final path、卷号+128-bit file ID 分开；保留原始路径拼写，基于真实身份判断相同目标/alias 重复，保守拒绝安装范围重叠。Preflight 有十类可解释结果；写盘前重验 snapshot。未知 reparse 或可靠 identity 不可取得时拒绝新关系，不把字符串当替代身份。目录离线只显示状态，保留历史安装。最后重验到写盘仍有窄 TOCTOU 窗口；未来启动必须再验证。
- scan BFS 上限：深度6、目录5000、目录项50000、候选2000，队列有界，子 reparse 不跟随、identity 防重复、取消和节流进度；本阶段不读取 metadata/text、不执行 exe。用户先核对候选再确认一次批量事务；100项测试只保存一次。支持多安装、结构化 args、exe、范围内 working directory、首选切换、只移除关系。安装草稿有 stale token，已保存+刷新失败不变成未保存，不重复提交。
- personal cover 继续在 v1 JSON 保存 Base64 JPEG，最多128KiB；不放入可丢缓存。PNG/JPEG输入≤20MiB、单帧/≤4000万像素、decode edge≤1600，按控件尺寸和DPI请求缩略图；有效IHDR的2.25亿像素测试明确在pixel decode前拒绝。默认并发2、64MiB LRU、128MiB派生磁盘缓存；坏图占位、旧结果不能覆盖复用后的条目，退出取消/drain。pending tasks没有另设全局硬上限，后续可调优；真实远程图片尚不下载。纯cover priority已差分，不能说在线封面系统完成。
- NativeLibraryCodec 保留 legacy 校验与原始安装路径；workingDirectory 为 optional 扩展。旧 Electron 会忽略且在重写时丢掉 workingDirectory，回退前须保存候选 profile 副本，两端不能共用同一可写 profile。更严格的 identity/范围/队列/候选规则是记录在案的 intentional differences，未迁移正式资料。
- 实际锁定 restore、Debug/Release build各0 warnings/errors、完整 C#各 **440/0/0**（保留102 differential/14 fault）；新增61项覆盖filesystem11、offline-files17、filesystem differential13、images10、presentation10。Artifact tests **18/0/0**；Electron relevant **183/0/0**，build/import regression通过：37新增、重复0/37、个人字段和重启保留、文件未删、renderer errors0。Release `publish-final/win-x64` 已实际启动/退出0。
- identity 实际验证 lexical别名、长路径、junction/断链、symlink、8.3、subst、hardlink、rename/替换、ACL拒绝。`filesystem-capabilities.json` 将 UNC（没有获授权synthetic SMB share）、generic filter reparse（无可创建fixture）明确列为 **skipped**，不能用全套xUnit的0 skipped冒充这些环境通过。scan loop/unreadable/disappeared另有可控适配器注入，不等于所有OS环境验证。
- 真 WinUI smoke覆盖目录picker→扫描→取消→再次扫描→核对→导入2条→重复0/1，多安装/参数/工作目录/首选/失效路径、本地封面/坏图保留、坏封面占位、Grid/List、重启持久化、scan/image活动关闭。用户额外指定 `D:\entertainment\PC` 后仅只读扫描36候选，未导入；隔离库仍2条synthetic。正式 `%APPDATA%\KunGalManager`、真实凭据、NextMoe、game launch/playtime、Liquid Glass、SQLite均未访问或实现。
- `.artifacts/native-phase3b/` 是本机生成证据，不提交。38组截图/manifest逐项hash/runtime核验，38个不同截图hash；`debug-multi-install` 专门保留为草稿区残留bug诊断，由 `fixed-edit-draft-closed` supersede。其余场景用途、早期/最终build范围见DEVELOPMENT。最终WinUI MVID `c923ac56-5f7e-47f4-8970-c27611d5d95d`；最终与大库采集版只有Settings阶段标签的产品源码差异，五个核心层DLL字节相同，源文件/DLL/XBF/PRI清单保存以便复验。原Phase3A无效截图不恢复可信；Phase3A.1有效证据保留。
- **Phase 3B baseline，非SLA**：1k/5k/10k带四张重复40×60真实JPEG，viewport1042×455.5；Grid首容器848.4/2359.1/3090.0ms，Grid容器45→51/45→46/45→46，List28→30（全部规模），Grid/List首屏working set295.4/342.7、373.9/427.1、451.7/485.1MiB。10k首次窗口被用户关闭，后续滚动/List来自磁盘缓存复测，Grid3014.2ms/462.8MiB。图片最高并发2、稳定后outstanding0/errors0，10k往返滚动与Light/Dark均实际检查。容器不随库规模线性增长；5k/10k读取时间和内存高于Phase3A.1无图片基线，不宣称正式性能SLA或万张大原图压力通过。
- **本机验收完成，可进入独立 Phase 3B Gate Review；暂停等待下一阶段范围。** 仍缺UNC/未知filter真实环境、真实大库/长时间运行、干净机安装签名升级、断电证据；本阶段不是正式资料接管/联网/启动授权。完整架构、语义差异、baseline、复验命令与artifact列表见 [DEVELOPMENT](../../DEVELOPMENT.md)。

## WinUI Phase 3A.1：Presentation State Remediation（验收完成）

- 基线 `codex/winui-phase3` / `29e5577c0e82384b66ce9d12fd6b06b996dc8b9c`；仅整改 M1–M5、Search lifecycle 和 Selection contract。没有 Phase 3B 授权，不访问正式资料、真实凭据或游戏目录。
- Presentation 保留原分层和事务内核：详情响应检查请求 generation、草稿修订号和同条目 lifecycle；偏好有 Uninitialized/Loading/Ready/LoadFailed，启动操作按字段合并，读取失败后本次运行只作临时修改；完整批量操作从解析起受关闭等待；PersistenceResult 与 LastRefresh 分开，提交后的刷新故障明确提示已保存。
- 选择契约为明确清空：Grid/List、查询/排序/筛选、Reload、projection 替换和重新进入 Library 都清空选择；隐藏后重现、删除/同 ID 恢复均不自动恢复选择，也不继承旧草稿 token。
- **旧 Phase 3A UI 证据作废**：`.artifacts/native-phase3a` 的 31 PNG / 31 UI txt 都是初始 1k Dark Grid 快照。已查到 `saveState` 闭包绑定旧 REPL cell 的 `state`，并以两次 cell 调用复现。旧首屏/容器表不能作为验收依据；build/TRX/Electron 记录不因此作废。
- 新采集入口 `native/scripts/capture-ui-evidence.mjs` 每次自行读取当前窗口；先比较期望场景与应用运行时 metadata，再保存 screenshot、UI tree、含时间/进程/MVID/checkpoint/状态/容器/内存/文件 hash 的 manifest。场景不符、快照过期、无可视容器均失败；不再闭包引用外部快照。
- 重启 Codex 后桌面工具恢复，已实际完成 M1 新草稿抗延迟覆盖、M2 启动偏好合并并保留已有筛选、M3 真实事务提交后的刷新故障、M4 后台预处理期间关闭等待/放行提交及主动取消、Search active → Reload failure，以及 Grid/List/Search/Filter/Reload 选择清空。批量取消实测发现提示误称保留草稿，已改为明确未写入，不改变取消或事务行为。CommittedWithWarning + RefreshFailed 有确定性回归，未冒称真实桌面故障注入。
- 真实 UI 新证据位于 `.artifacts/native-phase3a1/ui`，逐场景 manifest 与 UI metadata、文件 SHA-256 已交叉核验；旧 `.artifacts/native-phase3a` 截图继续作废。1k/5k/10k 均为 Grid **45→51**、List **29→31**（首屏→快速滚动后）；Light/Dark、Normal/Read-only/Corrupt/Empty/Zero-results 独立场景齐全。原生发布版本实际启动、滚动、持久化、重启及正常退出。baseline 与构建身份分层见 DEVELOPMENT，不是 FPS 或正式性能 SLA。
- Debug/Release build（各 0 warnings/errors）、C# full（各 **379/0/0**；新增 30，原 102 differential / 14 fault injection 保留）、artifact tests **10/0/0**、Electron relevant **130/0/0**、build/import 与 Release publish 通过。验收只使用隔离合成库，无正式资料、真实凭据、API 或游戏启动。**PASS FOR PHASE 3B** 表示本阶段 Gate 就绪，不是执行授权；建立 Phase 3A.1 checkpoint 后暂停，不进入 Phase 3B。

## WinUI Phase 3A：Library & Personal Data UI（2026-10-04，候选待 Gate）

- 从可信 `codex/winui-phase2` / `d0702cafe1b6762069d63fe08bcba07b199ca713` 建立 **`codex/winui-phase3`**。本节为当前状态，下面 Phase 1/2 的“尚未接入 UI writer”是历史状态。本阶段只开放带既有合成标记的隔离目录；没有访问正式 `%APPDATA%\KunGalManager`、凭据、真实游戏或联网目录 API。完成 Phase 3A checkpoint 后暂停，**没有 Phase 3B 授权**。
- Solution 新增纯 .NET **TheOrder.Presentation**，现为七项目。调用路径为 `WinUI View → WorkspaceViewModel / PersonalDraft → IOfflineLibraryClient → TransactionalLibrary → LegacyLibraryCodec / IsolatedLibraryPersistence`。View 不调用 repository；Presentation 无文件 IO、JSON serializer、HttpClient、Win32 或具体持久化实现。composition root 负责装配，Theme Resources 负责基础外观，业务层不依赖 XAML。
- Library 使用默认虚拟化 GridView/ListView、受限高度 viewport、轻量 GameSummary、按需 GameDetails 和封面占位。详情只开放个人名称、评分、状态、笔记、分类、个人标签和收藏。数据不从 binding 直接写入 authoritative snapshot；Save 经事务后刷新，Cancel 丢弃草稿。导航、关闭、重新读取保护 dirty draft；旧 epoch、删除/恢复 generation 和个人字段冲突均拒绝过期提交。批量状态/收藏/分类/标签最多 1000 个 ID，一次 candidate、一次 durable transaction。
- 搜索索引和查询在后台工作，120 ms 输入防抖与 cancellation/generation 防止旧结果覆盖新查询。迁移旧五种排序、五种库视图、状态/收藏/分类/个人和目录标签/最低评分筛选。实际 Electron oracle 提供 **38 查询 + 5 标签拆分 + 8 批量差分**；NFKC、Unicode lower、AND 搜索项、标签大小写/次序/追加/删除保持参考行为。排序使用 zh-CN 名称比较，ID 使用 en-US 比较；未声称任意 Windows/NLS/ICU 版本的所有 Unicode 排序等价。
- 原 legacy v1 和提交内核不变；Application 新增个人草稿 token 与有限批量用例。`native-ui.json` 单独保存主题、Grid/List、排序和显式命名筛选；临时查询不自动保存，损坏偏好不覆盖旧文件。正式 Electron localStorage 迁移后置。只读能力来自统一 LibraryCapability；所有个人编辑入口禁用。CommittedWithWarning 显示“修改已经保存，但存在附加警告”，不自动重试；失败保留草稿；损坏库显示“尚未加载书架”，不显示为真实空库。
- **实际验证**：locked restore、Debug/Release 全 solution build 均 0 warnings/errors；C# 两配置各 **349 passed / 0 failed / 0 skipped**，比 Phase 2.1 新增 79，原 102 differential / 14 fault injection 保留，新增 UI differential 51。Electron 7 个相关单元文件 **130/0/0**；build + desktop import 37 新增/重复 0 新增、37 跳过/个人字段与重启保留/renderer errors 0。独立 Release publish 成功，发布 EXE 实际启动、滚动、批量写入并正常关闭。
- **UI 验收记录已被 Gate 否定为有效证据**：原截图/UI tree 被错误重复保存，无法支持各场景声明；原首屏/虚拟化表撤回。详见上方 Phase 3A.1 根因与替代采集流程。旧进程内存 JSON 不能单独证明库规模和视图身份。
- 仍保留的阶段边界：图片只是占位和数据说明；没有图片下载/解码/缓存、安装管理/扫描/导入 UI、启动/计时、API、识别或 Liquid Glass。长时间运行、全 Unicode 排序、干净机器/安装包签名/升级和无障碍完整验收尚未完成。当前可交给 **Phase 3A Gate Review**；通过后才能规划 Phase 3B filesystem identity/preflight，不能直接接入真实目录。

## WinUI Phase 2.1：Transaction Kernel Gate Remediation（2026-10-04）

- 当前工作基于 `codex/winui-phase2` / `f495d06c6b3b6d8dc2b6125405490876b5d7fab1`，只整改 B1/B2/M1/M2 和异步 token 入口。六项目结构、legacy v1、32 MiB / 每条目 100k sessions 上限、事务提交顺序与正式资料隔离保持不变。WinUI 仍未注册 writer，未进入 Phase 3，未访问正式资料或真实凭据；完成 checkpoint 后暂停。
- **B1**：`LegacyLibraryCodec.Inspect` 明确返回 InvalidInput / ValidReadOnly / ValidWritable。原始输入仍经 32 MiB 限制和完整验证；规范化后内部验证不把缩进扩张当作输入损坏，输出能力另行检查。有效但不可写的主库保持 Main，snapshot 提供 Access/ReadOnlyReason，mutation 在 candidate 与 IO 之前拒绝。构造实例不再重新 Encode，因此也不会从构造路径误回退备份。没有提高限制、压缩内容或删 session；既有 unknown-field 白名单规则未作任何“腾空间”变更。
- **B2**：默认时间通过 TimeProvider 获取 UTC instant，再用明确 invariant Gregorian 格式生成 persisted timestamp。测试注入 instant 而非字符串，覆盖 en-US / zh-CN / ja-JP / th-TH / ar-SA 的 addedAt、exportedAt、metadata 时间及主/备份；输入日期规范化和校验原本即显式 invariant。UI 本地化显示不受此约束。
- **M1**：在轻量 admission lock 内登记原 semaphore 的等待顺序，然后通过 Task.Run 在后台执行 candidate、导入解析、规范化、验证、序列化及持久化；持久化内部 await 不捕获 UI context。ShutdownAsync 停止接单，异步排空已经接收的操作；排队/候选阶段仍可取消，已开始 durable 阶段完成最小提交并发布状态。DisposeAsync 用此协议，旧同步 Dispose 仅允许空闲调用，忙时明确拒绝而不阻塞。WinUI Closing 先取消关闭、await 容器异步释放，再投递真正关闭；当前窗口只读，不能把内核测试称为完整 UI 写入集成验证。
- **M2**：session ID → index 字典替代循环扫描/IndexOf；source 顺序、同 ID 冲突、等长 source 优先、较长整条替换和历史累计算法不变。80k+80k 不相交时仍因 160k 超过单条 100k 上限而整笔拒绝，不调整契约。
- 即时用户意图继续用 LinkAsync；所有延迟 API/识别结果必须用 token 必填的 CommitLinkAsync。提交结束（含失败/取消）清理本次 Pending；BeginRequestAsync 的取消也安排无取消的清理，清理不影响较新 generation/request。退休请求注销取消回调，关闭排空后释放剩余登记。ReadOnlyMemory 导入遵循异步缓冲区所有权约定：调用方在任务结束前不修改输入；后台复制/验证后才构造候选。
- 专项回归位于 `TransactionRemediationTests.cs`，真实 Electron oracle 增加 4 个小型 session 差分和大输入边界观察。最新实际验证、性能与证据路径见 DEVELOPMENT 的 Phase 2.1 节；下面 Phase 2 数值是整改前历史证据。
- **实际结果 / Gate 自检**：locked restore、Debug/Release 全 solution build（0 warnings/errors）；两配置 C# 各 **270/0/0**，新增 **36**（32 专项 + 4 差分），包含 **102 differential / 14 fault injection**。Electron oracle 可重复生成，相关单元 **120/0/0**，desktop import 37 新增/重复 0/37/个人字段和重启保留/renderer errors 0。Debug/Release 原生窗口实际正常关闭 exit 0、合成资料哈希未变。Release 完整事务中位数：10k 普通条目 **182.1 ms**，20k/40k/80k 每侧重叠合并 **160.1/294.3/604.7 ms**（各预热 1、测量 3；范围见 DEVELOPMENT）。B1/B2/M1/M2 均有实测回归，**PASS FOR PHASE 3**；此结论不是下一阶段执行授权，仍须暂停。真实资料、完整 UI 写入、断电/网络盘和干净机器发布未在本轮验收。

## WinUI Phase 2：Legacy Compatibility & Transaction Kernel（2026-10-04，历史 checkpoint）

- 从已通过 Gate 的 `d988d2bf75d6885061e4a860337c95ada1371b7b` 新建 `codex/winui-phase2`。当前新增内容是 **仅用于显式合成目录的事务内核**；WinUI 仍只读，没有注册 writer，也没有迁移正式资料、网络、扫描、启动、计时或视觉系统。Electron 产品源码、依赖、正式发布目录不变。完成本 checkpoint 后暂停，不能把本文当作 Phase 3 授权。
- 先运行真实 Electron 44.5.1 `LibraryStore` 建立 oracle，再实现 C#。`native/scripts/generate-transaction-oracle.mjs` 使用锁定 Electron 的 Node 模式、固定时钟/UUID 提供器、临时虚构目录，调用原类而非抄写一份参考算法。`transaction-oracle.json` 包含 50 个 mutation 场景、5 个异步生命周期序列、5 个旧恢复观察和 43 个 Phase 1.1 字符串/路径/数字向量；记录 library/title-matching/local-cover 原文件 SHA-256，改动参考必须重新审查和生成。
- Application `TransactionalLibrary` 持有 immutable authoritative snapshot、候选值、串行队列、生命周期 token；Infrastructure `LegacyLibraryCodec` 负责旧格式规范化、全库验证/序列化、目录身份及名称保护；Platform.Windows `IsolatedLibraryPersistence` 负责隔离、目录/独占 writer lease、备份和原子替换。六项目依赖方向未改变，无新增 NuGet 包。代表性操作为 add、个人字段、安装记录增删、delete、旧备份 append/restore、明确作品 ID 的 link/merge。
- 必须保持的顺序：**取队列中的最新状态 → immutable candidate → 全库验证/规范化 → 大小校验 → 最后取消检查 → 保存 lastGood 备份 → 保存主文件 → 更新 generation/revision → 发布 authoritative snapshot**。IO 前失败不得更改内存；主文件替换成功后出现检查异常，必须按已提交返回 warning，并同步内存，不能谎报回滚。备份可能先于失败的主保存更新为 lastGood，这与旧版一致。
- 取消在队列前/等待时可中止；进入持久化后不向文件 API 传递取消令牌，完成最小安全提交，结果 `CancellationAfterCommit` 表示取消发生于提交阶段。请求 token 独立于 cancellation，包含仓库 epoch、条目 generation、request ID 和 revision。后发请求使先发结果失效；名称/关联/安装身份变化、每次 link、删除再同 ID 恢复均使旧 token 失效；普通个人字段编辑可与资料返回共存。目标条目有待完成请求或较新意图时拒绝合并。
- unknown fields **按旧版主动丢弃**：`parseBackup` 是白名单重建对象，不保留扩展。本阶段保持这一行为并用 oracle 验证；没有承诺任意未来 schema 的无损转存。显式 null / 0 / false / 缺省 nameIsCustom / 缺省或 null 的封面模式分别保留。旧格式 version/format 不变；ID、数组顺序、字段存在性、数值和 UTF-16 内容逐项比较，空白与 JSON escape 拼写不要求字节相同。旧版 trim 后 ID 相等的首选安装检查现已补齐，Phase 1.1 原值读取保持不变。
- **有意差异和边界**：旧 Electron initialize 会创建空主文件或从备份修复并隔离损坏文件；C# 打开只加载 Main/Backup/New 状态，不改主/备份。首次显式 mutation 才保存；若从备份恢复，此次保存以恢复后的有效库作为 lastGood，替换损坏主文件，不另生成 corrupt 副本。打开会留下无内容 `.the-order-writer.lock`，不应描述为整个目录绝对零写入。安装移除是新增的纯记录操作，首选项被移除时设 null，永不删游戏文件。安装添加接受上游已授权的值，不扫描/打开路径；import 使用 lexical Windows identity，没有移植旧 realpath 对真实目录别名的解析，因而不是可用于正式资料的完整导入/迁移器。当前 token 是单一显式请求通道，未实现旧 automatic batch / association / cover 的完整多通道调度。
- 实际验证：locked restore、Debug/Release solution build（均 0 warnings/errors）；Debug/Release C# 各 **234 passed / 0 failed / 0 skipped**，比可信基线新增 **150**。其中 **98 个差分测试**（50 mutation + 5 lifecycle + 43 codec），**14 个标记 fault-injection 测试**（13 个备份/主文件阶段组合 + 1 个包含权限/目录消失/ENOSPC 三种异常的测试），此外有真实只读文件、主/备份锁定、硬链接、目录固定和互斥验证。Electron 6 个相关单元文件 **120/0/0**；构建和 desktop-import 全通过：37 条导入、重复 0/37、个人字段/重启一致、游戏文件保留、renderer errors 0。
- 整库成本如实保留：candidate 初始共享 immutable 记录，验证会重建整库；一次事务约三次整库序列化、两次整库 parse，加 identity 比较。10k 个最简条目约 5.02 MB，本机完整套件中 Debug/Release 单次事务约 577/703 ms，进程累计分配观测约 198/185 MB（并行测试会干扰，不是独立微基准）；单条 10k sessions 约 1.76 MB，约 299/334 ms。不得在 UI 线程执行这种路径；真实大元数据/图片/session 库和 UI 流畅度未验收。32 MiB 上限可能早于 10k 条达到；此证据供后续数据库阶段使用，本阶段未引入 SQLite。
- `.artifacts/native-phase2/` 保存本机日志/TRX，不提交。合成 fault injection 不等同于断电实验；网络盘/非 NTFS、恶意外部进程、突然断电、任意手工日期/WHATWG URL 文法、完整 JPEG 解码/EXIF、真实资料与凭据仍未验证。目录 lease 和 writer lock 只协调 native writers，Electron 不识别此锁；禁止两端共用同一 profile。详细恢复矩阵、合并表和复验步骤见 DEVELOPMENT 的 Phase 2 节。

## WinUI Phase 1.1 Gate Remediation（2026-10-04）

- 在 `codex/winui-phase1` / `775450e` 上完成限定整改；Gate 为 **PASS FOR PHASE 2**，但尚未开始 Phase 2。无可写 repository、事务、数据库、联网或正式资料迁移；Electron 产品源码与 npm 依赖未改动。
- `LegacyJson` 在真实 reader → validation → DTO → mapping 边界保留 UTF-16 code units，包括孤立 high/low surrogate；不替换 U+FFFD、不丢字段/条目。整数 DTO 也按旧 JavaScript 的数字解析语义读取 `1` / `1.0` / `1e0`，仍拒绝字符串、非整数和越界值。Domain/WinUI 字符串处理未改。
- `LegacyPathIdentity` 单独构造目录身份，原始保存路径不改写；参考 `electron/library.ts` 的 trim/isAbsolute/normalize/resolve/lowercase，覆盖尾斜杠、`.` / `..`、大小写、重复/混合分隔符、根、UNC、drive root。JavaScript 全量小写与 .NET invariant 不等价，因此 Legacy 内嵌从 Electron 44.5.1 内置 Node 24.21.0 / Unicode 17.0 生成的 27 KiB 大小写快照，并处理词尾 sigma；运行时不调用 JavaScript，不依赖本机 NLS。此身份函数不是文件访问授权；正式资料隔离适配器未修改。
- 新增 **45** 项 C# 用例：7 个生产链字符串、29 个路径正反差分、7 个数字词法、1 个参考完整性/新鲜度、1 个指纹陈旧源码拒绝。现有指纹实验也增加源码 SHA-256、元数据和六向量完整性校验。差分参考由真实 Electron `validateLibraryBackup` / `pathKey` 生成；测试使用固定向量，正常测试不执行 JavaScript 自证。所有生产链用例通过临时合成 `library.json` 加载并核对字节未变。
- 锁定 restore、Debug/Release solution build 通过（0 warnings/errors）；Debug、Release 完整 C# 测试各 **84 passed / 0 failed / 0 skipped**。Electron build 与修复后的 `tests/desktop-import.mjs` 完整通过：37 条导入，重复 0 新增/37 跳过，个人字段与重启一致、游戏文件保留、renderer errors 0。旧失败点是原第76行等待 compact grid 不存在的启动按钮；现在从作品详情验证启动可用及收藏，不实际运行合成 EXE。
- 发布目录正常运行与 SDK 查找禁止探针均通过真实 WinUI 窗口核对（3 条合成资料）并正常退出。探针收窄当前进程 PATH，并令 SDK 查找立即失败；没有移动或卸载 SDK，也不等同于干净机器验证。证据在 `.artifacts/native-phase1.1/`。VC++ 系统前置、干净 Windows 11、完整 JPEG/EXIF、真实资料/凭据、大库性能与全功能迁移仍是后续 gate。
- 完成独立 checkpoint 后暂停。以下 Phase 1 记录是整改前历史证据。

## WinUI 并行候选：Phase 1（2026-10-04）

- 用户在审计通过后授权 Phase 1；新源码位于 `native/`，从 Electron 基线 `387336801981ae30e9de317916024b016dee3605` 建立 `codex/winui-phase1`。这不是正式程序替换，也不是 Phase 2 授权。下文既有 Electron 基线、功能和发布入口仍有效。
- 六项目 Solution：Domain、Application、Infrastructure、Platform.Windows、WinUI、Tests。Presentation 置于 WinUI 内部，没有独立 Materials 项目。实际锁定 .NET SDK **10.0.401**、.NET runtime **10.0.12**、Windows App SDK **2.5.1**（WinUI 3 的 NuGet 子包 `Microsoft.WindowsAppSDK.WinUI` **2.3.9**），目标 `win-x64` / Windows 11。SDK 通过官方 ZIP 与 SHA-512 验证后安装在忽略目录；未修改系统 PATH 或 VS 工作负载。
- 已实现原生窗口、系统标题栏、书架/设置导航、System/Light/Dark 会话主题、全局读取错误、轻量摘要 ListView、旧版 v1 JSON DTO/校验/映射。只读，无自动修复写回；本地 Base64 图片只保留数据和结构校验，页面使用占位图，不宣称完成原生解码与 EXIF 校验。
- 资料仅来自显式 `THE_ORDER_NATIVE_DATA_DIR`，还必须带 `.the-order-synthetic` 标记。拒绝正式目录及其父子目录、重解析目录/文件和硬链接；缺目录/损坏/真空库分别反馈。未指定目录时可开壳，但拒绝数据操作。没有读取正式 settings、真实密钥、用户库或 Chromium 存储。
- Debug/Release 构建、锁文件 restore、39 项测试已实际通过；Debug/Release 原生窗口、发布目录运行、导航、主题、正常/损坏/空库和退出有真实验证。证据在 `.artifacts/native-phase1/`，操作见 DEVELOPMENT。发布过程中发现应用 XBF/PRI 未被默认 pipeline 纳入，已在项目声明发布资源并复验。
- 两项合成实验：① Electron 44.5.1 密文不能直接 DPAPI 解密；同一合成 profile 的 `Local State` 中 DPAPI 加密主密钥 + v10 AES-GCM 可在 C# 验证，三个独立 profile 均通过解密/重新保护/篡改拒绝。仅测试代码，无生产迁移器；遗失 profile 密钥或账户不兼容时仍应重新输入凭据。② 从当前 TypeScript 提取 `reviewIdentity` / `decisionFingerprint` 生成 6 组向量，Unicode、缺项、null、属性顺序、孤立代理项的序列化及 SHA-256 对照通过；新进程不能继承旧决定授权，后续仍需重建 token/重新确认。
- 发布为未签名 unpackaged、自包含目录，.NET 与 Windows App Runtime / WinUI 从发布目录加载；VC++ 运行库实际从本机 System32 加载。未验证无 VC++ 运行库的干净机器、ARM64、完整图像解码、大库性能、真实 API/游戏/私库或 MSIX 安装。
- Electron 源码、npm 锁文件及发布脚本未改动；`npm run build` 和 `tests/packaged-b-smoke.mjs` 通过。额外运行 `tests/desktop-import.mjs` 在导入 37 条后旧网格启动按钮定位处超时（第76行；当前 compact grid 无该按钮），未修改范围外页面或旧测试，不宣称整套 Electron 回归通过。
- Phase 1 完成后停止。Phase 2 建议只做旧格式兼容/事务与失败恢复的差分验证；任何正式资料接管、SQLite、联网识别、游戏启动或高级材质均需后续明确范围。

## 基线与用途

- 正式项目：`D:\Projects\The-Order`，内部版本 **0.7.1**；用户于2026-10-04在三风格候选交付后明确要求“替换正版”，已部署第六轮候选至原`release\win-unpacked`，完整备份旧夜间程序。正式资料未读取、迁移或修改；准确入口、备份与替换验证见[第六轮替换](ITERATION.md#用户授权替换第六轮正式程序2026-10-04)。
- 当前正式程序对应候选交付`707aaeb79075ef7a7ca12c79928d07d450594b1e`，业务`dd89ea2326d3651955cfb2e86c91a1bd47286a9b`，分支`codex/appearance-styles`；后续替换说明提交只改文档。旧正式夜间业务`583e46d`及更早`16ccc28`程序、分支和候选仍保留，准确历史见[ITERATION](ITERATION.md)与[OVERNIGHT](OVERNIGHT.md)。
- 第五轮保留候选分支`codex/windows-experience`，代码`57bce0f0b452051ab0056694410e535925adef5d`从第四轮交付`8adc1a0276d55240859bb6eecbe1d6737814d06d`接续。中性明暗、有效系统字体、原生标题栏/主题同步与窄窗口适配已实现，69项相关单元、最终生产9组/39图与隔离启动通过。宿主Windows11/200%，高对比为媒体模拟，Win10与Snap鼠标操作未验证；准确范围与证据见[第五轮记录](ITERATION.md#第五轮windows体验与审美适配2026-10-04)。用`& 'D:\Projects\The-Order\scripts\start-iteration-candidate.ps1' -Iteration 05`隔离试用，资料`.artifacts/iteration-05/trial-profile`，ASAR`e916f089…`。第四轮代码`3b26dfb25b7fcc1089aa632d0ae42b9c264aabc6`、程序和trial-profile保留：84/72px导航、仅封面与两行名字、长竖图铺满；评分/收藏/启动在列表及详情保留。这两轮候选均未替换正式程序。
- 当前开发分支`codex/appearance-styles`，第六轮候选代码`dd89ea2326d3651955cfb2e86c91a1bd47286a9b`从第五轮交付`3ef02f8b0cdcaa3553b298eed5f03b5b18772c75`接续。设置中可保存经典书架、Windows、Liquid Glass三种风格，明暗独立；玻璃使用应用内边缘折射/轻微色散，保留原生窗口及清晰封面，不依赖桌面捕获或额外原生面板。236项相关单元、15组生产UI检查/53图、真实像素对照和隔离启动通过，准确范围及上游参考取舍见[第六轮记录](ITERATION.md#第六轮三套可切换界面2026-10-04)。启动`& 'D:\Projects\The-Order\scripts\start-iteration-candidate.ps1' -Iteration 06`，数据`.artifacts/iteration-06/trial-profile`；ASAR`8fac672c…`，已按后续明确授权部署为正式程序；试用入口继续保持独立数据。
- 本文用于帮助下一位维护者或评审者理解当前程序。用户操作见 [README](../../README.md)，构建、数据隔离和 Git 规则见 [DEVELOPMENT](../../DEVELOPMENT.md)。
- 上一次夜间替换时，夜间候选与当时正式程序75个文件逐一核对一致，ASAR SHA-256为`652c3c2beba98c7b363f90dfc5deeb3f68b5510a1618f9877640eaf9f111176f`。用`& 'D:\Projects\The-Order\scripts\start-overnight-candidate.ps1'`仍可隔离试用，资料固定为`.artifacts/overnight-2026-10-04/trial-profile`；六条虚构书架/模拟游玩记录与原创封面可编辑重开保留，没有预置密钥。九次已完成，任务PAUSED；夜间交付及未验证范围见[晨间交付](OVERNIGHT.md#晨间交付)，此次部署没有带入试用数据。
- 第一轮独立评审、A-1修复和历次正式替换记录仍保留在 [迭代记录](ITERATION.md)。下面以当前源码为准；第四至六轮的简洁网格、窄导航、所有比例铺满、Windows适配与三风格选择现已随第六轮进入正式程序。各轮隔离入口仍可使用，不会把试用书架或测试密钥带入正式资料。
- 以下第0–7次描述保留各轮当时的交付状态，“未替换”指该轮结束时；这些成果现已随用户授权部署。夜间开发接续同一仓库的 `codex/overnight-2026-10-04`，睡前分支和程序备份保留。第0次已实现可检索的分类收藏页：分类/所含作品/别名搜索、名称/数量/近期排序、封面与在玩/完成进度、全部标签搜索，以及小窗口图标导航的中文名称。准确提交、原生窗口前后图和检查范围见 [OVERNIGHT](OVERNIGHT.md)。
- 第1次夜间源码增加命名“常用筛选”：完整筛选/排序保存、实时匹配数量、书架/分类页直接打开，以及独立改名/更新条件/删除；过时分类/标签保持精确条件。记录位于当前profile的本机UI存储，不随书架备份导出；保存失败与损坏记录有明确反馈，应用时清除批量选择，批量写入中禁用操作。同profile重启与跨profile隔离已通过真实桌面合成回归，17项定向单元、构建、原有书架相关回归和10张修改后原生图通过；未验证真实大库性能。准确代码提交与比较图见 [第1次记录](OVERNIGHT.md#第1次自己的动态书架已完成)，仍未替换正式程序。
- 第2次夜间源码让状态、个人评分和笔记可在作品资料页明确保存，与管理共享草稿并仅提交三个字段；小窗口保留发行日期/公司，封面选择器支持焦点往返。未完成评分输入保留并校验，失败/晚输入/切详情保护通过；77项相关并发与分页单测、26项封面/桥单测、构建、新详情和既有资料保留桌面回归通过。前后20张原生图、准确提交与尚未验证范围见 [第2次记录](OVERNIGHT.md#第2次详情里的个人记录已完成)。仍是夜间源码，未替换睡前程序。

- 第3次夜间源码统一设置页明确保存，保留保存中的后续编辑；移除已保存密钥不提交草稿，连接测试对应已保存配置，失败持续提示并可重试。设置开关具备中文控件名称与可见键盘焦点，侧栏在高度不足时可滚动。82项相关单测、构建、设置/资料保留/书架桌面回归通过；24张原生图及准确提交见 [第3次记录](OVERNIGHT.md#第3次明确的设置保存流程已完成)。本轮仅使用模拟目录接口，未实连NextMoe；当时任何设置保存均停止识别，此行为已在第4次单独核实修正。仍未替换睡前程序。
- 第4次夜间源码 `4af0469b350eaebf626893095fb3cd3e03006cae` 让外观/封面保存、无效设置与设置写盘失败保留识别任务；资料源、密钥、分级或程序名称读取变化成功保存后停止旧任务。启动、设置及资料提交采用同一顺序边界；手动取消也撤回此前排队的手动/导入自动启动，一次识别固定读取偏好。设置保存与识别状态保存分别反馈，不把后者失败误报为设置未保存。188项相关单测、构建、新交错桌面回归及原导入/匹配/设置/取消回归通过；模拟接口、惰性文件，未实连NextMoe。原前后6张和设置回归18张原生图、准确版本见 [第4次记录](OVERNIGHT.md#第4次设置变更与识别连续性已完成)；第5次误写导致其中3张after原图缺失，原回执保留、索引已明确标记，不能把后来文件冒充原图。仍未替换睡前程序，未迁移正式数据。
- 第5次夜间源码最终`cbfa8c293c397d34d32995c898692f1b73b34cd5`改善扫描导入：状态分组、当前结果选择与隐藏已选提示、完整路径展开、窄窗固定确认区；取消与真空结果区别，写入失败保留选择，已经导入但读取失败仅重读。导入已提交而自动补全启动失败时明确返回warning，并恢复识别旧状态与待核对时效保护。229项相关单测、构建、真实合成扫描/失败重试/37目录重复与重启、17组设置识别时序回归通过；11张修改后导入状态图及4张基线图，版本/证据缺口见 [第5次记录](OVERNIGHT.md#第5次可控的导入清单已完成)。无真实API/游戏验证，尚未打包或替换正式程序。

- 第6次夜间源码`1908c3123192ebb07fdf28125e1db650ab8cf474`将核对改为可搜索队列与独立对比/决策区，支持窄窗150%、筛选外当前项、完整来源展开及持续错误恢复；提交期间锁定交互，部分成功重新读取实际关联，已确认后的读失败仅重读，关闭重开也不重复提交。最终167项定向单元、新6组原生核对与9组既有核对语义回归通过；2张基线/11张最终状态图和5张来源回归图准确版本见 [第6次记录](OVERNIGHT.md#第6次可定位可比较可恢复的核对工作区已完成)。均为合成资料/模拟API，停滞运行与未验证范围如实保留；尚未打包或替换正式程序。

- 第7次夜间源码`583e46dd8df921fff25b4dbacc76ae6dbc656e9e`明确详情四组草稿与分组保存，正常关页/标签导航先确认放弃，封面切页保留，保存中阻止离开；原安装参数不会因默认切换静默丢失或投到新安装。已保存而读取失败仅重读，恢复后才离开，重开不会从旧快照覆盖已提交值；Escape与嵌套确认已实际检查。219项相关单元、8组新原生及5/9组既有详情/核对回归通过，16张图与版本、停滞/未验证范围见 [第7次记录](OVERNIGHT.md#第7次详情草稿与可信保存已完成)。合成profile/模拟接口，不保证应用退出或强杀后的未保存草稿持久化；尚未打包、替换正式程序。

## 已实现的核心流程

| 流程 | 当前行为 |
| --- | --- |
| 书架与整理 | 窄导航保留中文短名，收藏列表包含全部分类及特别喜欢。顶栏搜索、排序、封面/列表切换；进度、本地/愿望单、收藏、分类、标签和评分筛选仍可组合。短继续条仅显示有效候选。本地封面网格只显示3:4封面及两行名字，列表/详情保留单评分（个人 0 分优先）、状态、收藏、时长、未安装/待设程序与启动动作。显式铺满对长竖图及所有比例生效；柔化/纯色保留特殊比例全图，普通竖图仍按20%裁损边界自适应。单游戏模式优先于已保存全局默认，小缩略图保持原策略。整理勾选独立，批量修改只处理当前可见勾选项；偏好保存在本机。主栏目保留醒目的中文标题及较小Galgame装饰名。 |
| 本地导入与附加 | 普通扫描用户勾选确认，精确重复跳过、父子范围重叠明确拒绝。未安装条目的「添加本地游戏」由明确根目录和程序选择把安装附加到现有条目，不自动猜测跨条目合并。已有有效默认不会因给另一个安装设置程序而切换，显式设置默认仍可用。 |
| 补全资料 | 默认导入后自动补全；未配置密钥仍可本地导入/明确附加。常驻「资料整理」的「只补缺失」处理没有资料或缺少封面的本地游戏；已有绑定但缺封面时刷新原作品。 |
| 重新识别 | 支持整库、所选、单个游戏和扫描范围内已入库游戏，包含已有完整资料的本地条目。重新读取独立本地线索，旧的自动生成标题不能证明旧关联正确。同 ID 且证据可靠时刷新，不同 ID 保留原关联并进入待核对，确认后才改绑。 |
| 核对与取消 | 「资料整理」显示当前/最近任务与稳定核对入口。夜间源码支持队列搜索而保留当前决策，实时关联/候选比较、完整安装来源展开与独立详情，持续失败及只读恢复；稍后保留队列，保留当前只结束该建议。旧队列降级可读，过时建议须手动搜索/重新识别，提交复核用户看到的核对 ID 和身份指纹。取消阻止尚未开始保存的结果，已开始的原子保存会完成。 |
| 发现与详情 | NextMoe 搜索、分页、去重和原页重试；详情展示简介、角色、开发商、来源评分与链接。使用资料源默认封面，票数不影响自动选图。详情「封面与展示」可反复选资料图、恢复默认、选择本地PNG/JPG及指定展示模式或跟随全局。目录手选图同作品刷新保留、换关联清除；本地图片作为个人字段在刷新/换绑后保留，明确选默认或另一资料图才清除。无资料关联也可选本地图片。默认随明确刷新更新，新远程图须联网核验；恢复已存默认、仅改展示及本地图片可离线。支持愿望单、手动关联和刷新；未安装条目先附加明确的本地游戏。 |
| 启动与记录 | 通过选定 EXE 和参数数组启动，记录被跟踪进程的运行时长；尝试接续同目录启动器产生的游戏进程。关闭窗口可留在托盘继续计时，「结束计时」不结束游戏进程。 |
| 个人数据与统计 | 名称、状态、0–10 分个人评分、分类、标签、笔记、收藏、会话与总时长；继续游玩、近 14 天、最近会话及常玩榜。当前会话单独显示，不重复计入已保存检查点。 |
| 配置与备份 | 夜间源码统一明确保存全部偏好、保存中后续输入保护、独立移除已保存密钥、保存后测试与配置时效反馈；API 地址、加密密钥、成人分级、明暗主题、界面风格、全局封面展示、程序内名称读取；书架JSON导入导出、原子保存及上一有效版本恢复。第四轮展示文案包含长竖图，内部旧字段及备份格式兼容。导出含本地封面压缩副本与每游戏展示，不含API密钥、全局设置或游戏文件；远程图片仍只保留网址。 |

### 必须保持的行为

- 个人名称、评分、笔记和时长不能因自动识别或迟到响应而丢失；编辑页的未保存草稿也有请求代次保护。
- 资料提交在保存队列内重新检查身份。名称、关联、首选安装、安装 ID、目录或启动程序变化会使旧识别失效；启动参数和个人字段编辑不会无故取消有效结果。
- 同批自动识别可以合并同一作品的多个安装；后来的明确人工操作会终止旧批次的合并权限。
- 实际联网请求由 Electron 主进程发出。前端只获得是否保存密钥的状态；正式包忽略开发页面环境变量。

## 模块入口

| 文件 | 维护时先看什么 |
| --- | --- |
| [src/App.vue](../../src/App.vue) | 页面、搜索分页、导入、识别、核对和详情编辑的异步状态；多数流程仍集中在此文件。 |
| [src/components/GameCard.vue](../../src/components/GameCard.vue)、[LibraryOverview.vue](../../src/components/LibraryOverview.vue)、[PlayStatistics.vue](../../src/components/PlayStatistics.vue) | 封面卡片、继续游玩和统计；共享计算在 `src/library-workspace.ts`、`src/library-insights.ts`。 |
| [LibrarySavedViews.vue](../../src/components/LibrarySavedViews.vue)、[saved-library-views.ts](../../src/saved-library-views.ts)、[LibraryCollections.vue](../../src/components/LibraryCollections.vue) | 夜间源码的命名动态筛选与分类浏览；UI存储的有界校验/显式失败、改名与更新区别、过时标签保留、实时匹配数量与键盘交互。 |
| [PersonalRecord.vue](../../src/components/PersonalRecord.vue)、[App.vue](../../src/App.vue)、[detail-drafts.css](../../src/detail-drafts.css) | 夜间源码的详情分组草稿、正常离开确认、持续失败和只读恢复、原安装参数归属；共享个人记录/管理输入与明确保存；小窗口基础资料和封面焦点往返。 |
| [CoverPicker.vue](../../src/components/CoverPicker.vue)、[DetailCover.vue](../../src/components/DetailCover.vue)、[cover-display.ts](../../src/cover-display.ts) | 详情封面草稿、原子保存、比例展示与共享个人选图/分级规则；默认预览须与恢复后实际图一致。 |
| [local-cover.ts](../../electron/local-cover.ts)、[SectionSignature.vue](../../src/components/SectionSignature.vue) | 有界PNG/JPG读取、方向/透明处理与JPEG副本校验；主栏目小副标题及视觉主次。 |
| [LibraryMaintenance.vue](../../src/components/LibraryMaintenance.vue)、[installation-attachment.ts](../../electron/installation-attachment.ts) | 常驻整理面板；明确安装附加的真实目录边界与有界独立编号冲突检查。 |
| [MatchReviewWorkspace.vue](../../src/components/MatchReviewWorkspace.vue)、[App.vue](../../src/App.vue) | 夜间源码的核对队列检索、响应式对比/决策区、准确建议身份、提交锁定与持续错误/只读恢复；既有来源与过时保护继续生效。 |
| [src/shared/types.ts](../../src/shared/types.ts)、[src/desktop-bridge.ts](../../src/desktop-bridge.ts)、[electron/preload.ts](../../electron/preload.ts) | 数据类型、Vue 响应式对象转普通数据快照、受限桌面 API。修改接口时三处需一致。 |
| [electron/main.ts](../../electron/main.ts) | IPC 参数及来源校验、原生对话框、身份操作队列、窗口与托盘生命周期。 |
| [electron/library.ts](../../electron/library.ts) | 本地扫描、安装、备份、原子事务、资料代次及合并保护；数据可靠性的关键入口。 |
| [electron/metadata-matching.ts](../../electron/metadata-matching.ts) | `missing` / `reidentify` 模式、任务取消、来源时效、候选详情复核与待核对队列。 |
| [electron/game-identification.ts](../../electron/game-identification.ts)、[evidence-matching.ts](../../electron/evidence-matching.ts)、[title-matching.ts](../../electron/title-matching.ts) | 本地线索、公开索引、候选排序、独立证据以及章节和版本冲突规则。 |
| [electron/local-game-evidence.ts](../../electron/local-game-evidence.ts)、[local-text-evidence.ts](../../electron/local-text-evidence.ts) | 有界 PE 版本资源读取、Steam 配置、已知说明书或配置标题；不执行游戏或 HTML。 |
| [electron/nextmoe.ts](../../electron/nextmoe.ts) | NextMoe v2 请求、鉴权、超时、错误脱敏、重试、游标、作品合并、作品和发行映射。 |
| [PreferencesSettings.vue](../../src/components/PreferencesSettings.vue)、[App.vue](../../src/App.vue) | 夜间源码的统一设置工作台与草稿/连接反馈；字段编辑代次、成功回复确认、清除旧密钥独立于草稿、明确失败与键盘焦点保护。 |
| [electron/playtime.ts](../../electron/playtime.ts)、[settings.ts](../../electron/settings.ts) | 进程跟踪、累计检查点，以及设置队列和 Windows 密钥加密。 |
| [src/bridge.ts](../../src/bridge.ts) | 无桌面桥时启用的浏览器演示；不能用此模式证明真实导入、识别或启动有效。 |
| [tests](../../tests) | 单元测试与隔离 Electron 回归。重点先看 `metadata-concurrency`、`metadata-matching`、`desktop-reidentification`、`desktop-cancel-queue` 及各页面并发测试。 |

样式主要分布在 `src/style.css`、`src/order-theme.css`、`src/library-layout.css` 和组件局部样式；卡片/继续条与整理/核对样式集中在 `src/library-refresh.css`、`src/workspace-refresh.css`。第六轮依次加载 `src/windows-theme.css`（保留全局系统字体/无障碍，Windows覆盖避开经典）、`src/glass-theme.css`（玻璃材料）与 `src/desktop-caption.css`（原生按钮安全区域）；`src/components/LiquidGlassMaterial.vue`仅在已保存玻璃风格时挂载，`src/liquid-glass.ts`负责有界、事件驱动的SVG背景折射及卸载；准确顺序见 `src/main.ts`。保持 KunUI 组件、明暗主题和较小窗口下的可用性。

## 真功能、演示数据与未实现内容

### 真实桌面能力

桌面应用通过 preload / IPC 操作本地 JSON 书架和安装，NextMoe 是作品目录 API。搜索先读取 `catalog/search`，再批量取作品卡片；详情、来源编号查询及有界发行列表也已接入。它没有 LLM 模型参数或 SSE 文本生成。

识别使用文件名、已知小配置、标题索引和可选程序版本资源。正式标题、罗马字、机器翻译、缩写等来源有区别；旧 PotatoVN 编号不能单独证明身份，相关线索不会重复计作独立证据。

### 演示与测试数据

- `public/preview-data.json` 是 12 部公开作品的缓存资料；浏览器演示在内存中生成无本地安装的书架。它不是个人库，也不是实时搜索结果。
- `tests/ui-fixtures/` 的封面为原创合成测试素材。桌面回归中的游戏、安装和会话为测试记录，联网结果由测试替换。
- `data/title-index.json.gz` 是 PotatoDBMapper 公开数据的派生索引：56,064 条记录、97,038 个标题，只含标题与来源编号。来源和许可见 [THIRD_PARTY_NOTICES](../../THIRD_PARTY_NOTICES.md)。
- 已有截图使用独立合成 profile。截图展示真实程序界面；其中封面、评分、路径、时长和匹配结果不能解释为用户真实书架或线上识别率。

### 尚未实现

云同步、Steam 库自动扫描、自动补丁安装、压缩包解包、`.bat` / `.lnk` 启动、仅统计前台时间、PotatoVN 全量迁移、远程封面离线缓存，以及自动更新公开标题索引。本地手选图片副本已保存，不能与远程缓存混称。不要把未实现内容写为可用功能。

## 已知边界与待办

1. **识别仍需要核对。** 目录缩写、合集、版本差异和上游缺失可能没有唯一答案；来源编号及程序元数据都可能过时或被复制。排序系数不是正确概率，资料关联也不验证游戏本体或文件安全。
2. **PE 读取有实际系统影响。** 新设置默认开启程序内名称读取，可在偏好设置关闭。即使不执行 EXE，读取也可能触发 Windows 扫描和隔离。隔离演示只使用合成程序夹具；已有采集保留默认开启状态，只扫描合成惰性 EXE。不能据“仅被读取”认定检测为误报。
3. **统计有明确范围。** 包括后台运行时间；一次跟踪一个游戏。跨日分布按会话区间分摊，未保存暂停发生位置，因此不是逐分钟精确活动日志。复杂外部启动器或目录外子进程仍需验证。
4. **上游接口与数据会变化。** 请求已有错误、限流和超时处理；第二轮已使用用户新提供的测试密钥实际验证作品84的详情、票数和图片，不代表全部作品、权限或配额已验证。尺寸缺失不猜竖图，图片失效仍显示失败占位。不得从正式个人设置提取密钥用于测试。
5. **开发依赖公告尚未处理。** 本机迁移审计记录 9 个受影响依赖条目（8 high、1 low；不等于 9 个独立漏洞），涉及 `electron-builder` 及下载/缓存依赖链和 `esbuild`。生产依赖审计当时为 0。这是迁移阶段的报告，本轮没有重新审计或升级；后续应单独评估升级并重跑打包回归。依据：[完整审计](../../.artifacts/migration/dependency-audit.json)、[生产依赖审计](../../.artifacts/migration/runtime-dependency-audit.json)。这些本机报告不进 Git，其他环境需重新生成。
6. **资产许可分别保留。** 应用源代码、KunUI、公开索引和游戏资料各有原有许可或权利归属。当前纹章重建自用户提供的游戏截图，源码许可不包含底层游戏图案的权利；详见第三方说明。
7. **本地照片是压缩副本。** 只接受静态PNG/JPG，源图不超过20MiB/4000万像素，保存最长边1600及128KiB以内JPEG，透明铺白底；原有书架32MiB总容量不变。新程序读取旧备份兼容，不保证旧候选无损读取新字段。真实照片的全部编码变体、大库照片性能尚未验证。
8. **缩放布局的证据范围。** 睡前正式版本分类长名称在1000×700、150%下横溢；夜间第0次用更长的合成分类复现1503px文档/667px视口并修为657px文档/667px视口，另三种窗口与缩放也通过，此修复现已随正式替换部署。第0次同时发现第三轮部分“1000×700”详情/设置图实际仍为大窗口，撤回这部分尺寸证据；六栏目独立布局采集仍有效。详见迭代记录勘误与夜间记录，不能据局部结果声称所有页面布局通过。

## 评审顺序

| 优先页面 | 建议检查 |
| --- | --- |
| 1. 我的书架 | 1280×720 下首屏信息与可点击区域；长名称、完整封面、缺封面、无默认程序；搜索、筛选、列表切换及批量操作范围。 |
| 2. 详情 → 管理 | 个人名称、笔记和评分的可读性；多安装和默认程序是否易懂；刷新、切换游戏或切换安装时未保存草稿是否稳定。 |
| 3. 添加游戏 → 重新识别 → 待核对 | 区分新导入与已入库；区分补全、刷新和重新识别；进度、取消、不同候选的确认、稍后与保持当前是否清楚。 |
| 4. 发现、统计与偏好设置 | 分页重试和空状态、统计解释、密钥保存与连接测试反馈、程序内名称读取说明，以及明暗主题。 |

先评审实际页面，再提出改版；保留上述数据与并发约束。截图无法替代键盘、滚动、窗口缩放和交互检查。

## 验证记录与截图

### 第四轮简洁书架候选（2026-10-04）

定向单元50/50、类型/构建/隔离打包、正常生产EXE五组回归通过；同夹具前13/后17张原生截图覆盖明暗、长竖铺满/柔化/纯色、列表/详情/预览、小窗150%、导航收藏/选择/重启。补充当前构建的模拟IPC回归26项测量/15图通过，覆盖125–200%键盘与加载/失败恢复。截图索引`.artifacts/iteration-04/compact-library/README.md`，包/提交/图哈希回执`.artifacts/iteration-04/committed-evidence.json`；准确结果、诊断修正与未验证范围见[第四轮](ITERATION.md#第四轮简洁书架与封面铺满2026-10-04)。未重跑整套测试，没有真实API/游戏验证。

隔离试用：`& 'D:\Projects\The-Order\scripts\start-iteration-candidate.ps1' -Iteration 04`，数据固定为`D:\Projects\The-Order\.artifacts\iteration-04\trial-profile`，六条虚构书架及原创图片、无预置密钥，重开保留试用修改。该脚本实际可见窗口/退出0已验证。候选ASAR为`c95327d82981bd69a9d28cff8e642d1f08a96c70d9daaf017d3d54f1e06fc0bd`，原正式仍为下方夜间版本，正式数据未访问。

### 夜间最终候选（2026-10-04）

完整产品单元639项、辅助测试9项，以及正常生产EXE的6组烟测和原资料保留回归通过；实际隔离试用入口、六条数据与本地图解码、宽100%/小窗口150%已检查。本夜真实API/游戏未验证，不把早前作品84的线上检查算作本夜结果。最终截图索引与准确提交为[第8次记录](OVERNIGHT.md#第8次最终候选与晨间交付已完成)，本机总回执`.artifacts/overnight-2026-10-04/round-8/committed-evidence.json`；后续用户授权正式替换及正式入口隔离检查见 [替换记录](OVERNIGHT.md#用户授权替换正式程序2026-10-04)。本次没有重跑上述整套测试。

### 第六轮三风格证据

当前图像清单：`.artifacts/iteration-06/appearance/after/2026-10-04T04-58-16-064Z/manifest.json`（53图），同条件Windows基线：`.artifacts/iteration-06/appearance/baseline/2026-10-04T04-19-01-922Z/manifest.json`（11图）。代表图为`glass-grid-dark-1440x940-zoom1.png`、`glass-detail-light-1440x940-zoom1.png`、`glass-active-filters-dark-500x600-zoom1.png`和`glass-reduced-transparency-detail-simulated-500x600-zoom1.png`，均位于最终目录；同目录`classic-*`、`windows-*`供比较。原创合成数据，未调用真实API。光学棋盘对照另在`.artifacts/iteration-06/optics/2026-10-04T04-52-35-076Z/`，是实际滤镜像素验证，不是正常产品界面。版本绑定与未验证范围见[第六轮记录](ITERATION.md#第六轮三套可切换界面2026-10-04)。

### 睡前候选（第三轮）

第三轮证据、启动方式及准确候选提交见 [第三轮记录](ITERATION.md#第三轮全局封面偏好本地图片与栏目装饰2026-10-03)，截图索引为 `.artifacts/iteration-03/cover-personalization/README.md`。部分详情/设置小窗口采集的实际尺寸与文件名不符，不再作为1000×700验证，详见该轮后补勘误；其他栏目独立布局测量仍保留。图片真实codec/备份、源码定向回归及正常EXE试用哈希不受采图缺口影响。夜间源码的分类页同条件原生窗口对照另列于 [OVERNIGHT](OVERNIGHT.md)，不冒充睡前正式程序。

### 第二轮调整（历史候选）

候选 `9c27af3` 的入口、223项定向回归、真实桌面、最终包与线上默认图结果集中在 [调整记录](ITERATION.md#第二轮调整默认封面与个人选择2026-10-03)。该轮截图索引为 `.artifacts/iteration-02/cover-preferences/README.md`：相同4部合成作品、CC0封面、两种窗口与缩放的修改前8图、修改后24图及1张小窗口键盘保存图。先于提交采集的源码/构建哈希已核对与候选一致，详见该目录 manifest 和 package-check。线上作品84图另作真实接口证据，不能与合成图当作同条件视觉对照。

初次票选候选 `c58ec38` 与200条夹具、14张界面图保留在 [原第二轮记录](ITERATION.md#第二轮封面改进2026-10-03) 和 `.artifacts/iteration-02/cover-final/README.md`，属于被默认封面调整取代的历史证据；该历史候选交付时正式程序仍是第一轮。

### 第一轮迭代（历史已发布程序）

第一轮的提交、隔离启动方式和截图索引集中在 [ITERATION](ITERATION.md#提交程序与试用数据)。当时执行 447 项单元、必要桌面链路和专项、实际明暗/缩放/键盘检查及独立打包入口验证；具体结果与限制以该记录为准。当时的书架对照、整理/核对和附加安装材料均使用合成资料，无真实 API 或游戏执行。下面的迁移与 14:06 截图仍作为历史基线，不能说明第二轮改动后的当前界面。

### 已有迁移记录，非本轮重跑

已阅读本机 `.artifacts/migration/` 的报告和日志：395 项单元测试通过；迁移报告记录 13 个打包桌面测试套件及 26 项界面检查通过，新目录的安装、类型检查、构建、打包和隔离启动完成。测试使用临时 profile、合成游戏及模拟 API，没有据此证明真实游戏库识别率。

依据：[迁移报告](../../.artifacts/migration/migration-report.md)、[单元日志](../../.artifacts/migration/unit-tests.log)、[打包日志](../../.artifacts/migration/packaged-tests.log)。这些结果属于迁移阶段；历史文档交接没有重新运行整套测试，当前迭代的验证另列。

### 已有页面证据（2026-10-03 14:06 采集）

证据目录：`D:\Projects\The-Order-Artifacts\review-current`。已有采集记录显示，于 **2026-10-03 14:06（北京时间）** 实际运行 `release/win-unpacked/The Order.exe`，应用版本 0.7.1、`packaged=true`；源码对应旧交接基线 `9c456115b0420513d25388463b8bb14a0f17d956`。manifest 记录的内容区域为 1440×960，6 张 PNG 实际像素均为 2882×1922。文件清单、SHA-256 和结果见 [manifest.json](../../../The-Order-Artifacts/review-current/manifest.json)，场景说明见 [截图 README](../../../The-Order-Artifacts/review-current/README.md)。

| 已有截图 | 场景及数据性质 |
| --- | --- |
| [01-library.png](../../../The-Order-Artifacts/review-current/01-library.png) | 6 部虚构作品的明亮书架：状态、0 分、收藏、时长及未安装的想玩条目。 |
| [02-detail-overview.png](../../../The-Order-Artifacts/review-current/02-detail-overview.png) | 作品详情：合成简介、开发商、来源评分和标签。 |
| [03-detail-management.png](../../../The-Order-Artifacts/review-current/03-detail-management.png) | 管理表单：状态、个人评分、分类、标签、笔记和合成安装目录。 |
| [04-import-selection.png](../../../The-Order-Artifacts/review-current/04-import-selection.png) | 真实扫描 4 个合成目录；1 个已有条目禁选，3 个新条目可导入，默认勾选自动补全。 |
| [05-recognition-running.png](../../../The-Order-Artifacts/review-current/05-recognition-running.png) | 真实识别任务的进度横幅；测试暂挂模拟 API 响应以保留运行中状态。 |
| [06-recognition-review.png](../../../The-Order-Artifacts/review-current/06-recognition-review.png) | 同名冲突产生的 2 个模拟作品候选，以及真实待核对界面和未匹配队列。 |

管理与导入截图记录的是弹窗当时的滚动位置，下部操作需要继续滚动，不表示所有控件同时可见；弹窗可滚动本身不是已确认缺陷。

**该次采集记录的覆盖：** 桌面程序、IPC、扫描、导入、持久化、识别算法、NextMoe 响应解析和候选手动确认。原生文件夹选择器和网络响应由测试驱动替换，8 次 API 请求均使用模拟响应；封面来自仓库 CC0 矢量夹具，标题、角色、评分、笔记和安装全部为合成数据，假 EXE 未执行。

运行记录显示：初始 6 条，加上导入的 3 条后为 9 条；自动匹配 1 条，同名冲突有 2 个候选，手动关联已保存，另有 1 条未匹配记录保留待核对。`rendererErrors=[]`，真实 API 调用为 0，实际游戏运行次数为 0；合成密钥已清除。截图目录仅含 6 张图片、说明和 manifest，不含 profile、密钥、源码或安装包。

**该次采集没有覆盖：** 全量单元测试、重新构建、完整桌面回归、实时 NextMoe 服务、真实账号和游戏库、真实游戏启动与计时。该次采集也没有修改正式版本。6 张截图不包含窄窗口、深色主题、完整键盘遍历或取消竞争验证；这些需在后续相应评审中单独检查。

### 第一轮协作的评审准备核对（历史）

- 准备起点为 `feab2ec91692132507cb83306c3b2e9af4e8c08b`（`main`）。与截图 manifest 的源码基线相比，仅新增 `AGENTS.md`、`CLAUDE.md` 和本文，业务源码、资源、配置与锁文件没有变化；本轮也只修改这三份文档。
- 六张截图均已实际打开检查，SHA-256 全部与 manifest 一致；现有材料覆盖书架、详情与管理、导入选择、缺资料与识别运行、完成和待核对状态，足以开始 Claude 的源码与静态界面只读评审，无需重新采集。
- 本轮只读核对证据，没有重跑测试、构建、打包或启动应用。上文采集运行结果与迁移测试结果均为已有记录；截图不能证明交互、取消竞争、真实联网或游戏启动行为。发现、统计、偏好设置、窄窗口和深色主题缺少本组截图，评审这些界面时须标明证据范围，不把缺图视为已验证。

## 接手时的工作方式

- 在正式仓库工作，不把历史输出、旧候选源码或发布目录作为开发入口。
- 普通开发、`npm start` 和正式 EXE 默认共用 `%APPDATA%\KunGalManager`。调试先设置 `KGM_DATA_DIR` 为独立目录；具体命令见 DEVELOPMENT。
- 不提交 profile、密钥、个人备份、构建、报告或截图；不运行真实游戏来验证纯界面改动。个人库兼容工具要求显式启用且不属于默认测试。
- 改动匹配、保存或身份逻辑时，先补有行为意义的合成回归；修复后运行对应测试，再由维护者统一进行构建和必要的桌面验证。
