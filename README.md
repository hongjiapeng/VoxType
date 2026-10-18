# VoxType

Windows 11 x64 全局语音输入 MVP。默认按住 **Ctrl + Shift + Space** 说话，松开后识别并向原窗口发送文本；**ESC** 取消。启动常驻托盘，设置窗口关闭后隐藏，托盘“退出”释放资源并结束进程。

当前已实现应用、录音、在线/本地 Provider、剪贴板输入和加密设置。自动测试及打包结果见 [验证记录](docs/verification.md)。真实麦克风、焦点交互与在线/离线中文完整流程尚待验收，不能把 fake 测试视为真实语音输入通过。

语音浮窗支持拖动、位置记忆和输入区域避让；可在「设置 → 常规 → 语音浮窗」开启常驻麦克风入口。待机时不录音，点击才开始。使用说明和验证范围见 [语音浮窗](docs/overlay-interaction.md)。

## 开发运行

需要 Windows 11、x64 .NET 10 SDK、Windows SDK 和 WinApp CLI 0.7+。本项目锁定 .NET SDK 10.0.301，并允许同 feature band 的较新 patch。首次构建会恢复 NuGet 依赖。

```powershell
dotnet restore VoxType.slnx -p:Platform=x64
dotnet build VoxType.slnx -p:Platform=x64
dotnet test VoxType.slnx -p:Platform=x64
winapp run src/VoxType.App/VoxType.App.csproj --arch x64 --on local
```

通过托盘打开设置，或在首次启动时传入设置参数：

```powershell
winapp run src/VoxType.App/VoxType.App.csproj --arch x64 --on local --args '--settings'
```

修改程序后先从托盘退出旧进程，再重新运行，避免开发包内的文件被旧进程占用。重复激活会打开当前实例的设置。

## 配置识别

**R2T2 本机／局域网** 已支持 `Confucius4-R2T2-Q4_K_M.gguf`，通过独立服务连接本机或局域网设备。先运行 `pwsh -File scripts/prepare-r2t2.ps1`，再运行 `pwsh -File scripts/start-r2t2.ps1`；设置中选择 R2T2，Base URL 填 `http://127.0.0.1:8272/v1`，模型填 `r2t2-q4`。需要主模型和音频投影文件，合计约 1.63 GiB。支持明确选择 HF-Mirror 下载源、Windows CPU/Vulkan 和 Linux Docker Compose 部署。默认开启“实时识别预览”：按住按键说话时，持续识别最新录音并在悬浮层更新临时文字；松开后对完整录音校正，完成后一次性输入。临时文字可能变化，长录音和较慢设备的预览更新会更慢；当前使用录音快照重复识别，尚未接入官方原生分块推理。按 Esc 丢弃预览。关闭开关并保存后，只在松开后上传和识别。升级后需重新启动 VoxType；连接旧版适配服务时请关闭开关或先升级并重启服务。局域网配置及接口说明见 [R2T2 部署文档](docs/r2t2-server.md)，实际 Q4 验证见 [R2T2 验证记录](docs/r2t2-verification.md)。

Windows 本机 R2T2 也可直接在 VoxType“语音识别 → 本机 R2T2 服务”选择 `cpu` 或 `vulkan`、指定数据目录，然后依次点击“准备本机资源”“启动本机服务”。这会打开可见的 PowerShell 7 窗口：准备资源需用户主动下载约 1.63 GiB 模型，启动时会编译适配服务并在窗口中常驻；关闭服务请按 Ctrl+C。启动按钮会保存本机地址和模型别名。需要已安装 PowerShell 7 与 .NET 10 SDK；远端 R2T2 仍在其运行设备上选择后端，VoxType 本机按钮不会修改远端服务。

**Whisper 本地** 为默认模式。在“语音识别 → 本地模型”查看多语言 **Whisper base GGML**（约 148 MB），选择 Hugging Face 官方源或明确标注的 HF-Mirror 第三方镜像，点击“下载模型”。显示进度并支持取消，失败后可更换来源并重试；程序不会自动切换来源。也可通过“导入已有模型”选择 `ggml-base.bin`，原文件保留。下载/导入经过精确大小、上游 SHA-256 和模型头校验后原子安装，已有文件不被覆盖。取消和失败清理临时文件，重试从头开始。完成后点击“使用此模型”，保存本地服务与模型路径，下一次语音输入生效；其他设置不变。支持打开文件夹、刷新校验状态和确认删除；识别/录音使用模型期间拒绝删除，空闲时先卸载缓存，且仅删除程序模型目录的文件。关闭设置继续下载，托盘退出会取消并等待清理。

Whisper 本地可在“语音识别 → Whisper 本地推理设备”选择 `cpu` 或 `vulkan`。默认 CPU；Vulkan 使用显卡和驱动，支持与设备有关，首次推理时验证加载，失败会提示改选 CPU。选择 Vulkan 不会自动退回 CPU，以免误以为正在 GPU 加速。Whisper.net 的原生运行时在应用进程内首次加载后固定，因此已使用过 Whisper 再更改设备时，需从托盘退出并重启 VoxType。Whisper 与 R2T2 各有自己的设备选择。GPU 是否更快需用相同模型和录音在目标设备实测。

