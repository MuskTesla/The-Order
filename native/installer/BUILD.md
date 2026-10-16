# The Order 通道统一安装器

版本来自 native/Directory.Build.props。正式版1.1.1与Beta1.2.0-beta.2共享原安装ID及程序入口，Beta标记只改变版本、说明与更新通道。
以固定SDK10.0.401、锁定依赖、Release x64、NativeProductionCandidate=true发布，再用 package-native-release.ps1、package-native-installer.ps1 打包。
安装器编译器固定为官方Inno6.7.3、现有VC C++/Windows SDK26100；脚本验证工具哈希和所有载荷哈希。
`update-channel.json` 必须与Tag版本一致，GitHub附件摘要与下载字节校验通过后才允许跨通道。

只按目标程序目录判断运行实例；不强杀任何实例，不改资料或迁移Profile。禁止安装到资料命名空间、链接、磁盘根或非受管非空目录。
新卸载器始终保留用户资料，仅清除安装目录里的active-profile.json选择。旧1.1.0的手动卸载行为属于历史版本，不能用于通道回退。
历史 test-native-installer.ps1 仅供无任何现有安装的独立环境，不应在本机日用安装上执行。本轮用独立实验AppID、无注册/快捷方式、固定目标根验证准确载荷，并明确不等于干净OS原身份测试。

详细用户操作、一次旧beta.1过渡、快照边界、回退和中断修复见 docs/release/<版本>/README.md、ROLLBACK.md。
