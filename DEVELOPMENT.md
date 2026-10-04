# 开发与维护

## WinUI Phase 3B.1：Filesystem Boundary Remediation（整改自检完成）

起点 `e75663c42fff9b0eea194cbca7d2706cbae45412`，保持 `codex/winui-phase3`。Phase 3B 独立 Gate 判为 NO-GO；本节 supersede 下方历史阶段关于扫描范围、不可用历史 identity 和候选连续性已经可靠的判断。只修复三项 filesystem MAJOR、深度提示和过期导航；没有 Phase 4、在线 API、识别、启动、计时、SQLite、Liquid Glass 或正式迁移。

### 授权根与枚举边界

- `BoundedScanner` 首次取得的 root observation 始终不变：stored/display/final path、volume + 128-bit file ID、目录属性、reparse tag 和 lexical reparse components。出队、枚举后及结束前确认根仍是该观察；根不可用、替换或重定向使整次扫描 Failed，UI 不接受本次候选。
- 队列保存子目录 observation，出队必须仍与原 observation 一致。范围判断结合目录属性、相同卷、final path 组件边界、根当前 identity 和 reparse 链，不只靠字符串前缀。支持显式选择 root alias，但根下不跟随额外子级 reparse，即使指回 root 内。普通目录换成 junction/symlink、另一个普通目录或删除时跳过并提示。
- Windows adapter 枚举时对 lexical/final 两条祖先链持有短期 no-follow 句柄、限制 write/delete sharing；持有后重验 root/目录，再进入顶层枚举。句柄随枚举器退出、异常、取消释放。占用或无法安全枚举时跳过并提示，可重新检查；不是永久文件系统锁，也不承诺任意第三方 filter 的绝对 sandbox。
- 预算仍为深度6、目录5000、条目50000、候选2000。`ScanResult.Truncation=DepthLimit` / `IsPartial` 表示因深度未入队的普通子目录；保留已找到候选，零候选也说明深度限制。发现 exe 后不再递归该游戏目录的既有策略保留。

### 重复性、连续性与导航

- `DuplicateStatus` 区分 NotChecked、ConfirmedUnique、ConfirmedDuplicate、Indeterminate。候选 identity 不可用仍拒绝；历史安装无法比较时，同规范路径保守判重复，不同路径进入 `PreflightStatus.DuplicateIndeterminate`，携带 `Unresolved` 路径/状态。核对页解释原因，不能全选为可导入；即使手动勾选，事务仍拒绝。
- 这是比旧 Electron 更保守的 intentional strengthening：不同字符串不能证明不同目标。不可比较的历史关系可能阻止新关系写入，但记录不删除。本轮无人工强行放行。恢复历史路径/权限后使用“重新检查当前目录”重新判定，或到安装管理明确处理关系。
- `PreparedInstallation.ReviewedCandidate` 保留原核对 observation。预处理必须与它一致；事务处理及 final precommit 先重验原候选，再检查 prepared folder/exe/working directory、重复与范围。变化返回结构化 `ClientOutcome.CandidateStale`，要求重新扫描/核对。同卷 rename 即使保留 ID，原路径失效或 final path 改变也不能自动接受；99 stable + 1 stale 整批零写入。一次串行原子事务保留，最后重验至持久化之间仍有窄 TOCTOU 窗口。
- `InstallationNavigation` 为异步打开安装页设置 generation。其他导航及停止使旧意图失效；完成后只有当前意图能导航，连续请求最新者生效。既有 draft/token、mutation drain、已保存但刷新失败的语义保留。

### 验证与证据范围

新增 **32 项**：`FilesystemBoundaryTests` 27、`FilesPresentationTests` 新增5。前者在真实临时合成目录上运行 Windows identity/枚举器，注入确定性的变更时机；导入经过生产 `OfflineFilesClient` 和 `TransactionalLibrary`，计数 persistence 证明零写入/一次写入，不只是 helper 判定。部分不可用状态为受控 identity 故障注入，不冒充全部真实 OS 故障。

| Gate | 实际验证 |
| --- | --- |
| M1 | 排队目录换成库外 junction/symlink、库内 junction、普通新目录、删除或环路，不枚举新目标，库外 marker 不进入结果；根替换/重定向停止。Windows 枚举边界直接拒绝 observation 后替换；显式 root alias 正常扫描。 |
| M2 | 同路径判重复；AccessDenied/NotFound/IdentityUnavailable/OtherIOError 注入判 indeterminate；真实 junction 因目标 rename 断链、同 ID 新路径仍可用时判 indeterminate，恢复后 Retry 判 duplicate；无关 missing 不假装 unique，批次零写入并保留历史。 |
| M3 | 首次重验后、exe 检查前真实换目录/junction/删除/rename，CandidateStale 且零 persistence 调用；100项在 final precommit 换最后一项，整批零写入；正常 scan→import 一次提交。 |
| Depth | 真目录 no-deeper、deeper、另有有效候选三个分支；不进入更深目录，partial 原因与候选保留正确。FilesViewModel 显示 partial 提示，Retry 复用 root。 |
| Navigation | 生产 coordinator + FilesViewModel + WorkspaceViewModel 的受控异步集成回归：Library、Settings、仍当前、两次请求；旧结果不夺回页面。不是新的桌面点击/截图证据。 |

锁定 restore；Debug/Release build 各 **0 warnings / 0 errors**；full C# 各 **472 passed / 0 failed / 0 skipped**。保留102 differential、14 fault injection、13 filesystem/cover differential。Artifact **18/0/0**；相同 Electron relevant 集合 **183/0/0**；Electron build/desktop-import 通过（37新增、重复0/37、个人资料/重启保留、文件未删、renderer errors0）。Release publish 到 `.artifacts/native-phase3b1/publish/win-x64` 成功。

