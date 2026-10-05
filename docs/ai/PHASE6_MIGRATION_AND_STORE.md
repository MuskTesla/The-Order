# Phase 6.0 — Formal Migration / Authoritative Store Design Gate

日期：2026-10-05。结论：**READY FOR PHASE 6 IMPLEMENTATION**。

这是待实施规范，不是迁移工具或正式迁移验收。起始核对：`codex/winui-phase5`，HEAD `3d9a71ad920b5c20fe38772ef3e5c1698dd5010d`，工作区干净。Phase 5B 独立最终 Gate 为 **PASS FOR PHASE 6**；本机证据 `.artifacts/native-phase5b2-regate/final-audit.json` 与用户确认一致。下列设计不重新定义 Phase 2 transaction receipt、Phase 5A Process Fact 或 Phase 5B Session accounting。

本轮只读源码、文档、fixtures，运行合成存储测量和既有隔离测试，修改文档。**没有访问、复制或盘点正式 profile，没有读取或解密正式 credential，没有建立正式 native store。** 代码支持某字段不表示正式用户实例一定有该字段。实际用户实例清单、大小、密钥可用性及迁移效果均未知。

## 1. 直接决策

| 问题 | 本设计规定 |
| --- | --- |
| Native 与 Electron 是否物理分离 | 是；独立 profile 根、文件名称、格式 envelope，不共享可写文件、目录链接或硬链接。 |
| 切换后 Electron 能否写 authoritative data | 不能通过受支持入口定位或写 native store；不依赖旧 binary 理解新锁。 |
| Old Electron 如何保留 | 保留旧程序及 source/archive，直到后续 retirement；旧程序自身并不会自动变成只读，误开后写的是非权威 legacy 分支，绝不自动回灌。 |
| Old native / future schema | Phase 1–5 build 无 formal marker、路径及格式支持；Phase 6 起在任何 writable open / backup recovery / sidecar write 前严格检查格式、minimum writer、required features、identity。未知则只读或拒绝打开，不能降级重写。 |
| workingDirectory | 保留 missing/null/显式路径语义与原字符串；逐 InstallationId 核对；旧 Electron 不接触 authoritative 文件。 |
| 现在改 SQLite 吗 | **不改**。继续 JSON；保留 whole-library 原子事务，正式副本试迁移必须通过容量及延迟门槛。 |
| SQLite 重新评估条件 | 见第 11 节：容量接近上限、checkpoint/退出预算或内存压力失守；先独立 storage Gate，再正式 cutover。 |
| Credential | 默认 **Require User Re-entry**；不实现 Chromium 密钥提取。Library 可独立迁移成功。 |
| localStorage | 设计一次性 legacy-context 白名单 preference exporter，在授权的完整副本上运行；失败则逐项 unavailable/defaulted，保留原始副本。 |
| Personal Base64 cover | 本阶段不拆分；完整保留并核对内容 hash、尺寸及选择关系。 |
| 幂等 | SourceFingerprint + migration path/version + mapping options 决定 MigrationId；已有候选复用或重建，不向 active store 重放 append。 |
| 崩溃恢复 | 以 sealed snapshot、candidate manifest、validation digest、authority pointer 和 mutation latch 判断；journal 只是进度提示。 |
| Activation commit point | 同卷原子替换唯一 `authority.json` 为指向已验证 StoreId 的 Native 指针。 |
| 第一次 native mutation | 所有用户状态 writer 共用 coordinator；首次写之前先持久化不可自动清除的 mutation latch；成功数据 envelope 同时记录本次 mutation sequence。 |
| 已有新数据后 rollback | 只允许兼容 native binary、明确数据损失范围的 native backup restore、修复或显式 compatibility export；不自动回到迁移前 Electron backup。 |
| Electron 继续作为 writer | 不作为本产品 authoritative writer；其旧目录不能成为第二个可合并主库。 |

真正待用户决定的产品问题：**None**。性能门槛、路径、锁和 manifest 是工程决定；正式资料访问、helper 执行、凭据处理与 cutover 是后续授权，不是本轮未决产品选择。

## 2. 源码证据与现状

所有路径相对仓库根。这里的“当前”指上述源码 checkpoint，不是未来实现。

| 实际阅读的代码/fixture | 当前事实及设计影响 |
| --- | --- |
| `electron/library.ts`：installation、session、game、parseBackup、initialize、mutate、atomicWrite | `kun-gal-manager / version:1`；最多 10,000 games、32 MiB；逐记录校验、串行 whole-store commit、lastGood 写 `.bak`、temp flush/rename。投影会丢未知字段；installation 无 cwd，session 只有四字段。普通 import 会按 ID/work/path 去重跳过，不是正式迁移器。 |
| `src/shared/types.ts`；`electron/library.ts:recordSession` | 用户字段与 metadata 分离；整数 total/count 可大于已知 history；旧 Session 按 ID 增量保存，不能从 history 重算 aggregate。 |
| `electron/playtime.ts`；`electron/main.ts` | 旧 spawn cwd 为 executable 所在目录；单调时钟、15 秒保存；shutdown 先 matcher 再 tracker，失败阻止正常退出；持久化 endedAt 是保存/停止观察时间，不是可靠 game exit。`app.requestSingleInstanceLock` 是旧应用自己的入口保护，不是 native migration lease。 |
| `electron/settings.ts`；`electron/main.ts` 的 SettingsStore 注入 | `settings.json version:1`，encryptedKey 为 safeStorage Buffer 的 Base64；provider baseUrl 与 credential 绑定，换 origin 清旧 key。默认资料根 `%APPDATA%\KunGalManager`，可被 `KGM_DATA_DIR` 覆盖。 |
| `src/library-workspace.ts`；`src/saved-library-views.ts`；`src/App.vue`；`electron/window-appearance.ts` | localStorage 已找到两个应用 key：布局/排序/overview 与 saved filters；theme 来自 settings。当前搜索/临时筛选不是独立持久化 key；窗口按工作区创建，没有发现已实现的持久化窗口坐标。未发现其他应用 localStorage key，不把 Chromium 内部数据当业务 schema。 |
| `electron/metadata-matching.ts` | `metadata-matching.json`，最多 16 MiB；review、候选、旧证据/人工决定关联。library metadata 是已采用关系；待核对建议不是重新关联许可。 |
| `electron/local-cover.ts`；`src/shared/types.ts` | personal cover 为内嵌 JPEG dataUrl + width/height；本地照片导入后规范化，不靠原照片路径持续取图；remote coverChoice 与 workId 绑定。 |
| `native/TheOrder.Domain/Library.cs`；`Infrastructure/Legacy/{LegacyLibraryReader,LegacyValidation,LegacyMapping,LegacyLibraryCodec,NativeLibraryCodec,NativeSessionCodec}.cs` | Native 增加 cwd 与 native Session extension；NativeLibraryCodec 恢复原路径字符串，但仍使用 legacy v1 外壳、32 MiB/10k 限制和多次整库验证。通常未知字段仍可被 legacy projection 丢弃；native Session 的未来字段/枚举另有只读门禁，不能把它等同于全 store version gate。 |
| `native/TheOrder.Application/{Transactions,SessionTransactions,SessionEngine}.cs` | writer queue、runtime epoch/incarnation/request revision、先校验后 I/O 后发布；SessionId/revision/delta 保证 accounting；runtime epoch 不是 durable migration/profile identity。portable export 将非终态变为 Incomplete/ImportedSnapshot，去掉 recovery token。 |
| `native/TheOrder.Platform.Windows/{IsolatedLibrarySource,IsolatedLibraryPersistence}.cs` | 显式绝对目录 + synthetic marker；拒绝正式目录及父子目录，检查 reparse/hardlink，pin directory；exclusive writer lease；备份后同目录 temp/Flush(true)/MoveFileEx replace，post-replace warning 仍是 committed。当前只支持隔离 profile，没有 formal writer。 |
| `Platform.Windows/{IsolatedCredentialStore,IsolatedUiPreferencesStore,IsolatedRecognitionReviewStorage,IsolatedLaunchPositionStore,IsolatedThumbnailCache}.cs` | raw current-user DPAPI key；UI envelope v1；native recognition v1 并可读取 legacy review；DPAPI launch position envelope v1；缩略图是可丢弃派生缓存。这些各自的 queue 不是全 profile migration/first-mutation gate。 |
| `WinUI/Startup/Composition.cs`；`WinUI/Views/SettingsPage.xaml.cs`；`Application/{CoverSources,Images,PreferenceValidation}.cs` | Native 当前固定 NextMoe endpoint；UI 持久化 theme/layout/sort/saved filters。不能声称已覆盖 Electron 所有 general settings。图像默认双并发、64 MiB decoded cache、128 MiB disk cache、edge 1600。 |
| `Infrastructure/Recognition/RecognitionReviewCodec.cs`；`Tests/{LegacyCompatibilityTests,TransactionDifferentialTests,TransactionSafetyTests,TransactionRemediationTests,SessionPersistenceTests,SafeStorageExperimentTests}.cs`；`tests/local-cover-fixtures.ts` | legacy floor/缺项/损坏、差分、future Session/read-only、不因输出过大而回落旧 backup、fault injection、合成 safeStorage v10 实验均有源码。结构 JPEG fixture 不是可真实解码图像证据。 |

