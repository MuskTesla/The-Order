# 1.0.2 认证、同步、程序更新与兼容候选源码及构建

本轮认证与同步增量位于 `.artifacts/auth-sync-20261011/`。认证分层为 Application `NextMoeAuthentication`、Infrastructure 固定 OAuth／个人 API／共享限流、Windows 环回监听与 DPAPI 伴随文件、Presentation 协调状态及 WinUI 设置／详情。`PersonalSyncCoordinator` 独立完成读取、差异、授权、事务及恢复，UI 不拼请求或处理令牌。没有新增 NuGet 包或升级依赖；为完整历史回归按现有 npm 锁文件恢复了合成 Electron 安全存储测试运行时，不启动旧正式程序。

始终用 `-p:Platform=x64` 构建和执行本轮测试，测试夹具根据真实 bin 相对路径寻找对应子进程。完整 Release 回归与专项、原生 Test 流程、匿名真实请求分别记录，不能把模拟 OAuth／云端写入计为真人验收。真实账号只读验证使用 Test + `THE_ORDER_NATIVE_READ_ONLY_OAUTH_ACCEPTANCE=1`；仅该显式隔离入口选择阻止写入的个人 API 包装。普通 Production 不能选择合成账户或验收包装。

复现：`dotnet test native/TheOrder.Tests/TheOrder.Tests.csproj -c Release -p:Platform=x64 --no-build --no-restore`；专项过滤器 `FullyQualifiedName~NextMoe|FullyQualifiedName~PersonalSync`。真实匿名探针：`TheOrder.RecognitionLab.dll anonymous-auth-smoke <新输出JSON>`；实际授权后的只读恢复／退出探针：`oauth-readonly-smoke <本轮隔离Test目录> <本轮脱敏输出JSON>`，先由用户完成系统浏览器授权并正常退出同一 Test 窗口。探针只记录路径、HTTP 状态、数量和不变量，不输出账户、记录值或令牌。

认证与隐私说明见 [AUTHENTICATION](AUTHENTICATION.md)。严格跨设备无覆盖的自动双向替换写受官方无条件更新契约限制，采用手动预览确认替代后台替换。真人 OAuth 登录、重启刷新、获准个人只读接口及退出闭环已通过，证据为 `oauth-real-readonly.json`；最终产物绑定、条件结论见候选附带 `ACCEPTANCE.md` 与本机 `validation-summary.json`。真实云端写入／删除、收藏夹额外权限和 Production 升级本轮均未验收，不计为通过。

维护源码为 `native/`。本地候选版本 1.0.2，公开版本仍为 1.0.1；本轮没有发布或替换日用安装。构建环境为 Windows 11 x64、固定 .NET SDK 10.0.401、Visual Studio C++ x64 构建工具、Windows SDK 10.0.26100.0 和 PowerShell 7。构建工具版本与用户运行下限是两件事。

`native/Directory.Build.targets` 将每个 Windows 程序集的 `TargetPlatformMinVersion` 与 `SupportedOSPlatformVersion` 固定到 10.0.17763.0；保留 `net10.0-windows10.0.26100.0` 引用目标，CA1416 以警告当作错误检查未保护的新版系统调用。`WindowsCompatibility.MinimumBuild`、启动检查、安装器 `MinVersion` 和包身份也使用 17763。桌面工具及测试为 x64，输出路径保留原约定。

Windows App SDK 2.5.1 自包含；Windows 平台层显式引用同版既有 ML 组件 2.1.94，其固定运行时为 Microsoft.Windows.AI.MachineLearning 2.1.74，未升级 Windows App SDK 或替换模型。CPU ONNX C# 接口直接使用随包运行时，不调用 WinML 自动执行提供程序选择或下载。

```powershell
dotnet restore native/TheOrder.slnx --locked-mode
dotnet build native/TheOrder.slnx -c Release --no-restore -p:Platform=x64 -p:NativeProductionCandidate=true -p:NativeMultilingualEnabled=false
dotnet test native/TheOrder.Tests/TheOrder.Tests.csproj -c Release --no-build --no-restore -p:Platform=x64
dotnet publish native/TheOrder.WinUI/TheOrder.WinUI.csproj -c Release -p:Platform=x64 -p:NativeProductionCandidate=true -p:NativeMultilingualEnabled=false --no-restore -o .artifacts/publish-1.0.2
pwsh -File native/scripts/package-native-release.ps1 -PublishedDirectory .artifacts/publish-1.0.2 -OutputDirectory .artifacts/package-1.0.2
pwsh -File native/scripts/package-native-installer.ps1 -CandidatePayload .artifacts/package-1.0.2 -InnoCompiler '<已核对的 Inno Setup 6.7.3>/ISCC.exe' -OutputDirectory .artifacts/installer-1.0.2
```

