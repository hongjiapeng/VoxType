# R2T2 接入验证（2026-10-10）

工作区 `D:\30_Code\Personal\OpenSource\VoxType`，Windows x64，Intel Core Ultra 5 225H，.NET SDK 10.0.301。验证只使用新服务和官方公开样例，没有修改用户现有识别配置。

## 真实 Q4 模型烟测

运行 `pwsh -NoProfile -File scripts/prepare-r2t2.ps1 -Source HuggingFace` 成功。固定提交 `86ff0251cb9f456b63aeef5f80137f104e22869a` 的 Q4 主模型及 F16 mmproj，文件大小、GGUF 头和 SHA-256 均通过；CPU 运行时 b10950 官方 zip 摘要通过。再次运行准备脚本时复用校验通过的模型和压缩包。对现有错误模型文件的检查确认拒绝下载覆盖且原内容保留。

`pwsh -NoProfile -File scripts/start-r2t2.ps1` 实际启动隐藏 llama-server 和 .NET 适配器，健康检查就绪。推理使用 CPU、8 线程、8192 上下文、单槽位和仓库内 ASR 模板。

输入为官方仓库的 [resources/test.wav](https://github.com/netease-youdao/Confucius4-R2T2/blob/master/resources/test.wav)：16kHz 单声道 PCM16，107,840 个采样，6.74 秒。下载到被 Git 忽略的 `artifacts/r2t2/test.wav`。

```powershell
curl.exe -sS --max-time 120 -w '\nHTTP %{http_code}, elapsed %{time_total}s\n' `
  http://127.0.0.1:8272/v1/audio/transcriptions `
  -F 'file=@artifacts/r2t2/test.wav' -F model=r2t2-q4 -F language=zh
```

返回 `{"text":"之前有顾客自己带酒水，也没加收钱或者不让喝。"}`，HTTP 200，第一次请求 1.496308 秒。随后使用 `language=auto` 返回同文，HTTP 200，0.629909 秒。模型协议前缀没有进入客户端结果。两次样例不足以得出真实人声、噪声、各语言的准确率或普遍延迟结论。

Ctrl+C 停止监督脚本后，确认自己启动的 llama-server、适配器均退出，8272／8273 监听端口消失。未配置 `R2T2_API_KEY` 时以 `-ListenAddress 0.0.0.0` 启动，验证在创建子进程前拒绝。模型和运行时留在被 Git 忽略的 `r2t2-data`，可再次启动，无后台服务遗留。

## 部署静态验证

PowerShell AST 解析 `prepare-r2t2.ps1`、`r2t2-common.ps1`、`start-r2t2.ps1` 均无错误。Compose YAML 使用 PyYAML 解析通过，核对共享网络命名空间、只发布适配器端口和固定镜像摘要。官方 registry 已确认该 CPU 镜像的 b11515 标注和内容摘要存在；HF-Mirror 固定模型提交地址 HEAD 返回 200，未用镜像重新下载整份模型。

本机没有 Docker，未执行容器 build/up；没有实测 Linux/ARM 设备、Windows Vulkan 或另一台局域网客户端。提供这些配置不等于这些环境均已运行验收。LAN 身份验证和客户端地址规则由真实 HTTP 契约测试及客户端单元测试验证；局域网防火墙和 HTTPS 代理由部署者配置。

## 自动验证

测试用本机 Kestrel 上游替身验证 HTTP 契约，与上面的真实 Q4 推理分别记录。

```powershell
dotnet build VoxType.slnx -c Release -p:Platform=x64 --no-incremental --nologo
dotnet test tests/VoxType.Tests/VoxType.Tests.csproj -c Release -p:Platform=x64 --no-build --nologo --logger 'trx;LogFileName=r2t2-full-regression.trx'
openspec validate add-r2t2-server --strict
git diff --check
```

Release 非增量构建完成：0 警告、0 错误，21.16 秒。完整测试完成：**Passed: 320, Failed: 0, Skipped: 0**，约 10 秒。TRX 位于被 Git 忽略的 `tests/VoxType.Tests/TestResults/r2t2-full-regression.trx`；其中新增 R2T2 分区 115 case 全部通过，包括最终加入的缺失 WAV 块、奇数 PCM 数据、重复模型和超长录音 case。OpenSpec 严格校验和 diff 空白检查通过。

独立窄范围命令 `dotnet test tests/VoxType.Tests/VoxType.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~VoxType.Tests.R2T2 --nologo` 也以 exit 0 完成：**Passed: 115, Failed: 0, Skipped: 0**，约 10 秒，无编译或 analyzer 警告。

| Requirement | Evidence |
|---|---|
| “部署到本地或者局域网的设备上” | 上述真实 Q4 本机 CPU 烟测；`VoxTypeProvider_UsesActualAdapterAndPreservesR2T2Identity` 验证客户端到真实适配器 HTTP；`R2T2_PrivateIpHttpAllowed_OriginalProviderStillRejects` 与 `R2T2Provider_ForwardsExactAudioKeyModelLanguageAndIdentity` 验证私有 IP 请求地址和音频／密钥 |
| “你帮我实现吧” | 完整构建与 320 case clean run，R2T2 服务、Provider、Windows 脚本、Compose 和接口文档已交付 |
| 中文音频、热词、JSON／text 响应 | `SuccessfulAudio_ExactUpstreamPayloadAndResponseContract`；字节逐项相等、提示、语言前缀和响应文本均有明确断言 |
| 密钥、健康检查、无效输入 | `MissingOrWrongKey_RejectedBeforeUpstream`、`HealthPublic_ModelsAuthenticated_AndReadinessTracksUpstream`、`DuplicateModelAndOversizedAudio_RejectedBeforeUpstream` |
| 串行、取消、超时、响应上限、错误隐私 | `ConcurrentRequests_AreSerialized_AndBothReturnTheirOwnResult`、`CallerCancellationAndQueuedCancellation_ReleaseGateForNextRequest`、`ResponseBodyAfterHeaders_IsIncludedInDeadline`、`ResponseSize_DeclaredAndUndeclaredBodiesBounded`、`UpstreamErrorBody_NeverExposedByHttpAdapter` |
| 设置及原有规则回归 | `SettingsAndViewModel_R2T2LanSave_SharesPolicyAndPreservesKey`、`R2T2_PublicOrAmbiguousHttpAndEmbeddedSecrets_Rejected`，全套包含既有加密设置、HTTP、Whisper 与会话回归测试 |

没有采集行／分支覆盖率，不把测试个数作为准确率或环境验收。代码测试技能的来源映射、计划及断言／静态缺口审阅记录追加在 `.testagent`，保留原记录。