本轮 isolated filesystem smoke 是上述自动化 Win32 lab；没有使用真实游戏目录或正式资料。Navigation 为生产 Presentation 集成 smoke；没有新增 WinUI 桌面截图、重跑10k图片baseline，也不声称最终发布包重新通过整套桌面验收。旧38组Phase3B图保留历史用途，不证明本轮竞态整改。`.artifacts/native-phase3b1/` 保存 ignored 日志/TRX/发布文件/验证摘要。

图片架构、decoder、64MiB/128MiB cache与source transport未改，10k unique cover压力测试留到Phase7。UNC/非NTFS/ReFS/generic reparse没有新增真实fixture，仍为未验证/不支持边界；套件0 skipped不代表这些环境通过。

### 正式资料与降级 Gate

`workingDirectory` 是 native optional 字段，旧 Electron rewrite 会丢弃。**最迟 Phase 6，且先于首次允许 WinUI 写正式用户库**，必须建立独立 authoritative migrated store、版本/写入门禁或其他可执行机制，阻止旧 Electron 无提示回写丢字段；备份提醒或禁止同时打开不够。有损 backward export 必须明确损失。本轮不实施迁移。

五项整改与规定自动化验证自检通过：**PASS FOR PHASE 4（整改自检）**。建立 Phase3B.1 checkpoint 后暂停，不进入 Phase4；后续独立 Gate 应按上述证据层级审查。

## WinUI Phase 3B：文件系统、安装与图片（历史本机验收；后续 Gate 与整改见上）

用户在 Phase 3A.1 Gate 通过后明确授权本阶段。分支仍为 `codex/winui-phase3`，起点 `55069ac4fddd1491e3122fd4c69a24487a3b2d86`；以下旧阶段的“不进入 Phase 3B”是当时的停点，不覆盖此次新授权。只使用带 `.the-order-synthetic` 标记的隔离 profile，没有接管正式资料、读取真实凭据、联网匹配、执行游戏、计时、SQLite、Liquid Glass 或删除 Electron。最终 checkpoint 后暂停，不自动进入 Phase 4。

### 架构与兼容性

| 层 | 本阶段职责 |
| --- | --- |
| Domain | Installation 保留路径和结构化参数；新增可选 WorkingDirectory。文件 identity 不写入便携书架。 |
| Application | `FilesystemContracts`、有界 `BoundedScanner`、安装/封面事务 token、`OfflineFilesClient`、`ImagePipeline` 与来源/解码/缓存接口。 |
| Infrastructure/Legacy | `NativeLibraryCodec` 先执行既有 legacy 校验，再保留安装路径原始拼写；不改变旧差分 oracle 的路径归一化实现。 |
| Platform.Windows | 句柄 identity/preflight、顶层目录枚举、WinRT WIC PNG/JPEG 解码/归一化、隔离缩略图磁盘缓存。 |
| Presentation | `FilesViewModel` 管理扫描、安装草稿、受跟踪 mutation、取消/关闭收尾；`ImageRequestSlot` 管理复用容器的请求身份。 |
| WinUI | Windows App SDK 标准 picker、候选核对/确认、安装管理、CoverPresenter；View 不自行访问 Win32、文件或解码原图。 |

七项目 Solution 与既有事务内核保留，无新增 NuGet 包、无依赖升级。扫描、identity、图片 decode/normalize、磁盘缓存都在后台执行；UI 只接收进度、展示轻量 projection，并把已限制尺寸的 BGRA 上传至 WriteableBitmap。所有安装/个人封面写入仍经过完整校验、串行事务、原子持久化和已有 Committed/CommittedWithWarning/刷新失败语义。

`StoredPath`、`DisplayPath`、`FinalPath`、`FilesystemIdentity(volume serial + 128-bit file ID)` 分开。通过 `CreateFileW` / `GetFileInformationByHandleEx(FileIdInfo)` / `GetFinalPathNameByHandleW` 得到实际对象身份；不使用 lowercase 字符串冒充 identity。目录需读取属性和列举权限，逐级检查重解析 tag；只接受普通路径、junction/mount point、symlink，未知 tag 和取不到可靠 identity 时拒绝新关系写入。

Preflight 区分 Valid、NotFound、AccessDenied、FileInsteadOfDirectory、UnsupportedReparseCase、DuplicateExistingInstall、SameTargetViaAlias、BrokenLink、IdentityUnavailable、OtherIOError，提供中文提示。重复检查覆盖已有同游戏/其他游戏安装及同批候选；真实 identity 相同的别名为重复，已失效历史记录只对相同规范显示路径作保守重复判断。离线历史路径不会自动删除；可用别名、Missing、AccessDenied/Unknown 有独立状态。祖先/子目录重叠保守拒绝。

identity 只代表当前机器/卷上的观察：同卷 rename/move 通常保留 file ID，但原路径 snapshot 会失效；跨卷复制/move 产生新身份；删除重建、恢复、卷序号冲突或文件 ID 重用均不能视为永久身份。网络 share 还依赖服务器是否支持 FileIdInfo 和 final path；本机没有获授权 SMB fixture，未声称网络可靠性。不可用路径没有字符串降级写入通道。

PreparedInstallation 绑定原始值、目录/exe/工作目录 observation。事务生成 candidate 前及持久化前重新确认 identity、final path、范围和重复，拒绝检查后消失/替换的目标。没有对整个文件系统加事务锁，也不一直持有安装句柄：最后重验与写盘之间仍有窄 TOCTOU 窗口；未来真正使用路径时仍须重新验证。

