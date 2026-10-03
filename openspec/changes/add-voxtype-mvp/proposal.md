# Proposal

## Why

VoxType 让用户在 Windows 应用的文本输入框中按住快捷键说话，松开后自动输入识别结果，减少切换窗口和手动复制粘贴。第一版需要先验证完整的全局输入链路，并明确 Windows 焦点、权限和剪贴板的实际边界。

## What Changes

- 创建 Windows 11 x64、.NET 10、C#、WinUI 3 / Windows App SDK 应用，启动常驻托盘；设置窗口关闭后隐藏，托盘退出才结束进程。
- 实现默认 Ctrl + Win + F8 的可配置 push-to-talk，按下录音、组合键解除时停止并识别，ESC 取消；处理重复键、不同松开顺序与异常中断。按用户更新要求替换旧默认 Alt + Space，并保留其他自定义配置。
- 实现真实麦克风音量驱动的小型 Fluent 浮层，覆盖 Listening、Processing、Success、Error，显示时保持外部应用焦点。
- 统一语音识别 Provider，先以 OpenAI Compatible 的 Base URL / API Key / Model / Language 打通链路，再增加单个本地多语言 Whisper 模型。
- 记录开始时的目标窗口，优先向原窗口发送文字；以剪贴板加 Ctrl+V 为主要策略，明确处理焦点变化、权限、发送失败和剪贴板恢复。
- 设置只实现常规、语音识别、快捷键；配置持久化，API Key 使用当前 Windows 用户的 DPAPI 加密。已保存的配置从下一次会话生效。
- 增加阶段性构建、行为测试和 Windows 手动验收计划；测试结果必须区分自动化通过与尚未验证的系统交互。

第一版不包含账号、同步、多设备、AI 润色、LLM 重写、摘要、翻译、大型历史、多语言 UI、自动更新、模型市场、插件、会议转录或说话人分离。语音识别设置提供固定的多语言 base 模型下载、导入与安装管理。快捷键 toggle 模式、UI Automation 输入、GPU 加速、开机自启动与 Store 发布不在本次交付范围；托盘“开始语音输入”在设计中使用明确的单次控制，不增加全局 toggle 模式。

规划已完成，用户已授权继续开发。当前按 tasks 的六阶段实施，逐项记录构建、测试与真实系统验收结果；未验证项目不视为已交付。

## Capabilities

### New Capabilities

- `app-shell`：托盘常驻、设置窗口生命周期、显式退出及托盘发起输入。
- `global-hotkeys`：可配置 push-to-talk、完整按下/松开处理、取消及中断恢复。
- `audio-capture`：麦克风捕获、真实音量、标准音频输出、静音和设备错误。
- `voice-overlay`：不抢焦点的语音状态浮层、真实波形及自动隐藏。
- `speech-transcription`：统一服务选择、OpenAI Compatible 和本地 Whisper 的识别行为。
- `text-injection`：原窗口定位、文字发送、权限降级与剪贴板保护。
- `settings-security`：三个 MVP 设置页面、保存与下一次生效、凭据保护及隐私。

### Modified Capabilities

无。当前仓库没有现有 capability、应用工程或兼容性迁移需求。

## Impact

预计新增 `src/VoxType.App`、`src/VoxType.Core`、`src/VoxType.Infrastructure` 和必要的测试工程；本轮这些路径只出现在设计中。UI 使用 MVVM；音频、识别、快捷键、焦点、注入、托盘和安全存储通过独立服务连接。

拟使用 CommunityToolkit.Mvvm、NAudio、Whisper.net / whisper.cpp 的 Windows x64 CPU runtime，以及 Windows 原生 Win32 / DPAPI。具体稳定版本在工程阶段验证兼容性并锁定，不依赖仓库不存在的包配置。

在线模式只向用户配置的转录端点上传本次音频；本地模式不联网识别，不默认下载模型。用户可主动从设置下载模型，明确选择官方源或第三方镜像，文件校验后安装；不上传音频。运行保持普通用户权限；管理员窗口和安全桌面不承诺自动输入。剪贴板恢复属于尽力而为，发送按键不等同于验证目标已接受文字。

参考项目仅用于理解架构和交互，核对结果与来源见 design.md。不得直接搬用许可证不明确的代码；实际引入的依赖和模型须保留相应许可说明。