设计复用 [Phase 5B Session 语义](PHASE5B_SESSION_SEMANTICS.md) 的时间与历史契约。既有文档中的 synthetic-only 限制不因本 ADR 自动解除。

## 3. Schema inventory 与 Source → Target 对账

迁移不是调用普通 ImportAsync 后检查 added count。目标为同一用户库的新权威表示，不自动去重作品、合并安装、重新识别或重新下载资料。每个字段有规则 ID、来源 JSON path、目标 path、比较方式和结果。

| 类别 | 源码支持/可能存在 | Target mapping / verification | 失败分级 |
| --- | --- | --- | --- |
| Library | id、name/nameIsCustom、addedAt/lastPlayedAt、status；metadata 内 title/originalTitle/aliases/titleEvidence/description/dates | Entry 数与 ID 集合完全相同；稳定 ID、个人名、可选标志保留；日期表示可规范化但时刻相同。alias 是 metadata，不能拿来生成新 Entry。 | ID/count/用户值不同 FAIL |
| Installation | id/folderPath/executablePath/arguments/preferredInstallationId；native 或外部 v1 输入可有 workingDirectory | 每个游戏逐 ID 比较安装数、首选、参数数组顺序、路径字符串及有效 cwd 语义；无安装不造安装；迁移只读文件文本，不执行/解析真实游戏。 | 任一 binding/cwd 丢失 FAIL |
| Personal | categories/customTags、favorite、personalRating、notes、status、个人名称、封面显示 override | tags/categories 集合相等且不混入 API tags；null rating 不变成 0；notes/名称值保留；区分 override 缺失、null 继承与显式值。 | 用户值变化 FAIL |
| Play aggregate | totalPlaySeconds、playCount、lastPlayedAt | total/count 原值保留；total ≥ sum(known seconds)，保留差额 historical floor；不由 history 重算 count；无 history 合法。 | 降低 total、改 count FAIL |
| Legacy sessions | id/startedAt/endedAt/seconds | 保留 ID、数量、秒数与原时间投影；Native=null，显示 LegacyUnknown；真实 exit/InstallationId/process identity 未知。旧 endedAt 不升级成 ExitConfirmed。 | Semantic mapping，伪造身份/Completed 为 FAIL |
| Native sessions | native.version、binding、observed/start/end/cutoff、100ns、lifecycle/reason、pause、revision/owner/recovery/token | 已终态保留已知事实；跨 profile 的非终态走现有 portable snapshot 语义：Incomplete/ImportedSnapshot、null true end/token、保持 seconds/100ns/count/floor；记录变化清单。复制不授予旧进程所有权。 | 未知 version FAIL；token 失效 Semantic mapping |
| Media | metadata cover/banner/characters URLs、coverChoice(workId, cover)、localCover(dataUrl,width,height)、display mode | 保留 remote/provider linkage；personal JPEG decoded bytes SHA-256、尺寸相同，不重新有损编码；cache 可重建；不扫描原照片路径。 | personal 缺失/损坏 FAIL；网络不可达 WARN，不联网补图 |
| Metadata/manual link | provider ID（字符串）、references、ratings、developers、characters、fetchedAt、已采用 metadata 与个人名 | 保留数值字符串精度及 provider/source/work 关系；没有独立 manual-link boolean 时不伪造，采用现有已关联 metadata。 | 错关联/丢关系 FAIL |
| Review | legacy matching mode/reviews/reviewId/source fingerprint/options；native review v1 | 稳定 review identity；legacy/native 历史建议进入 NeedsRecheck/Stale，不恢复旧请求 token 或自动提交；详细原候选在 snapshot 保存，对投影缩减列 Semantic mapping。 | 解析损坏 FAIL；过期权限降级 WARN |
| General settings | baseUrl/includeNsfw/theme/appearanceStyle/readExecutableMetadata/nonPortraitCoverDisplay | 新 versioned general settings；保留 provider 和隐私/内容开关并在相关消费者生效。custom endpoint 不受支持时禁用联网并报 Unsupported，不能把旧 key 送默认站点。 | privacy 开关被放宽 FAIL；style 不适用 WARN |
| UI/localStorage | `the-order:library:v1`：layout/sort/overview；`the-order:saved-library-views:v1`：version=1/views，最多50 | theme/layout/sort/saved filters 映射现有 native 偏好；overview、Electron style 保留在 legacyPreferences 兼容字段，标记暂不应用；缺失与不可读取分别报告。 | 无法获取 WARN；未知版本不自动覆盖 |
| Credentials | settings encryptedKey、baseUrl；可能的 Chromium Local State key envelope | 只记录 presence/provider/state，目标 ReentryRequired 或 Absent；密文仅留受保护 snapshot，不装作 native key。 | 可单独 WARN；不阻断 library |
| Native-only companions | native-ui.json、native-recognition.json、native-online-key.dpapi、native-launch-positions.dpapi、native-thumbnails-v1 | 正式 Electron 输入不假定这些存在；明确 native source adapter 才读。新 profile launch confirmation/recovery 授权失效，历史与偏好保留；缓存不搬。 | binding 不匹配 FAIL |
| 非业务 Chromium/临时资料 | Local Storage、Local State、cache/log/temp 等 | snapshot 清单逐文件记录 included/excluded/reason；必要偏好与加密依赖保留原字节；临时缓存不当作事实；从不在报告打印 secret payload。 | 必需 companion 缺失 FAIL；可重建缓存 WARN |