### 扫描、导入与多安装

- BFS 显式队列；深度 **6**、目录 **5,000**、目录项 **50,000**、候选 **2,000**。目录项同时包括文件和子目录；队列本身也受目录预算约束。每目录/每项检查取消，进度至少每 50 ms 合并一次，读取提示最多 100 条。本阶段不读 metadata/说明文本，读取预算为 0。
- 沿用旧 utility exe 排除、补丁/备份和系统目录保护；子级 reparse 不递归，另用已访问目录 identity 防环。根目录发现 exe 即作为候选，不再递归其子目录。目录不可读/消失会跳过并明确告知部分未检查；全局失败、取消、真空候选分别展示。
- 选择目录 → 扫描 → 候选名称/路径/exe 线索/已有关系 → 手动选择 → 确认 → **一次事务**。一个 exe 自动选中，多个 exe 保留待用户选择；不做 metadata recognition。100 条批量导入测试实际只持久化一次。重复提交跳过已有关系，不增加安装；纯重复批次仍可更新导出时间/执行一次事务，不能拿整文件 hash 变化推断重复新增。
- 安装支持新增、编辑 folder/exe/working directory/args array、切换首选、移除关系；已有模型没有独立安装标签字段，本阶段没有虚构 rename 字段。exe 和工作目录必须在解析后的安装范围内；args 保留独立字符串、空格、引号与空参数，不拼 shell command。移除首选关系令 preferred 为 null，保留其余安装；永不删游戏文件。
- 安装编辑从 draft 经 preflight 提交，token 校验 epoch/game lifecycle/原安装集合及首选项，防乱序覆盖。提交后草稿退役；刷新失败仍表达“已经保存”，不重试 mutation。退出取消扫描/图片并等待已接受的 mutation；关闭过程中不抛出未处理 async void。

旧版 v1 JSON / Base64 JPEG 封面继续保留。`workingDirectory` 是 optional 扩展，空值不输出；原 Electron 不使用它，并会在重新保存时丢弃未知字段。回退测试前保留 native profile 副本；禁止两端同时打开同一 profile。原始路径拼写保留是相对 Electron canonicalization 的有意加强，legacy backup reader 的既有差分行为保持原样。更早限制队列预算、拒绝无 exe/伪造候选、真实 identity 去重、保守范围检查也是明确差异。

### 图片与缓存

- `IImageSourceProvider` 为未来 remote source 保留接口，当前只取书架中的 local personal JPEG，未下载远程图片。`CoverSources.Selected` 的本地/当前作品个人选择/默认封面及内容分级优先级已与 Electron 做纯行为对照，但远程 transport 尚未接入；没有本地图片的条目仍显示占位。
- 标准图片 picker 只选 PNG/JPEG，源文件最多 **20 MiB**，解码前检查宽高（每边至多 65,535）和总像素（至多 **4,000 万**），只接受单帧。请求目标最大边 1,600；卡片按实际尺寸×DPI向上取 64px 桶，详情最低目标 512。WIC 使用缩放 transform、EXIF orientation、sRGB；应用不先为整张原图分配 BGRA，但不宣称限制 WIC 内部所有临时分配。
- 个人封面归一化为白底 JPEG、去源 metadata，质量/尺寸有限次回退至旧格式 **128 KiB** 上限；唯一用户副本始终在 library.json。坏图/截断/不支持格式/超尺寸/缺失/锁定/取消不替换原封面；已存坏图显示占位，错误计数不记录图像内容。
- 默认 decode concurrency **2**（配置 clamp 1–8），LRU 原始像素缓存 **64 MiB**，按 BGRA 字节估算；派生磁盘缓存 **128 MiB**、最多枚举 4,097 个文件后拒绝继续写，按旧写入时间淘汰。key = 源内容 SHA-256 + 尺寸桶，像素文件有尺寸/长度/hash 校验；profile/cache 路径继续检查 reparse/hardlink。缓存删除不会改 personal cover。
- Load/Unloaded/SourceKey/尺寸变化推进请求 generation 并取消旧任务；完成时再核对 generation/key/loaded，A 的晚结果不能写入复用后的 B。退出统一取消并 drain pipeline。待处理任务没有独立全局队列上限，目前由虚拟化容器数量和取消收敛；后续调优应关注并发请求合并、重复坏图负缓存和 pending queue，不能把两个解码槽宣称为所有 task 的硬上限。

### Gate 与证据范围

实际完成锁定 restore，Debug/Release build（0 warnings/errors），两配置完整 C# **440/0/0**，保留原 102 differential / 14 fault injection。新增 61 项：FilesystemIdentity 11、OfflineFilesystem 17、FilesystemDifferential 13、ImagePipeline 10、FilesPresentation 10。FilesystemDifferential 的 13 项含 1 项源 hash 新鲜度、7 项封面、4 项扫描边界、1 项复合扫描/安装行为。`generate-filesystem-oracle.mjs` 调用现有 Electron 实现生成参考；重复生成 SHA-256 不变（`oracle-repeat.json`）。

Artifact tests **18/0/0**；Electron relevant **183/0/0**、build、desktop-import 均通过：37 新增、重复 0/37、个人字段/重启保留、文件未删、renderer errors 0。日志/TRX 在 `.artifacts/native-phase3b/`，不是提交的源码。正式 release 未替换。

