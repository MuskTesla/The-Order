# 1.0.1 对应源码与构建

维护源码为 `native/`；根目录 Electron 实现作为历史兼容代码保留，本版发布原生应用。源码包包括锁文件、构建脚本、数据索引、模型及许可证，不包含个人资料、图片缓存、游戏文件、工具安装或生成包。

构建环境：Windows 11 x64、.NET 10 SDK、Visual Studio C++ x64 构建工具、Windows SDK、PowerShell 7。锁文件固定依赖；不要顺手升级或重建索引。首次构建需要从 NuGet 获取锁定依赖。

```powershell
dotnet restore native/TheOrder.slnx --locked-mode
dotnet build native/TheOrder.slnx -c Release --no-restore -p:NativeProductionCandidate=true -p:NativeMultilingualEnabled=false
dotnet test native/TheOrder.Tests/TheOrder.Tests.csproj -c Release --no-build --no-restore
dotnet publish native/TheOrder.WinUI/TheOrder.WinUI.csproj -c Release -p:Platform=x64 -p:NativeProductionCandidate=true -p:NativeMultilingualEnabled=false --no-restore -o .artifacts/publish-1.0.1
pwsh -File native/scripts/package-native-release.ps1 -PublishedDirectory .artifacts/publish-1.0.1 -OutputDirectory .artifacts/package-1.0.1
```

打包输出必须为不存在的 `.artifacts/` 子目录；脚本拒绝覆盖历史候选。`NativeProductionCandidate` 是发布构建标记，不能替代真实资料访问授权。验证使用 `Start-Test.cmd` 或指定隔离 Test 入口，不执行普通正式启动入口，不运行真实游戏。直接运行 EXE 的正式资料路径仍需要显式授权参数；普通发行入口仅替最终用户传入该参数。

每次打包生成逐文件 SHA-256 的 `files.json`、版本与构建来源的 `release-identity.json`、固定依赖与许可对应表 `dependencies.json`，以及运行依赖的许可文本和元数据。完整源码快照与源码包另行冻结并逐字节核对，包含本目录的使用说明与构建文档；HEAD 加 dirty 清单共同绑定源状态，不能仅凭 HEAD 或一个程序集标识声称覆盖全部继承成果。保留全部失败证据，代码测试与某次显示确认不能扩大为未执行的宿主或正式资料验收。


安装包额外需要 Inno Setup 6.7.3，详见 native/installer/BUILD.md。本版使用固定 .NET SDK 10.0.401，依赖版本由锁文件约束。先生成完整程序目录，再用 package-native-installer.ps1 -CandidatePayload 构建 Setup.exe。
