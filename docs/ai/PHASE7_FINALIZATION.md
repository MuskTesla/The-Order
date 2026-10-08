# Phase 7 Finalization / Release Candidate Preparation

## 当前 Full Closure：工程与资源通过，RC GATE BLOCKED（2026-10-08）

接管 `codex/phase7-finalization`／完整HEAD a6ee5635e9fe5ed33d249b8568fdc9a4688a75d9，tracked clean；当前应用本地提交 **f62860cd7b42dd92ca55887655e1e7a605127374**。用户撤销全部工作预算，允许范围内修复和可恢复系统设置验证；旧30/45分钟／轮数限制不适用，单步超时、保护、冻结负载／门槛不变。旧91b98d6工程包、源码archive、报告和全部FAIL／无效记录保持不变。以下历史章节属于此前回合，其宿主设置禁止与停止安排已由本轮用户授权替代，不能误当当前结果。

本轮已证明fixed-content decode cache命中仍重复创建display WriteableBitmap／复制完整BGRA。弱decoded引用身份复用按pipeline隔离，因果对照 **3273→80** display surface、**1,882,187,776→49,506,560**复制bytes；不改变decode预算／尺寸、功能、虚拟化、过期结果或C1。对照未复现原FAIL，两臂均自然回落／通过；只证明重复分配压力，不虚称查清全部历史retaining owners。一次VirtualQueryEx分类和失败CDB摘要保留，未重新启动Shared阶梯／XAML池等历史调查。正常最终候选另按原门槛验收。

真实HC试验暴露不可用Fluent layer颜色的红色sentinel，已改阶段background／plate使用系统contrast palette。WinRT订阅0x80070490 NotificationWarning仍记录，getter known；通过实际Win32 settings/color/theme通知刷新主policy，在XAML默认处理之后执行、无新timer。真实HC／透明变化／动画实际状态、C1退休及恢复通过；窗口lifecycle测试覆盖新增event与dispose。

system-06 HC是在提交前dirty构建完成；hc-policy-source-binding.json以Portable PDB源码SHA256绑定该构建与最终Release的policy／stage／App／visual／accessibility五文件，10/10均匹配f628提交源码。版本标签不混称。后续透明／主题／文字实机检查直接使用clean f628候选。

| Phase7需求 | 最新实际结果与证据 |
|---|---|
| 独立包／运行时／许可 |新635文件x64 self-contained ZIP／源码／manifest全验，旧635全验不变；medium空Test profile中文／空格TEMP路径无SDK/helper/seed启动，runtime modules来自包，deploy-final-11 PASS。本机不代替真正干净Windows |
| 设置／版本兼容 |原optional material v1 companion、native-ui schema及原子备份／串行提交不变；普通Advanced意图重启仍Standard／无250ms诊断；完整missing/unknown/corrupt/future/write/cancel回归。哈希绑定14d8ede旧程序集实际读f628编辑的384合成条目和旧UI偏好，忽略新material；旧→新已有证据按未变store复用 |
| 标准权限／profile／单实例 |实际same-user medium child S-1-16-8192、Administrators deny-only；初始化、保存／重开、第二实例forward exit0／hash不变，controlled-final-07b与deploy-final-11 PASS。独立非管理员账户留隔离系统 |
| 升级／移除／重装／回退 |只移除owned程序目录不删资料／外部合成sentinel；换路径重装读Opaque；快照后新增修改默认拒绝、零写入；显式合成丢弃确认＋CAS＋同root/ProfileId/StoreId，15闭合文件精确恢复，deploy-final-11 PASS。不是Phase8真实快照恢复功能 |
| Standard资源／高级OFF |final-resource-08／09两轮、四组冷Standard／ON→OFF在clean f628正常候选上 **全部PASS**，原协议／分析器／64MiB不变，owner与pending0、无OFF绘制、hide/show、exit0、库hash不变。旧92.79/106.67FAIL及有限开发例外保留，不扩大到整个历史负载 |
| 主题／策略／无障碍／文字 |真实HC／动画／透明关闭和恢复、System Root.ActualTheme Light/Dark、TextScale1→1.44→1、长CJK客户607×693DIP／scale2、live XAML peers子集PASS；普通键盘／托盘／草稿历史实际证据按未改源码复用。新候选真实HWND／accent／100/150%DPI／物理键盘焦点和Narrator仍BLOCKED_TOOL，不标干净环境依赖 |
| 结束会话 |代码审查、30项Win32 lifecycle窗口测试及Session回归；owned HWND实际WM_QUERYENDSESSION／cancel后dirty保留且closing恢复，controlled-final-07b PASS。宿主真实关机／注销／重启未获本机授权，留独立环境 |
| 最终回归 |clean Debug／Release publish、90/90受影响与 **native Release1813/1813，0fail/skip**。含三项actual Electron合成AES-GCM/DPAPI/profile envelope跨实现测试；仅测试工具，不加入包 |
| 候选身份／设计 |新 `.artifacts/phase7-closure-20261008/frozen-rc/f62860c/win-x64`，source f628；OpticalMaterial.dll与91旧包SHA相同，Clear/Continuous配置／质量／位置、Standard默认及Advanced资格不改。launch-test.ps1／Start-Test.cmd独立隔离入口，旧包／提交保留可回退 |
| 宿主恢复／清理 |text100/DWORD、transparency1/DWORD、appTheme0/DWORD及存在性精确恢复；HC flags126／31色／animations=true恢复。**默认HC方案名空→高对比黑色仍未精确恢复**，不能报整个宿主恢复PASS；无本任务app／sampler／CDB／WPR／Narrator遗留 |