报告每类有 **PASS / WARN / FAIL**，另有字段 disposition：**Exact preservation / Semantic mapping / Defaulted / Dropped intentionally / Needs user re-entry / Unsupported**。任何 intentionally dropped 必须列具体字段、数量、原因和原始 snapshot 位置；不允许笼统“兼容字段已忽略”。未知 legacy 字段先保存原始快照并报告 JSON path；无法证明不影响用户语义则 FAIL，不让现有 normalizer 静默删除。已知无语义缓存字段才可按固定规则 dropped。

验收比较直接针对 source snapshot 的值，不只比较经过同一个有损 codec 的前后输出。现有 trim/date/floor normalization 对非 canonical 源值可能有影响；日期可记录 semantic equality，notes/个人名称/ID 等不能仅以 trim 后相等通过。6.1 mapping/正式 codec 必须保留其合法值，否则该 candidate FAIL。源本身违反已知 numeric/floor/binding 规则时停止，不“修复”后当作无损。

### workingDirectory 的明确规则

已存在的 workingDirectory 原字符串和显式/缺省语义完全保留，不变大小写、不把相对路径猜成绝对路径。legacy 缺该字段时 target 保持未指定，launch 的 effective cwd 继续是 executable directory，与旧 `spawn(..., path.dirname(file))` 一致。路径校验不等于重新解析磁盘文件；不存在游戏、长路径、Unicode、空格本身不能触发丢弃安装。未来第一次 launch 仍走 Phase 5A preflight，迁移不签发 launch-position approval。

## 4. Authoritative profile 与 writer fencing

