# Design

## Context

见 proposal.md。现有 ITranscriptionProvider 接收整段 16kHz 单声道 PCM WAV；HTTP Provider 已处理凭据、取消、120 秒超时、大小限制和结构化错误。现有地址校验在三个位置重复，非回环 HTTP 被拒绝。官方 GGUF 使用主模型和 mmproj；llama-server 提供音频 chat/completions。

## Goals / Non-Goals

**Goals:** 让一个轻量跨平台 .NET 服务进程桥接现有客户端与独立 llama-server，支持 Windows 本机和 Linux／局域网部署；可复用既有模型文件并有明确启动失败诊断。

**Non-Goals:** 本次不改变录音为实时流式，也不自动切换或修改当前用户设置；不重新实现原生推理库。

## Decisions

- 新增 services/R2T2.Server，ASP.NET Core minimal API，无新增第三方生产 NuGet。它暴露兼容 multipart 接口，构造 R2T2 音频提示并解析 llama-server 响应。使用已有服务进程比把 Python/C++ ABI 嵌入 WinUI 更容易验证和局域网部署。
- 上游只监听回环，适配器承担 LAN 认证；入口绑定非回环时没有 Key 则启动失败。日志不包含音频、文本或 Key。
- 新 Provider r2t2-server 沿用 HTTP 转写实现，仅区分显示名、ProviderId 和地址规则。地址规则统一放在 Core；RFC1918 IPv4 和 ULA IPv6 允许 HTTP，DNS 名称使用 HTTPS 避免地址身份不确定；回环继续支持。
- 模型准备脚本主动下载到用户指定目录，校验上游 LFS SHA-256；运行时不下载。启动脚本分别启动两个子进程，等待健康就绪，在退出／失败时只清理自身子进程。Linux 使用 Compose，模型以只读卷挂载。
- 先验证请求契约和客户端保存行为，再尝试真实 Q4 识别；外部下载或硬件不可用时如实记录，不把模拟上游的成功当成模型成功。

## Risks / Trade-offs

- [Q4 与 llama.cpp 版本／提示兼容] → 固定验证版本、提供真实模型烟测并记录限制。
- [CPU 速度不足] → 支持 GPU 参数，先测试整段识别；不承诺实时延迟。
- [LAN HTTP 明文] → 仅明确私有 IP 的 R2T2 选项允许，UI 说明上传位置，服务要求 Key；可用 HTTPS 反向代理。
- [请求取消后上游仍在清理] → 上游并发槽位设为 1，适配器不自动重试。

## Migration Plan

先准备模型和 llama-server，启动服务后选择 R2T2 地址和模型别名。原有配置继续有效。回退时切回 Whisper 或现有 HTTP Provider，停止服务即可。
