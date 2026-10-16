# 构建与来源

本Tag完整维护源码为准。版本唯一来源 `native/Directory.Build.props`：1.2.0-beta.3，文件版本1.2.0.3。
固定.NET SDK10.0.401、锁定NuGet、现有Visual Studio C++/Windows SDK26100、官方Inno6.7.3。
`dotnet restore native/TheOrder.WinUI/TheOrder.WinUI.csproj --locked-mode` 后，以Release x64和 `-p:NativeProductionCandidate=true` publish。
通过 `package-native-release.ps1` 和 `package-native-installer.ps1` 生成自包含分发、GUI入口、许可及 `update-channel.json`。
两种通道共享安装ID F7940B13-6814-42F7-AB71-69A9901D7DB3；`profileContract:1` 只供已验证的新通道包使用，旧beta.1不具备此能力。
分发不含SDK、测试Profile、凭据、node_modules或开发脚本；诊断入口仍限制Test资料。正式Profile写入只能由用户正常启动程序执行。
验证报告与Release附件SHA-256分别绑定最终源码及最终包；隔离安装实验用独立AppID、无卸载注册/快捷方式，不宣称替代干净OS或真实用户安装验证。
源码AGPL-3.0-or-later；数据、依赖和图像遵守各自许可。