| 正常最终候选 |静置2−1 MiB|静置2−预热 MiB|重复段峰差MiB|结果|
|---|---:|---:|---:|---|
|08冷Standard|4.00|38.71|1.11|PASS|
|08Advanced→Standard|2.98|47.72|2.54|PASS|
|09冷Standard|−4.72|41.96|11.70|PASS|
|09Advanced→Standard|−0.50|54.14|3.73|PASS|

沿用原384条、24ID、1147×802DIP/scale2/Dark/Grid、8warmup且≥30s、两段各24往返／每轮≥3s、30s静置末15s中位、1Hz；三个Private差≤64MiB、handles≤64及owner硬条件不变。每窗15样本、最大gap≤1.002s、最低RAM15.05GiB／磁盘139.29GiB，保护持续实测；四组峰值≤386.88MiB。资源串行无其他suite重叠／截图循环／heavy诊断／forced GC。当前普通可见边界active、旧FAIL inactive，协议未固定inactive但运行差异如实记录。原32MiB代理FAIL、Shared解释、GA1和有限开发集成例外均不改写。

**客观平台阻断**：Computer Use node_repl导入前 `failed to write kernel assets: 系统找不到指定的路径。(os error3)`，reset与再初始化未恢复；native CUA disabled。只读排障确认runtime／模块／TEMP存在，未得到可安全修复的路径，不构造替代UIA／SendInput协议。真实HWND／accent／DPI／键盘／Narrator仍NOT PASS。HC默认scheme的初始API空串已保存；受支持SPI、documented选项与最小registry对照未恢复live值；后续试探恢复前值／类型，初始registry未记不能补造，禁止通过宿主重启／私有hook绕过。因此目前**不能报告 NON-ENVIRONMENT PHASE 7 REQUIREMENTS: PASS**。

**隔离Windows剩余**：真正无SDK／源码／Electron／helper启动及app-local runtime；独立普通账户首次初始化／保存、升级／移除／重装／程序与数据回退；真实宿主结束会话／取消／重启恢复。缺工具／还未做的本机验收不归入该清单。未进入Phase8，未访问Production／真实游戏，未替换／push／发布。

详细根因／前后对照／完整需求矩阵／raw索引／恢复步骤见 `.artifacts/phase7-closure-20261008/report.md` 与checkpoint.md。新程序ZIP SHA256 **750A2FC9692CA0DF60922DE4B078C5AC111000312A4210601A78FAD866B8C655**；源码ZIP **FD5A9E8F8D4E49B2105521CCCB0564F95C093FD434C09AEDA4744466F478FD46**；manifest **0C73861B56AA1EFCBAEDCBFE199AD85D778D752D52EE7A2E86C1932843CBCC2D**。新tag `codex/phase7-closure-engineering-frozen-20261008`指向f628应用；回退点91b98d6／a6ee563与旧包保留，不执行reset。文档HEAD由最终checkpoint-receipt绑定，与app source分开。

## 历史：此前工程冻结与首次资源FAIL（原证据保留）

## 范围与运行前验收表（2026-10-08）

