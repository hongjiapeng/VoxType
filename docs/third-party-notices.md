# 第三方依赖与模型

本实现独立编写，未复制参考产品的业务代码或资产。项目自身开源许可证尚待维护者决定。NuGet 的精确版本与传递依赖见各工程 `packages.lock.json`；发布前应一并审阅传递依赖。已收集包内及上游许可至 `docs/licenses`，应用产物包含这些文件和本说明。

|组件|锁定版本|上游许可/来源|
|---|---|---|
|Windows App SDK|2.5.1|[Microsoft 仓库及许可](https://github.com/microsoft/WindowsAppSDK)|
|CommunityToolkit.Mvvm|8.4.2|[MIT](https://github.com/CommunityToolkit/dotnet/blob/main/License.md)|
|NAudio.Wasapi / Core|3.1.0|[MIT](https://github.com/naudio/NAudio/blob/master/license.txt)|
|Whisper.net / CPU Runtime / Vulkan Runtime|1.9.1|包内 `LICENSE`；[MIT 上游](https://github.com/sandrohanea/whisper.net/blob/main/LICENSE)|
|whisper.cpp / ggml|随 Whisper.net.Runtime|[所锁定源提交的 MIT](https://github.com/ggml-org/whisper.cpp/blob/f24588a272ae8e23280d9c220536437164e6ed28/LICENSE)；版权主体为 The ggml authors，已保留该许可|
|ProtectedData / Microsoft .NET 组件|10.0.12 及锁文件版本|[.NET MIT](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT)|
|WinApp / Windows SDK BuildTools / WinUIAnalyzer|0.7.1 / 10.0.28000.2705|Microsoft 对应 NuGet 包提供的条款；开发构建依赖|
|MSTest / TimeProvider.Testing|4.0.2 / 10.10.0|Microsoft 对应 NuGet 包的许可；测试依赖|

模型没有打包，也不会在启动或识别时自动下载。用户可主动在设置下载/导入多语言 base GGML（`ggml-base.bin`，147951465 字节），上游为 [ggerganov/whisper.cpp 模型仓库](https://huggingface.co/ggerganov/whisper.cpp/tree/main)，说明见 [whisper.cpp 模型说明](https://github.com/ggml-org/whisper.cpp/tree/master/models)；[OpenAI Whisper](https://github.com/openai/whisper/blob/main/LICENSE) 发布 MIT 许可。设置也提供明确标注的 [HF-Mirror 第三方镜像](https://hf-mirror.com)，用户主动选择，不静默切换；所有来源与导入文件使用相同的上游 SHA-256 `60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe` 校验。模型大小和哈希于 2026-10-03 从上游模型 API 核实。配置第三方在线接口时遵循该服务器的数据与使用条款。

CPU 与 Vulkan runtime 要求依据 [Whisper.net 官方说明](https://github.com/sandrohanea/whisper.net#pre-requisites)。Vulkan 原生库通过 NuGet 随应用发布；本项目没有安装全局 GPU/Python runtime，也没有将各参考项目的产品许可视为本项目可复制其实现的授权。

新增 R2T2 的独立适配服务不增加第三方生产 NuGet 包。用户主动执行准备脚本时，会下载独立 [llama.cpp b10950 Windows CPU/Vulkan 运行时](https://github.com/ggml-org/llama.cpp/releases/tag/b10950)（[MIT 许可](https://github.com/ggml-org/llama.cpp/blob/b10950/LICENSE)），保留官方压缩包内的许可文件；Compose 使用固定内容摘要的官方 b11515 CPU 镜像。本次未安装全局 GPU/Python runtime。

R2T2 的音频请求／提示协议依据 [netease-youdao/Confucius4-R2T2](https://github.com/netease-youdao/Confucius4-R2T2)，其代码使用 [Apache-2.0](https://github.com/netease-youdao/Confucius4-R2T2/blob/master/LICENSE)。Q4 主模型及 F16 音频投影文件源自 [官方 GGUF 仓库](https://huggingface.co/netease-youdao/Confucius4-R2T2-GGUF)，固定提交 `86ff0251cb9f456b63aeef5f80137f104e22869a`。权重另外遵循 [网易有道模型许可](https://github.com/netease-youdao/Confucius4-R2T2/blob/master/MODEL_LICENSE_zh)，不因代码采用 Apache-2.0 而变更。模型不打包分发。`scripts/r2t2-assets.json` 记录经官方 API 核验的大小、SHA-256 和运行时发布摘要；核验日期 2026-10-10。可明确选择 HF-Mirror 第三方源，所有来源使用相同校验。部署和数据流见 [R2T2 说明](r2t2-server.md)。
