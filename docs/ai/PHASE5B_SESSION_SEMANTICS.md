# Phase 5B.0 — Session / Playtime Semantic Design Gate

日期：2026-10-05。结论：**READY FOR PHASE 5B IMPLEMENTATION**。

本规范的“必须 / 不得 / 应 / 可以”分别对应 MUST / MUST NOT / SHOULD / MAY。除“源码事实”部分外，下文均为**待实施的 5B 设计**，不是已经具备的产品能力。5B.0 仅写文档，完成后暂停；后续各阶段仍需用户授权。

## 基线、证据与架构判断

开始时实际核对：分支 `codex/winui-phase5`，HEAD `041ddca804a134aa146ee7b884d6eff596f451d0`，工作区干净。此前独立 targeted Gate 已通过 5A.1，Gate 本身没有产生实现提交。本规范及其纯文档提交不得被称为新的 5A 二进制或 5B 实现 checkpoint。

**源码事实**（文件相对仓库根目录；符号是定位依据）：

| 材料 | 实际契约及设计影响 |
| --- | --- |
| `native/TheOrder.Application/LaunchContracts.cs` | `ProcessEventKind` 实名是 ProcessStarted、ProcessTrackingAttached、ProcessHandoff、ProcessExited、TrackingLimited、Detached；没有名为 ExitConfirmed 的 enum。`LaunchStatus.ProcessCreated` 仅对 Started / StartedButTrackingLimited 为真。`ProcessIdentity` 包含 PID、CreationFileTime、ImagePath、可空 ImageIdentity。事件没有周期性存活采样或 awake 时钟值。 |
| `GameLaunchService.cs` / `LaunchLibrary.cs` | Starting 不等于创建。成功创建后发布 ProcessStarted；若保留 scope lease 时抛错，则可只发布 TrackingLimited，同时返回 ProcessCreated=true。订阅者异常被捕获，不能以事件回调成功作为持久化成功。全局 starting/reserved 限制同时只有一次 owned launch；external AlreadyRunning 也可保留全局占用。 |
| `WindowsGameProcesses.cs` / `ProcessSnapshotGeneration.cs` | Attach 打开受限查询/同步句柄，核对完整 identity；保留句柄约束该代进程。候选代际、真实父关系、父代生命周期区间由 5A 核对。CreationFileTime 可随 identity 跨管理器重启核对；Application 的 Epoch / Incarnation 每次打开重新建立，不能当作跨重启 token。 |
| `WindowsProcessLifetimeScope.cs` / `GameLaunchService.Monitor` | 创建时原子关联匿名、无 kill-on-close Job；独立 lease 支持降级后的 admission 复核。ProcessExited 依赖当前进程真实 exit、authoritative checkpoint、空且完整 scope、未增加的 total 等条件。匿名 Job 不能按持久化 token 在重启后重新打开。Limited / Detached 后原 monitor 会停止并释放进程观察句柄。 |
| `electron/playtime.ts` / `electron/library.ts:recordSession` | duration 使用 performance.now()，不是 Date.now() 相减。随机 UUID 的 session 在 spawn 成功后只存在内存；15 秒 checkpoint 或结束时写累计整秒；零秒不保存；首次保存正秒数才增加次数。同 ID 只加正差额。历史 endedAt 是保存/结束计时时的墙钟，不保证真实退出。 |
| `electron/main.ts` | 有 active/launching 时关窗隐藏，否则允许退出；托盘可显示窗口及退出。requestSingleInstanceLock + second-instance 唤起窗口。before-quit 等待 tracker shutdown，保存失败可取消退出。 |
| `native/TheOrder.Domain/Library.cs` | 现有 PlaySession 仅 Id / StartedAt / EndedAt / Seconds；GameId 在外层 entry，session 无 InstallationId、完成状态、恢复 token。总时长为 long 秒，次数为 long。 |
| `Transactions.cs` / `IsolatedLibraryPersistence.cs` | 单 writer lease、串行队列、最新快照 mutation、提交前 encode/decode 校验；原子替换 main 是提交点。开始 Save 后不以取消制造回滚假象；提交后 warning 仍是成功。备份和 main 是不同写入，不能当成两文件共同原子提交。Open 本身不回写，孤立 tmp 不作为已提交状态。 |
| `LegacyLibraryCodec.cs` / `NativeLibraryCodec.cs` / `LegacyValidation.cs` / `LegacyMapping.cs` | 当前 native codec 包装 legacy codec 并额外保留 installation 路径信息；尚不能保留新 session 元数据。旧 session 的 endedAt 必须不早于 startedAt，total 允许高于 session sum。既有合并对正秒数重复记录扣一次 count；不能直接用于新的零秒且已计次 session。 |

直接相关测试审计入口：`tests/playtime.test.ts`、`tests/library.test.ts`；`native/TheOrder.Tests/LaunchTests.cs`、`LaunchSafetyTests.cs`、`LaunchPathTests.cs`、`LaunchDifferentialTests.cs`、`ProcessFactRemediationTests.cs`、`ProcessGenerationTests.cs`、`ProcessScopeObservationTests.cs`、`TransactionSafetyTests.cs`、`TransactionRemediationTests.cs`、`TransactionDifferentialTests.cs`、`LegacyCompatibilityTests.cs`、`LegacyRemediationTests.cs`，以及测试中引用的原子持久化故障素材和 legacy oracle。它们是现有行为的依据，不是本规范新能力已通过的证据。5B.0 不重跑构建、发布、桌面或真实游戏测试。

**架构判断：无须重开 5A.x。** 5B 可以新增单消费者 Session coordinator、只读存活采样、awake 时钟、恢复验证及 session transaction；不得更改 5A 事件的含义、放松 handoff / exit 或 occupation 门槛。5A 是事实来源，不是现成的计时器。只依赖 Started / Exited 的 UTC 相减不能实现本规范。

## 1. Session 状态机

生命周期：`Open`、`RecoveryPending`、`Completed`、`Incomplete`、`LegacyUnknown`。后三者是不可重新开启的历史状态。`NoSession`、`Requested`、`PendingDurable` 是运行时准备状态，不是已计次历史。

跟踪能力独立为 `LaunchScope`（仍受本次 5A monitor 约束，**不保证当前就有完整退出证明**）、`CurrentProcessOnly`、`Unknown`、`Detached`。计时许可独立：用户暂停标志，以及 `Running`、`AwaitHandoff`、`Suspended`、`Unverified`、`PersistenceBlocked`、`Stopped`。表中简称 Active=Open+Running；Limited=Open+CurrentProcessOnly/Unknown；Paused=Open+用户暂停。休眠、存储阻塞不得清除用户暂停意图。

`W` 表示进入同一 library 原子事务，包含状态及本次累计差额；重复且无变化可以不写。`Δ` 是第 18 节的非负整秒差额。所有行均不得 kill 游戏。未列明的不合法事件必须保持原状态并记录有界诊断，不得“尽量猜”一个转移。