本轮起点：`b1acff4`，应用冻结 `14d8ede`；独立分支 `codex/phase7-finalization`。保留已批准视觉、publication修复、C1和旧证据。不重复Phase7.2验收，不访问Production，不迁移Electron，不替换正式程序，不自动进入Phase8。

路线权威：Phase6原生Production初始化及6.2日用Gate已通过。Phase8只准备原生快照、版本兼容、受控真实日用、回退和正式发布验收。

| 项目 | 本轮目标 / 判据 |
|---|---|
| Portable x64 Release | self-contained运行时/WinAppSDK/VC运行时/资源/许可证齐全；非开发目录启动不依赖SDK/helper/seed。干净Windows证据独立必需 |
| 材质设置 | 独立版本化可选文件，旧native-ui schema不变；atomic commit、identity binding、未知/损坏/中断/重启可控；普通入口保存Advanced仍只生效Standard |
| Advanced OFF | 冷Standard不得加载光学DLL/创建renderer；受控ON→OFF专属纹理归零、创建/退休平衡；无图像pending复活；普通无250ms诊断 |
| 系统策略 | 保守读取失败降级；通知失败有变化感知替代；实际OS accent/HC/transparency/animations/DPI/text/Narrator证据与注入区分 |
| Upgrade / removal / rollback | 隔离原生profile、旧候选实际读写、新版本写入后旧版本读回；便携移除不删除资料；快照不静默覆盖；真实Production不访问 |
| 回归 | 先相关测试；代码稳定后最多一次完整native Release suite，保留全部首次失败 |
| Gate | 必需干净环境/真实系统证据缺失为RC BLOCKED，不用有限开发集成例外豁免发行 |

## 运行前冻结的资源短验协议

只记录本轮候选身份与合成Profile；累计资源负载硬预算30分钟。目标两组各约4分钟（固定预热30秒、24次同节奏Library/Details、静置30秒、第二次24、静置30秒）；同384条目、相同窗口/DPI/theme。组A始终Standard；组B受控Advanced预热后切Standard，后续操作完全相同。不截图循环、不GPU readback、不forcedGC、不清缓存。

硬正确性：OFF的scene/draw/frost增量=0，专属renderer/texture=0、retirementPending=false；图片/导航/详情pending边界0；句柄无连续单向累积；功能完成/无错误/正常退出；库hash不变。Stage共享fallback封面仍是Standard需要的资源，不要求全部图像/WinUI资源为0。

保护停止：Private>1.5GiB、系统可用内存<3GiB、响应停止>15秒、设备错误/crash。不是验收通过门槛。容量复用判断采用两个重复段峰值与各30秒静置末15秒中位数，避免单点GC抖动。正式资源合格需两组终点不呈有实际影响的持续增长：相对预热/第一静置增量<=64MiB且第二段峰值增量<=64MiB，handles静置增量<=64。理由：控制2段同内容生命周期的额外保留，64MiB是既有整个decode cache预算而非把偶然GC低点当零；超过即FAIL/需要明确类别，不自动推论泄漏。单组或丢失边界INCONCLUSIVE；共享增长不被ON−OFF抵消。阈值与负载不随结果修改；旧32MiB代理FAIL不改写。

## 已知环境缺口

初始未发现可用WindowsSandbox/虚拟机入口；用户确认没有现成隔离Windows环境。不得启用宿主组件或修改个性化/辅助功能。继续独立工程及本机隔离Profile验证；干净机启动、实际系统矩阵、Narrator及VM结束会话不能被本机或Test注入替代。本轮预期保留这些RC阻断，除非后续出现真实可用环境。

## 最终工程结果：RC GATE BLOCKED

应用源码 `91b98d6d6b3740434ba5c28b4c0630adeb85cbab`，本地tag `codex/phase7-rc-engineering-frozen-20261008`。最终635文件包由干净源码重新publish；其源码、zip、manifest均有SHA256，完整解压后635/635重验一致。源代码归档同步提供；没有修改原冻结557文件包，原包557/557哈希重验一致。最终文档提交在应用提交之后，不代表另一个应用版本。

