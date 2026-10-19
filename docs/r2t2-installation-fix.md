# R2T2 安装包启动修复验证（2026-10-19）

原 v0.1.0 安装包携带适配服务源码，却遗漏仓库根目录 `Directory.Build.props`。启动脚本现场编译时因此缺少隐式 using 和 nullable 配置，出现基础类型找不到及 CS8632 警告。服务项目现显式声明这些设置，并增加脱离仓库的构建回归。

正式发布改为自动发布 Windows x64 自包含适配服务，安装包／便携版不再携带服务源码。应用通过原生 C# 管理资源下载、大小／GGUF／SHA-256 校验、运行时安全解压、后台进程、健康检查及停止。普通设置页入口无需 PowerShell、SDK 或共享 ASP.NET Core Runtime。高级脚本保留，优先运行预编译服务，仅源码仓库回退到编译。

服务进程加入带 `KILL_ON_JOB_CLOSE` 的 Windows Job Object。正常停止、任一进程失败和宿主异常退出都会清理关联进程；停止后等待 Windows TCP 表释放监听项，避免刚停止就误报端口占用。设置页在运行时禁用设备／目录编辑和重复启动，提供内联状态和停止／取消。

## 证据

- Release 回归：406 项通过，0 失败，0 跳过。最终 TRX：`artifacts/r2t2-fix/tests/r2t2-fix-final.trx`。一次与真实模型内存压力重叠的较早执行出现托管 OOM，随后在限制测试宿主堆大小的条件下全套通过。
- 脱离仓库构建：测试只复制服务项目和 `.cs` 到临时目录，不复制 `Directory.Build.props`，实际调用 SDK 构建成功。
- 自包含发布：Release 与 Debug portable publish 成功。适配服务 runtimeconfig 使用 `includedFrameworks`，包含 .NET 和 ASP.NET Core 本机运行时，没有共享 `framework`／`frameworks` 要求。
- 生命周期烟测：真实发布适配器搭配模拟 llama 健康服务，验证就绪、模型列表、适配器异常退出后的伴随清理、停止后立即重启及宿主直接退出后的 Job 清理。使用已下载模型的真实哈希校验，没有调用麦克风。记录位于 `artifacts/r2t2-fix/lifecycle-smoke.txt`、`crash-smoke.txt`。
- `winapp ui`：4 个设置页流程全部通过，覆盖初始按钮、错误目录、健康检查后就绪、运行时控件禁用、停止及立即重启。使用独立 Debug 配置 `6673746292614c319d2404f226c17677` 和模拟推理进程，未改个人设置。JSON 和四张已检查的截图位于 `artifacts/r2t2-fix/ui-results-fixture/`。批处理脚本为 `scripts/test-r2t2-settings.ps1`。
- 工具：按用户要求改用 WinApp CLI 0.7.1；用户级安装 CLI。机器的 Windows Sandbox 功能未启用，Windows build 22631 也低于 WinApp 沙箱集成所需 24H2，故本机运行。Computer Use 只做过窗口列举，未操作设置页。

## 真实模型边界

已下载的两个模型及 Vulkan ZIP／展开文件通过校验，准备流程复用这些文件，没有重新下载约 1.63 GiB 模型。真实 Vulkan 启动出现 `ErrorOutOfDeviceMemory`；系统提交余量一度约 1 GiB。CPU 模式使用同一固定 Vulkan 发行版内的 CPU 后端，曾加载成功并通过适配器健康检查，但再次启动在低提交余量下失败。因此这些检查不能证明当前机器的 Vulkan 兼容性或稳定的真实模型运行；没有完成真实麦克风转录验收，也没有静默降级后端。

以上是首次本地修复构建的记录，未覆盖现有安装目录。后续 v0.1.1 消除了重复的 .NET 运行时文件，发布说明见 [v0.1.1](releases/v0.1.1.md)。共享运行时合并会逐一核对同名文件 SHA-256，遇到不一致时在复制前拒绝；发布包中的两个 apphost 共用根目录运行时，不要求用户另装共享框架。
