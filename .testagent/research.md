# VoxType 测试研究

## 2026-10-03 模型下载管理

新增范围为 LocalModelManager、ModelManagerViewModel 和 LocalWhisperService 的删除保护。沿用 MSTest 4.0.2、VSTest 命令；HttpMessageHandler、阻塞流与管理服务替身控制网络和取消，无真实网络单元测试。UI VM 源文件链接到测试工程，使用相同 CommunityToolkit.Mvvm 8.4.2，避免引入 WinUI 测试宿主。验证安装发布时机、进度、固定来源、大小/哈希/类型、取消/重试、互斥、导入保留源、默认目录删除、活跃 lease/缓存保护、命令状态和使用模型保存边界。真实源下载和 packaged UI 单独记录。

当前仓库为新建 SDK-style 三工程，SDK 10.0.301；没有既有 .NET 测试、CI 或框架约束。按六阶段增加一个 Windows x64 MSTest 测试工程，并登记在 `VoxType.slnx`。采用 VSTest 命令模式，不修改全局 runner 设置。

测试范围为 Core 的快捷键序列、会话生命周期，以及随后 Infrastructure 的音频转换、HTTP 合约、注入决策、剪贴板所有权、模型生命周期与设置安全。真实系统 Hook、麦克风、焦点和转录验收另记 Windows 证据，fake 不作为真实 ASR 通过证据。

首批目标 `HotkeyMatcher.cs`：首次完整组合一次 Pressed；两种松键顺序一次 Released；重复/主键先按/额外修饰/注入事件不触发；左右同类修饰键；ESC 成对吞键和取消一次；中断/遗漏释放后重新武装；活动序列保持旧配置直到全部相关键释放。`HotkeyBinding.cs`：非法修饰和主键拒绝，合法多修饰显示正确。

静态 pairing 报告只用于定位测试，不代表行或分支覆盖率。后续模块实现后扩展需求矩阵，当前不宣称整个测试计划完成。

2026-10-03 快捷键缺陷复测目标限定为 `HotkeyMatcher` 的异步状态核对及 `JsonSettingsStore` 的旧默认迁移：被拦截主键不在异步状态时仍保持录音，重复/主键松开/ESC 配对不退化；真实修饰键丢失仍取消且不重新启动。默认 Ctrl + Win + F8 验证左右修饰与三个松键顺序。迁移必须保留自定义键、Provider、路径和原 DPAPI 密文，仅执行一次；写入失败返回有效新配置和提示，原磁盘保留。既有 Alt + Space 测试改为显式自定义测试夹具，保留原序列断言。
