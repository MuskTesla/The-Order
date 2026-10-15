# 候选构建与验证

从当前维护工作区的 AGENTS.md、docs/ai/BRIEF.md、DEVELOPMENT.md 接管。长期根目录的历史性能修改和先前工作树不是本轮候选的可随意覆盖目标。

使用仓库锁定的 .NET 10.0.401、Windows App SDK 2.5.1 和 x64 配置。先 locked restore，再以 Release、自包含、NativeProductionCandidate=true 构建；同时传入 `PathMap=<源码根>=/_/TheOrder` 与 `CopyOutputSymbolsToPublishDirectory=false`。发布输出必须使用新的目录，不能重写已验收候选。

完整客户端回归通过 `services/bangumi-auth-worker/test/run-client-bridge.mjs --all-client-tests` 运行，显式指定固定 SDK 和全新结果目录。这包含本地合成 Worker、DPAPI 和直接元数据契约，不是 Cloudflare 部署或真实账号授权。启动测试程序必须指定有标记的 Test Profile；裸启动生产候选可能选择真实资料，不能用于测试。

分发流水线为 `package-native-release.ps1` 后接 `package-native-installer.ps1`，传入本目录作为 DocumentationDirectory。前者保留文件、依赖及源码清单，默认移除开发测试启动器和 PDB；后者核验实际 release 入口与哈希、官方 Inno 编译器后构建 GUI 入口和安装包。生成安装包不等于执行安装，也不等于发布。

本候选保留产品版本 1.1.0，不会被同版本在线更新器当作新发行。未来公开版本号、签名和发布仍需所有者决策；本轮不改公共更新源。候选提交、实际构建身份、源码及包哈希以随附回执为准，不能把文档提交冒充程序内嵌提交。

支持策略仍是 Windows 10 1809／17763 以上的 x64 Windows；本轮可用宿主为 Windows 11，其他系统须独立验收。不同安装目录不是操作系统隔离，不得在宿主运行安装器来冒充 VM 中的干净升级。没有已准备好的 VM／Sandbox 时，将相应门禁标记 BLOCKED。