| Current State | Event / Fact | Preconditions | Next State | Duration? | Durable write? | PlayCount? | Total update? | Exit claimed? |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| NoSession | LaunchRequested / Starting | 5A admission 允许 | Requested | 不计 | 否 | 0 | 0 | 否 |
| Requested | 启动失败 / 创建前取消 | 无 ProcessCreated 事实 | NoSession | 不计 | 否 | 0 | 0 | 否 |
| Requested | ProcessStarted | owned operation，成功创建 | PendingDurable → Open | 首次提交后按可信样本计 | W 创建 | 首次 +1 | 初始 0 | 否 |
| Requested | StartedButTrackingLimited | ProcessCreated=true，即使 identity=null | PendingDurable → Limited | 有可靠当前存活才计；否则冻结 | W 创建 | 首次 +1 | 初始 0 | 否 |
| PendingDurable | 首次创建写失败 | 进程实际已创建 | PendingDurable / PersistenceBlocked | 不计，重试同一 ID | 失败不改变磁盘 | 0 | 0 | 否 |
| PendingDurable | 提交成功 / 已知终结事实 | 同一 operation 的排队事实 | Open 或直接终态 | 不补提交前时间 | W 可合并创建与终结 | 总共 +1 | Δ | 仅有效 ProcessExited 才是 |
| Open / PendingDurable | duplicate start | 同一 ID、同一 binding | 原状态 | 不重置 baseline | 无变化不写 | 0 | 0 | 否 |
| Open | ProcessTrackingAttached | operation/current identity 一致 | Open / LaunchScope | 不重置 start；按样本 | W 能力变化 | 0 | Δ | 否 |
| Active | 同 identity 存活采样 | 无暂停/休眠/存储阻塞 | Active | 加已确认 awake 区间 | 周期 W | 0 | Δ | 否 |
| Active | 当前进程退出，5A 尚观察 handoff | 无最终 ProcessExited | Open / AwaitHandoff | 冻结；仅暂存候选桥接区间 | W 状态变化 | 0 | Δ | 否 |
| Open / AwaitHandoff | ProcessHandoff | 5A 验证 Previous→Current，operation 相同 | Open，current=child | 符合第 13 节才连续桥接 | W 含新恢复身份 | 0 | Δ | 否 |
| Paused | ProcessHandoff | 同上 | Paused，current=child | 仍不计 | W | 0 | Δ | 否 |
| Active / AwaitHandoff | TrackingLimited | 当前 identity 仍能可靠验证 Running | Limited / Running | 只计当前进程已验证区间 | W | 0 | Δ | 否 |
| Active / Limited | TrackingLimited / Query Unknown | 无可靠当前 liveness | Limited / Unverified | 冻结在最后确认区间 | W | 0 | Δ | 否 |
| Limited / Unverified | tracking restored | 完整核对同一当前 identity | Open；能力取实际证据 | 新 baseline；不补 gap | W | 0 | Δ | 否 |
| Limited | 当前进程确实 gone / 身份冲突 | 5A 观察已明确结束且无 final fact，或仅恢复后的当前进程观察 | Incomplete | 停止 | W + reason | 0 | Δ | 否 |
| 任意 Open（包括 Paused） | suspend / 检测漏报 suspend | 电源通知或时钟断点 | Open / Suspended | 冻结 | 尽可能立即 W | 0 | Δ | 否 |
| Suspended | resume | 同 identity、installation 验证成功 | Open；保留用户暂停 | 新 baseline；sleep 不计 | W | 0 | Δ | 否 |
| Suspended | resume 验证 Unknown | 无法证明当前存活 | Limited / Unverified | 不计 | W | 0 | Δ | 否 |
| Suspended | resume 验证 gone / mismatch | 5A 仍在确认则等待；已停止且无 final fact 才终结 | AwaitHandoff / Incomplete | 不计 | W | 0 | Δ | 否 |
| Open | 用户停止计时 | 合法当前 session | Paused | 冻结；仍可观察 | W | 0 | Δ | 否 |
| Paused | 用户恢复计时 | 同一 session/当前 identity/安装可信 | Open / Running | 新 baseline，不补暂停 | W | 0 | Δ | 否 |
| Paused | 用户恢复但 identity Unknown / mismatch | 不得 PID-only 恢复；5A尚在确认则等待 | Unverified / AwaitHandoff / Incomplete | 不计 | W | 0 | Δ | 否 |
| Open | 5A Detached | 观察停止 | RecoveryPending / Detached | 冻结 | W；有 token 才可恢复 | 0 | Δ | 否 |
| Open / RecoveryPending | 用户显式停止观察 | 有意放弃此次后续跟踪 | Incomplete / UserStoppedObservation | 停止；不可自动恢复 | W；清 token | 0 | Δ | 否 |
| 任意非终态 | Close Window → tray | 托盘可用 | 原状态 | 原规则继续 | 立即 checkpoint，可合并 | 0 | Δ | 否 |
| Open / Paused / Limited | Explicit Exit | 排空事实并停止新命令 | RecoveryPending；无恢复身份则 Incomplete | 冻结 | W 成功才正常退出 | 0 | Δ | 否 |
| 任意非终态 | shutdown / logoff | OS 生命周期结束 | 同 Explicit Exit 的目标状态 | 冻结 | 有限时间尽力 W | 0 | 已提交 Δ | 否 |
| 任意非终态 | crash | 无 graceful 回调保证 | 磁盘保留最后状态 | 后续时间不计 | 无额外写保证 | 0 | 0 | 否 |
| 磁盘 Open / RecoveryPending | restart recovery success | 第 8 节全部核验，writer 唯一 | Open / CurrentProcessOnly | 新 baseline，保留暂停 | W 恢复标记 | 0 | 0 | 否 |
| 磁盘 Open / RecoveryPending | recovery failure / PID reuse / reboot | 任一必要证明失败 | Incomplete | 最后 durable duration | W；清 token | 0 | 0 | 否 |
| 任意非终态 | authoritative ProcessExited | 匹配 operation/current；真实 exit 时间有效 | Completed / ExitConfirmed | 冻结已确认区间，不计观察延迟 | W 终结 | 0 | Δ | 是，仅 5A 所证明范围 |
| 任意非终态 | “exit” 缺真实时间 / identity 不符 | 不满足上一行 | Limited / Unverified 或忽略旧代 | 不追加猜测时间 | 状态变化才 W | 0 | Δ | 否 |
| 任意 Open | 写失败 / 未持久化达 60 秒上限 | 尚未确认 commit | 原生命周期 / PersistenceBlocked | 达上限冻结 | 重试同一累计版本 | 0 | 未提交为 0 | 否 |
| PersistenceBlocked | commit receipt + 再验 liveness | 同 binding，存储可用 | 原暂停状态或 Running | 新 baseline，不补阻塞 gap | W 的 receipt | 0 | 仅 Δ 一次 | 否 |
| Completed / Incomplete / LegacyUnknown | duplicate exit/checkpoint/recovery/旧代事件 | terminal absorbing | 原终态 | 不计 | 同内容 no-op；冲突拒绝 | 0 | 0 | 不新增声明 |
| NoSession / Requested | external AlreadyRunning | 未创建 owned process | NoSession；5A occupation 另存 | 不计 | 无 session 写入 | 0 | 0 | 否 |
| Open / Paused / RecoveryPending | 请求启动另一游戏/安装 | 全局 owned slot 未解除 | 原状态；拒绝新启动 | 原规则 | 无新 session | 0 | 0 | 否 |

“停止计时”“停止观察”“释放启动占用”“确认退出”必须是不同操作/事实。历史终结或暂停不得直接释放 5A reservation。用户停止观察后，5A 若仍不能证明安全释放，仍可拒绝新的启动；界面必须说明原因，不提供暗中强制释放路径。

## 2. Process Fact → Session event