| 项目 | 本机实际证据 |
| --- | --- |
| identity | trailing、dot/dotdot、slash/case、长路径、junction/断链、symlink、8.3、subst、hardlink 文件 ID、rename/替换、ACL拒绝均执行。`filesystem-capabilities.json` 明确 UNC 与未知 generic reparse fixture **skipped**，不是 passed；unknown-tag 拒绝只有代码检查。旧 lab 描述中的 pending 由此报告 supersede。 |
| scan | 实际普通目录、utility 排除、候选/重复；所有预算、取消正反回归。测试中的 unreadable/disappeared 和 symlink/junction loop 是受控 IScanFilesystem 故障/链接项注入；不冒充真实 OS 环路挂载试验。 |
| UI import | 标准 picker 选 synthetic root、扫描、取消、再次扫描、核对并一次导入 2 条；重复选 1 条返回新增 0/跳过 1，readback 比较 games 完全不变。 |
| UI installations | 第二安装、工作目录及含空格/引号参数写盘并重启；首选切换与参数编辑；临时移走仅自己创建的 synthetic folder 后显示“路径不可用，记录保留”，随后目录已恢复。移除关系/首选移除由事务测试验证，未另做桌面删除操作。 |
| UI image | 有效 PNG 变为个人 JPEG；坏 JPG 被拒绝且原图保留；发布版重启/磁盘缓存读取、Grid/List 正常；legacy-valid 但 WIC 不可解码的封面仅占位、Errors=2、书架 normal。 |
| close | 受控 scan-start gate 期间关闭，进程结束、library hash 不变；45 个图片请求在 source-read gate 挂起时关闭，发布进程 exit 0。此时 ActiveDecodes=0，证明请求取消/drain，不冒充 WIC 内部阻塞中断。accepted mutation 收尾继续由全量状态/事务测试覆盖。 |
| authorized ordinary folder | 用户明确指定 `D:\entertainment\PC`，仅只读扫描得到 36 个候选；没有导入，隔离库仍为 2 个 synthetic entries，没有访问正式 profile/凭据、执行 exe 或改真实游戏文件。 |

当前可信 UI 场景保存在 `.artifacts/native-phase3b/ui/`。每次先捕获新 runtime，Assert requested == actual，再写截图/UI tree，最后写 manifest；增加 import stage/root/candidate/duplicate/installation/draft/local cover/image metrics 和嵌套属性匹配，继续校验 build、timestamp、count、mode/theme/state/readOnly、viewport 与文件 SHA-256。实际曾请求 ActiveDecodes=2 而 runtime=0，采集正确拒绝；观察确认暂停位于 source-read 后，使用 Outstanding=45 / ActiveDecodes=0 精确记录，没有伪造 decode 活动。

- `debug-import-review` / `debug-import-committed` / `debug-authorized-readonly-scan`：较早 Debug 构建上的范围限定 smoke。
- `debug-multi-install`：**发现旧草稿区残留的诊断图，不能当作成功关闭草稿的证据**。由 `fixed-edit-draft-closed` / `fixed-multi-install-restart` supersede。修复使用 HasDraft bool 可见性，避免 XAML null 函数绑定短路。
- `fixed-preferred-changed`、`fixed-missing-install`、`fixed-personal-cover`、`fixed-invalid-cover-preserves-personal`、`fixed-import-{scan-held,cancelled}`、`fixed-duplicate-{review,import}`、`fixed-close-scan-before`。
- `release-personal-cover-{restart,list}`、`release-invalid-cover-placeholder`、`release-images-held-before-close`。
- `images-{1000,5000,10000}-{grid,list}-{initial,scroll}`、`images-10000-grid-repeat-initial`、`images-10000-{grid,list}-{return,light}`。

旧 Phase 3A 无效截图和 Phase 3A.1 有效证据的分类不变。新增 `artifact-index.json` / `artifact-sanity.json` 逐文件验证大小、hash、ui.txt/runtime 与场景，`image-baseline.json` 保存数值；生成产物仍被 Git 忽略。预提交 assembly informational version 指向 `55069ac...`，不是新 checkpoint SHA；必须结合实际 exe 路径、MVID、全部 TheOrder DLL hashes 和源码清单判定构建身份，不能只凭标题或 MVID。

最终为 **38 screenshots / 38 manifests / 38 unique screenshot hashes**，其中 `debug-multi-install` 是上述已 supersede 的 bug 诊断，不计成功功能证据。索引 SHA-256：`BDE721B240F8FB492F6BBFB9A0E8A7EB1901750D8967541698C2981646B7A503`。最后只修正 SettingsPage 的 Phase 3B 名称，并加强超大图片 fixture 的 IHDR CRC/尺寸拒绝断言；最终两配置 build 仍各 0 warnings/errors、full tests 各440/0/0。`build-source-manifest-ui.json` / `build-source-manifest-final.json` 记录源码和发布 DLL/XBF/PRI hashes，`build-equivalence-final.json` 确认五个核心分层程序集字节一致；WinUI MVID 从 `317efdf4-fba3-49dc-8f0d-973d8e1d7aab` 变为 `c923ac56-5f7e-47f4-8970-c27611d5d95d`。最终 `publish-final` 实际重启显示持久化封面/安装、正确阶段名称，`final-published-{persistence,settings-phase3b}` 对应新路径和 MVID，PID2932 正常关闭 exit0。早期 Debug 图不作为最终 build 的逐字节证明。

### Phase 3B image baseline（不是 SLA）

发布版，固定实际 viewport **1042 × 455.5 DIP**。四张 40×60 的原创可解码 JPEG 重复分配给 1k/5k/10k 条目，library 约 1.91/9.59/19.19 MB；不是 10,000 张大原图的解码压力测试。另有实际 600×900 PNG 个人封面与目标尺寸/缓存淘汰/超尺寸单测。没有 FPS、峰值内存或长时间稳定性结论。

