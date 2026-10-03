# 开发与维护

## 项目边界

这是 The Order 的长期源码仓库。Windows 正式工作目录为 `D:\Projects\The-Order`；Claude Code、终端和编辑器都从这里开始工作。

维护 `src/`、`electron/`、`public/`、`data/`、`scripts/`、`tests/`、项目配置、锁文件及许可证。`node_modules/`、`dist/`、`dist-electron/`、`release/` 和 `.artifacts/` 均为本机生成内容，不提交 Git。

历史发布 ZIP、界面截图、验证报告、旧交付说明和候选源码保留在原工作区中，不作为本仓库的开发输入。

## 技术栈与资源

- Electron 桌面主进程与 preload：`electron/`，TypeScript，esbuild 生成 CommonJS 文件。
- Vue 3、KunUI、Tailwind CSS 与 Vite：`src/`，浏览器入口为 `index.html`。
- `package-lock.json` 固定依赖版本，使用 Node.js 24 与 npm 安装。
- `public/` 保存应用图标及浏览器预览资料，Vite 将其复制到 `dist/`。
- `data/title-index.json.gz` 是运行时所需的公开标题索引，连同授权说明一起版本管理。
- Python 仅用于手动重建标题索引，普通构建和运行不需要 Python。重建命令为 `python scripts/build-title-index.py <vn_mapper.db>`；外部数据库不提交。

## 安装、启动与构建

在仓库根目录执行：

```powershell
npm ci
npm run dev
```

`npm run dev` 同时启动 Vite 与 Electron；`npm run dev:ui` 仅启动浏览器预览。

```powershell
npm test
npm run build
npm start
npm run package
```

`npm start` 使用已经构建的 Electron 与前端文件，首次执行前需运行 `npm run build`。`npm run package` 包含类型检查和构建，生成 `release\win-unpacked\The Order.exe`；运行时保留完整 `win-unpacked` 目录。

## 应用数据与隔离验证

正式应用继续使用 `%APPDATA%\KunGalManager` 保存书架、设置及游玩记录。搬迁源码不会改变这一路径。API 密钥由 Windows 当前用户加密保存，不属于项目源码。

日常 `npm run dev`、`npm start` 和正式 EXE 默认会使用正式应用数据。验证、调试或尝试修改时，先选择独立目录：

```powershell
$env:KGM_DATA_DIR = Join-Path $env:TEMP 'the-order-development-profile'
npm run dev
# 完成并退出程序后恢复默认数据目录：
Remove-Item Env:KGM_DATA_DIR
```

不要把真实 API 密钥写入源码、命令参数或提交。需要连接服务时，在应用设置中填写。`.env`、加密凭据、个人 profile 和本地备份已由 `.gitignore` 排除。

当前第三轮候选使用独立打包目录及虚构书架，启动命令为：

```powershell
& 'D:\Projects\The-Order\scripts\start-iteration-candidate.ps1' -Iteration 03
```

程序位于 `.artifacts/iteration-03/candidate/win-unpacked/The Order.exe`，资料固定为 `.artifacts/iteration-03/trial-profile`。启动脚本保留已有试用记录，不复制正式书架或上一轮试用资料。`synthetic-photos/` 内有原创合成 PNG 可用于本地封面选择。具体提交、截图和验证见 [迭代记录](docs/ai/ITERATION.md#第三轮全局封面偏好本地图片与栏目装饰2026-10-03)。

本地封面仅在桌面版通过文件选择器读取静态 PNG/JPG；保存有界 JPEG 副本，不引用原文件路径，也不上传。照片副本随书架 JSON 备份保存，全局展示偏好属于设置，不在书架备份中。新程序兼容旧 version 1 备份；旧候选可能不认识新增照片或“跟随全局”字段，回退应使用旧版本对应的备份，不能视作无损逆向迁移。

## 回归验证

```powershell
npm test
npm run test:desktop
npm run package
npm run test:packaged
```

桌面回归使用临时独立 profile、合成游戏和模拟 API 响应。清除旧的 `KGM_TEST_EXECUTABLE` 环境变量，确保测试启动当前项目构建的程序。界面截图与测试报告写入 `.artifacts/`。

`tests/private-library-compat.mjs` 是可选的个人书架兼容检查工具，不属于默认测试链。必须显式提供 `--allow-private-profile --source-data-dir <绝对数据目录>` 才会读取所选数据，清除复制设置中的密钥，并在独立临时目录中只读验证。普通迁移与开发验证不需要这个工具，也不会运行它。

## Git

初始基线记录迁移后的可构建源码。运行前后的构建、截图、日志和本机凭据不会进入基线。使用个人提交身份时，仅在此仓库配置：

```powershell
git config user.name "你的姓名"
git config user.email "你的提交邮箱"
git status
```

每次提交前检查暂存内容；不要提交正式应用数据，也不要把发布目录作为新的源码目录。
