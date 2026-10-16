# Proposal

## Why

用户希望在本机或局域网设备部署 Confucius4-R2T2 Q4 GGUF，并让 VoxType 使用它进行语音输入。现有客户端只有 Whisper 本地和 OpenAI 兼容接口，不能直接调用 llama-server 的音频聊天接口。

## What Changes

- 增加跨平台 .NET 10 常驻 HTTP 服务，将 WAV 转写请求适配到 llama-server。
- 增加 R2T2 本机／局域网识别选项，沿用现有录音完成后识别流程和 DPAPI 密钥存储。
- 提供 Windows 启动／模型准备脚本、Linux Docker Compose 部署和实际验证记录。
- LAN 模式使用 API Key；仅新增服务选项允许私有 IP 的 HTTP，原在线接口地址规则保持不变。

## Capabilities

### New Capabilities

- `r2t2-service`: Q4 推理服务的 HTTP 契约、部署、客户端接入和资源生命周期。

### Modified Capabilities

无；当前主规格尚未同步，现有 MVP 增量规格保持原样。

## Impact

新增 services/R2T2.Server、部署脚本和文档；修改 Provider 定义、地址校验、设置 ViewModel 和注册代码。不改变已有录音／悬浮层改动，不在本次实现实时麦克风流式输入。