| 条目数 | Grid 首容器 ms | List 切换首容器 ms | Grid 首屏→滚动 | List 首屏→滚动 | 首屏 working set Grid/List MiB |
| --- | ---: | ---: | --- | --- | --- |
| 1,000 | 848.4 | 12.2 | 45→51 | 28→30 | 295.4 / 342.7 |
| 5,000 | 2359.1 | 11.2 | 45→46 | 28→30 | 373.9 / 427.1 |
| 10,000 | 3090.0 | 11.5 | 45→46 | 28→30 | 451.7 / 485.1 |

10k 首次首屏后用户手动关闭窗口，工具随后的滚动返回 foreground PID 错误；确认用户关闭后才重新启动。Grid 滚动、回滚、List 来自第二次进程（已有磁盘缓存），Grid 首容器 **3014.2 ms / 462.8 MiB**；没有将两进程说成同一次测量。1k/5k 均为同进程切换。所有快速滚动后的 offset 为 6825 DIP；10k 两视图回滚到 0 并再次检查对应条目颜色，没有可见 stale overwrite、几何跳动或永久加载，稳定后 Outstanding=0 / Errors=0。

| 条目/视图（滚动后累计） | requests | 实际 decode loads | memory hits / misses | memory hit ratio | max concurrent decode |
| --- | ---: | ---: | --- | ---: | ---: |
| 1k Grid | 166 | 6 | 160 / 6 | 96.4% | 2 |
| 1k List | 242 | 10 | 232 / 10 | 95.9% | 2 |
| 5k Grid | 151 | 4 | 146 / 5 | 96.7% | 2 |
| 5k List | 235 | 8 | 226 / 9 | 96.2% | 2 |
| 10k Grid（复测） | 156 | 0 | 151 / 5 | 96.8% | 0 |
| 10k List（复测） | 360 | 4 | 351 / 9 | 97.5% | 2 |

miss 是 memory miss，可能命中 disk，所以不等于 decode loads；首次 10k Grid decode=4、最高并发2，复测命中 disk 无需 decode。重复图片本身使命中率很高，不外推真实库。容器数量没有随 1k→10k 线性增长，但整库 JSON/Base64 加载和缓存索引仍有规模成本。Phase 3A.1 无图片的 1k/5k/10k Grid 为 1038.6/1670.1/2283.0 ms，working set 281.9/314.7/329.7 MiB；本次 5k/10k 时间和内存上升，不能宣称整体性能优于无图基线。旧 viewport 高度466.5，亦非完全相同条件。

### 复验入口与后续边界

```powershell
# 从仓库根目录；只向 ignored artifacts 写构建结果。
& native/scripts/native.ps1 -Action Restore
& native/scripts/native.ps1 -Action Build -Configuration Debug
& native/scripts/native.ps1 -Action Build -Configuration Release
& native/scripts/native.ps1 -Action Test -Configuration Debug
& native/scripts/native.ps1 -Action Test -Configuration Release
node --test native/scripts/capture-ui-evidence.test.mjs
node native/scripts/generate-filesystem-oracle.mjs
node native/scripts/generate-offline-library.mjs --out D:\Projects\The-Order\.artifacts\new-image-profile --count 1000 --cover-json D:\Projects\The-Order\.artifacts\native-phase3b\image-fixtures\cover-pool.json
```

最后一个命令要求输出目录尚不存在，cover-pool 是本轮测试生成的合成 JPEG，不是用户图片。`native.ps1` 的默认日志/Publish 仍沿用 native-phase1 路径，不能误称它会自动更新本轮发布目录。Phase 3B 实际发布通过 pinned dotnet `publish TheOrder.WinUI/TheOrder.WinUI.csproj -c Release --no-restore -o ../.artifacts/native-phase3b/publish-final/win-x64`；运行时必须设置 `THE_ORDER_NATIVE_DATA_DIR` 为标记目录，交互验收才开启 `THE_ORDER_NATIVE_DIAGNOSTICS=1`。`THE_ORDER_NATIVE_HOLD_IMAGES=1` 与“延迟扫描”仅诊断装配生效，退出/取消可解除等待。

剩余边界：UNC/非 NTFS/ReFS/generic filter 真实环境、真实大图片与大型 session 库、长期内存、干净机安装/签名/升级、突然断电未验收；没有正式资料迁移、在线图片下载/NextMoe、完整识别、游戏启动/计时或高级材质。Phase 4 应先独立定义范围和风险 Gate，不能把本阶段 checkpoint 当成正式数据/API/启动授权。

**本机 Phase 3B 验收完成，可提交独立 Gate Review。** 本地/可创建 alias、隔离事务与图片、必需 UI smoke、回归和发布已通过；UNC/generic reparse 的实际环境验证仍明确待补。建议先 review 本 checkpoint 再授权下一阶段，不把未测场景或未来功能写成 PASS。

## WinUI Phase 3A.1：状态整改与场景证据（验收完成）

本轮只在 `codex/winui-phase3` 修复 Phase 3A Gate 问题。基线 `29e5577c0e82384b66ce9d12fd6b06b996dc8b9c`，完成全部验证后才建立新 checkpoint；不进入 Phase 3B。

### 状态契约