| 剩余项 | 结果与边界 |
|---|---|
| 独立部署 | 完成self-contained目录式x64包、app-local VC/.NET/WinUI、许可/元数据、完整性检查、WindowsPowerShell5.1入口、有界日志。本机普通权限在中文/空格非开发目录实际运行；runtime modules来自包。干净Windows无SDK验证缺失 |
| 设置 | 完成MaterialPreference/MaterialPreferencesStore与UI异步保存；optional v1 companion绑定ProfileId/StoreId，旧native-ui v1不变；missing/default、4模式重启、未知/损坏/未来schema、失败写入、取消与排队释放回归。普通入口Advanced意图重启后仍Standard、无光学DLL |
| OFF隔离 | 两组各8预热+48正式Library↔Details，2段各24/相同3秒节奏和30秒静置；OFF scene/draw/frost不变，专属资源0、创建/退休平衡、image pending/导航/详情/过期封面0；隐藏/恢复完成、exit0、库hash不变。保留量判据FAIL |
| 主题/无障碍 | 当前宿主Windows11 26300、实际200% XamlRoot scale。新包Light/List、Dark空状态、中文名称编辑/保存/读回实机证据；HC订阅仍报告0x80070490，仅通知失败，直接状态读取known、已有Session owner refresh轮询仍有效，没有新增轮询。真实OS变更/100%及150%/文字缩放/Narrator/VM注销未完成，不用注入替代 |
| Upgrade/rollback | 旧冻结native程序集哈希匹配，实际旧候选读取新companion后可浏览384合成条目；旧版写Light/List/name，新UI实际读回；新UI保存条目后旧程序集解码读回。便携移除程序不改94个资料文件和外部合成游戏占位；换安装路径重装读回保存条目。闭合Test profile全文件快照核验94项、同物理根恢复演练成功，不声称真实用户资料恢复 |
| 回归 | Debug/Release publish PASS；349/349相关；C1/native光学fixture PASS。完整native仅首跑一次：1807PASS/3FAIL，全部为缺Electron文件的环境失败；复用已安装测试runtime仅补3项，3/3PASS。没有删/跳过/改弱测试。最终干净提交重build与普通启动另验；源代码哈希与测试前工作树一致，二进制元数据/哈希已重新绑定 |

### RC资源协议结果（MiB）

运行前协议独立副本 `.artifacts/phase7-finalization/frozen-resource-protocol.md`；SHA256 `9D485CC0CE659F4547A4E293417BE41622FBCF11BA369075480F569B929FD0CD`，与驱动run identity完全一致。没有根据结果调整64MiB。峰值是对应操作段外部1Hz采样最大值；静置为每30秒静置末15秒Private中位数（冷Standard两窗14/15样本，Advanced→Standard两窗15/14样本）。预热值是冻结驱动的warmup边界单点，不冒称中位数。

| 组 | 预热Private | 段1峰值 | 静置1中位 | 段2峰值 | 静置2中位 | 峰值2−1 | 静置2−1 | 静置2−预热 | 结果 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---|
| 冷Standard |386.49|572.23|440.61|606.39|533.40|34.17|92.79|146.91|FAIL：两项保留量>64 |
| Advanced→Standard |463.69|588.29|397.61|578.02|504.27|−10.27|106.67|40.58|FAIL：静置差>64 |

两组均1147×802DIP、XamlRoot scale2、Dark/Grid/Library顶端、相同seed与24个固定GameId，采样最大间隔1.045秒；正常边界可见但inactive，两组一致，未采集遮挡图或GPU物理分配，不能推断完整的前台硬件场景。handles静置中位1272→1259/1247→1245，无累积。GC committed静置84.21→85.98/80.10→81.85MiB，最近GC heap74.25→65.67/67.27→65.54MiB；不与Private窗口统计强行拼成精确原生账目。没有证据把92.79/106.67MiB归因于已释放的C1 owner，亦不能凭owner0宣告整体资源PASS。共有增长不能被ON−OFF抵消；保留为未闭合的RC保留量风险，不宣称应用泄漏或修复。

观测限制：保护停止线中的系统可用内存未接入连续sampler（只实测Private与命令响应停止）；事后宿主13GiB空闲不能补证明整段RAM保护。没有ETW/GPU Committed/AllocationBase/原生分配栈采集，不能定位共同Private增长实际owner。协议两组完成总504.793秒；首轮约128秒被主动打断为INTERRUPTED（与完整suite合成窗口重叠），不能当FAIL/PASS；原始驱动错误保留，sampler实际有输出，早先“没有记录”的观察已在补充说明纠正。连同完整suite3分31秒全额保守计入及数秒光学fixture，预算少于15分钟；没有追加资源长测。