输出目录必须不存在且位于 `.artifacts/`，脚本拒绝覆盖历史候选。运行依赖、CRT、第三方许可、逐文件 SHA-256 清单与来源身份保留。普通启动会打开用户正式资料；代理验证只能使用新建的合成 Test，不能以发布构建标记替代真实资料授权。

差量发布需将 `The-Order-<version>-Setup-x64.exe.blocks.json` 和 Setup 同时上传到该正式发行；打包脚本已用 `TheOrder.UpdateTool` 自动生成，校验清单同时收录它。工具没有额外 NuGet 包，使用既定 SDK 和锁文件；`TheOrder.slnx` 包含它。只有 GitHub asset 的 SHA-256 digest、大小、固定仓库／标签／名称均有效时才启用这个可选附件；后台检查不下载块表。缺少或异常附件保持完整安装包兼容。

块表 schema 1、`gear-v1-16k-64k-128k-sha256` 内容边界，块大小 16–128 KiB（尾块除外），平均约 64 KiB；全包最多 512 MiB，块表最多 4 MiB。旧 `update-base.bin` 必须来自当前托管安装、同一应用和运行版本，读取时锁住并检查普通路径；不会从个人资料、其他应用或远端取一个“旧版本”。比较支持任意较旧版本，复用块在复制时再核对 SHA-256。最多 128 个合并 Range 请求、每个最多 4 MiB，包含块表的预计下载量小于全包 80% 才走差量；其余直接全包。HTTP 必须返回 206、准确 Content-Range／长度、认可的 HTTPS 附件源；最终合成包仍以正式 Setup 的整个 SHA-256、MZ、PE 身份和版本重新验证。不接受远程脚本、补丁指令或本地运行文件的就地修改。

Inno `SolidCompression=no` 为每个文件建立独立压缩流，使未变内容可以复用；`[Files]` 的 `{srcexe}` 外部复制仅将当前安装包放到 `{app}/update-base.bin`，普通卸载记录管理该单份缓存，安装前拒绝链接缓存路径。更新工作进程保持现有保存／退出／再校验／安装流程。缓存缺失、异常、网络 Range 不支持或差量失败时重新截断本次临时文件，只回退一次完整下载；取消停止两条路径。新旧整包与临时合成仍需要磁盘空间，差量不等于免安装器或免安全校验。

安装器的每个程序／入口／许可文件条目由已验证载荷清单生成，`PayloadFileNeedsUpdate` 比较目标文件与编译进去的 SHA-256；相同则不解压复制，保留文件时间，缺项或内容不同才写入。预安装检查所有预期目标路径及父目录，拒绝链接；匹配时不缓存旧判断，防止先前结果掩盖后来的变化。通常的卸载日志继续追加，已有未变文件保留旧卸载记录；不会删除不归当前清单的额外文件或游戏。`test-native-selective-install.ps1` 只安装几份虚构文本到新建 `.artifacts` 目录，不写系统卸载注册信息、快捷方式或真实应用，验证跳过／替换／新增／重复／修复／缓存复制／链接拒绝。

相关测试过滤器：`FullyQualifiedName~AppUpdate|FullyQualifiedName~DifferentialUpdate|FullyQualifiedName~WindowsCompatibility`。发布工具 `compare <old Setup> <new Setup> <old version> <new version> <新的输出>` 只读两个本机构建，按实际变化重建并核对字节，不安装或联网；可用于记录下载量，不把同版本内部构建比较冒充真实发行升级。

`THE_ORDER_NATIVE_COMPATIBILITY_TEST=win10|native-caption|mica-fault` 仅在显式 Test profile 且 `THE_ORDER_NATIVE_VISUAL_TEST=1` 时有效，用于模拟缺失 Mica、自定义标题栏及 Mica 附加失败；证据仍报告实际宿主版本。该开关不改变真实 OS，也不是 Win10 验收。