- **M1**：`WorkspaceViewModel` 在详情请求开始捕获草稿修订号；SetDraft 与用户编辑均推进修订号。完成时同时核对请求 generation、取消、修订号、同条目 epoch/generation。Cancel 后仍允许继续输入，但旧结果不能覆盖新输入。离开 Details 释放已处理草稿；保存开始使旧详情请求失效。
- **M2**：偏好有 Uninitialized/Loading/Ready/LoadFailed。Loading 时只记录用户真正更改的 Theme/Layout/Sort 字段；读取后与完整旧偏好合并，再排队持久化。常用筛选在 Ready 前禁用。读取失败保留旧文件，后续主动外观/排序操作仅本次有效，本次运行永不重写偏好；修复文件后须重启。
- **M3**：`PersistenceResult` 是事务结果，`LastRefresh` 是刷新结果。已提交草稿退役，重新获取权威 projection；刷新失败仍首先说明已保存，并保留 commit warning。查询 helper 返回 structured outcome，不擅自覆盖提交提示，不重试 mutation。
- **M4**：`ApplyBatchAsync` 在后台解析前进入 tracked operation / IsSaving。解析、校验、Application 调用与结果处理属于同一个 task；关闭等待此 task。主动取消在 admission 前检查，durable 阶段仍遵守原事务契约。异常统一处理，View 入口另有最后一层保护。
- **Search**：Reload 取消并接管旧搜索，立即清理旧 busy 状态。打开新库前的新查询只更新意图；新库打开后的新查询可以取代 Reload 查询。旧结果和旧错误不能修改当前结果或 busy 状态。
- **Selection**：选择使用明确清空规则，不依赖控件保留对象引用。Grid/List、Sort/Filter/Search、Reload、任何 projection 替换及重新进入 Library 都清空；即使 ID 仍存在也不恢复。被隐藏后重现、删除、同 ID 新 lifecycle 恢复同样不继承旧选择/草稿。当前没有 delete UI；事务 epoch/generation 校验保持不变。

### 新证据采集

`THE_ORDER_NATIVE_DIAGNOSTICS=1` 显示合成验收控制和 `RuntimeEvidence`。只有此开关开启时才装配 WinUI 内部 `SyntheticScenario`：可以受控延迟详情、后台批量预处理、搜索，触发下一次重读或提交后查询故障。`THE_ORDER_NATIVE_HOLD_PREFERENCES=1` 可暂停首次偏好读取，界面“放行”解除；只在 diagnostics 模式生效。底层仍是原 `IsolatedLibraryFactory` / `IsolatedUiPreferencesStore` 和真实事务，不改库格式、隔离校验或只读能力。普通启动没有这些控制和 timer。

“延迟批量”必须在确认批量之前设置：现在暂停于后台 `IClientTextRules.SplitLabels` 入口，而非解析之后的 `BatchAsync`。诊断门只阻塞 Task.Run 工作线程，拒绝在 UI 线程等待，并有两分钟超时；放行后仍调用原文本规则。取消不会中断同步解析器，解析返回后、事务准入前观察取消。搜索门使用可取消异步等待。首次采集的 `m4-batch-held-*` 仅证明旧门处的事务准入等待；精确预处理证据为 `m4-preprocessing-*` / `m4-close-awaits-preprocessing`。

运行时记录来自实际 ViewModel、ActualTheme、Grid/List panel、进程与 assembly：itemCount/totalCount、viewMode、theme、readOnly、state、page、viewport/scrollOffset、realized/maxRealized、firstContainerMs、内存、事务/刷新结果、MVID 和 assembly informational version。首容器计时是 LibraryPage 构造到首 Grid 容器，List 为切换模式到首 List 容器；不是完整冷启动 SLA。

Computer Use 中先观察和执行单步 UI 操作，再调用仓库模块：

```javascript
const capture = await import('file:///D:/Projects/The-Order/native/scripts/capture-ui-evidence.mjs');
const result = await capture.captureEvidence({
  sky, window: targetWindow,
  scenarioId: 'published-10000-grid-scroll',
  outputDirectory: 'D:/Projects/The-Order/.artifacts/native-phase3a1/ui',
  expected: { itemCount: 10000, totalCount: 10000, viewMode: 'grid', theme: 'Dark', readOnly: false, state: 'normal', page: 'Library' }
});
```

模块内部自行捕获新 snapshot，不引用 REPL 外部 `state`。先验证场景身份、时间有效性、可视 viewport，再创建目录；任何不符直接失败且不生成成功 manifest。同名目录拒绝覆盖。manifest 最后写入，包含实际 runtime metadata、期望状态、窗口、输出文件 SHA-256。截图 hash 相同本身不判错，场景身份必须一致。

### 验证状态

证据写入 `.artifacts/native-phase3a1/`。当前锁定依赖 restore 成功；最终 Debug/Release solution build 各 0 warnings / 0 errors，full C# 各 **379 passed / 0 failed / 0 skipped**。原 102 differential / 14 fault-injection 保留全部通过，新增 `PresentationRemediationTests.cs` 30 项。采集反例 `node --test native/scripts/capture-ui-evidence.test.mjs` **10/0/0**。Electron 七个相关文件 **130/0/0**，build 成功，desktop import 37 新增、重复 0 新增/37 跳过、renderer errors 0。最终 Release publish 输出 `.artifacts/native-phase3a1/publish-checkpoint/win-x64`，旧采集批次 publish / publish-final 保留用于追溯；没有替换正式程序。最终发布 PID 33764 正常退出 exit 0，见 `checkpoint-publish-exit.json`。

`debug-final.trx` / `release-final.trx` 与 `*-build-final.log` 是最终回归；Electron 与 artifact 日志沿用本轮已通过且对应源码未再改变的记录。普通产品路径没有同步等待或 ViewModel 文件 IO；诊断 `SyntheticScenario.TextRules` 与单测 `GatedText` 有明确受限的后台解析等待，用于确定性时序，不能当作生产等待方式。

