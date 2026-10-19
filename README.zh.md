<div align="center">

**中文** · [English](./README.md)

<img src="docs/images/logo.svg" alt="VoxType" width="88" />

# VoxType

**按住快捷键说话，松开，让文字进入当前输入框。**

![Windows 11 x64](https://img.shields.io/badge/Windows_11-x64-0078D4?style=flat-square&logo=windows&logoColor=white)
![WinUI 3](https://img.shields.io/badge/UI-WinUI_3-0078D4?style=flat-square)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet&logoColor=white)
![Preview](https://img.shields.io/badge/status-preview-e6a23c?style=flat-square)
[![下载](https://img.shields.io/badge/下载-GitHub_Releases-2ea44f?style=flat-square)](https://github.com/hongjiapeng/VoxType/releases)

[快速开始](#快速开始) · [识别服务](#选择识别服务) · [使用详解](docs/usage.md) · [构建发布](docs/releases.md)

</div>

VoxType 是 Windows 11 上的全局语音输入工具。先把光标放进编辑器、聊天窗口或表单，按住 **Ctrl + Shift + Space** 说话，松开后识别并输入文字；按 **Esc** 取消本次会话。

它常驻托盘，不需要 VoxType 账户。你可以在本机离线运行 Whisper，也可以连接本机／局域网 R2T2，或自己的 OpenAI 兼容转写服务。

## 产品预览

### 边说边看，松开输入

按住快捷键说话，R2T2 在浮窗里更新临时转录；松开后，完整录音会再次识别并把最终文字输入当前应用。

<p align="center">
  <img src="docs/screenshots/live-transcription-dark.png" alt="VoxType 按住快捷键录音时，深色语音浮窗显示实时转录文字" width="320" />
</p>

临时文字可能随上下文修正。Whisper 本地和 OpenAI Compatible 在松开后识别；录音期间预览由 R2T2 提供。图中文字为示例，显示在应用实际使用的浮窗界面中。实际体验与验证范围见[验证与当前边界](#验证与当前边界)。

### 随手拖动，需要时常驻

按住手柄、波形或文字直接拖动；浮窗记住位置，并尽量避让输入区域。开启「固定在桌面」后，空闲时收成小麦克风入口，点击才开始录音。

<p align="center">
  <img src="docs/screenshots/idle-entry-dark.png" alt="深色常驻入口，包含拖动手柄、麦克风和更多菜单" width="120" />
</p>

## 功能概览

| 功能 | 说明 |
|---|---|
| **全局按住说话** | 默认 Ctrl + Shift + Space，支持自定义组合；Esc 取消 |
| **识别服务** | Whisper 本地、R2T2 本机／局域网、OpenAI Compatible |
| **录音期间预览** | R2T2 在说话时更新临时文字，松开后用完整录音校正并输入 |
| **轻量浮窗** | 波形、进度和失败重试；直接拖拽、位置记忆、自动避让、可选常驻入口 |
| **本地推理设备** | Whisper 与本机 R2T2 分别选择 CPU / Vulkan |
| **模型管理** | Whisper base 下载、导入、完整性校验及删除；R2T2 提供资源准备与启动入口 |
| **文本交付** | 尝试恢复原输入窗口并粘贴，无法自动发送时保留结果供手动复制 |

## 快速开始

1. 获取 Windows x64 [发布包](https://github.com/hongjiapeng/VoxType/releases/tag/v0.1.2)：运行 `VoxType-0.1.2-win-x64-Setup.exe`，或完整解压便携 ZIP。
2. 启动 VoxType。便携版双击 `VoxType.App.exe` 后在托盘打开设置，也可运行 `VoxType.App.exe --settings`。
3. 在「语音识别」中选一个服务。首次使用推荐 **Whisper 本地 → 下载模型 → 使用此模型**，然后保存设置。
4. 聚焦目标应用输入框，按住 **Ctrl + Shift + Space** 说话，松开后等待文字输入。
5. 要取消时按 **Esc**。关闭设置窗口后程序继续在托盘运行；从托盘选择「退出」才会结束。

安装包按用户安装到 `%LOCALAPPDATA%\Programs\VoxType`，无需管理员权限。便携版需保留解压后的全部文件。应用随包携带 .NET 与 Windows App SDK 运行时，语音模型需另行下载或导入。

## 选择识别服务

本机引擎在回环地址运行 llama-server。要连接其他设备，请部署独立的 R2T2 Server 包。

| 服务 | 适合 | 配置 |
|---|---|---|
| **Whisper 本地** | 离线使用，让音频留在本机 | 下载／导入多语言 Whisper **base GGML**，约 148 MB；选择 CPU 或 Vulkan |
| **R2T2 本机** | 离线推理、录音期间预览 | 准备约 1.63 GiB 的 GGUF 模型，在设置中启用 CPU / Vulkan 引擎 |
| **R2T2 服务器** | 连接另一台设备或独立部署的服务 | 填写地址、模型与密钥；部署者使用独立服务器 ZIP |
| **OpenAI Compatible** | 已有兼容语音转写接口 | 填写 Base URL、模型与可选 API Key；接口支持 `/v1/audio/transcriptions` |

**Whisper：** 设置页支持官方 Hugging Face 或明确标注的 HF-Mirror 镜像，下载与导入会校验大小和 SHA-256。当前支持多语言 base GGML，不支持 `base.en` 或 GGUF。更改已加载的 Whisper 推理设备后，需从托盘退出并重启。

**R2T2：** 本机模式直接调用共享类库，只运行一个后台 llama-server 推理进程；服务器模式连接独立 HTTP 适配服务，部署包单独提供。旧服务器配置保留原语义，需主动选择“R2T2 本机”才使用内置引擎。实时预览会反复识别当前录音快照，临时文字可能变化；松开后才提交最终文字，不承诺固定延迟。部署与局域网鉴权见 [R2T2 指南](docs/r2t2-server.md)。

**兼容服务：** 普通 OpenAI Compatible 的远程地址要求 HTTPS，HTTP 仅允许回环地址。R2T2 的局域网模式有单独的地址校验与鉴权要求。

完整的下载行为、设备选择与会话规则见[使用与配置详解](docs/usage.md)。

## 运行要求

| 要求 | 说明 |
|---|---|
| 系统 | Windows 11 x64；麦克风可用且已允许桌面应用访问 |
| 桌面应用 | 发布包内含 .NET 10 与 Windows App SDK，无需单独安装这两项运行时 |
| Whisper 本地 | CPU 支持 AVX、AVX2、FMA、F16C；需要 x64 Visual C++ 2022 Redistributable |
| Vulkan | 额外需要兼容显卡和驱动；不支持时手动改为 CPU |
| 本机 R2T2 准备／启动 | 模型下载空间与数 GiB 可用内存；本机模式无需 HTTP 适配服务、PowerShell 或 .NET SDK |

当前提供未签名的预览包，Windows 可能提示发布者未验证。应用不会安装信任证书或修改系统安全策略。

## 常见问题

<details>
<summary><strong>说完后没有自动输入文字</strong></summary>

先聚焦输入框，再按快捷键。目标关闭、最小化、焦点变化、权限更高或不接受粘贴时，自动发送可能失败；可以在设置中复制最近一次结果后手动粘贴。自定义快捷键请避开系统和编辑器已占用的组合。

</details>

<details>
<summary><strong>听不到声音、录音太短或模型加载失败</strong></summary>

检查 Windows 麦克风权限和默认录音设备。一次录音至少 250 毫秒、最长 60 秒。Whisper 使用前先完成模型下载／导入；Vulkan 加载失败时切换 CPU 并重启。资源不会在启动或录音时自动下载。

</details>

<details>
<summary><strong>R2T2 准备或启动失败</strong></summary>

先点击“准备本机资源”，等待校验完成后启动；进度和错误显示在设置页，可停止或取消。默认端口为 8272 / 8273；升级前请在旧服务窗口按 Ctrl+C。Vulkan 启动失败时可停止后选择 cpu 重试。准备资源会复用已校验文件。原 v0.1.0 包存在现场编译和编译配置缺失问题，需使用修复后的完整构建。详见 [R2T2 指南](docs/r2t2-server.md)。

</details>

<details>
<summary><strong>配置和模型在哪里，卸载会删除吗？</strong></summary>

安装版与便携版使用 `%LOCALAPPDATA%\VoxType`。卸载或删除便携目录会保留设置和模型。旧 MSIX 开发版使用包的虚拟化目录，请以设置页显示的实际路径为准；切换分发方式不会自动迁移模型和配置。

</details>

## 数据与隐私

- 录音和最近识别文本只保存在内存，VoxType 不创建录音文件或转录历史。
- Whisper 在本机推理；R2T2 与在线识别把音频发往你配置的服务。只有主动准备／下载资源时才下载模型。
- API Key 使用 Windows DPAPI CurrentUser 加密，不以明文保存；这不防御同用户身份运行的恶意程序。
- 自动输入通过剪贴板与 Ctrl+V 实现。程序尝试恢复原剪贴板、保留用户新复制的内容；复杂格式、占用和慢应用可能影响恢复。

路径、剪贴板行为与服务限制见[使用详解](docs/usage.md)。

## 设置与个性化

<details>
<summary><strong>查看设置工作台</strong></summary>

配置识别服务、快捷键、深浅主题与浮窗常驻方式。

<p align="center">
  <img src="docs/screenshots/settings.png" alt="VoxType 设置窗口，显示使用指引、外观和语音浮窗选项" width="900" />
</p>

</details>

## 开发与发布

开发需要 .NET 10 SDK（约束见 [`global.json`](global.json)）、Visual Studio 的 WinUI 开发工具、Windows SDK 和 WinApp CLI。

```powershell
dotnet restore VoxType.slnx -p:Platform=x64
dotnet build VoxType.slnx -p:Platform=x64
dotnet test tests/VoxType.Tests/VoxType.Tests.csproj -p:Platform=x64
winapp run src/VoxType.App/VoxType.App.csproj --arch x64 -p Platform=x64 --args '--settings'
```

构建安装包与便携 ZIP（额外需要 Inno Setup 6 和 PowerShell 7）：

```powershell
./scripts/build-release.ps1 -Version 0.1.2
```

脚本执行 Release 测试、发布自包含的 x64 应用、检查必要文件，并生成安装包、ZIP 和 SHA-256 清单。详见[发布说明](docs/releases.md)。

## 验证与当前边界

这是首个预览版本。已完成自动回归、浮窗界面检查及部分真实 Whisper / R2T2 模型检查；不同应用中的真实麦克风、快捷键冲突和焦点交付仍需按设备验收。自动测试通过不等同于所有应用都能可靠粘贴。

- [完整验证记录](docs/verification.md)
- [手工验收表](docs/manual-acceptance.md)
- [浮窗交互与验证范围](docs/overlay-interaction.md)
- [R2T2 实际模型验证](docs/r2t2-verification.md)

## 致谢

VoxType 的按住说话和直接输入文字体验，部分受到 macOS 菜单栏语音输入工具 [VoiceInput](https://github.com/yetone/voice-input-dist) 的启发。这里致谢的是产品交互灵感；VoxType 使用独立实现。

## 许可与第三方组件

第三方组件与模型来源见[许可说明](docs/third-party-notices.md)。项目自身的开源许可证尚未选定。
