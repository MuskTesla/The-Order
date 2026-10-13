# The Order 1.1.0 安装器与 GUI 入口

当前安装器用于新构建的 1.1.0 兼容候选，系统门槛为 Windows 10 1809 x64（17763），不再包装旧 ZIP。维护源码仍在 `claude-recognition-rnd/native`；安装器补充源包不替代完整应用源码。许可为 AGPL-3.0-or-later，图标原美术权利不随源码许可改变。

## 构建

先用固定 .NET SDK 按锁文件构建发布目录，再运行 `native/scripts/package-native-release.ps1` 生成包含 `files.json` 的完整候选包。之后在仓库根目录使用 PowerShell 7：

```powershell
& native/scripts/package-native-installer.ps1 `
  -CandidatePayload '<仓库根>\.artifacts\新候选完整目录' `
  -InnoCompiler '<隔离 Inno Setup 目录>\ISCC.exe' `
  -OutputDirectory '<仓库根>\.artifacts\新的安装器输出目录'
```

脚本检查 1.1.0.0 程序版本、全部载荷文件及编译器哈希。需要官方 Inno Setup 6.7.3、现有 Visual Studio C++ x64 Build Tools 和 Windows SDK 10.0.26100.0；编译 SDK 不代表用户运行下限。官方 Inno Setup 安装器 SHA-256 为 `9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732`，准备工具时仍需核对官方签名。GUI 入口静态链接 VC CRT，使用 asInvoker。

输出为 Setup.exe、安装器补充源码 ZIP 和 INSTALLER-SHA256SUMS.txt。本轮只生成本地候选，未上传或发布。

## 行为和检查范围

安装仅面向当前用户，默认 `%LOCALAPPDATA%\Programs\The Order`。桌面和开始菜单快捷方式按系统主题选择黑色／白色图标。程序运行期间和下次启动时同步；完全退出期间不额外运行主题监听进程。

升级和修复保留使用数据。交互卸载询问是否删除默认 Production 使用数据，默认“否”；静默卸载保留数据。选择删除只清理 `%LOCALAPPDATA%\TheOrder\Profiles\Production`，拒绝跟随目录链接，不删除真实游戏、手动导出备份及自定义 Test／Development 目录。运行中拒绝安装／卸载。

`native/scripts/test-native-uninstall-data.ps1 -InnoCompiler <ISCC.exe>` 可在新的合成目录中编译实际清理函数，验证普通目录删除和链接边界。快捷方式变更用临时 .lnk 回归，不改用户桌面。本轮未在真实用户安装上试卸载。旧 `test-native-installer.ps1` 是固定 1.0.0 的历史端到端检查，不作为 1.1.0 已通过证据，也不能在已有用户安装上强行运行。真实 Win10 安装／升级／卸载未验证。

`TheOrder.exe --validate-only` 只检查程序完整性；`--test-profile <绝对路径>` 使用独立 Test。正常入口会打开用户正式资料，代理不默认运行。Setup 未签名，下载信誉提示仍可能出现；不能把编译和本地检查说成微软认证。
