# R2T2 本机和局域网识别服务

VoxType 的 `R2T2 本机／局域网` Provider 可以连接本仓库的独立 HTTP 服务。服务把 WAV 转成音频聊天请求，交给常驻的 [llama-server](https://github.com/ggml-org/llama.cpp)，使用 [网易有道 Confucius4-R2T2 GGUF](https://huggingface.co/netease-youdao/Confucius4-R2T2-GGUF)。按住快捷键录音、松开后整段识别，沿用 VoxType 的取消和文本输入流程。

此接入使用离线整段推理；模型官方的实时分块算法和 WebSocket 协议尚未接入。无需 Python、云账号或付费 API。模型准备阶段需要联网，运行阶段只连接配置的设备和本机 llama-server，不会自动下载。

## Windows：准备和启动

需要 Windows x64、PowerShell 7、.NET 10 SDK。默认 CPU；若选择 Vulkan，还需要设备提供可用的 Vulkan 驱动。模型主文件 1,107,404,736 字节，音频投影文件 641,773,984 字节，合计约 1.63 GiB；运行内存会高于文件大小，请预留数 GiB。

在仓库根目录执行：

```powershell
pwsh -NoProfile -File scripts/prepare-r2t2.ps1
pwsh -NoProfile -File scripts/start-r2t2.ps1
```

Hugging Face 访问不畅时，可以主动选择 HF-Mirror 第三方镜像；脚本不会自动切换来源：

```powershell
pwsh -NoProfile -File scripts/prepare-r2t2.ps1 -Source HfMirror
```

脚本使用固定模型提交和上游 LFS SHA-256 校验，不接受大小或哈希不符的现有文件，也不覆盖已有模型。下载失败或取消会清理本次临时文件。可直接把已下载的两个文件放入 `r2t2-data/models`，脚本会复用校验通过的文件。模型和运行时不会纳入 Git。

Windows CPU/Vulkan 运行时锁定 llama.cpp **b10950**，与有道原生后端说明使用的版本一致。下载 zip 也校验官方发布 SHA-256。要使用 Vulkan：

```powershell
pwsh -NoProfile -File scripts/prepare-r2t2.ps1 -Backend Vulkan
pwsh -NoProfile -File scripts/start-r2t2.ps1 -Backend Vulkan
```

`start-r2t2.ps1` 校验模型、构建适配服务，启动两个隐藏子进程并等待 `/health` 就绪。默认适配服务 `127.0.0.1:8272`，llama-server `127.0.0.1:8273`。终端保持运行；Ctrl+C 或任一子进程退出时，脚本清理自己启动的两个进程。启动前检查端口占用，不终止已有进程。可用 `-Threads 8`、`-Port 8272`、`-LlamaPort 8273`、`-StartupTimeoutSeconds 240` 调整。`-LlamaServerPath` 可指定自备兼容运行时，其来源与兼容性由部署者负责。

在 VoxType「语音识别」设置中选择 **R2T2 本机／局域网**，填写并保存：

| 字段 | 本机配置 |
|---|---|
| Base URL | `http://127.0.0.1:8272/v1` |
| 模型名称 | `r2t2-q4` |
| API Key | 默认留空；服务配置密钥时填写同一值 |
| 语言 | `zh` 或 `auto` |

服务启动与 VoxType 独立；退出 VoxType 不停止手动启动的服务。

## Windows：允许局域网设备连接

在同一个 PowerShell 7 终端先设置访问密钥，再启动。密钥通过子进程环境传递，不放入进程命令行：

```powershell
$env:R2T2_API_KEY = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
./scripts/start-r2t2.ps1 -ListenAddress 0.0.0.0
```

把该环境变量的值填入客户端 API Key；客户端 Base URL 使用服务设备的实际私有 IP，例如 `http://192.168.1.100:8272/v1`。`0.0.0.0` 是监听地址，不能作为其他设备的连接地址。Windows 防火墙需允许私有网络访问 TCP 8272；脚本不会修改防火墙。8273 只监听回环，无需开放。

局域网 HTTP 传输录音、文本和密钥均为明文，只应在可信局域网使用。跨网络连接请通过你配置的 HTTPS 反向代理。新 Provider 允许 RFC1918 IPv4 和 IPv6 ULA 私有地址的 HTTP；其他远程地址要求 HTTPS。原有 OpenAI Compatible 的远程 HTTPS 规则继续适用。

## Linux／局域网设备：Docker Compose

需要 Docker Engine 和 Compose，CPU 镜像支持的平台以官方镜像清单为准。先把上述两个校验通过的模型放到仓库 `r2t2-data/models`；安装 PowerShell 7 的设备也可以运行 `prepare-r2t2.ps1 -ModelsOnly`。不要只准备 Q4 主文件。

```bash
export R2T2_API_KEY="$(openssl rand -hex 32)"
# 默认仅主机本地连接；设为主机私有 IP 或 0.0.0.0 后可供局域网访问。
export R2T2_BIND_IP=127.0.0.1
docker compose -f compose.r2t2.yaml up --build -d
curl http://127.0.0.1:8272/health
docker compose -f compose.r2t2.yaml down
```

Compose 固定官方 CPU 镜像 **b11515** 的内容摘要，适配器和 llama-server 共享容器网络命名空间，只有适配器的 8272 端口发布到主机。容器内模型以只读方式挂载；默认禁用 llama 请求日志。适配器使用非 root 用户。模型加载中或上游不可用时健康检查返回 503，就绪返回 200。

本机没有 Docker 时不能据此宣称容器实际运行通过；已执行的验证与限制见 [验证记录](r2t2-verification.md)。Compose 当前为 CPU 配置，Windows Vulkan 加速由上述脚本提供。

## HTTP 契约

`GET /health` 不要求密钥，只返回 `{"status":"ok"}` 或 `{"status":"unavailable"}`；200／503 反映 llama-server 就绪情况。配置密钥后，其他接口必须携带 `Authorization: Bearer <key>`，缺失或错误返回 401。

`GET /v1/models` 返回模型列表，默认唯一 ID 为 `r2t2-q4`。

`POST /v1/audio/transcriptions` 使用 multipart/form-data：

| 字段 | 规则 |
|---|---|
| `file` | 一个 WAV：16kHz、单声道、16 位有符号 PCM，250ms–60s；校验 RIFF 和块边界 |
| `model` | 必填，与配置别名一致，默认 `r2t2-q4` |
| `language` | 可选：`auto`／空自动检测；`zh`、`en`、`ja`、`ko`、`de`、`fr`、`es`、`yue` 指定语言 |
| `response_format` | `json`（默认）或 `text` |
| `prompt` | 可选上下文／热词，最多 4000 字符；不改变服务系统配置 |
| `stream` | 仅接受省略或 `false` |

```powershell
curl.exe http://127.0.0.1:8272/v1/audio/transcriptions `
  -F "file=@sample-16k-mono.wav" -F "model=r2t2-q4" -F "language=zh"
```

成功响应 `{"text":"识别的文字"}`，会剥离模型 `language Chinese<asr_text>` 协议前缀。文件最多 2,000,000 字节，整个请求最多 2,065,536 字节，上游响应最多 1 MiB。单实例串行推理；排队与推理共享 115 秒期限，客户端请求总期限 120 秒。并发用户或更长录音应另行评估设备容量。

错误统一为 `{"error":{"code":"…","message":"…"}}`。无效输入 400，文件超限 413，无识别文字 422，上游无效或截断输出 502，上游断开 503，超时 504。错误不会回传上游原始响应、录音、提示或密钥。客户端取消会取消排队和上游 HTTP 请求、丢弃结果；原生计算停止时间取决于 llama-server。

录音和 multipart 仅在内存处理，没有录音文件落盘；正常转录内容不写入日志。操作系统交换文件及部署者额外启用的代理日志不在服务控制范围内。

## 手动运行适配器

安装 .NET 10 runtime 后，可将发布目录复制到目标设备：

```powershell
dotnet publish services/R2T2.Server/R2T2.Server.csproj -c Release --no-self-contained -o r2t2-data/adapter
$env:R2T2__ListenUrl = 'http://127.0.0.1:8272'
$env:R2T2__UpstreamUrl = 'http://127.0.0.1:8273'
$env:R2T2__Model = 'r2t2-q4'
dotnet r2t2-data/adapter/R2T2.Server.dll
```

需另行启动兼容的 llama-server，指定两个模型文件、别名和 `services/r2t2-chat-template.jinja`（ASR 专用提示格式），参数见启动脚本。适配器环境变量 `R2T2__ApiKey`、`R2T2__UpstreamApiKey`、`R2T2__TimeoutSeconds` 可覆盖配置。上游只接受回环根地址且不跟随重定向。`ASPNETCORE_URLS` 不覆盖服务自己的监听设置，避免意外公开无密钥服务。

## 来源与许可

代码实现参考官方 [R2T2 的 llama-server 客户端及 ASR 提示协议](https://github.com/netease-youdao/Confucius4-R2T2/blob/master/r2t2_llama/llama_native_backend.py) 与 [llama.cpp 服务接口](https://github.com/ggml-org/llama.cpp/blob/b10950/tools/server/README.md)。下载清单记录官方模型提交、大小和 LFS SHA-256，以及 Windows 运行时发布摘要，核验日期 2026-10-10。

llama.cpp 使用 MIT；R2T2 仓库代码为 Apache-2.0，权重另遵循 [网易有道 MODEL_LICENSE](https://github.com/netease-youdao/Confucius4-R2T2/blob/master/MODEL_LICENSE_zh)，不能按代码 Apache 许可推断模型权重许可。本仓库不分发权重。HF-Mirror 是第三方下载源。详情见 [第三方说明](third-party-notices.md)。
