# The Order 1.2.0-beta.1 · Public Beta

这是 ACGN 跨媒体浏览的公开测试版，不是稳定版。建议在独立 Beta 书架中体验，重要资料另行备份。

## 安装与隔离

下载 `The-Order-1.2.0-beta.1-Setup-x64.exe`，先核对同页 `SHA256SUMS.txt`。运行安装后，从 **The Order Beta** 入口打开。
需要 64 位 Windows 10 1809 / 17763 或以上；运行时随包提供，无需 Node.js、.NET SDK、开发脚本或测试凭据。安装包暂未签名，Windows 可能提示未知发布者；不要关闭安全防护。

- 默认程序：`%LOCALAPPDATA%\Programs\The Order Beta`。
- 独立资料：`%LOCALAPPDATA%\TheOrder-Beta\Profiles\Beta`。
- 与稳定版使用不同安装 ID、快捷方式、任务栏 ID 和 Profile。Beta 直接启动也不会打开稳定版 Profile。
- 从空书架开始，不自动复制、迁移或读取稳定版资料与登录凭据。NextMoe/Bangumi 账号需在 Beta 中自行连接；匿名浏览支持的来源无需密钥。
- Beta 只从 GitHub Pre-release 手动更新。稳定版仍只接收正式版更新。
- 当前 Beta 与稳定版的用户资料格式不可互换。**不要直接复制 Profile 文件夹或凭据文件。**

## 备份、退出与回退

修改重要资料前，在设置中导出书架备份并自行妥善保存；备份可能包含个人名称、笔记和本地路径，不能公开上传。
本轮没有验证从稳定版自动导入资料，不提供自动迁移。无需迁移即可在独立空库中重新导入选定游戏目录。

关闭窗口可能隐藏到托盘；结束测试请从 Beta 托盘选择“退出”。在 Windows 应用列表卸载 **The Order Beta**；默认保留 Beta 资料，游戏文件不会删除。
回退只需退出 Beta，再打开原有 **The Order** 稳定版。稳定版程序和资料无需回写或恢复。旧版 Beta 的程序回退不应覆盖较新的资料；先保留备份，不将 Beta Profile 交给稳定版。

## 测试限制与反馈

详见 CHANGELOG.md 和 HUMAN-CHECKS.md。外部平台入口不保证当前地区、版权或订阅条件下可播放。无播放链接不代表没有正版资源。

[GitHub Issues](https://github.com/Ph-Rank/The-Order/issues/new/choose)：请提供完整 Beta 版本、Windows 版本、复现步骤及预期/实际行为。
截图和脱敏日志可选。**不要上传 Token、账户凭据、私密文件路径、完整 Profile 或未经处理的私人数据。**
