# Windows 发布包

最新版本为 **0.1.2（预览）**。桌面包使用共享 R2T2 核心库运行本机引擎，独立服务器 ZIP 继续提供 HTTP 服务。本地输出在 `dist/<版本>/`，不纳入 Git；公开发布内容以 [GitHub Releases](https://github.com/hongjiapeng/VoxType/releases) 为准。

| 文件 | 用途 |
|---|---|
| `VoxType-0.1.2-win-x64-Setup.exe` | 当前用户安装，无需管理员权限；可选桌面快捷方式 |
| `VoxType-0.1.2-win-x64-Portable.zip` | 完整解压到本地目录后运行 `VoxType.App.exe` |
| `VoxType-R2T2-Server-0.1.2-win-x64.zip` | 可选的自包含 HTTP 服务与部署脚本，普通本机用户无需下载 |
| `SHA256SUMS.txt` | 核对安装包、两个 ZIP 和构建信息的 SHA-256 |
| `build-info.json` | 构建时的源提交、工作区状态和 Release 测试计数 |

桌面安装程序与便携 ZIP 包含相同的自包含 WinUI 应用、.NET / Windows App SDK、Whisper CPU / Vulkan 原生库和 `VoxType.R2T2.Core` 共享类库。R2T2 本机模式直接调用类库，只启动 llama-server；桌面包不含 `R2T2.Server`、ASP.NET Core 和部署脚本。模型及 llama 运行时由用户在设置中准备。

独立服务器 ZIP 包含 `R2T2.Server.exe`、共享类库、.NET / ASP.NET Core 运行时、准备／启动脚本、模板、说明与许可。它无需 .NET SDK，部署脚本需要 PowerShell 7。构建分别使用全新目录，并拒绝桌面包混入服务依赖。不要只复制单个 EXE；任何包均不含模型、密钥、个人配置或录音。

Whisper 仍要求 CPU 支持 AVX / AVX2 / FMA / F16C 和 x64 Visual C++ 2022 Redistributable；Vulkan 还要求兼容显卡驱动。修复后的本机 R2T2 入口在应用内准备资源、等待健康检查及停止服务，不要求 PowerShell 7、.NET SDK 或另装 ASP.NET Core Runtime。

包未进行 Authenticode 签名，Windows 可能提示发布者未验证；没有附带自签名证书安装脚本。没有配置自动更新、开机自启动或 Store 身份。卸载与删除便携目录均保留 `%LOCALAPPDATA%\VoxType` 下的数据。MSIX 开发版的数据在其虚拟化目录，不会自动迁移。

## 本地构建

需要 Windows 11 x64、仓库要求的 .NET SDK、Windows / WinUI 构建工具、PowerShell 7 和 Inno Setup 6。选择未占用的版本号；以下使用尚未发布的 0.1.2 举例：

```powershell
./scripts/build-release.ps1 -Version 0.1.2
```

脚本始终执行 Release 测试；只有测试报告显示实际执行且全部通过时才发布应用。每次使用新的 `artifacts/publish/<版本>-<GUID>/`，避免旧文件混入。若已有同名安装包、ZIP 或校验清单，会拒绝覆盖。

`Portable.pubxml` 启用 `WindowsPackageType=None`、`SelfContained=true` 与 `WindowsAppSDKSelfContained=true`；关闭裁剪、单文件打包、ReadyToRun 和调试符号，保留 WinUI 原生文件。正常 Debug / MSIX 开发运行方式不变。[微软的自包含部署说明](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)解释了 Windows App SDK 的随包部署方式。

输出包含 `README.txt`，分别检查桌面依赖分离和服务器运行时／部署资源。TRX 报告位于 `artifacts/test-results/<版本>-<GUID>/release.trx`；`build-info.json` 同时随应用、服务器包与发布附件保存。校验清单覆盖安装包、两个 ZIP 和构建信息。

## 检查与交付

```powershell
Get-FileHash ./dist/0.1.2/VoxType-0.1.2-win-x64-Setup.exe -Algorithm SHA256
Get-FileHash ./dist/0.1.2/VoxType-0.1.2-win-x64-Portable.zip -Algorithm SHA256
Get-Content ./dist/0.1.2/SHA256SUMS.txt
```

交付前确认解压后的应用能打开设置、从托盘退出，并检查安装／卸载流程；真实语音输入按[手工验收表](manual-acceptance.md)检查。开发机器运行成功不能代替无开发环境的干净机器验收。

构建脚本只创建本地文件，不创建或推送 Git tag，也不创建 GitHub Release。

## 发布到 GitHub

参考流程来自 SlidePace 的发布 skill，VoxType 使用自己的仓库和脚本。需要已登录且能写入 `hongjiapeng/VoxType` 的 GitHub CLI，以及 Git 推送权限。

先把预期代码合并到 `master` 并提交，更新源版本与 `docs/releases/v<版本>.md`，再从干净提交构建。发布已有产物时必须与当前提交一致，不能把脏工作区的预览产物当作正式附件。

```powershell
./scripts/release.ps1 0.1.2 -DryRun
./scripts/release.ps1 0.1.2
```

发布脚本检查源版本、仓库、分支、标签是否存在、测试计数与产物 SHA-256。没有产物时先执行本地构建。确认后推送 `master`，创建并推送 annotated 版本标签，把五个附件上传到草稿，核对附件名称与大小，再公开为 **prerelease**。本项目采用本地构建上传。

仅推送了 tag 不表示发布完成；只有 Release 的 `isDraft=false` 且五个附件齐全时才算完成。上传失败会保留草稿供排查。