| Gate 项 | 实际 UI 证据与结果 |
| --- | --- |
| M1 | `m1-cancel-details-held` 确认 Cancel 后 detailsLoading=true；`m1-new-draft-survives` 放行后笔记仍为新输入、dirty=true、detailsLoading=false。 |
| M2 | `debug-preferences-held` Loading 时改 Light/List/name；`debug-preferences-merged-light-list` Ready、savedFilterCount=1。`preference-merge-readback.json` 核对实际文件中原 keep 筛选及最新三项 override。 |
| M3 | `m3-committed-refresh-failed` 为 Committed + Failed，提示“修改已经保存…不要重复提交”，Save 禁用。`m3-readback.json` 主库为新笔记、备份仍为旧笔记，未自动重复提交。CommittedWithWarning 的查询/详情刷新故障由 C# 确定性测试覆盖，未在桌面注入真实写后警告。 |
| M4 | `m4-preprocessing-held` / `m4-close-awaits-preprocessing` 均为 saving=true、batch-preprocessing-held；关闭等待，放行后退出并实际写入标签，`m4-preprocessing-readback.json` 核验。发布版主动取消后库 SHA-256 不变，`m4-cancel-readback.json` 核验；没有未处理 async void 导致的崩溃。诊断暂停点改进不改变事务实现。 |
| Search | `search-active-held` 确认 searching/query-held 与实际进度条；重读故障后 `search-active-reload-failed-ended` 为 error/reload-failed，进度结束，界面明确尚未加载书架。先前 `search-filter-reload-failed` 只是查询条件存在，不用它证明搜索并发。 |
| Selection | List→Grid 的前后截图为 selectedCount 1→0；`selection-journal.jsonl` 逐对记录 Grid→List、Search、Filter、Reload 的 1→0，既有 ID 继续存在也不保留选择。 |
| Artifact pipeline | `pipeline-negative-runtime.json`：故意请求 10k Grid Light normal、实际为 0/10k List Dark zero-results，采集拒绝且不创建场景目录。`artifact-sanity.json` 全量核验 manifest 与实际 ui.txt metadata、文件大小与 SHA-256；`artifact-index.json` 保存逐场景摘要及 manifest hash。 |
| 大库/主题/状态 | 1k/5k/10k Grid/List 首屏及快速滚动、Light/Dark、Normal/Read-only/Corrupt/Error/Empty/Zero-results 均有独立真实截图，详见下表及场景索引。 |

### Phase 3A.1 baseline（本机合成数据）

相同实际 viewport 1042 × 466.5 DIP；快速滚动后的 offset 均为 6989 DIP，截图中条目已改变。Grid 为 LibraryPage 构造到首次容器事件，List 为模式切换到首次容器事件；working set 是首屏采集时的进程值，包含诊断/桌面验收开销，不能解释成峰值或生产 SLA。基础批次每规模一次，10k 在补强诊断后的发布版重复一次。

| 实际库规模 | Grid 首容器 ms | List 首容器 ms | Grid 容器首屏→滚动 | List 容器首屏→滚动 | Grid/List 采集 working set MiB |
| --- | ---: | ---: | --- | --- | --- |
| 1,000 | 1038.6 | 19.3 | 45→51 | 29→31 | 281.9 / 299.1 |
| 5,000 | 1670.1 | 12.3 | 45→51 | 29→31 | 314.7 / 329.5 |
| 10,000 | 2283.0 | 18.0 | 45→51 | 29→31 | 329.7 / 377.8 |
| 10,000 复测 | 2282.6 | 11.8 | 45→51 | 29→31 | 331.7 / 376.6 |

两个视图均实际接受快速滚动并显示新条目，容器没有随 1k→10k 近线性增长；没有进行 FPS、帧时间、图片解码或长期内存 benchmark。封面仍为占位，本阶段不宣称真实图片系统性能。

场景目录（每项含 screenshot、ui.txt、manifest.json）：

- `release-{1000,5000,10000}-{grid,list}-{initial,scrolled}`：12 个基础规模/滚动场景。
- `final-release-10000-{grid,list}-{initial,scrolled}`：4 个 10k 复测场景。
- `release-readonly`（2 条、200k sessions、编辑禁用）、`release-corrupt-error`（错误而非空库）、`release-empty`、`release-10000-zero-results`。
- `debug-preferences-held`、`debug-preferences-merged-light-list`：Dark/Light 及启动偏好。
- `m1-*`、`m3-*`、`m4-*`、`search-*`、`selection-*`、`final-release-smoke-reopened`：状态与重启证据；完整 ID、规模、状态、容器数与 SHA-256 以 `artifact-index.json` 为准。
- `checkpoint-preprocessing-held`、`checkpoint-batch-cancelled`：最后取消文案修正的实际发布版复验；`checkpoint-cancel-readback.json` 确认文件未变。此前 `m4-preprocessing-cancelled` 截图保留为发现误导文案的历史证据，最终文案由此场景 supersede。

最终 sanity 为 **39 screenshots / 39 manifests / 39 unique screenshot hashes**，全部 manifest 与 ui.txt runtime 一致、文件大小及 SHA-256 一致，规模/主题/状态覆盖完整。`artifact-index.json` SHA-256：`3E91DBF0853F1A17C522591C70E681EBBBE25AA571F48CBD77E5C87448ABEBE9`。最终源码/程序集清单 SHA-256：`1FC45A10480B1775027BE74B53174284A88D3C38497F4E25ED555D8E26D97536`。