仍可在设置手动指定模型完整路径；留空使用设置页面显示的默认模型文件。MSIX 版本默认位于当前包的 `LocalCache\VoxType\models\ggml-base.bin`；非打包运行使用 `%LOCALAPPDATA%\VoxType\models\ggml-base.bin`。Windows 会重定向 MSIX 的普通 AppData 路径，请以页面显示的实际目录为准。Whisper 本地 Provider 目前不支持英文专用 `base.en`、其他模型尺寸或 GGUF；R2T2 GGUF 使用上述独立服务。手动路径仅进行模型头初步校验，实际完整性由加载验证；程序内下载/导入及刷新状态另有完整哈希校验。缺失/无效模型在打开麦克风前提示。应用不在启动或识别时自动下载模型，也不自动切换在线服务。

本地使用同版 `Whisper.net`、CPU/Vulkan runtime 1.9.1，要求 AVX、AVX2、FMA、F16C 和 x64 Visual C++ 2022 Redistributable；Vulkan 还需兼容的显卡和驱动。首次识别后台加载模型；连续识别复用模型，5 分钟空闲或切换设置后安全卸载。ESC 会丢弃结果，正在执行的原生推理仍需结束后才能重新使用资源，不保证瞬间中止原生推理。

**OpenAI Compatible** 在同一个设置表单填写 Base URL、模型名称、可选 API Key 和语言。例如 Base URL `http://localhost:8000/v1`，请求目标为 `/v1/audio/transcriptions`。远程服务器要求 HTTPS；HTTP 只支持回环地址。地址不接受账号、查询参数或片段。`auto` 自动检测语言，`zh` 指定中文。提交的是内存中的 WAV，服务器应返回 JSON `text` 字段。请求含 `response_format=json`，120 秒超时；不跟随重定向或自动重试请求。

保存后的 Provider、语言、模型和快捷键用于下一次会话。当前录音保留开始时的配置。API Key 留空后保存会清除已保存的密钥。

## 输入与使用边界

先聚焦记事本或其他应用的输入框，再按住快捷键。录音最长 60 秒，不足 250 毫秒的录音不提交识别。收音先转换为 16 kHz 单声道，滤除直流偏置与低频轰鸣，再按 20 毫秒窗口检查声音：累计至少 200 毫秒达到约 -56 dBFS 即可继续，避免轻声和长停顿被整段平均音量误判。有效录音按声音窗口进行音量补偿，最多放大 8 倍（约 18 dB），峰值保留余量；保留全部音频，不裁剪停顿或尾音。静音及短促碰麦不会触发补偿。这是音量检查，不是可区分人声和环境声的语音活动模型；持续环境噪声仍可能通过。浮层波形来自真实音频 RMS；识别阶段显示进度环。

WASAPI 优先使用 RAW 流，跳过系统麦克风增强处理而不修改 Windows 声音设置；设备明确返回 RAW 不支持时回退常规流。停止请求会持续到原生录音线程真正结束，避免快速松键/取消被底层启动线程覆盖。

原窗口有效且权限可验证时，程序尽力恢复原窗口并发送 Ctrl+V。请释放 Alt/Ctrl/Shift/Win；等待超过 2 秒会改为复制。目标关闭、最小化、子焦点变化或以更高权限运行时，结果保留供手动粘贴，不自动提权。浏览器 DOM 输入位置、光标位置和应用是否接受粘贴无法普遍确认；“文本已发送”表示输入事件已提交。

发送后约 750 毫秒尝试恢复原剪贴板，仅支持可安全快照的文本、HTML、RTF 等格式，并核对 sequence 与本次会话标记。用户新复制的内容会保留。复杂格式或持续占用时提示未恢复/复制失败。慢应用可能晚于恢复时刻才读取剪贴板；常规设置提供最近一次结果的复制入口。部分按键发送失败时不会重复发送全文。

快捷键支持 Alt/Ctrl/Shift/Win 加 Space、A–Z、0–9、F1–F11。默认 Ctrl + Shift + Space，设置页可更换；请避开系统和编辑器已占用的组合。旧版本配置中的默认 Alt + Space 自动升级一次，其他自定义组合保留；升级后仍可自行选择 Alt + Space。重新保存设置会尝试重新安装 Hook。真实左右修饰键、AltGr 和常用软件冲突需要按 [验收表](docs/manual-acceptance.md) 检查。

## 数据与隐私

设置路径为当前用户 LocalApplicationData 下 `VoxType\settings.json`，使用版本化 JSON 与同目录临时文件原子替换。磁盘 DTO 只含 DPAPI CurrentUser 加密的 API Key；解密失败会清空密钥并提示重新输入。DPAPI 不防御同用户身份的恶意程序。

MSIX 下该逻辑设置路径会虚拟化到 `%LOCALAPPDATA%\Packages\<包 family>\LocalCache\Local\VoxType\settings.json`，直接查看未打包的 AppData 目录不能证明开发包没有保存设置。

录音和最近识别文本只保留内存，不创建录音文件或转录历史。本地 Provider 不联网；主动点击模型下载时只请求所选模型文件，不上传录音、不携带在线 API Key。在线 Provider 只上传到用户配置的服务器。写入识别文本时设置 Windows 剪贴板历史/云同步排除标记。系统及第三方程序仍可能有自己的剪贴板行为。

## 打包

```powershell
dotnet build VoxType.slnx -c Release -p:Platform=x64
winapp package src/VoxType.App/VoxType.App.csproj --arch x64 --no-sign
```

CLI 会返回实际 MSIX 路径。当前生成包未签名，供检查和后续签名使用，不能直接视为已签名安装包。本版本依赖 x64 .NET 10 runtime、Windows App Runtime 2.5.1 和上述本地识别 VC++ runtime；未做干净机器安装验收。没有建立 Store 身份、自动更新或开机自启动。

第三方依赖和模型来源见 [许可说明](docs/third-party-notices.md)。项目自身许可证尚待项目维护者选择。
