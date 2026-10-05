# Phase 6 — Native Production Profile Bootstrap

状态：scope reset checkpoint `62da4b4`；**READY FOR PHASE 6.1 INDEPENDENT GATE**。实现与自检完成，Debug/Release 全量各 1363/0/0；最终证据及两项间歇测试失败观察见 DEVELOPMENT。独立 Gate 尚未进行，正式 profile 未创建或访问，暂停等待 Gate。

## 产品边界与阶段

没有要求保留的重要 Electron 正式资料。Electron 今后仅为 legacy/archive；它不是 native 数据链中的 writer。保留现有显式 reader/import，但 bootstrap 不搜索、不读取、不自动导入 legacy，不迁移 credential/preferences/cover/history。归档旧 Electron 目录是以后可授权的普通复制，不是 native backup。

- **6.1 Production Store Bootstrap**：只在 synthetic/isolated production-like root 实现与验证基础设施。
- **6.2 Production Readiness / First Real Profile**：以后获得用户明确授权才建立第一份正式 native profile 并开始日用验收。

禁止本轮访问 `%APPDATA%\KunGalManager`、创建或访问真实 native production profile、读取真实凭据、启动真实游戏、推送。无 Electron migration engine、MigrationId、sealed source、journal/reconciliation、authority activation/mutation latch、legacy writer coordination 或 Electron rollback。SQLite、实际 v1→v2 upgrader、media store、personal cover 文件化及高级 backup UI 延后。

## 最小存储契约

Production、Development、Test 与 Electron legacy 使用四个物理隔离根。默认 native production 为 `%LOCALAPPDATA%\TheOrder\Profiles\Production`，Development 为同级 `Development`；Test 必须显式提供隔离根。发布产品身份显式选 Production，普通开发构建选 Development；不根据环境变量缺失猜测 Production。真实 Production 默认拒绝，未来可由专用明确授权入口启用。

最小 manifest 包含稳定 ProfileId、StoreId、ProfileKind、StoreFormatVersion、CreatedByWriterVersion、MinimumWriterVersion、RequiredCapabilities 与 Initializing/Ready 状态。格式/能力/身份不匹配或未知字段拒绝写入，不读取已知字段后降级覆盖。component 与 backup 必须属于同一身份；文件缺失不自动创建第二身份。

先取得唯一 bootstrap/writer ownership，再检查目录、持久化初始化身份，建立空 library、native defaults 和 empty credential，最后原子发布 Ready，重新打开校验后才给应用使用。Initializing 可续作；Ready 重开不能重置用户数据。无法证明安全的部分目录保留并报告 RecoveryRequired。Bootstrap 没有 Electron migration 状态。

正常写入复用 Phase 2 原子替换/last-good/事务校验与 stale commit protection；Session 继续 Phase 5B。备份与恢复也验证身份/版本，不把未知未来 main 当损坏后以旧 backup 覆盖。原生凭据使用现有 Windows DPAPI，偏好使用现有 native model，封面保留当前 schema；workingDirectory 经写入、重开和恢复不丢失。

## 验证与完成门槛

覆盖 fresh/idempotent/partial bootstrap、并发真实进程首次启动、唯一 writer、错身份/种类/未来格式/能力、未知字段、正式路径 guard、全部组件跨 profile 隔离及无自动 legacy import。故障点覆盖 root、manifest temp/replace、library/settings、缺 companion、receipt lost、reopen、backup 和 recovery。

实际进行 bootstrap→library/install/cwd/preferences/cover/synthetic credential/Session→backup→corrupt或missing main→restore→reopen→语义对比；重复打开、冲突与恢复验证资源，1k/5k/10k synthetic native store 测 open/write/recovery 开销。修改共享 persistence 后完整 Debug/Release native suite 与 builds/locked restore 必须通过；未修改 Electron 时不要求全量 Electron 重跑。

所有实现/自检证据必须区分历史与本轮、synthetic 与真实。完成后建立 implementation checkpoint，报告 **READY FOR PHASE 6.1 INDEPENDENT GATE** 并暂停；不得宣称正式 profile 已创建。

## Phase 6.1 实际结构

`NativeProfilePaths` 负责纯路径选择和 I/O 前权限检查。拒绝 Electron 正式根及父子目录、跨默认 scope、UNC/扩展/短路径别名、尾点/尾空格、ADS 与目录重解析。先固定已有父链，再逐级创建并固定缺失目录。`NativeProfileRequest.ExplicitProductionAuthorization` 是未来 6.2 的明确授权接入点；当前 WinUI/脚本不提供解除真实 Production guard 的参数或环境变量。

`NativeProfileStore` 从应用入口取得 `.the-order-writer.lock` 独占句柄，整个应用生命周期只有此 owner。库事务通过 `CreateLibraryPersistence()` 借用，最多一个；凭据/偏好/识别建议/启动位置通过同一 owner 串行访问。先 dispose Session/库再 dispose profile；未释放库时拒绝释放 profile。目录与两份身份文件一直固定，临时流均有释放路径。AppInstance 仍在 engine/writer 之前处理 secondary；第一次需建空目录以计算物理 instance key，建空目录不等于取得 bootstrap 所有权。

文件布局（仅指定 profile 根内）：