**待实施位置**：默认 `%LOCALAPPDATA%\TheOrder\NativeProfiles\<ProfileId>\`。理由：当前 native 没有正式默认根；legacy 明确在 roaming appData/KunGalManager；native identity/DPAPI/launch confirmation 是本机绑定。该位置不是新增自动迁移行为，也不是改变内部 AppID。自定义根必须显式、绝对、同机本地受支持卷，拒绝与 legacy/snapshot/staging 互为父子目录、junction/symlink/hardlink 别名；保留 Phase 2 物理身份检查。网络盘/跨卷 activation 不支持。

Profile container 持有 `authority.json`、`profile.lease`、独立 sealed source archive 和 `stores/<StoreId>/`。source archive 可以在另一个明确路径，永不作为 active writer 的 backup。试迁移的 archive/staging 放在单独授权的工作目录；**6.2 仅生成副本候选，不在默认正式 native 根创建资料**。切换授权后才部署/复验正式 store 并写 authority。

Store 包含不可变 `store-manifest.json`，native 专用 `state-library.native.json`、`state-settings.native.json`、`state-ui.native.json`、`state-reviews.native.json`、`state-credential.native.json` 及对应 last-good。不得使用旧 writer 的 `library.json/settings.json/native-ui.json` 等受识别名称作为正式状态文件。每个组件是 native envelope，内含 ProfileId/StoreId/Generation/ComponentVersion/MutationSequence/data；library data 保持现有 native JSON domain 表示。portable export 才生成 legacy-compatible library envelope，它不是工作 store。正式 codec 在 legacy/domain projection 之前递归检查完整已知 schema，不能只检查外层version而让未知内层字段被旧codec丢弃；payload仍守原32MiB上限，包装元信息另设有界额度，不靠放宽上限掩盖增长。

| Durable 字段 | 规定 |
| --- | --- |
| StoreFormatVersion | native formal v1；与 legacy envelope.version、native.session.version 分离；所有组件须为已支持组合。 |
| MinimumWriterVersion / RequiredFeatures | 单调 writer protocol 整数（v1=1），不能用可回退产品 SemVer 判权限；新增不可保留语义必须提高 protocol/feature gate。 |
| ProfileId / StoreId | profile 稳定 GUID、一次候选 store GUID；manifest、authority、所有组件匹配；只复制某个 JSON 不允许成为新空库。 |
| MigrationGeneration / MigrationVersion | 同一 profile 的迁移代数和迁移 edge 实现版本；相同 job retry 不加 generation。 |
| SourceFingerprint / MigrationId | 绑定封存来源、规则和候选；不能用 exportedAt 判断归属。 |
| WriterEpoch / Incarnation | durable lease-acquisition epoch 与本次 runtime nonce；组件写入携带 writer identity/revision。锁才是进程互斥，epoch 不替代锁。Session 自己的 WriterOwner/revision 契约不改变。 |
| AuthorityGeneration / Mode | 唯一外层指针：Legacy 或 Native(StoreId, manifest hash, MigrationId, activation receipt)。不得以目录存在或最后写入时间选主库。 |

获得 profile 根独占 OS 文件 lease 后才检查 authority/identity/version、建立 writer、启动 SessionEngine。普通 writer 与 migrator 用**同一把根 lease**，在 staging 独立工作也须占住迁移 job lease；长任务 UI 可取消，锁随 owner disposal/process exit 释放。每个 component commit 重新确认 held physical directory identity、active StoreId 和 writer epoch；失去授权立即拒绝。对非 library 的偏好、review、settings、credential 写入也用这个 coordinator，不能继续沿用独立 sidecar queue 绕过门禁。

未来格式、未知必需字段/枚举、minimum writer 不满足：能安全显示才只读，否则拒绝打开。**不因不认识 main 而 fallback 到较老 `.bak` 并可写**。备份必须同 identity/format/generation；恢复状态、数据损失范围和 recovery-token 失效是显式行为。损坏 manifest/缺组件/复制混配一律 FailClosed，绝不自动创建空库。

已经发布的 Phase 1–5 native build 不会自觉执行新 gate：它们要求 synthetic marker 且只查旧名称；formal 不含此 marker、也不放可重写的 legacy library。不能把 `THE_ORDER_NATIVE_DATA_DIR` 的 synthetic 通道改成偷偷接受正式 profile。即使误把 KGM_DATA_DIR 指向 native 根，旧 Electron 至多创建自己认识的旁路文件，不能替换 native 专用权威组件；formal opener 对此污染提示并拒绝猜测合并。此保护针对旧客户端入口/误操作，不声称能防御同 Windows 用户恶意改 ACL、删文件或手工替换二进制。

## 5. Snapshot、fingerprint、versioned migration 与幂等

支持的初始 migration edge 明确为 `LegacyElectronLibraryV1 + SettingsV1 + PreferenceExportV1(optional) → NativeFormalStoreV1`，MigrationVersion=1。另一个明确 edge 接受 Phase5B NativeSessionV1/已知 native companion 的授权副本并按 portable 规则去除运行权限。两者不能靠猜文件名混选。未来 NativeFormalV1→V2 是另一条经过 Gate 的边；无路径/未知输入版本返回 Unsupported，保留来源且不激活。

Source inventory manifest 记录固定白名单数据文件及必要 companion：相对路径、presence/absent、size、SHA-256、schema、用途、来源物理身份；包括 library/main/bak（标明哪个被选为 source）、settings、review、嵌入 cover 所在字节、可用的 preference export。原始浏览器存储/Local State 按获准 snapshot 范围作为不透明文件保留，不直接解释 LevelDB，不解密。非 inline 的受支持 local media 须列独立 hash/长度；当前 personal cover 已 inline，不推断存在外部素材。

强 fingerprint = 对排序后的文件清单及 schema/presence/mapping-options/version 做 canonical encoding 后 SHA-256。时间戳仅诊断。preference export 的业务 payload hash 参与 fingerprint，运行时间、随机 export nonce 不参与；一次封存 job 使用同一个 export 结果，不因 retry 改指纹。MigrationId = SHA-256(source fingerprint + source/target schema + migration version + semantic options)，不含随机执行时间。ProfileId/StoreId 在 durable job descriptor 中首次分配并在 retry 复用；丢失 job 后重建也不得替换已有 activation。

**一致性步骤**：legacy 正常退出且保存成功；不得强杀以获得“稳定源”。源目录物理身份固定，现存业务文件以拒绝写入/删除共享方式同时打开后读取；全清单前后枚举、hash、size、presence 比较，副本逐文件复验。遇到共享冲突、新增/缺失 companion、源 hash 改变或不稳定，最多重试三次后 SourceChanged/Busy，不能把两个时刻的文件拼成 candidate。snapshot 未 sealed 前没有有效 SourceFingerprint。

Sealed snapshot 是独立 immutable archive，不与 staging 共用可覆写文件；reader 仅访问 seal 清单。失败不能更新 archive。`library.json.bak` 只是上一份 library，绝不是含 settings/preferences/credential 的完整 source backup。main 损坏而 `.bak` 可用时必须单独报告 source selection 及可能损失，由后续流程明确接受，不能当作 main 正常迁移。

原 live legacy 在耗时 mapping 期间若被再次启动/修改，当前 job 仍只能处理已封存副本，但 **ReadyToActivate 必须失效**：readiness 与 activation 前重新获取一致清单并比对 fingerprint。原始 snapshot 继续保存，重新迁移建立不同 MigrationId，不向旧 candidate 混入增量。

**最后复核→activation 的冻结机制选定为 legacy source guard + 只读文件 leases**，不是未选定的 TODO。guard 是一次性 helper 的独立 guard mode，以已验证兼容的旧 Electron runtime/application identity、原 userData 路径获取旧版已使用的 `app.requestSingleInstanceLock()`，在普通 library/settings/tracker/matcher 初始化之前取得它。若已有旧程序，guard 返回 Busy，由用户正常退出，不能接管或强杀。持锁时 second-instance 只返回“资料切换中”，不加载库或执行转交参数；已打开的业务文件再用拒绝写/删共享句柄冻结。先取得 guard，再做完整最终清单/presence/hash 复核，持有到 authority commit point 之后才释放。这样已知旧客户端的新启动不能在“原来没有 settings/review 文件”的槽位创建新业务状态；不是要求旧 binary 理解 native 锁。

**最终 pointer committer 必须就是持 source singleton 的 guard 进程**，禁止 native 先问一次“guard还活着”再独立 rename。否则 guard 在回答后崩溃、旧 Electron 抢写，仍有窗口。进入 activation 时 native coordinator 停止自己的写入，将根 lease 的独占执行权移交 guard；guard 获取根 lease 后必须重新验证 expected authority generation、job、candidate hash、source fingerprint及授权receipt，再执行一次限定目标的同卷原子 pointer switch。实现使用受限 Windows commit bridge（只接受已绑定profile、MigrationId、candidate digest，不能执行任意命令/路径）；不是放开通用renderer写盘接口。未成功取得两侧保护就不提交。lease移交中其他进程先取得锁或state改变只导致重试/失败，绝不跳过复核。

这样 guard 在 commit 前崩溃会释放锁且保持 Legacy；commit 后崩溃则已是 Native，旧 Electron 的后续操作只属于旧分支。native 丢失 IPC 不推断失败或成功，重新取得root lease并读 authority决定。guard 保持job授权receipt与source/root leases直到 pointer结果已可读；不会启动旧writer。此处跨进程 owner/receipt交接是6.1必测契约，不能用pid或心跳代替。外部任意文件编辑不属于旧客户端锁保护范围，仍受文件 leases、最终清单和差异检查约束。

这不是6.0实现或已验证的机制。6.1 必须用实际旧客户端入口的隔离 fixture 证明同 profile 锁冲突（包括路径拼写/别名、未知旧 build 拒绝、缺失 companion、新旧启动竞争和 guard crash），见 J/M；不能仅凭 AppID 相同推断锁相同。实际源 binary/runtime 不在通过验证的兼容集合时，返回 UnsupportedSourceGuard，禁止 cutover，不能退化为“用户应该没再打开”。[Electron 官方单实例 API](https://www.electronjs.org/docs/latest/api/app#apprequestsingleinstancelockadditionaldata) 定义同应用实例的锁结果，本项目跨 helper/旧包兼容性仍需上述验证。

guard 与 preference exporter 的权限不同：exporter 只在副本运行；guard 必须使用原 legacy context，Electron runtime 自身可能创建锁/缓存。因此 **纯 read-only/copy 授权不允许启动原-context guard**；切换请求必须明确包括此短期 guard 的运行和这些 runtime 副作用，不包含旧 library/settings 用户状态写入、解密或永久只读改造。没有这项明确授权就只交付副本 readiness，不 activation。这一附加授权边界不能被 helper 名称隐藏。

同一 MigrationId 只允许一个 candidate；已经 Activated 时重复运行返回 existing activation，不重导 Session/count/tag/cover。失败后可从 sealed snapshot 确定性重建 candidate；deterministic legacy review ID 使用原 review ID 或 source-position 命名空间，不在每次运行随机生成。重试不能自动 overwrite 有 PostCutover 数据的 StoreId。

## 6. State machine 与 crash restart

“source 可变”中的 live 与 sealed 必须区分。target writable 指迁移 owner 的 staging 写，绝不等于 normal writer 可写。Authority 只由 durable authority pointer 决定。

| 状态 | event → next | source 可变? | target 可写? | authoritative | rollback |
| --- | --- | --- | --- | --- | --- |
| NotStarted | 额外授权 inventory → Discovered | live 可；无 snapshot | 否 | Legacy | 无变更 |
| Discovered | 稳定读取/副本校验/seal → SourceFrozen | 读取期间冻结；sealed 否 | 仅 archive/job | Legacy | 丢弃未 seal 副本 |
| SourceFrozen | parse/map → Staging | sealed 否；live 变动使 readiness 失效 | 仅 staging owner | Legacy | A |
| Staging | bounded candidate write/reopen → CandidateWritten | 同上 | 仅 staging owner | Legacy | A |
| CandidateWritten | 结构+语义+容量验证成功 → Validated | 同上 | 修复候选须重新验证 | Legacy | A |
| Validated | 差异报告/spot-check/来源复核 → ReadyToActivate | 同上 | 候选 sealed，normal writer 否 | Legacy | A |
| ReadyToActivate | 独立 cutover 授权+freeze+复核 → Activated | 临界区 live 冻结，sealed 否 | 只部署已验候选/authority | Legacy，直至 commit point | A→B |
| Activated | 首次用户写 admission/latch → MutationPending | live 仅旧分支；sealed 否 | native coordinator | Native | B，仅严格零修改 |
| MutationPending | 数据原子替换 → PostCutover；失败仍留 latch | 同上 | 同一 native writer | Native | C 或显式审计证明零修改 |
| PostCutover | 后续事务/兼容升级 → PostCutover | 同上 | native coordinator | Native | C |
| 任意 preactivation | I/O/校验/源变化/未知版本 → Failed(stage,reason) | archive 不变 | 停止候选写 | Legacy | A；可重试 job |
| 任意 postactivation | I/O/未知状态 → NativeFaulted | archive 不变 | 禁止普通写直至诊断 | Native（故障），不回到 Legacy | C/保留证据 |

| crash 点 | 重启动作；不可根据 journal 猜测 |
| --- | --- |
| Source copy 中/刚完成 | 无完整 seal+逐文件 hash 则不能使用；保留诊断副本，重新冻结复制。已有 seal/hash 完整才复用。 |
| Parsing / Mapping | 没有 target authority，重复纯转换；稳定 IDs、固定规则，无外部调用。 |
| Candidate writing | temp/部分 component 不是 candidate；校验 job/identity/hash 后从 sealed source 重建，不能补齐后跳验证。 |
| Candidate validation | 没有绑定 candidate digest 的完整 report 就重跑所有 validation。 |
| Activation immediately before / during rename | 读唯一 authority pointer：旧值=Legacy，新值=Native；临时文件不选主；损坏/无法判定时 blocked，不启动任一 writer、不自动选 backup pointer。 |
| Activation immediately after / receipt journal 未写 | Native pointer 的 MigrationId/hash 已证明提交；补记 receipt，不能再复制计次或切回。 |
| mutation latch 后 / main replace 前 | 不冒称有新用户值，也不能以没有完成日志证明没修改；保守禁用自动 B rollback。 |
| main replace 后 / callback 抛错 | component revision/内容与 receipt 判已提交；沿用 Phase 2 post-commit warning 语义，不重做副作用。 |

## 7. Staging、validation 与 activation protocol

1. 获得 job/root lease，核对冻结 source 与 manifest，parse → map → 在 staging 写专用组件。target 相对路径固定白名单，不接受 snapshot 内容提供可写绝对路径。
2. 所有 temp Flush(true)、原子替换后重新打开；检查文件数、长度/hash、格式版本、ID唯一性、install/preferred/cwd binding、Session native invariants、每类上限及 unknown fields；secret 只查 envelope 状态。候选记录没有 launch/recovery capability。
3. 独立语义 verifier 按第 3 节逐项对账。每个 FAIL 阻断；WARN 逐条给出用户可理解的影响并在 readiness 中明确接受。报告绑定 source fingerprint、candidate manifest hash、migration version、验证器版本和原始结果；修改任一字节必须重验。
4. 后续授权的人工 spot-check 至少覆盖多安装/cwd、个人 notes/rating、historical floor、legacy Session未知退出、个人封面、saved filter、provider/密钥重新输入状态。不是通过点击正式游戏验证。
5. **Activation 之前**取得单独 cutover 授权，绑定上述 readiness digest。部署候选到正式 store 后再次 reopen/hash/identity/version 复验；跨卷来源先复制完整后复验，不把跨卷 move 当 commit。
6. 初次安装 authority registry 时先以已落盘的 Legacy 指针表示原状态；准备 Native 指针 temp，包含 active StoreId、manifest hash、activation generation、MigrationId、初始组件 hash 清单。按第5节把最终执行权交给同时持有 source singleton、文件冻结和 root lease 的 guard；它完成同目录同卷 flush+atomic replace `authority.json`，这是唯一 activation commit point。只有 pointer 已验证为 Native、guard 释放root lease 后，正常 native writer 才重新获取lease、完整校验并创建 SessionEngine。
7. 无论 progress callback 是否抛错，读回 authority 为 Native 就返回 committed 或 committed-with-warning。失败未知则阻断，不能自动回切。旧 archive 与 initial candidate 永不自动清理；后续保留策略另设。

本次迁移是离线切换，不移交运行中的 Session/owned process。未来 cutover readiness 还须确认旧客户端已正常保存、没有待补写状态或正在游玩的游戏；**Stop Observing/旧 endedAt 都不是退出证明**。有可验证 identity 时复用既有有界 presence 检查，Active/Unknown阻断切换；legacy缺身份时需在授权的人工核对中确认游戏已经正常结束，不能从history猜测。迁移器不启动或kill游戏，不在新profile伪造旧reservation/recovery权限。仅复制历史的试迁移不表示已经满足此运行态切换条件。

本设计复用现有 Windows 原子替换思路，但**不宣称只加一个文件就自动获得跨文件原子性或断电保证**。所有初始组件先完成并封存，最后只切一个 pointer；正常用户事务继续每个组件原子保存、library accounting 在同一 library component 内提交。正式 adapter 的 receipt 与已有 Application contract 一致。用本地受支持卷的进程 crash/fault injection 验证；物理断电/控制器丢写不是本轮已验证能力。[Microsoft MoveFileEx 文档](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw) 的 REPLACE_EXISTING/WRITE_THROUGH 是 API 选项依据，不是多文件事务证明。

## 8. 首次写入与分阶段 rollback

`store-manifest.json` 保留 activation 时每个用户状态组件的 hash 与初始 MutationSequence=0。coordinator 在任何 library/Session/checkpoint/settings/UI preference/review决定/credential写入之前，原子创建并 flush `first-mutation.native.json`：ProfileId、StoreId、ActivationGeneration、WriterEpoch、OperationId、sequence、kind。这个 **intent latch 永不被普通 transaction、backup restore 或 Session terminalization 清除**。latch 创建失败则不开始数据写；data envelope 的 committed sequence 随实际数据原子替换。全部用户 writer 共享门禁，没有 UI sidecar 绕路。无内容变化的真正 no-op 不创建 latch；派生 thumbnails/log 不属于用户 mutation。

该协议故意保守：有 intent 未必已提交新值，但必然不再能仅凭“没有成功回调”宣称零修改。latch 缺失还必须逐组件核对 activation hashes/sequence，并检查所有用户组件 inventory；任一无法解释变化都按 C。丢日志、恢复旧 `.bak`、component 缺失、目录复制不能把 PostCutover 倒退成零修改。缺少/损坏 latch 与变更组件并存时进入 NativeFaulted。

| 回滚时期 | 允许 | 明确禁止 |
| --- | --- | --- |
| A：activation 前 | 丢弃未激活 candidate/staging，保留 immutable snapshot 与报告；legacy 原数据不被迁移写。 | 用候选覆盖 source，失败后把半份候选当主库。 |
| B：activation 后，无新 native mutation | 持根 lease 停止 writer；无 latch、所有用户组件与初始 hash 完全一致、source 未分叉且可用、无 in-flight work 才能明确执行反向 authority 切换，保留 audit。先取得回切意图，不自动切。 | 只检查 Session/count 就称“未修改”；忽略 preferences/credential/review；以 UI没有打开过作为证据。 |
| C：已写新数据，或不能证明没写 | 兼容 native binary 回退；显式 native backup restore（列出备份时点之后会丢的全部类别、先保留当前副本并单独确认）；repair；显式 Electron compatibility export 到新的文件/目录并列 loss report。 | 自动恢复迁移前 backup；承诺把 native cwd、精准 Session、recovery、native preference 无损塞回旧 Electron；覆盖 legacy 原 source。 |

Compatibility export 不改变 authority，旧 Electron 导入后是 fork；不会成为双向同步。恢复数据备份也不能自动降低 MinimumWriterVersion/WriterEpoch/authority generation 或擦去 latch。一次 native mutation 之后，默认是 one-way authoritative transition。

## 9. Credential、general settings 与 preferences

**源码事实**：Electron 保存 `settings.json.encryptedKey`，调用主进程 safeStorage；native 当前 `native-online-key.dpapi` 是另一种 raw DPAPI payload。`SafeStorageExperimentTests` 对特定合成 Electron profile 验证了 v10 AES-GCM + Local State DPAPI key，不证明所有安装版本/历史 profile 都相同。[Electron 官方 safeStorage](https://www.electronjs.org/docs/latest/api/safe-storage) 只承诺其加解密 API 和 Windows 平台保护，不提供该内部字节格式的永久兼容保证。

**决策**：6.1 不实现自动 secret extraction，也不复制 ciphertext 后声称已迁移。默认 ReentryRequired；没有旧 key 则 Absent；无法读/损坏则 Unavailable/Corrupt，均不阻断 library migration。重新输入只在明确 provider association 下用 native DPAPI 保存。输入、错误、日志、临时文件、迁移报告均不包含 plaintext；本轮不解密任何正式密钥。未来若单独授权/设计安全 helper 传递 secret，是另一个验证项，不成为 Phase 6 cutover 前提。

正式 credential envelope v1 最小字段：Version、ProfileId/StoreId、ProviderId/normalized origin、Mechanism=`windows-dpapi-current-user-v1`、Payload、Source=`user-entry`（预留已授权迁移枚举）、Revision。Provider 与 schema 信息也封入受 DPAPI 保护的内容，避免只改外层 provider 转发 key。未知 envelope/mechanism 禁止自动覆盖/清空；修复或重新输入须明确替换操作并保留旧受保护版本。rotation 是同 provider 下的新 revision 原子替换，不扩展成复杂密钥协议。

general settings 目前尚无 native 完整迁移实现。6.1 需要 versioned adapter 与消费门禁：includeNsfw=false 不能被默认 true 覆盖；readExecutableMetadata=false 必须阻止相应读取；global cover display 与每项 override 保持优先关系；theme 映射 native；provider baseUrl 保留并严格采用既有网络来源验证。Electron style 不在 WinUI 实现同样外观，存 compatibility 字段并说明不应用；不是悄悄删除。

**Preference helper 决策：需要，限非 secret 偏好。** 当前 library export 不包含 localStorage，无稳定应用级 preference export。6.1 设计/实现一次性 helper，在与来源版本和 origin 匹配的 **legacy Electron context 的授权副本** 中，只读两项应用 key，用现有 validators 输出 versioned `preference-export.v1.json`；最大 200,000 字符 saved views、50条，限制总输出，包含 source-copy identity、producer version、schema、payload hash。无普通 library/settings/matcher 初始化、无自动联网、无凭据导出，禁止接受任意 JS/key 名。Chromium 启动自身可能写缓存，所以只允许副本运行，原始 immutable snapshot 不作为运行目录。输出 seal 进入 job fingerprint，helper 退出，不成为常驻依赖。

helper 不可用、源 origin 不明、未知 export version、corrupt key：输出逐 key 错误/Unsupported；layout/sort 使用明确默认，saved filters 不伪称空成功；原 bytes 保留。已支持字段的 values/IDs/order 用独立 verifier 核对。只有已知“缺失”才是 Defaulted；“读取失败”是 Unavailable。不解析 Chromium LevelDB 内部格式。browser内部缓存、Electron窗口临时状态不虚构迁移项。

完整 source snapshot 只复制已获授权的 companion，包含密文字节的 archive 仍须本用户受保护存放；报告不含密文 payload。只授权读取资料，不自动授权复制密文或解密/运行 helper；没有相关授权可用明确缺项清单完成 library-only readiness，并标记 prefs unavailable/credential re-entry。不能称这种副本是完整 profile archive。

## 10. Personal media 决策

维持 inline JPEG Base64。source backup 和 candidate 都包含 personal image，hash 对 decoded bytes；原照片不是引用/迁移依赖。已有 remote URLs 与 metadata/work 关系保留，thumbnail/cache 是可重建资料，cold cache 的网络失败不能删除个人封面。

Base64 至少约 4/3 encoded bytes，full-store save 及 last-good backup 会重复写它；JSON parser/UTF-16 字符串/验证造成更大的内存瞬时放大。当前图像 pipeline 另有 2 个 decode slot、64 MiB memory cache、128 MiB disk cache；1600²×4 的单张 BGRA 约 9.77 MiB，不可把 JSON benchmark 内存当作带全部真实解码的 UI 峰值。正式试迁移要用真实授权副本检查解码和总内存。

现在不引入 media 文件拆分、GC、引用更新、导出打包的新事务问题。若第 11 节触发条件表明主要压力来自 inline cover，再单独作 content-hash 文件名、hash verify、临时文件→引用 commit、缺图告警、引用可达性 GC、backup/export/rollback 一致性的 storage decision，不能一边拆媒体一边首次正式切换。

## 11. Synthetic benchmark 与 SQLite 决策

本轮实际执行；不是正式资料测量。独立 harness 位于 ignored `.artifacts/native-phase6-design/benchmark/`，引用现有 Release 的 Domain/Application/Infrastructure/Platform.Windows DLL；link 未修改的 SessionTestRig fake clock/process，真实 `TransactionalLibrary` / `NativeLibraryCodec` / `IsolatedLibraryPersistence`，真实本地磁盘 Flush/backup/atomic replace。没有启动游戏。每个规模独立子进程，三次 load/普通事务/metadata/backup reopen；三次 fake-owned launch 新增 native history，每次后一个真实 10 秒 checkpoint；StopObserving 只用于 harness cleanup。history-add 包含 launch 初始化持久化（每次两次 save），不冒称单次数据库 append。

每条基础记录含一条 60 秒 legacy Session、1000秒 total、count=5、短 notes/tags；只有首条合成安装，metadata 在 edit 时添加，不代表每条都含完整真实 API metadata。10k history 在首条放 50,000 Session；notes 是每十条一条 8192 ASCII notes +100 tags；covers 是128张各64 KiB payload的结构JPEG fixture，**只验证存储负载，不是真实图像 decode 测试**。数据没有正式 profile 成分。这里不是完整 Phase 7 性能 Gate。

| 样本 | 初始 JSON MiB | load ms | full personal transaction ms | metadata ms | add history ms | checkpoint ms | backup recovery ms | 进程峰值 MiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1k base | 0.71 | 163.1 | 177.1 | 171.3 | 277.2 | 124.3 | 103.6 | 102.1 |
| 5k base | 3.57 | 341.6 | 340.1 | 238.3 | 490.3 | 247.6 | 209.4 | 180.6 |
| 10k base | 7.14 | 493.0 | 473.8 | 474.5 | 930.5 | 488.4 | 428.3 | 289.0 |
| 10k +50k history | 15.81 | 894.4 | 1478.3 | 987.3 | 1995.2 | 1031.6 | 884.3 | 531.8 |
| 10k notes/tags | 16.72 | 769.0 | 808.3 | 737.2 | 1650.2 | 827.2 | 724.9 | 738.7 |
| 10k covers | 17.83 | 679.5 | 717.7 | 682.3 | 1489.7 | 755.1 | 639.9 | 932.4 |

时间为三次中位数，不是 p95。全部 load 最大 1138.5 ms，checkpoint 最大 1033.2 ms，history-add 最大 2294.2 ms；warm filesystem cache 未清除。PeakWorkingSet64 是整个子进程生命周期高水位（包括生成、验证、操作和 recovery），不是每步独立峰值或 native UI RSS；运行之间不叠加。内存与 latency 说明接近上限时必须再验，不能外推为任意 10k 真实记录都健康。

写放大：当前 SaveAsync 写 previous backup + candidate main，应用层持久化 payload 为 `oldBytes + newBytes`，稳态约 **2×store size/commit**，另有原子 namespace 操作/文件系统开销未计量。10k base checkpoint 实测 persistence 部分平均约15 ms；cover 约35.9 ms，说明此次大部分成本在全库序列化/多次 decode/分配，换一个原子 rename API 不会消除放大。初始 pretty JSON 在第一次 native Session 后变成另一种 serialization，故不能把初始文件大小直接当每次写量；原始结果保留每次 storeBytes/saveCalls。短 notes 编辑仍写全库，50字节编辑对应 MiB 级写放大。保留 main+lastGood 约2S，写临时及 source/candidate/archive 同时存在时容量至少按额外若干个 S 预算，readiness 按实际文件清单校验空间。

Recovery 测量是把本次合成 main 改为损坏文本，再打开有效 lastGood 并断言 Backup；没有在真实断电时测磁盘。额外实际运行既有 `LegacyCompatibilityTests|SessionPersistenceTests|TransactionSafetyTests`：**109 passed / 0 failed / 0 skipped**，含各 persistence fault boundary 和 future Session/read-only。没有新增或更改生产/test 源码；没有把这109项说成重新跑完整 native suite。

**决策：Phase 6 保留 JSON，不同时改 SQLite，也不拆 inline media。** 本轮范围内 checkpoint 仍明显低于 Phase5B 55→60秒的余量；尚无证据证明必须承担 storage engine rewrite 风险才能正式迁移。JSON 有明确限制，不作无条件 cutover 许可。

以下为 **未来副本 readiness 工程门槛**，不是本轮三次样本已证明的 SLA：library 保持32 MiB硬上限/10k条；canonical candidate ≥24 MiB 或预计一年正常增长逼近32 MiB触发重新评估；在目标机器复制样本≥30次 checkpoint 的 p95>2秒、任何关键提交超4秒/不能保留退出预算、或实际 UI 峰值>1.25 GiB/持续内存压力时停止 cutover。history增速、normalization输出扩张、notes和covers比例都纳入；不得删除用户记录来过门槛。容量规划至少包含 source snapshot、candidate、main、last-good、最大临时写与失败保留空间。

触发后优先根据 profile 分类成本确定是否 SQLite/媒体分离；如确需 SQLite，在 **6.1 内独立 storage 子 Gate** 完成算法、schema、transaction/Session exactly-once、crash/recovery/export/rollback 全量回归，再进入6.2；正式 JSON 和 SQLite 不双写为两个 truth。不能把首次 SQLite 验证与首次正式数据 cutover 绑一起。本设计不实现 SQLite。

本机证据：`.artifacts/native-phase6-design/{1000-base,5000-base,10000-base,10000-history,10000-notes,10000-covers}.json`、`tests/storage-fixtures.trx`、`evidence-audit.json`。harness build 使用 bundled .NET 10.0.401，Release；仅 linked fake fields 的8个未赋值 warning，无编译错误。可重复命令：在仓库根运行 `.artifacts/native-phase1/tools/dotnet/dotnet.exe build .artifacts/native-phase6-design/benchmark/Benchmark.csproj -c Release`；然后对上述六组分别运行该 dotnet + `benchmark/bin/Release/net10.0-windows10.0.26100.0/Benchmark.dll <count> <variant>`。harness 和运行结果是本机 ignored 产物，不随文档提交；规范表格及 fixture recipe 是可维护的证据摘要。

## 12. 可执行 migration regression matrix（6.1 实施要求）

所有项目先使用 synthetic profiles，断言 source hashes 不变、authority 唯一、失败没有被当空成功。表中的要求是待实现测试，不是6.0已通过。

| ID | 输入/故障 | 必须断言 |
| --- | --- | --- |
| A | empty / small / typical / 1k/5k/10k large，mixed metadata | IDs/count/values 全量对账；空库确实来自有效空源；容量超限不可 activation。 |
| B | 无安装、多安装、首选null、cwd missing/null/显式、Unicode/空格/长路径/缺 optional/unknown old fields | 参数顺序和 cwd 完整；未知语义阻断，不能默认有损 import；不启动 exe。 |
| C | aggregate-only、history-only输入、total>sum、零Session、legacy unknown、native+legacy共存 | 已知合法 aggregate 原值不降；缺必需 aggregate 或 total<sum 时 FAIL不猜；count不重算；无虚假 exit，跨profile token不授权。 |
| D | tags/categories、0/null rating、notes空白/Unicode、favorite/status/custom name | source→target 独立value/set比较；不被 metadata刷新覆盖。 |
| E | 无/remote/Base64 cover、corrupt/超大、wrong dimensions/hash、冷缓存 | personal字节hash与选择关系；损坏阻断；网络失败不删除；有界真实decoder另外测。 |
| F | all known prefs/partial/unknown version/unavailable localStorage/错origin/oversize helper输出 | migrated/defaulted/unavailable分开；saved-filter ID与条件保持；不解析LevelDB、不写source。 |
| G | synthetic migratable secret、corrupt/unknown/unavailable/absent/re-entry | 本版本仍按策略re-entry不提取；非默认provider不误绑定；unknown envelope不能覆盖；日志无secret；library可通过。 |
| H | snapshot/parse/map/write/reopen/validation故障；activation前/中/后杀合成worker；callback抛错 | 不完整候选不选主；指针旧/新唯一；postcommit不假失败；所有temp均非权威。 |
| I | 相同snapshot重复、进程重启、不同export时间、重复activation、重试与已PostCutover store相撞 | MigrationId稳定；Session/count/media不重复；已有active返回同receipt，不能重新导入。 |
| J | copy中改main、replace settings、添加原本缺失companion、helper export期间写prefs、最后复核→activation间旧writer抢写 | SourceChanged/Busy；不会混合A/B；不能拿native lock代替legacy冻结；来源失稳则不切换。 |
| K | unknown legacy/native/component/session/credential version、无migration path、valid future main+旧backup | 拒绝写且不回落旧backup；Unknown不是corrupt；保持所有原字节。 |
| L | A取消；B零修改；B有pref/credential变化；latch后crash但未数据commit；C新Session/notes后restore/export | A不碰源；B必须证明零变化；不明视C；C有loss report、备份当前、明确确认；authority不双写。 |
| M | 旧Electron默认入口及误设KGM_DATA_DIR、Phase1–5旧native、Phase6旧protocol、同时两writer/两migrator、symlink/hardlink/复制组件 | native权威文件hash不被旧入口改变；lease唯一；identity mismatch拒绝；formal无synthetic bypass；冻结窗口用竞争写实际验证。 |
| N | 所有sidecar首写、普通Session transaction、no-op、backup recovery、latch丢失/损坏、Cache-only | 用户mutation不能绕latch；普通commit不清除；cache不伪造用户修改；不能错误获得B rollback许可。 |
| O | 多轮Active/Unknown occupancy复核、恢复StopObserving、失败持久化重试、shutdown与migration竞争 | 保留已Gate Phase5B不变量；不kill、无额外count/time；锁/handle释放；formal adapter替换后完整native回归。 |

Data verification 自动输出每类 PASS/WARN/FAIL 与 disposition，至少包含：Entry/Installation source-target count、ID集合、cwd逐值、参数数组、tags/categories集合、rating/notes/favorite值、count、total及floor、Session ID/秒数/native状态、cover hash/link、provider/manual/review关系、prefs classification、credential state。任何核心用户数据 FAIL 禁止生成 readiness；WARN不能只藏日志。验证器还检查原始源未变、候选可重新打开、未来writer无法重写、报告digest与激活对象相同。

## 13. 后续最多两段及授权顺序

**6.1 — Migration Engine & Authoritative Store Protection**：实现上述 versioned adapters、inventory/snapshot/fingerprint、staging/verifier/report、authority pointer、store身份/version gate、全用户writer协调与mutation latch、source freeze、一次性非secret preferences helper、native credential envelope/re-entry、general settings保留、rollback mechanics。保持Phase2 receipt与5B accounting行为。全部在synthetic/fixture上验证A–O矩阵、完整native regression及涉及共享导入/数据映射的Electron兼容回归；不以编译替代故障注入。JSON继续使用；若容量gate触发storage改造，在此段内部先独立Gate，不增加正式数据操作授权。

**6.2 — Formal Profile Copy Trial & Cutover Readiness**：必须另获授权，按实际 scope 执行：read-only inventory → fingerprint → 完整或明确受限的 source snapshot → 在副本运行授权 helper → snapshot migration → candidate → automated comparison → manual spot-check → rollback drill → readiness report。记录正式instance真实存在的文件/版本/大小/异常，不能沿用本schema inventory当事实。若只有读取授权，不自动复制；若只允许复制资料，不自动解密credential/运行legacycontext。secret重新输入不阻断library迁移。

**Ready 不等于 cutover**。再次独立获得用户明确 cutover/首次 authoritative write 授权后，才按第7节部署正式store、冻结与复核来源、activation。切换后的旧Electron不再作为权威writer；不会删除程序、替换安装或修改旧程序成只读。将它描述为“可无损随时换回的同等writer”是错误的。

6.0 完成后暂停。未实现/未验证：正式 source freeze、migration mapper、新 profile adapter/version gate/activation/latch、helper、credential envelope、正式instance对账、真实图片decode压力、正式恢复演练、物理断电。它们是6.1/6.2明确的交付和Gate条件，不是当前已实现功能。本轮无未解决设计 BLOCKER/MAJOR；未来实施不能跳过这些条件。
