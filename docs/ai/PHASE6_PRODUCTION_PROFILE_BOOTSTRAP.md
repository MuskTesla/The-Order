# Phase 6 — Native Production Profile Bootstrap

状态：scope reset 已确定；Phase 6.1 实施中，未完成 Gate。

## 产品边界与阶段

没有要求保留的重要 Electron 正式资料。Electron 今后仅为 legacy/archive；它不是 native 数据链中的 writer。保留现有显式 reader/import，但 bootstrap 不搜索、不读取、不自动导入 legacy，不迁移 credential/preferences/cover/history。归档旧 Electron 目录是以后可授权的普通复制，不是 native backup。

- **6.1 Production Store Bootstrap**：只在 synthetic/isolated production-like root 实现与验证基础设施。
- **6.2 Production Readiness / First Real Profile**：以后获得用户明确授权才建立第一份正式 native profile 并开始日用验收。

禁止本轮访问 `%APPDATA%\KunGalManager`、创建或访问真实 native production profile、读取真实凭据、启动真实游戏、推送。无 Electron migration engine、MigrationId、sealed source、journal/reconciliation、authority activation/mutation latch、legacy writer coordination 或 Electron rollback。SQLite、实际 v1→v2 upgrader、media store、personal cover 文件化及高级 backup UI 延后。

## 最小存储契约

Production、Development、Test 与 Electron legacy 使用四个物理隔离根。默认 native production 规划为 `%LOCALAPPDATA%\TheOrder\Profiles\Production`，Development 为同级 `Development`；Test 必须显式提供隔离根。发布产品身份显式选 Production，普通开发构建选 Development；不根据环境变量缺失猜测 Production。真实 Production 默认拒绝，未来可由专用明确授权入口启用。

最小 manifest 包含稳定 ProfileId、StoreId、ProfileKind、StoreFormatVersion、CreatedByWriterVersion、MinimumWriterVersion、RequiredCapabilities 与 Initializing/Ready 状态。格式/能力/身份不匹配或未知字段拒绝写入，不读取已知字段后降级覆盖。component 与 backup 必须属于同一身份；文件缺失不自动创建第二身份。

先取得唯一 bootstrap/writer ownership，再检查目录、持久化初始化身份，建立空 library、native defaults 和 empty credential，最后原子发布 Ready，重新打开校验后才给应用使用。Initializing 可续作；Ready 重开不能重置用户数据。无法证明安全的部分目录保留并报告 RecoveryRequired。Bootstrap 没有 Electron migration 状态。

正常写入复用 Phase 2 原子替换/last-good/事务校验与 stale commit protection；Session 继续 Phase 5B。备份与恢复也验证身份/版本，不把未知未来 main 当损坏后以旧 backup 覆盖。原生凭据使用现有 Windows DPAPI，偏好使用现有 native model，封面保留当前 schema；workingDirectory 经写入、重开和恢复不丢失。

## 验证与完成门槛

覆盖 fresh/idempotent/partial bootstrap、并发真实进程首次启动、唯一 writer、错身份/种类/未来格式/能力、未知字段、正式路径 guard、全部组件跨 profile 隔离及无自动 legacy import。故障点覆盖 root、manifest temp/replace、library/settings、缺 companion、receipt lost、reopen、backup 和 recovery。

实际进行 bootstrap→library/install/cwd/preferences/cover/synthetic credential/Session→backup→corrupt或missing main→restore→reopen→语义对比；重复打开、冲突与恢复验证资源，1k/5k/10k synthetic native store 测 open/write/recovery 开销。修改共享 persistence 后完整 Debug/Release native suite 与 builds/locked restore 必须通过；未修改 Electron 时不要求全量 Electron 重跑。

所有实现/自检证据必须区分历史与本轮、synthetic 与真实。完成后建立 implementation checkpoint，报告 **READY FOR PHASE 6.1 INDEPENDENT GATE** 并暂停；不得宣称正式 profile 已创建。