| 文件 | 职责 |
| --- | --- |
| `native-bootstrap.json` | 原子发布的 Initializing 身份锚；长期保留，非 migration journal |
| `native-profile.json` | 相同身份的 Ready manifest；最后原子发布，之后重开验证 |
| `library.json` / `.bak` | 原有 native library payload 和上次有效库，外加身份 envelope |
| `native-ui.json` / `.bak` | native 偏好；System/grid/added/空 SavedFilters |
| `native-online-key.dpapi` / `.bak` | 当前用户 DPAPI 密文；fresh payload 空代表未配置 |
| `native-recognition.json` / `.bak` | native 建议；fresh 空；不读 `metadata-matching.json` |
| `native-launch-positions.dpapi` / `.bak` | 原生位置授权观察，沿用现有 DPAPI 机制；fresh 空 |
| `native-thumbnails-v1/` | 派生缓存，不是 authoritative 用户资料或备份内容 |

Manifest v1：ProfileId、StoreId、ProfileKind、StoreFormatVersion=1、CreatedByWriterVersion=1、MinimumWriterVersion=1、RequiredCapabilities=`native-profile-v1`/`native-session-v1`、PhysicalRoot、State。PhysicalRoot 是目录 volume/file identity 与 creation time 的散列，两个 GUID 从该身份分别派生，保证 manifest replace 前崩溃也不会生成第二身份。两份 manifest 必须一致；可选 ExpectedProfileId/ExpectedStoreId 进一步固定调用者期望。

组件 envelope v1：Version、ProfileId、StoreId、Name、RestoredFromBackup、Data。Data 是原有 JSON bytes 或 DPAPI bytes 的 Base64 包装；库内部 schema、cover 的 data URL、Session accounting 均未改。原库 payload 仍有 32 MiB 限制，外层读取限制 48 MiB；这种包装有约 1/3 磁盘开销，不代表实施了 media store。未知字段/重复 JSON key/未来格式/能力/错身份拒绝，不能以旧 backup 降级覆盖。跨 profile 复制组件不被接纳。

Initializing 续作前先核对所有已有组件只能是同身份的预期空值，再补缺项；发现用户数据立即拒绝。Ready 重开从不重建身份或重置数据。未知目录内容不自动 adopt；manifest 损坏/丢身份锚/backup 也不可用时保留现场并拒绝。崩溃留下的原子写临时文件不是 main，不参与恢复；本阶段不提供 orphan 清扫任务。

`WindowsAtomicFile` 是从既有 Phase 2 原语提取出的共享实现：同目录 temp、Flush(true)、MoveFileEx replace/write-through、提交后 warning 语义不变。库仍走 `TransactionalLibrary` 候选 round-trip、last-good 和 stale request；profile adapter 另核对主文件未在 owner 外变化。companion 也复用该原语，各自保留 last-good，不另造 production writer。

`CheckpointBackups()` 在 owner 串行区内验证并复制各组件；这是逐组件 last-good 链，**不是跨多个文件的全库原子快照**。`Restore(name)` 在库关闭后，验证当前主文件未属未来/错身份版本，再验证 backup，原子恢复并重开验证。库 main 损坏/缺失还可沿原 Phase 2 自动从 backup 读取，下一次明确保存写回；偏好等损坏可调用显式 Restore API。本轮不做高级恢复 UI 或跨机器/跨物理根 adopt。

显式恢复库 main 时 durable envelope 标记 `RestoredFromBackup`。可选 `ILibraryRecoveryProvenance` 把它传给现有 `LibraryRecovery.Backup`，SessionEngine 沿原 `BackupRecovery` 分支处理；普通编辑不能在待恢复 Session 仍存在时清掉来源。由此防止“backup 复制成 main → 重新获得 active Session 自动接管资格”。恢复不增加 count、不补时、不 kill 游戏；已终态历史、ID、cwd、cover/accounting 保持。

## 启动和隔离工具

普通 Debug/Release 开发 build 默认 Development。发布必须显式 `-p:NativeProductionCandidate=true`；`native.ps1 -Action Publish` 已带此标记，缺少标记的直接 publish 拒绝。Production candidate 默认选 Production，但真实根仍被 guard 拒绝；6.1 运行只能指定隔离根。Test 类型缺少显式目录即拒绝。

```powershell
# 示例只描述入口，真实执行使用新建 GUID 临时根；不是创建正式 profile 的授权。
$phase61Root = Join-Path $env:TEMP ('the-order-profile-trial-' + [guid]::NewGuid().ToString('N'))
& ./native/scripts/native.ps1 -Action Run -DataDirectory $phase61Root -ProfileKind Production
```

GUI 环境覆盖仅有 `THE_ORDER_NATIVE_DATA_DIR` 与 `THE_ORDER_NATIVE_PROFILE_KIND`；种类覆盖是显式选择，不能绕过真实路径 guard。旧 Phase 1–5 raw synthetic fixtures 保留 `-FixtureProfile` / `THE_ORDER_NATIVE_FIXTURE_PROFILE=1` 的显式测试入口，仍需既有 marker，且禁止真实 native production 根；不会自动识别、转换或导入 raw fixture。

## 未扩大的边界

没有 Electron 源发现、迁移、协调、credential/localStorage 转换、SQLite 或 v1→v2 upgrader；Electron 源码未改。没有读取旧正式 profile、读取正式 credential、创建/打开真实 native production profile、真实游戏操作或远端推送。恢复演练是本机同物理 profile；独立 Gate、真实首次 profile、跨机器恢复、power-loss 硬件耐久、长期媒体压力仍不在已验证结论内。