| 当前真实 contract | 5B 输入与规则 |
| --- | --- |
| `LaunchStatus.Outcome=Starting` + Snapshot | 记录运行时 operation → GameLaunchIdentity / 实际 Installation 快照；只产生 LaunchRequested。 |
| `ProcessEventKind.ProcessStarted` | 对该 operation 去重后产生 OwnedProcessCreated；若 Current 非空，从其 CreationFileTime / OccurredAtUtc 取真实开始 UTC；null 保持未知。 |
| `LaunchStatus.ProcessCreated=true`，只有 TrackingLimited 事件的 post-create 异常分支 | 同样产生 OwnedProcessCreated。必须订阅状态并核对启动返回结果，不能只订阅 ProcessStarted，否则真实成功启动会漏计。不得把它误报为“启动失败，请重试”。 |
| `ProcessTrackingAttached` | TrackingCapabilityChanged，不是 Start。 |
| `ProcessHandoff(Previous, Current)` | CurrentIdentityChanged；同一 operation/session。Previous 必须等于 coordinator 当前身份；重复相同 child no-op，乱序旧 child 不回退。 |
| `TrackingLimited` | 能力下降；独立只读采样决定能否继续计时；不是 exit。 |
| `Detached` | ObservationStopped；保留已累计，按状态表处理；不是 exit。 |
| `ProcessExited` + 有效 `ExitOccurredAtUtc` | 本规范的 ExitConfirmed。`OccurredAtUtc` 的 exit 值应与之相同；冲突或缺失不能正常 Completed。 |
| `LaunchStatus` AlreadyRunning / AlreadyLaunching | 不创建 owned session；不得把 external identity 转换成 ownership。 |
| 5B 新增的只读样本、电源、用户暂停、应用生命周期输入 | 属于 5B adapter，不是伪造的新 5A authoritative exit。 |

coordinator 必须在 launch 前接好订阅，回调仅同步捕获不可变事实并入队，不能 async-void 写库。事件没有 GameId，必须从该 operation 预先保存的 snapshot 取 binding，不能在稍后读取可能已变化的全局 Status。单消费者分配 revision、串行处理，UI 订阅异常不得丢失 accounting。成功返回结果要与已接收状态去重核对；关键事实队列必须有界且不得静默丢 Start/Exit（压力时冻结计时、拒绝新启动并显式故障）。轮询样本和 checkpoint 可合并。

实际 operation snapshot 必须取本次 LaunchAsync 发布的 Starting/创建状态。LaunchAsync 内部会重新 Prepare，不能把 UI 较早 PrepareAsync 的预览 OperationId 当作真实启动 ID。5B sampler 发现当前 gone 时，如果原 5A monitor 尚可能发布 handoff / 最终退出，必须冻结并等待该事实，不得抢先写不可逆的 Incomplete。状态回调早于 ProcessChanged 的正常发布顺序也不能造成提前终结。

## 3. Session start

**必须在 The Order 的同一 owned launch operation 确认进程创建成功后，首次原子持久化 Session，并且恰好增加一次 playCount。**

SessionId 使用该次 `LaunchSnapshot.OperationId` 的规范 GUID 字符串，不使用 PID、标题或安装路径；GameId 与实际 InstallationId 在创建时固定。点击、预检、Starting、启动失败、external AlreadyRunning 均不构成 start。已有独立 invocation 不得共用 ID。

StartedAtUtc 是初始进程真实 CreationFileTime 转成 UTC；不能取点击时间、TrackingAttached 时间或 child 创建时间。成功创建但身份/时间不可得仍建立零秒 Limited session，StartedAtUtc=null，并另存 CreatedObservedAtUtc 供显示“启动时间未确认”。同一初始身份稍后可靠取得时可以补齐空开始时间，不得被 handoff 子进程时间替换。首次 durable 成功之前不累积可结算时长；极端情况下进程已经创建但管理器在首次写入前崩溃，会少一条 invocation，不能承诺 OS 创建与 JSON 提交跨资源原子化。实现不得为了消除这个窗口重新启动游戏。

## 4. Session end

仅匹配当前 operation/identity 的 5A authoritative ProcessExited 可以写 `Completed / ExitConfirmed`，且 **EndedAtUtc = ExitOccurredAtUtc**。这只证明 5A 所覆盖的 lifecycle，不宣称外部 broker / RPC 或 breakaway 游戏全局结束。

Incomplete / RecoveryPending 的 EndedAtUtc 必须为 null；`AccountingCutoffAtUtc` 表达停止/冻结计账的墙钟观察边界，与真实退出完全分开。正常 Completed 同样可保存 accounting cutoff，因最后可信时长样本可能早于真实 exit。Paused 不终结 session；用户显式停止观察终结为 Incomplete，不允许稍后自动恢复。

关闭管理器、scope 空、占用释放、监视器停止、查询失败都不得生成 ExitConfirmed。未知可保留 Open/Unverified 等待同身份恢复或用户处理；明确 gone/mismatch，且原 5A 观察已结束、不会再提供 final fact，或仅有恢复后的 CurrentProcessOnly 观察时，才终结 Incomplete。不以超时猜退出，不抢在 5A 的正常确认窗口之前封死历史。

## 5. 时间模型与采样边界

5B 时间源必须提供：`Awake100ns`（单调、排除 sleep/hibernate）、`UtcNow`（仅展示/诊断）、`SuspendEpoch`（通知及漏报检测形成的断点）。Windows 的 awake 实现选择 `QueryUnbiasedInterruptTimePrecise`；不使用 DateTime 差、Stopwatch 偶然的 sleep 行为或 5A 的启动性能测量值来结算时长。测试注入独立可控的 awake / UTC / suspend sources。

