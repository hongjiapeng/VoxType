# Spec Delta

## Purpose

让用户在本机或局域网设备部署 Confucius4-R2T2 Q4 模型，以常驻进程提供兼容转写接口，并在 VoxType 中明确选择服务地址、凭据和语言，保留现有按住说话、松开后输入的行为。

## ADDED Requirements

### Requirement: 兼容整段转写
服务 SHALL 接收 WAV、模型、语言及可选热词的 multipart 请求，在内存中调用配置的 llama-server，并返回 JSON text；不得将音频或转录写入文件，不自动重试。

#### Scenario: 中文转写
- **WHEN** 客户端提交有效 16kHz 单声道 PCM WAV 和 zh
- **THEN** 服务向上游提交音频和中文提示，返回去除模型协议前缀的文本

#### Scenario: 无效请求
- **WHEN** 请求模型、格式或 WAV 无效，或音频超过限制
- **THEN** 返回明确 4xx 错误且不调用推理

### Requirement: 有界资源与错误
服务 SHALL 串行转写、限制请求及响应大小，支持超时和取消；健康检查 SHALL 反映上游就绪状态，错误响应不得暴露音频、凭据或上游响应正文。

#### Scenario: 上游失败
- **WHEN** 上游未启动、返回错误、格式错误或空识别结果
- **THEN** 返回可识别的错误代码且不产生成功文本

### Requirement: 本机和局域网部署
服务 SHALL 默认仅监听回环；非回环监听 MUST 配置 API Key。部署脚本 SHALL 支持已有模型和 CPU／GPU 后端，保留原文件，失败时停止自己启动的进程；提供 Linux 部署配置。

#### Scenario: 局域网访问
- **WHEN** 服务监听局域网且客户端未提供正确 Key
- **THEN** 拒绝转写请求，不调用上游

### Requirement: VoxType 服务选择
应用 SHALL 增加 R2T2 服务选项，允许 HTTP 回环或明确私有 IP 端点以及 HTTPS 端点；原 OpenAI Compatible 规则保持不变。设置和实际请求 SHALL 使用相同地址规则，凭据沿用 DPAPI。

#### Scenario: 私有 IP 服务
- **WHEN** 用户选择 R2T2 并保存 http://192.168.1.100:8272/v1
- **THEN** 下一会话提交 WAV 至该设备的 /v1/audio/transcriptions，结果仍按原流程输入

#### Scenario: 公网明文地址
- **WHEN** 用户在 R2T2 选项输入公网 HTTP IP 或无法确认私有地址的主机名
- **THEN** 保存和实际请求都拒绝该地址并提示使用 HTTPS