最后额外WindowsPowerShell5.1启动成功；Computer Use随后两次list_windows超时，应用Responding=true。这个额外退出交互记INTERRUPTED；核验本任务Test进程代次、无待写tmp及已提交文件后关闭/终止，库/设置哈希未变。它不覆盖此前正常退出证据，也不证明UI卡死。所有本任务进程已结束，未创建ETW会话。最终冻结普通包已在此之前完成两次真实菜单退出与保存重启；观察器未保留其数值exit code，不能补写0。资源两组exit0是独立证据。

低扰动不等于零诊断：`MainWindow.Visual.cs` 的命令回执仍调用 `OnDiagnostics`，并将 runtime JSON 写入应用进程内的 `RuntimeState.Text`（`MainWindow.xaml.cs`）；发生在有限命令完成边界，不是250ms轮询。普通入口未启用该Test命令路径。没有对象持有或因果对照证据，不能把Private增长归因于这段文字更新，也不能外推本次fixture数据为普通路径的精确资源表现。保留这一观测差异。

### RC Gate分类与最小剩余工作

**A — RC阻断**：①冷Standard和ON→OFF共同保留量FAIL，实际超过运行前风险门槛，已有数据不足以闭合容量/原生保留类别，不扩大旧开发例外；下一次只针对相同段/静置边界做一次有限的原生committed区域分类，判断增加的是可复用容量还是活分配，保持本次FAIL。②用户确认无干净Windows环境；最小剩余是在专用VM/账户核验无SDK启动、原生升级、真实accent/HC/transparency/motion/100-150-200%DPI/文字/Narrator及可恢复注销。不能以开发机成功替代，也不能宣布Phase7Complete。

**B — 可接受的现有限制**：Advanced仍仅显式受控Test，两个已认可位置/稳定作品背景，Standard默认；不能用于正式启用/发布。app-local运行时需随未来包维护；optional材质设置不被旧版UI显示、不会自动成为旧版第五模式；回退旧版仍使用其会话默认。品牌/游戏图片权利不随AGPL转让，许可已保留，工程包不授予公开分发。NotificationWarning本身不是已证实状态读取缺陷，实际OS动态验证仍属于A。

**C — 后续改进**：在A闭合以后扩大硬件、长文本和辅助技术组合覆盖；非关键视觉精修不在本轮实施。旧GA1和有限开发集成例外不改写，不因本轮FAIL撤销其既有适用范围。

没有merge/push/Production替换；原主、视觉、Claude、审查和集成工作区分支/HEAD/状态及已记录文件hash保留。视觉工作区首次比较的false仅为collapsed目录与--untracked-files=all展开格式差异，按原格式重比完全一致；原候选首次检查误用path字段亦已纠正，最终557/557一致。完整原始记录/错误不被擦除。

## Phase8交接准备（仅准备，未授权执行）

Phase6.2已通过原生日用Gate；**不做Electron迁移**。先闭合上面A，再获用户明确受控真实资料授权：关闭原生实例，核验当前manifest/writer/应用版本、独占lease已释放，复制整个原生资料根（library、native-ui/material、operation/recovery及其backup、app-managed图片、加密凭据等）到受保护快照，保存逐文件hash；不解密、输出或带入源码。日志与含私密字段的snapshot只存用户授权位置。

程序回退：保留旧程序完整目录与其哈希，关闭新程序后更换程序目录即可，先读现有资料；本轮v1 optional companion和实际修改的library/native-ui由14d8ede旧原生代码成功读回，但不授权以后未知schema/writer降级。资料恢复：只有显式同意丢弃快照后修改才恢复，目标必须是原绑定物理根，禁止删根重建、跨ProfileId搬运或静默覆盖；本轮94项同根合成恢复是范围有限的演练。程序回退与资料恢复不得混同。

真实日用试用只在专用授权范围，先Standard、不启动未经授权游戏；比较已承诺保存、重启、锁定、托盘退出、Session恢复与错误提示。发现读只/identity不符、提交警告、设置损坏、退出丢写入、资源保护越线或能力降级无法解释即停止保存现场；不reset/重迁移/自动恢复。正式替换、切换Production、高级默认启用、公开分发与推送分别需要明确授权。当前RC Gate仍BLOCKED，本轮到此暂停。