Microsoft 明确规定 unbiased interrupt time 不包含睡眠、休眠，单位为 100ns：[QueryUnbiasedInterruptTimePrecise](https://learn.microsoft.com/en-us/windows/win32/api/realtimeapiset/nf-realtimeapiset-queryunbiasedinterrupttimeprecise)。该 API wrapper 是待实现项，不是现有能力。

推荐每 1 秒只读查询一次当前保留句柄。用 awake 时钟包围一次完整 identity+liveness 查询；记 query 前后为 `[a,b]`。只有相邻两次均确认**同一代仍 Running**，且无暂停、suspend、unknown、detach、writer 切换时，才可计入 `[b_previous,a_current]`。查询期间的微小间隔保守不计；计时不向未来外推超过最后确认样本。不能以每个 UI tick 的到达时间直接增加秒数。5B 可通过现有 Attach 获取自己拥有的受限观察句柄；不得借此修改 5A scope / reservation 或重新判定 descendant。

初始创建到首次可信样本、最后 Running 样本到实际退出之间，没有可证明的 awake 边界时保守少计；不得用 UTC exit 减 UTC start 补尾。正常约少一个采样周期加查询耗时；调度阻塞时**没有固定 1 秒误差保证**。真实 exit 的 UTC 仍准确保存。6 秒的 exit 确认等待、阻塞的 observer、事件迟到均不得加入 duration；handoff 的有限桥接见第 13 节。

所有区间先用 100ns 整数相加，再统一向下取整秒；不得逐区间取整丢掉每次不足一秒的余数。clock 回退/不可用冻结并报 Limited，不回退 UTC 计时。UTC 前进、后退、NTP、时区变更不改变累计；真实 EndedAtUtc 可以早于 StartedAtUtc，必须保留事实，不修正 duration。历史默认按真实 StartedAtUtc、缺失则 CreatedObservedAtUtc、再 SessionId 稳定排序；只代表显示顺序。

## 6. Sleep / Hibernate

两者一律不计时。可靠 suspend 到达时立即冻结到最后确认样本并尽力 checkpoint，不结束 Session；resume 必须重验当前完整 identity 和 installation，成功才建立新 awake baseline，未知冻结，gone/mismatch 按第 1 节处理。重复通知幂等；用户暂停优先，不因 resume 自动取消。

不得只依赖 UI 消息及时送达。每次存活采样也必须检测 suspend discontinuity：以包含休眠的 interrupt time 与 awake time 的偏移变化作旁证，使用前后读取区间覆盖调用误差；不能把采样抖动算作可补时长。偏移有正向跳变、通知 epoch 改变或读取跨越不确定边界时丢弃跨边界区间并重新验证。即使通知漏报，计时源本身也排除了真正睡眠时间；可疑 awake 尾段保守丢弃，不补 gap。包含休眠的 interrupt time 仅用于断点检测，绝不直接结算。官方依据：[Interrupt Time](https://learn.microsoft.com/en-us/windows/win32/sysinfo/interrupt-time)、[PBT_APMRESUMEAUTOMATIC](https://learn.microsoft.com/en-us/windows/win32/power/pbt-apmresumeautomatic)。

## 7. 管理器退出但游戏继续

第一版采用 Recoverable Checkpoint：冻结已确认累计；在同一 session transaction 中写 RecoveryPending、cutoff、用户暂停状态和最小恢复 token。无可靠 token 则写 Incomplete / RecoveryUnavailable。只有 durable 成功并排空必要事实才正常退出；保存失败取消显式退出，保留窗口/托盘和清晰重试入口，停止新增累计，不自动退出并谎称已保存。

释放 observer 和 Job lease 不 kill 游戏。离线时间不计；token 只是恢复资格，不是后台计时器。下次启动仍须逐项验证，不能因为上次是“正常退出”就跳过检查。

## 8. Crash / restart recovery

现有 `ProcessIdentity` 的 PID + creation FILETIME + image path/file identity 可跨管理器重启复核；Epoch、Incarnation、OS handle、匿名 Job 不可跨重启使用。恢复必须增加 5B 的**本地资格封装与 boot guard**，不扩大 Attach 的宽松程度。

恢复条件必须全部成立（它们也定义首次生成/更新 token 时必须已经拥有的证据；不能在重启时补造 ownership）：

1. 单实例 primary 已取得 Phase 2 writer lease；只读打开合法 canonical main。候选只来自该文件中自己持久化的非终态 session，不能来自外部进程发现、legacy hint、导入或备份恢复。
2. token 以当前用户/机器 DPAPI 保护，绑定当前 profile 根目录的物理 identity、SessionId、原 GameId、原 InstallationId 及版本。重用现有 WindowsDataProtection 机制，但不读任何真实凭据；不是加密整份历史。token 解析有长度限制，失败不 attach。
3. token 保存同一次 boot 的 BootId。只读读取 `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters\BootId`，校验值存在、类型正确，验证前后未变，且与 token 相同。微软将其定义为每次成功 boot 递增的计数：[BootId 文档](https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/oem-hvci-enablement)。不修改该键、HVCI 或安全设置。不以墙钟 LastBootUpTime、相近 creation time 或相近 uptime 代替。读取不支持/拒绝/发生歧义时禁用这次恢复，标 Incomplete；必须在 Windows fixture 中验证重启及 Fast Startup 边界。BootId 仅为重启护栏，不独立证明进程身份或防本机管理员篡改。
4. token 当前进程的 PID、CreationFileTime、ImagePath、非空 ImageIdentity 必须与现有 Attach + Query 完整相同且确实 Running；不得 PID-only、按名字扫描、挑最新进程或认领新的 descendant。缺少可持久核对的 file identity 则该 session 不具备恢复资格。
5. 原 installation 仍存在，ID 与启动时保存的 folder / executable / effective cwd 的最终路径、类型和 filesystem identity 相同；child image 必须是 token 中已验证的当前 image。使用现有路径边界检查。preferred 改变不影响 binding；参数后来改变不重写这次 invocation，也无需持久化其副本。安装目录移动/替换不是同身份恢复。
6. 成功后先原子提交同一 SessionId 的 recovery marker 和新 writer owner，再开始新采样 baseline；不 +count、不把旧 accumulated 再加 total、不补最后 durable checkpoint → 本次观察之间的任何时间。

**恢复后为 CurrentProcessOnly / Limited**：无法取回旧匿名 Job，不能声称完整 scope 已恢复。可继续对同一当前进程计已证明的区间；它后来退出而没有原 5A authoritative exit 时终结 Incomplete / RecoveredProcessEnded，不把一个裸进程的 exit 冒充整个游戏 lifecycle 的结束。恢复后不重新调用 Launch；5B 为这条既有 owned session 保留全局 admission guard，直到该 identity 明确失效。Unknown 仍阻止新 owned launch；恢复 guard 的解除仍不等于 ExitConfirmed。

进程已不存在、PID 复用、不同 PID、image/installation mismatch、Windows reboot、DPAPI/profile/boot 验证失败：保持最后 durable 秒数，写 Incomplete + 明确 reason，EndedAtUtc=null，不 attach、不猜退出时间。重复恢复命令以 writer lease 生存期的 RecoveryAttemptId 去重；同一次启动最多标记恢复一次。若恢复写后再次 crash，下次确实是另一次恢复，可更新 marker，但计次和历史总量不变。

若打开的是 Phase 2 backup fallback：不可假定它包含最新 token 或 counted marker；旧非终态必须变 Incomplete / BackupRecovery，不 attach。备份回滚可能丢失最近已提交历史，这是显式存储灾难恢复边界，不属于正常 crash 的 60 秒上限；不得声称能从损坏/丢失的文件重建未见数据。

## 9. TrackingLimited、暂停与停止观察

- 当前 identity 可靠存活，只是完整范围或 handoff 能力受限：保持同一 Session，允许 CurrentProcessOnly 的已验证 awake 区间，不产生 ExitConfirmed。
- 当前 liveness Unknown：冻结在最后可信区间，标明“跟踪受限，计时已暂停”；可在同 identity 重新验证成功后恢复，不补 gap。Unknown 不是死亡，不无限继续计时，也不强制立即终结。
- 当前明确 gone/mismatch 且没有有效最终事实：按第 4 节先排除仍在等待的 5A 确认，再进入 Incomplete。未知状态一直无新证据时可以保持待处理，用户可显式停止观察；不设置“超过 N 分钟就当退出”的规则。
- 用户“停止计时”只设置暂停并 checkpoint；保留观察和启动占用。仅 liveness 已证实时显示“游戏仍在运行，计时已暂停”；否则显示未知。恢复按钮必须重验同 session/当前 identity/installation，重建 baseline。暂停期间 handoff 更新身份但不取消暂停；暂停期间 authoritative exit 可以 Completed。
- 用户“停止观察”清除恢复资格并以 Incomplete 终结。管理器退出造成的 detach 则采用 RecoveryPending；不得用一个 bool 混合两者。停止观察按钮可以不在首版 UI 暴露，Application 必须区分原因。

## 10. Play count

新 Session 首次 durable 创建与 `playCount += 1` 必须同一原子事务；0 秒、无窗口、立即退出、StartedButTrackingLimited 均可算一次成功 launch。无任意 5/10/30 秒阈值。

LaunchRequested、失败、TrackingAttached、handoff、Limited 切换、恢复、暂停/继续、重复事实、external AlreadyRunning 均不得增加。成功启动但首次写失败，只能显示“启动成功，记录待保存”，durable count 尚未增加；重试同一 ID。

**与旧 Electron 的有意差异**：旧版首次正秒记录才 +1，零秒完全忽略；5B 改为首次 durable 创建就 +1，使次数表达成功 invocation。仅作用于新 session，不重算、补算或降低历史次数。

既有 `LastPlayedAt` 继续作为历史展示摘要：首次创建事务取旧值与本次 StartedAtUtc（未知则 CreatedObservedAtUtc）的较晚值；同初始身份补齐真实 start 时也只向后推进。handoff、pause/resume、recovery 不写成新的“上次启动”；不得拿这个墙钟摘要结算时长或证明当前运行。

## 11. Total playtime

`TotalPlaySeconds` 保留现有整秒单位及合法历史底数。每次仅追加第 18 节的 Δ；不能用 `sum(sessions)` 覆盖总量。历史 total=1000、已知 session sum=100 是合法状态；新 session 首次记录 30 秒后总量应为 1030，而不是 130。

用户暂停、休眠、未知 gap、管理器离线、写盘阻塞后的冻结区间不计。运行时可显示已确认但尚未持久化的时长，必须区分保存状态；不得将 UI 值当作已保存总量。溢出、现有单 session/整库容量上限触发显式存储受限并冻结，不截断、删旧历史或静默丢新记录。合并/删除是独立库操作，不得拿 accounting 的单调不下降承诺当作自动恢复被用户删除条目的理由。

## 12. History schema（待实施，最小扩展）

保留 library envelope 版本及 legacy 四字段 `id / startedAt / endedAt / seconds`；新记录增加一个 `native` 对象，`native.version=1`。NativeLibraryCodec 必须保留它贯穿 normalize / project / mutation / merge / encode，不能仍经只认识四字段的投影丢失元数据。下表为语义字段名；固定 JSON spelling 在 5B.1 落地并加入 golden round-trip，不能改语义。

| 字段 | 新 session | 旧 session 无 native 时 | 语义 |
| --- | --- | --- | --- |
| SessionId (`id`) | required，operation GUID | 原样保留已有 ID | 稳定去重键，不能 PID 派生 |
| GameId (`native.gameId`) | required，原启动 game | 外层所属 game | invocation 原 binding；合并后的外层 owner 可改变，原 binding 不改 |
| InstallationId (`native.installationId`) | required、非空 | null | 实际启动安装，不是当前 preferred |
| StartedAtUtc (`native.startedAtUtc`) | nullable | 旧 startedAt，标 legacy provenance | 初始真实创建 UTC；新记录未知时必须 null |
| CreatedObservedAtUtc (`native.createdObservedAtUtc`) | required | 旧 startedAt 作为 legacy 展示 fallback | 观察时间，不是 OS 创建证明 |
| EndedAtUtc (`native.endedAtUtc`) | nullable；仅 Completed 非空 | null | 真实最终 exit UTC，允许早于 start |
| AccountingCutoffAtUtc (`native.accountingCutoffAtUtc`) | nullable；首次计账或冻结后有值 | 旧 endedAt | 最后已确认 accounting / 冻结边界的 UTC 展示值，非 duration 来源 |
| Accumulated100ns (`native.accumulated100ns`) | required，非负整数 | seconds × 10^7 | 已 durable 确认的累计，含不足一秒余数 |
| AccountedSeconds (`seconds`) | required，非负整数 | 原 seconds | 已加进 aggregate 的 marker；新记录必须等于 floor(Accumulated100ns / 10^7) |
| Lifecycle (`native.lifecycle`) | required | LegacyUnknown | Open / RecoveryPending / Completed / Incomplete / LegacyUnknown |
| Reason (`native.reason`) | nullable；terminal / RecoveryPending required | LegacyUnspecified | 精确定义见下文；不得从 endedAt 猜 ExitConfirmed |
| UserPaused (`native.userPaused`) | required bool | false | 跨休眠/重启保留用户意图；非存活证明 |
| Revision (`native.revision`) | required，非负安全整数 | 0（内存默认） | 同 writer 事件序列、累计 payload 去重/CAS |
| WriterOwner (`native.writerOwner`) | required，新随机 writer epoch | null | 提交拒绝旧 writer；不是 ProcessIdentity/跨重启真实性证明 |
| Recovery (`native.recovery`) | required `{count,lastAttemptId}`；初值 `{0,null}` | `{0,null}` | count 非负；同 attempt 不重复标记，lastAttemptId nullable |
| RecoveryToken (`native.recoveryToken`) | nullable；有资格的非终态才保存 | null | 第 8 节 DPAPI blob；终态必须清除 |

不永久保存完整 process chain、原始事件、OS handle、暂停区间列表或每秒样本；当前进程 identity 仅在 RecoveryToken 中保存恢复所需部分。其他跟踪/计时状态由运行时事实得出，重启一律先 Unverified，不能从磁盘的“Running”直接恢复计时。OccurredAt / ObservedAt 对照和 handoff 链只进入有界诊断，不包含凭据。

Reason 的 v1 闭集：`ExitConfirmed`、`ManagerExit`、`SystemShutdown`、`ObserverDetached`、`UserStoppedObservation`、`IdentityLost`、`ProcessGone`、`RecoveredProcessEnded`、`BootChanged`、`RecoveryUnavailable`、`RecoveryInvalid`、`BackupRecovery`、`ImportedSnapshot`、`LegacyUnspecified`。普通 Open reason=null；临时 Limited 原因是运行时状态，不反复增加永久历史字段。

兼容四字段的 `startedAt` 是真实 start，否则 CreatedObservedAtUtc；`endedAt` 是真实 exit，否则 cutoff，否则兼容 start，并 clamp 至不小于该 start。它们是**legacy display projection**，不能反读成 native 真实结束；native 时间保留原值。原生 UI 必须优先 native 字段，未知退出显示“结束时间未确认”。只改变 projection 不改变 duration。旧记录不要求补写 native，不因缺字段损坏；一旦有 native，v1 required 字段缺失/非法组合不得按 legacy 默默读取。

新数值必须 checked 运算并保持当前单 session（366 天）、安全整数、session 数量和 32MiB 整库校验边界；恢复 blob 解码前上限 32KiB，超限拒绝。未知 native version、enum 或无法无损保留的 future field：整库以 `ValidReadOnly / UnsupportedSessionSchema` 打开，显示原因、保留原始 bytes，不当成 corruption 去退回备份，不允许一次普通编辑把 future 元数据抹掉。当前 native codec 尚无这些能力，必须在 5B.1 实现并测试。

## 13. Handoff

只接受 Phase 5A 已验证的同 operation `ProcessHandoff(Previous,Current)`，SessionId / 原 GameId / 原 InstallationId / StartedAtUtc 均不变；更新当前恢复身份，立即 checkpoint，计次不变。多步逐次应用；旧 parent 的退出不是当前 session 的最终退出。

正常 handoff 保持连续：在旧进程最后可信样本与新 child 首次可信样本之间，可以暂存**同一管理器、同一 launch lifecycle**的 awake 桥接区间，只有收到权威 handoff、核对 Previous/Current，且该段没有 user pause、suspend、Unknown、Detached、存储冻结，才纳入累计。父子出生关系证明仍完全由 5A 提供，5B 不按路径或 PID 猜链。桥接等待上限沿用当前退出后 handoff 观察窗 6 秒；超窗、未确认、进入 Limited 的 gap 丢弃，但 Session 仍相同。此候选区间在确认前不进 durable duration；若 crash 也不补。

如果 child 已有可信样本，要对区间取并集，不能把 child 自身计时和 bridge 重叠相加。暂停时 handoff 只换身份；不补暂停段。handoff 与 checkpoint 同时发生按 coordinator revision 顺序提交，旧 checkpoint 不得覆盖新 child token；无需永久保存链。

## 14. External AlreadyRunning

第一版不 takeover。发现外部匹配进程只显示 AlreadyRunning 并服从 5A 占用复核，不创建 session、count、start time、恢复 token 或自动 Attach 计时。只有同 profile 的有效 owned durable token 通过第 8 节才是 recovery；不能把“可 Attach”解释为“属于用户从 The Order 启动”。未来显式接管功能需另行设计授权。

## 15. 多游戏 / 多安装

源码确认旧 Electron 和当前 5A 都是**全局单次 owned launch**，包括不同 Game 和同 Game 不同安装。因此 5B 第一版最多一个可恢复/活动 owned Session 占用；暂停或 Limited 不解除限制。不得为了 multi-session 表格放开 5A admission。历史可有任意多条合法 session，coordinator / persistence 以 SessionId 索引而不是以全局 timer 值当身份，给未来单独扩展留清晰边界。

实际 InstallationId 固定；修改 preferred、个人名称、笔记或后续启动参数不能重写此次 binding。当前管理器的合法 handoff 只换进程 token，不改变安装 ID。新的 unrelated metadata 编辑可以继续，session mutation 必须从队列最新 entry 追加，不能覆盖它们。

涉及该非终态 game 的删除、合并、导入替换、移除/替换绑定 installation 必须在同一事务入口拒绝并要求先处理该 session；普通 preferred 切换不受限。其他条目编辑不阻塞。终结后可以按正常库流程操作。删除再恢复同 ID 不能接收旧 session callback；使用现有 launch incarnation 加 session writer/revision 双重保护。重启时多个非终态 token 是违反单 owned slot 的状态：全部冻结，标 Incomplete / RecoveryInvalid，不任选一个接管。

## 16. Tray / close / exit / single instance

Close Window 默认隐藏到托盘，即使当前无游戏；计时、观察、checkpoint 继续。与旧 Electron 仅 active/launching 时隐藏不同，此改变使“关窗口”和“退出管理器”一致且可预测。托盘创建失败时必须保留可访问窗口和错误说明，不能隐藏成不可找回的进程。

Explicit Exit：封闭新 launch/session 用户命令 → 冻结计时并排空已捕获事实 → 同一事务 checkpoint/RecoveryPending（已确认 exit 优先 Completed）→ 停止观察并排空停止期间已接收的必要事实 → library shutdown 排空 → 退出。终态不因后来 Detached 降回 RecoveryPending。仍在创建的 launch 必须等其结果并记录成功事实；不能在进程已创建后以取消假装不存在。保存失败取消正常退出。全程不在 UI 线程同步阻塞，不 kill game。

Windows shutdown/logoff 只给尽力保存的总预算（目标最多 5 秒，不保证 OS 给足时间），不得无限阻止系统关机；不得把开始后的原子 Save 宣称为可撤销。OS 强制终止时只相信重启读到的 main old/new 完整版本，按 crash 恢复，不能声称 graceful 保存成功。

单实例必须在创建 Session Engine、恢复或取得 authoritative writer **之前**完成 primary/secondary 分流；使用 Windows App SDK AppInstance 注册/激活转发，第二实例只转交 activation/arguments 后退出，不直接启动游戏、不写库、不恢复、不增加 count。primary 处理参数仍走正常 admission 和用户动作语义，不因 activation 自动新建 Session。官方入口：[Windows App SDK single-instancing](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/applifecycle/applifecycle-single-instance)。

实例键为稳定 AppID + 当前 Windows 用户 + profile 规范物理 identity：同 profile 只能一个 primary，Phase 2 writer lease 是第二道独立防线；开发显式不同隔离 profile 可以并存，不访问正式 profile。无法判定 profile identity 时拒绝写入，不退回宽松键。激活转发失败不得把 secondary 升格为第二 writer。托盘显示从 coordinator 状态派生，不是计时或持久化权威。

## 17. Persistent checkpoint 与损失上限

采用 **55 秒确认且未持久化的 active duration 触发周期 checkpoint，60 秒为硬上限**。它是约每分钟写一次整库，并留 5 秒正常提交余量；与旧版 15 秒相比降低整库写放大。没有 duration/state 变化时不写，每秒采样不写盘。周期按累计差额而非 UTC 计算，暂停一小时不会生成 60 个空 checkpoint。

Start、handoff、suspend、pause/resume、关键 Limited/恢复、authoritative exit、关窗/显式退出、合法身份更新都立即请求 W；可以合并同一队列中尚未开始的 checkpoint，但不得越过 Start/terminal/recovery 的原子边界。库的非计时 mutation 仍串行；采用 latest snapshot，不锁 UI 等待整个文件写入。

硬不变量：运行时已接纳累计 `A` 与**最近确认提交 receipt** 的累计 `D` 之间，`0 <= A-D <= 60 × 10^7`。新增可信区间只接纳到这个上限，超出部分不先计入再回滚；达到上限冻结，直到收到 commit receipt 并再验同一 identity 才从新 baseline 继续。写入失败、队列堵塞、磁盘慢、线程长时间未调度也不能绕过上限；保存阻塞显示明确状态。不自动补阻塞 gap，重试可以逐步退避且不增加计时。

因此普通 crash（权威 main 文件仍完整）最多丢失 **60 秒已确认但未 durable 的 active duration**；不足一秒余数由 ticks 保存。这不是“任意状态最多少算 60 秒”的承诺：首次 Start 未提交、管理器离线、未知跟踪、长时间存储阻塞或备份灾难恢复可保守少计更多，但不得多计。查询间不确定尾段本来未确认，不冒称精确真实游玩时长。

## 18. Exactly-once accounting 与事务

每个原生 Session 的已存在 durable 记录即为“该次创建已计次”证明；不另设一个可独立漂移的 counted 文件。OperationId 在 profile 的新原生 session 中全局唯一；同 ID 不同 binding 必须拒绝。

每次 command 携带 SessionId、当前 writer owner、expected revision / 新 revision、不可变 binding、累计目标 A、状态目标。进入 Phase 2 串行 transaction 后读取最新 entry/session：

1. 新建：确认正确 launch incarnation 与 binding，创建 session（初始 A=0），playCount 加一；同 ID 已存在且完全相符则转为幂等核验，不再 +1。首次创建和立即退出可合并一次事务。
2. 更新：仅当前 writer/合法 revision 能写。目标累计不能下降；低 revision replay no-op；相同 revision 必须与原 payload 一致，否则是冲突。高 revision 必须满足合法状态转移。terminal absorbing；迟到非终态/旧进程事件不得修改终态或重新创建已删除记录。
3. `N = floor(A / 10^7)`；`delta = N - persistedSession.seconds`，必须非负。**同一 candidate library.json** 内设置 accumulated=A、seconds=N、TotalPlaySeconds+=delta、revision、恢复 token / terminal reason / end UTC。terminalization 也走这一事务。
4. validate 全文、原子替换 main、收到 receipt 后发布内存 committed 状态。只有 pre-commit failure 可称未保存；post-replace warning 仍记作成功。不允许先写 total 后写 session 或把 token 放另一 authoritative 文件。

例：历史 total=1000，session 已 accounted=15，目标累计=30 秒，则只加 15；同目标重复一次 +0。Exit 再到 +0；handoff/recovery 不新建；crash 在替换前看到旧版本，后重试同差额；替换后看到新版本，重试差额为 0。恢复无需把旧 A 再累计进 total。代码不能只靠“事件仅收到一次”保证 exactly-once。

保存期间的用户取消在进入不可取消 Save 后不回滚；shutdown 必须 drain。原 metadata 的 generation 随普通编辑变化，不应造成丢失合法计时；5B session mutation 使用自己的 revision/writer，加上删除/替换的 launch incarnation 保护，从最新 entry 合并。新 writer 仅在持有唯一 lease 时可通过恢复事务接替旧 owner；旧 owner 的排队 command 必须被拒绝。

终态库合并必须保留 legacy historical surplus：合并两边 totals 后仅扣真正重复 session 已记账部分，count 仅扣真正重复的 invocation。新的 native 零秒 session 也已计次，重复时必须扣一次；旧 legacy 零秒不推断曾计次。native 同 ID/binding 冲突拒绝；同身份重复取较新合法累计/终态，不丢 native token/status（非终态合并已被第 15 节禁止）。legacy-legacy 继续既有合并规则；native-legacy 同 ID 只能在四字段兼容且可以证明来源一致时合并，否则明确冲突，不能猜测后降低总量。

## 19. Legacy compatibility

| Legacy behavior | Phase 5B behavior | compatibility mapping |
| --- | --- | --- |
| totalPlaySeconds / sessions.seconds 为整秒 | 保持单位；内部累积 100ns | 旧 total 作为 floor，仅追加，不从 history 重算 |
| 首次正秒记录才 +count，0 秒不保存 | 首次 durable owned session 就 +1，含 0 秒 | 仅新记录改变；既有 count 不重算 |
| spawn 后 randomUUID，初始只内存 | 用 operation GUID，立即创建事务 | 旧 ID 原样保留，不能重新生成 |
| history 为 id/startedAt/endedAt/seconds | 添加有版本的 native 语义扩展 | 缺 native 合法、LegacyUnknown、InstallationId=null |
| endedAt 是保存/结束计时时间且被 clamp | 真 exit 与 cutoff 分离 | 旧 endedAt 只映射 cutoff，真实 EndedAtUtc=null |
| duration 是 performance.now 累计，UTC 用 Date.now | 明确 awake clock；排除睡眠 | 旧累计秒数直接保留，不倒算或修旧历史 |
| active 只内存，导出 ActiveSession 无可靠 identity | durable token +严格同身份恢复 | 旧 active/running hint 不 attach；无 token 不恢复 |
| launcher exit 后暂停、以目录/PID候选续接 | 仅 5A verified handoff，同 Session 可桥接 | 不继承旧宽松进程猜测；旧历史不重新识别 |
| 一次全局 active / launching / finishing | 保持 5A 全局单 owned slot | 不因新 Domain 结构放宽并发 |
| finish 停止计时并保存，不能 resume | pause 可 resume；停止观察另行终结 | 新行为明确不 kill，旧记录不自动重开 |
| 有 active/launching 才 close-to-tray | 关窗始终 tray；显式 Exit 才结束管理器 | 第一版有意改变；托盘失败不隐藏 |
| before-quit 等 shutdown，错误取消退出 | 冻结/RecoveryPending/原子 drain | 保留失败可见性，新增跨管理器有限恢复 |
| Electron single-instance 唤起窗口 | AppInstance redirect + writer lease | activation 不修改 Session identity |

导入/导出不是恢复授权。以后 5B 的可移植导出必须在输出副本清除 token，把非终态转为 Incomplete / ImportedSnapshot，保持秒数、count、ID；不得修改源库。导入无论带何种 token 都不得启用恢复资格。普通读取旧库不回写；正式 profile 仍不接管。本设计不是 Phase 6 migration。

旧 Electron writer 不认识 native 元数据，可能丢掉它，且新字段本身不能阻止旧程序写。因此“新 reader 能读旧记录”不等于“可以让旧 Electron 与新 native 共用一个可写 profile”。5B 仅隔离数据；正式切换/回退的防旧 writer 丢字段门禁留在 Phase 6，不能宣称已完成双向写兼容。

## 20. Phase 5B 测试矩阵（待执行）

层次：U=纯 unit（fake clocks/facts）；A=Application 集成（真实 coordinator+5A service，fake platform）；P=持久化集成（真实 codec/transaction/隔离文件+故障注入）；W=Windows 合成 LaunchFixture / 真实 API；R=后续单独授权真实游戏验收。下表所有预期都必须变成断言，不以截图/日志口头替代；本 Gate 未实施或执行它们。

| ID / 层次 | 输入 / 故障 | 必须断言 |
| --- | --- | --- |
| S01 U/A/P | normal launch；Requested→Started→Attached | 一个 ID，创建事务 count+1，Attached 不改 start/count |
| S02 U/A/P/W | 创建后立即退出、无窗口、0 秒 | 成功 invocation count=1；只在真正 5A exit 条件成立时 Completed，否则 Limited/Incomplete |
| S03 A/P | StartedButTrackingLimited，identity=null；RetainLifetimeScope 抛错仅返回 ProcessCreated | 均有零秒记录、开始 UTC=null、count=1；不漏 fallback、不重启游戏 |
| S04 U/A/P | launch failure、创建前 cancel、duplicate start/status/result | 前两者 0 记录/0 count；成功重复只一条 |
| S05 A/P | Start 保存前 crash；Start 保存失败后 retry；随后 exit 已排队 | 首次窗口允许无记录；重试只 +1；不丢已确认 terminal fact、不补保存前 gap |
| H01 U/A/P/W | launcher→child、多步 handoff | ID/game/install/start 不变、count=1、区间并集无重叠；当前 token 更新 |
| H02 A/P | handoff during checkpoint、旧 parent exit 晚到、重复/乱序 child | 旧 revision 不覆盖新 child；旧 exit 不终结；不第二条 history |
| H03 U/A | bridge 6 秒内、超窗、期间 Limited/pause/suspend | 仅可证明且未冻结区间桥接；其余 gap 不补，session 不拆 |
| E01 U/A/P/W | authoritative exit 正常、延迟 6 秒发布、重复发布 | EndedAtUtc=真实 exit，observer delay 不计；terminal/accounting 幂等 |
| E02 U/A/P | exit timestamp null/冲突；旧 generation exit；scope 空但无 final fact；sampler先见gone、5A后发布exit | 无 Completed/假时间；状态不错误回退/解除占用；正常5A确认窗口内不得提前Incomplete吞掉最终事实 |
| T01 U | UTC 跳前、跳后、NTP、时区变化 | awake 和累计完全相同；真实 end<start 被保留，compat projection 合法 |
| T02 U/A | awake 10.4+0.7 秒、多段短暂停；sample 延迟/时钟倒退 | 累计 ticks 后取整=11；不逐段取整；延迟不外推，倒退冻结 |
| T03 U/P | exit 前最后一次样本较早，真实 UTC exit 已知 | 仅最后确认区间，不用 UTC 补尾；显示准确 exit +保守 duration |
| PW01 U/A/W | suspend 5 分钟、hibernate、同进程 resume | sleep=0；新 baseline；不结束/不新 count；fixture 不需真实游戏 |
| PW02 U/A/W | resume 时 gone、Unknown、PID 换代；重复 resume | 等5A最终事实或在观察结束后Incomplete；Unknown冻结；duplicate 不重置已计值、不 +count |
| PW03 U/A/W | missed/late suspend/resume，带抖动的双时钟采样 | awake 排除睡眠，跨断点 interval 丢弃；不得一次补整晚 |
| U01 U/A/P | pause→resume，pause→exit，pause→handoff | gap=0，resume 不计次，exit 可 Completed，handoff 保留 pause |
| U02 U/A/P | pause→app exit→recover；停止观察与停止计时 | pause 跨恢复保留；停止观察 Incomplete/清 token，不暗中 kill/释放占用 |
| L01 A/W | Limited 但 retained identity Running | 只计证明区间；无完整范围不正常 Completed |
| L02 U/A/P | Query Unknown→同 identity restored→gone | Unknown gap=0，同 Session 恢复，gone 无 final fact 则 Incomplete |
| L03 A/W | 5A Limited 后 monitor disposed，但 scope lease Active/Unknown | 新只读 sampler 所有权独立，reservation 不被会话终结错误释放 |
| M01 A/W | close window（active/idle）、托盘创建失败 | 成功则观察计时继续；失败窗口仍可达；不是 Exit |
| M02 A/P/W | explicit exit game running、checkpoint失败/commit warning、创建中退出 | no-kill；成功写 RecoveryPending，失败取消退出；post-commit warning 不假失败 |
| M03 A/P/W | Windows shutdown/logoff、5 秒预算到期、forced terminate | 不无限等、不 kill；重启仅信 old/new 完整 main，不宣称已 graceful |
| R01 A/P/W | crash 时仍 Running，重启同 PID+creation+image+install+boot | 同 ID/count、new baseline、gap=0、恢复后 CurrentProcessOnly |
| R02 U/A/P/W | process gone；相同 PID 不同 creation；不同 PID；image/cwd/folder/installation mismatch | 不 attach；last durable duration；Incomplete / reason；无退出 UTC |
| R03 A/P/W | OS reboot/Fast Startup、BootId 不可读/变化、DPAPI 失败、profile moved/copied | 全部不自动接管；同 PID/名称不能绕过资格核验 |
| R04 A/P | duplicate recovery attempt、恢复 commit 后 crash 再 restart | 同 attempt marker 一次；新 attempt 可再标记；count/旧 duration 永不重复加 |
| R05 A/P | stale RecoveryPending、无 token、backup fallback、多个非终态、导入带 token | Incomplete、不任选进程接管、不伪造退出现时；保留可读历史 |
| R06 A/W | recovered 当前进程退出；另一外部同名进程出现 | Incomplete / RecoveredProcessEnded；不接续外部、无 ExitConfirmed |
| AC01 U/A/P | 55 秒周期、54.9 秒 crash、60 秒时队列/IO阻塞 | 不每秒写；A-D<=60 秒，封顶冻结；重试不补冻结时间 |
| AC02 P | 原子替换前/后、backup/main各故障点、post-commit receipt 丢失 | main 始终 old/new 合法版本；重启 replay 只加差额，无 split count/total/token |
| AC03 U/P | accounted=15→目标30→重复30→低版本20→duplicate end | 总量仅 +15；旧 revision no-op；同 revision 不同 payload 冲突 |
| AC04 P | total=1000、history sum=100；新30秒；旧session重复导入/合并 | accounting=1030；historical surplus 保留；不从 sum 重算 |
| AC05 P | native零秒重复合并；legacy零秒；同ID不同binding；晚到事件 | native重复仅扣一次count；legacy不猜；冲突拒绝；终态不重开 |
| AC06 A/P | notes/preferred 编辑与 checkpoint 并发；删除→恢复同ID；关闭后排队 callback | 个人数据保留；绑定安装不变；旧 incarnation/writer 不能回写 |
| AC07 U/P | 数值溢出、366天、100000 sessions、32MiB、token超32KiB | 清晰容量错误并冻结；旧文件有效；不删历史、截断或降 total |
| X01 A/W | external AlreadyRunning；重启时发现同名外部进程 | session/count=0；Active/Unknown保留5A reservation；Inactive释放后仍正常预检 |
| G01 A/W | A active 时请求 B；同 Game 第二安装/第二份 | 第二次拒绝，无第二个 ProcessStarted/Session；不改变 5A 限制 |
| G02 A/P | 两个历史 Session 的独立 checkpoint replay | 分别按 ID 去重；旧 A 不能污染 B，存储模型不依赖 global timer identity |
| G03 A/W | “A exits while B remains” / “app exits with both running” | 同时两个 owned active 当前不可达；以 A owned+B external 验证 B不计时不kill。注入非法两个owned恢复项则全部冻结，不伪造并发通过 |
| I01 A/P/W | explicit installation；preferred中途改变；原安装恢复；并发另一安装 | 实际 ID 始终不变，恢复核对原安装，并发仍拒绝 |
| C01 P | 原 legacy 四字段、aggregate-only、缺所有新字段 | 合法读取，ID/count/total保留，LegacyUnknown，真实 end=null；读取不写 |
| C02 P | native round-trip，UTC end<start，null start/end，tick余数 | 所有 native事实无损，兼容projection合法，秒数/total一致 |
| C03 P | future version/enum/field；已声明v1但required缺失 | future只读且不退备份/丢字段；坏v1显式无效，不当legacy静默接纳 |
| C04 A/P | portable export/import、旧Electron读写边界 | 输出无恢复资格且源不改；不宣称旧writer能保留新metadata |
| SI01 A/P/W | 同profile同时启动、secondary arguments、转发失败 | 只有primary engine/writer/recovery；secondary只激活退出；count不变 |
| SI02 A/P/W | 不同隔离profile、writer lease争用、primary退出瞬间激活 | profile之间不串记录；同profile始终只有一个authoritative writer |
| RGA R | 后续授权 A直接启动/B launcher/C特殊cwd；短玩、pause、tray、exit、restart | 核对 build/profile/进程代际和历史差额；不以合成通过宣称真实全兼容 |
| RGB R | 可支持样本的sleep/hibernate、manager crash/gone、Limited | 无睡眠/离线补时、不误报exit、不kill；不可观察链明确Limited，保留证据边界 |

W 的重启/hibernate 等会影响主机，实施时必须在显式允许的受控 fixture/VM 内执行；本 5B.0 没有这种运行授权。R 需要后续真实游戏授权，不能沿用历史验收当作本轮已执行。

## 21. 实施拆分与各 Gate

| 阶段 | 纵向交付 | 独立 Gate（通过后暂停） |
| --- | --- | --- |
| 5B.1 Session Domain + Accounting Core | 状态机、可注入时间契约、native codec extension、串行 session mutation、exactly-once、legacy read/merge；纯及隔离持久化测试 | S/AC/C 的无OS部分；所有原Phase2与legacy oracle不退化。不可启用正式启动入口写时长。 |
| 5B.2 Process Fact Integration | coordinator、成功状态fallback、只读存活采样、handoff/exit/limited、保持单owned slot | S/H/E/L/G/I生产集成及Windows合成fixture；6秒observer延迟0额外计时，no-kill，5A专项回归。仅隔离harness启用。 |
| 5B.3 Suspend / Recovery | Windows awake clock、power discontinuity、DPAPI资格/boot guard、恢复与writer接替 | PW/R/AC crash矩阵；同代恢复Limited、gap=0、reboot失败关闭；没有sleep/recovery验收不能宣称可用。 |
| 5B.4 Tray / Single Instance / App Lifecycle | 窗口隐藏、托盘、primary转发、显式退出及OS终止drain | M/SI及真实发布入口fixture；second writer=0、Exit失败可见、game不kill。启用桌面集成前必须先具备primary+writer防线，不能等UI完成才补。 |
| 5B.5 UI / Acceptance / Real Lifecycle Gate | 展示计时/保存/暂停/Limited/Incomplete、历史真实时间、后续授权真实样本 | 对应U/A/P/W回归、最终包身份、RGA/RGB；报告合成/实机/真实游戏的不同范围。 |

每阶段保持小的可复审提交；不得在 5B.0 预先加入空 Session Engine、修改 5A contract、托盘骨架或测试代码。Phase 6 正式数据/旧writer迁移、SQLite、Phase 7视觉均不在这些默认授权内。

## 22. 仍需用户产品决定的问题

**None**。计次与关窗行为的有意差异、保守少计、恢复降为 Limited、全局单owned slot、失败时冻结/不接管等均已依据现有方向和技术边界作出明确选择，无未决产品或 5A 架构 blocker。

## Gate 与尚未验证范围

**READY FOR PHASE 5B IMPLEMENTATION** 表示语义闭合、可据此实现及测试；不表示实现已经完成。当前仅完成源码/测试阅读、官方API语义核对及文档检查。新的 clock、sleep排除、recovery、session写入、tray、single-instance、真实游戏验收均**尚未实现或验证**。正式 profile/凭据、真实游戏、发布包均未因本设计任务被操作。纯文档 checkpoint 后停止，等待下一阶段授权。