程序更新使用独立的无鉴权 GitHub transport，固定 `Ph-Rank/The-Order` 的 latest 正式发行。优先使用附件的 `digest`，缺失时仅读取同一发行的 `SHA256SUMS.txt`；下载只允许 GitHub 官方附件重定向，不能由 API 改仓库或命令。`native/scripts/update-worker.ps1` 随应用及清单分发，使用系统 Windows PowerShell 5.1；准备前、原实例退出后都校验附件，原实例正常保存完成后才发出独立的命名事件提交信号。每次重试使用新 ticket／事件，拒绝旧 ready 结果。应用内关闭沿用原有保存／会话收尾，不终止游戏，不写卸载清理选项。

`THE_ORDER_NATIVE_UPDATE_FIXTURE=1` 只在 Test 且 `THE_ORDER_NATIVE_VISUAL_TEST=1` 时选择合成更新说明；没有可下载执行文件。平台安装器拒绝 Test／Development。`test-native-update-worker.ps1 -Plan <合成 Test 的校验 plan> -OutputDirectory <新的 .artifacts 路径>` 仅校验；`test-native-update-handoff.ps1` 使用真实命名事件和合成父进程探针，但始终携带 `-ValidateOnly -ProbeHandoff`，不安装或重启日用程序。普通交互安装、真实资料升级及 Win10 实机仍未验证，不能把这些探针算作实际用户升级成功。

Production 初始化后延迟 15 秒进行第一次静默检查，之后每 12 小时再检查。DispatcherTimer 在窗口生命周期结束时停止；Test／Development 不启动自动计时器。自动检查只读公开发行，无安装包下载、弹窗或声音，失败保持已有状态；手动检查可取消并取代后台请求，迟到结果不能覆盖新结果。设置图标提示点与 `HasUpdate` 使用同一状态，辅助功能名称包含新版本提示。主要改动在本地提取发布者的“更新内容”等段落，最多六条；忽略代码块、不执行 HTML，完整文本仍可展开。

差量改进本轮证据在 `.artifacts/delta-update-20261011/`，替代 `.artifacts/self-update-20261011/` 的候选包；前轮更新与兼容证据保留。对应最新 Setup、源码和验证摘要见 BRIEF／DEVELOPMENT 顶部。

更新依据：[GitHub latest 正式发行与 digest](https://docs.github.com/en/rest/releases/releases#get-the-latest-release)、[Inno Setup 安装参数](https://jrsoftware.org/ishelp/topic_setupcmdline.htm)。

差量设计参考：[Microsoft 内容块比较原理](https://learn.microsoft.com/en-us/windows/msix/app-package-updates)、[Inno 独立／固实压缩](https://jrsoftware.org/ishelp/topic_setup_solidcompression.htm)、[外部文件复制](https://jrsoftware.org/ishelp/topic_filessection.htm)。本项目使用自己的版本化块表与普通安装器，并未迁移为 MSIX。

按文件安装参考：[Inno Check 条件](https://jrsoftware.org/ishelp/topic_scriptcheck.htm)、[文件 SHA-256](https://jrsoftware.org/ishelp/topic_isxfunc_getsha256offile.htm)。

Win10 阶段证据保存在 `.artifacts/win10-compat-20261011/`；该阶段未进行真实 API、用户安装升级、真实游戏、Production 或长稳。后续认证阶段的真实匿名及获准 OAuth 只读证据独立存于 `.artifacts/auth-sync-20261011/`。没有 Win10 实机，不把当前 Win11 与缺失能力模拟算作 Win10 通过。

依据：[Windows App SDK 系统下限](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/)、[Mica 能力检查](https://learn.microsoft.com/en-us/windows/apps/develop/ui/system-backdrops)、[标题栏兼容说明](https://learn.microsoft.com/en-us/windows/apps/develop/title-bar)、[桌面文件选择器](https://learn.microsoft.com/en-us/windows/apps/develop/files/using-file-folder-pickers)、[旧 WinML 与 ONNX 版本](https://learn.microsoft.com/en-us/windows/ai/windows-ml/onnx-versions)、[Windows ML 自包含部署](https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/distributing-your-app)、[.NET Windows 支持范围](https://learn.microsoft.com/en-us/dotnet/core/install/windows)。技术可运行下限不等同于微软对每个 Windows 10 发行通道的维护承诺。
