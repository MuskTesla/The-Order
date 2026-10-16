# 构建与来源

以本 Tag 的完整维护源码为准。版本唯一来源 `native/Directory.Build.props`：产品版本 `1.2.0-beta.1`、Windows 数值文件版本 `1.2.0.1`。

使用固定 .NET SDK 10.0.401、锁定 NuGet 依赖、Visual Studio C++ 工具和 Windows SDK 10.0.26100。
先 `dotnet restore native/TheOrder.slnx --locked-mode`，再构建/发布 `TheOrder.WinUI` Release x64，传入 `-p:NativeProductionCandidate=true`。
该开关允许分发构建，但此预发布二进制仍强制默认 Beta Profile，不能进入 Production。
`native/scripts/package-native-release.ps1` 组装自包含运行时、CRT 和许可；`package-native-installer.ps1` 使用校验固定哈希的 Inno Setup 6.7.3 生成独立 Beta 安装器。

发布包不包含开发 SDK、测试 Profile、私人数据、密钥或 `node_modules`。主程序、GUI 入口、Setup、清单和 Tag 均应对应相同预发布版本。
代码及衍生数据依仓库 LICENSE、THIRD_PARTY_NOTICES 和包内各许可。游戏图像、远程数据库资料和播放器内容不属于应用源码许可。