### 构建与证据可追溯性

场景 manifest 自报 assembly MVID、进程、时间、基线 informational version；未提交阶段的 informational version 仍指向 `29e5577...`，不能把它误称成最终提交 SHA。`build-source-manifest.json`、`build-source-manifest-diagnostic.json`、`build-source-manifest-final.json` 分别记录各批次的完整 native 源码哈希；最后的 checkpoint 与最终清单绑定。

MVID 只标识 WinUI 模块，不唯一标识全部依赖：最后只修改 Presentation 文案，WinUI MVID 仍为 `87a7e110-842f-4126-b958-4871597f8cf7`。因此还核对 manifest 的实际 executable 路径/PID、对应发布目录中各 TheOrder DLL 的 SHA-256，以及源文件 SHA-256 / Git blob；不能只凭 MVID 相同断言完整构建相同。

基础 Release MVID 为 `6e9c983a-1826-40ab-9011-2aa85f744ccb`；补强诊断后的 Release 为 `87a7e110-842f-4126-b958-4871597f8cf7`。中间只改变四个诊断相关文件，五个核心分层 DLL 字节一致，证据 `build-equivalence-diagnostic.json`。最后一次真实取消发现共用文案错误，按 detailId 区分批量/详情提示，并在既有取消测试中增加断言；最终构建清单另行记录该差异，不把先前 DLL 等价性扩大到最后文案修正。该修正不改变布局、搜索、选择、解析、事务或持久化，最终发布版再次实际确认取消文案和文件不变。

旧 `.artifacts/native-phase3a/` 的 31 PNG/31 txt 不作为有效 UI 证据，标记文件继续保留；本轮新 index/manifest 明确 supersede 旧截图与旧首容器表，不删除历史。旧 automated build/TRX/Electron 记录没有因截图错误而被作废。

重启 Codex 后原 `foreground window did not report a process id` 故障消失。验收中单次输入几何信息缺失和一次 monitor lookup 失败，均停止使用旧索引、重新选择窗口后仅重试一次即恢复；没有据此改产品业务逻辑。此次额外改动只包含精确合成验收门和实测暴露的批量取消提示。

**PASS FOR PHASE 3B**：本阶段要求的 M1–M4、Search lifecycle、Selection、artifact pipeline、大库、主题、只读/损坏/空/零结果均有对应证据。Phase 3A.1 checkpoint 后暂停，不将 Gate 结论当成 Phase 3B 授权。剩余边界：CommittedWithWarning + RefreshFailed 仅确定性回归；合成资料/单机诊断 baseline 不代表真实图片、大 session 库的长期运行、任意机器、干净机器安装/签名/升级或断电保证；不涉及正式数据、API、filesystem identity、游戏启动或 Liquid Glass。

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
- **Gate 更正：旧 UI 场景证据无效，原首屏和 realized-container 表撤回。** 31 张 PNG 和 31 份 UI 文本全部来自同一初始 1000 条 Dark Grid 快照，不能证明 List、Light、只读、损坏、空库、10k 或滚动场景。原始材料保留并由 `.artifacts/native-phase3a/INVALID-UI-EVIDENCE.txt` 明确标记，不用于新 Gate。
- 根因来自原采集代码：`saveState = async name => { ... state.accessibility ... state.screenshots ... }` 闭包捕获第一次 REPL cell 的 `state`；后续 cell 的 `var state = await ...` 是新绑定。两 cell 的最小复现输出 `current=second, captured=first`。不是依据 hash 猜测 UI 操作未生效。
- 原 build/TRX/Electron 日志及 Application 微基准可继续按各自范围查阅；旧进程内存文件没有完整场景身份，不作为新 UI baseline。

布局按 [Microsoft GridView/ListView 虚拟化指导](https://learn.microsoft.com/en-us/windows/apps/develop/performance/optimize-gridview-and-listview)：有限 viewport、保留默认 ItemsWrapGrid/ItemsStackPanel、无外层整页 ScrollViewer，inactive view 的 ItemsSource 为 null。窗口尺寸换算参考 [Microsoft 窗口管理](https://learn.microsoft.com/zh-cn/windows/apps/develop/ui/manage-app-windows)。

**Application 微基准**使用另一组更小的、无 metadata 的合成 entry，每项预热 1 次 + 测量 3 次；仅计后台查询、不计 120 ms 防抖、XAML 渲染或图片。它与真实 UI 表是不同指标。

| 条目 | 打开库 ms（单次） | 建索引 ms（单次） | 搜索 median ms | 评分排序 median ms | 组合筛选 median ms |
| --- | --- | --- | --- | --- | --- |
| 1,000 | 21.9 | 3.2 | 0.21 | 0.42 | 0.13 |
| 5,000 | 87.5 | 22.4 | 0.79 | 2.81 | 0.40 |
| 10,000 | 179.7 | 41.9 | 0.50 | 4.94 | 0.41 |

证据目录 `.artifacts/native-phase3a/`（不提交）：`restore.log`、`debug-build.log`、`release-build.log`、`debug-full.trx`、`release-full.trx`、`publish.log`、`electron-unit.log`、`electron-build.log`、`electron-import.log`、`oracle-repeat.json`；`ui-*.txt/png` 与 `published-*-{initial,scroll}.txt/png` 已作废，不支持对应场景；`published-memory-*.json` 只保留为缺少完整场景身份的历史进程记录；`ui-close-persistence.json`、`published-batch.json` 为合成文件回读。所有测试窗口已正常关闭。复验应从新生成目录开始，不把本次编辑过的 profile 当原始基准。

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
