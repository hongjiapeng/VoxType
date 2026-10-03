# Tasks

用户已授权 apply，当前继续实施。按下面六个阶段执行，不先研究本地模型阻塞在线链路；各阶段完成构建和相关测试，人工验收受设备或交互桌面限制时保留未验证记录并继续独立开发。所有真实系统测试记录环境、Provider 和结果，未具备模型、凭据或设备时标为未验证。

## 1. Phase 1 — 应用骨架、Tray、Settings、Overlay

- [x] 1.1 使用 WinApp CLI 官方 WinUI MVVM 模板建立 VoxType.App 与 MSIX 清单，新增 Core / Infrastructure 工程和 solution，限定 Windows 11 / x64 / .NET 10 并设置依赖方向；验证工程列表及首次 `dotnet restore`、x64 `dotnet build` 成功。
- [x] 1.2 锁定兼容的稳定 Windows App SDK、CommunityToolkit.Mvvm 及 WinUIAnalyzer 引用（分析器 PrivateAssets=all），在恢复后的 App 目录通过 `winapp find-api` 核实 AppWindow、NavigationView、ProgressRing 所需成员；验证项目 API 查询及 analyzer build，记录工具或包不可用的真实限制。
- [x] 1.3 建立 Core 会话 DTO、服务契约与 composition root，使用内存有效设置和固定两项 ProviderDefinition；验证 Core 不引用 WinUI / NAudio / Win32，所有工程构建，页面可读配置；未实现服务显示不可用，不伪造录音或识别结果。
- [ ] 1.4 实现 MVVM NavigationView 常规、语音识别、快捷键三个页面与 Provider 驱动统一表单，PasswordBox 和字段标签齐全；验证本地/在线字段切换、语言及模型输入、TextBox 即时更新、键盘导航与主题，业务验证不堆在 code-behind。
- [ ] 1.5 实现独立原生托盘服务、菜单、Explorer 重建、打开设置、关闭隐藏和显式退出；新增单实例激活协调；验证启动仅托盘、重复启动仅一个图标、关闭设置仍驻留、Explorer 重启后图标恢复、退出后进程结束；录音命令在 Phase 2 接入前明确禁用。
- [ ] 1.6 创建 OverlayWindow 和状态 VM，先设置 no-activate / tool window / topmost，再使用无激活显示，增加托盘录音结束按钮的 MA_NOACTIVATE 路径；验证显示、点击、隐藏均保持外部 foreground HWND，浮层不进入任务栏和 Alt+Tab。
- [ ] 1.7 实现 360 × 84 DIP 默认布局、目标屏工作区定位、DPI 换算与 Acrylic / 实色退化，用静态状态检查四种状态；验证主副屏、100% / 150% / 200% 缩放、暗色/亮色/高对比度，阶段演示不把静态状态作为真实波形。
- [ ] 1.8 写入开发启动及 Phase 1 手动检查记录；执行 x64 build、相关检查及 `winapp run` project-mode 激活，验证托盘、设置隐藏和浮层焦点链路，记录已锁定 SDK / 包版本及实际结果。

## 2. Phase 2 — Global Hotkey、Audio Capture、真实 Waveform

- [x] 2.1 实现可测试的组合键匹配、左右修饰键状态、重复消除、解除组合和重新武装规则，支持默认 Ctrl + Win + F8 与有效配置校验；用事件序列测试验证默认组合左右修饰键与三种松键顺序，以及自定义 Alt + Space 的长按重复、先松 Alt、先松 Space、额外修饰键、主键先按、ESC 和无效组合。
- [ ] 2.2 封装专用消息线程 WH_KEYBOARD_LL、强引用回调、自身/注入事件过滤、成对吞主键与 ESC、快速投递和 shutdown；手动验证记事本不出现窗口菜单/额外空格，普通 Alt / Space 可用，不遗留按键；覆盖左右 Alt、AltGr 环境与新组合。
- [ ] 2.3 接入按键 watchdog、会话/电源中断清理、60 秒录音上限与空闲后重新应用快捷键；验证锁屏/解锁、休眠恢复、松开遗漏模拟及长按超时后不重新录音，测试处理中忙碌请求被拒绝。
- [ ] 2.4 实现 NAudio WASAPI 默认麦克风服务，真实样本 RMS、下混/重采样与 PCM16 WAV，Stop 等待最后数据回调后释放；用已知音频验证 WAV header、16 kHz/mono、时长、幅度与尾部完整性，手动验证真实麦克风设备。
- [ ] 2.5 实现过短与低信号筛选、设备缺失、权限关闭和录音中断错误；验证静音不会调用 Provider、250 毫秒边界、拔插后可重试和 60 秒内存缓冲上限，记录真实设备下阈值表现。
- [ ] 2.6 接入 DictationCoordinator 的 Starting/Listening/Stopping/Cancel 和 AudioLevel → Overlay 的 DispatcherQueue 限频；以延迟启动的录音 fake 测试快速松键只停止一次且无残留录音，真实录音中说话/安静时波形变化且停止后不再动。
- [ ] 2.7 实现 FocusContext 服务的实时前台采样与托盘前最近有效外部窗口缓存，把托盘开始命令接入同一录音服务，结束按钮停止，ESC 取消；验证记录发生在浮层之前，托盘/设置不会成为输入目标，点击结束按钮不抢焦点。
- [ ] 2.8 写入快捷键冲突、麦克风隐私设置与 Phase 2 验证记录；运行 x64 build 和键序列/录音/状态机测试，再手动连续 10 次按住说话/松开及取消，确认无需重启、无录音句柄或订阅持续增长。

## 3. Phase 3 — 统一 Provider、OpenAI Compatible

- [x] 3.1 完成 ITranscriptionProvider、Provider 注册表、结果/错误模型及会话设置快照 lease，接入 Processing 与结果交付；用不同 Provider fake 验证协调器无厂商分支，保存设置后仅下一次会话改变服务及语言。
- [x] 3.2 实现 HttpClient multipart WAV 转录、Base URL 规范化、Model、可选 Bearer、自动语言省略和 JSON text 解析；用捕获请求的 HTTP handler 验证 `/v1` 保留、尾斜杠、自定义 path、表单字段、空密钥、中文结果，禁止通过空字符串 Key 发送空 Bearer。
- [x] 3.3 实现 HTTPS / loopback HTTP 校验、拒绝 URI userinfo/query/fragment、禁自动重定向、120 秒超时、响应大小限制和错误分类；测试 401/403、429、5xx、TLS/网络异常、HTML 响应、空文本、302、超大响应与取消，确认不自动重试付费请求、不记录敏感内容。
- [ ] 3.4 完成识别阶段取消、迟到结果守卫和 session ID 状态更新；以故意忽略 CancellationToken 的 fake 测试 ESC/退出后不交付结果，旧隐藏 timer 不覆盖新会话，忙碌时没有第二段录音。
- [ ] 3.5 在线配置先仅在内存生效，API Key 不在本阶段落明文磁盘；真实配置一台兼容服务器验证 Hotkey → Audio → Processing → 实际转录结果，目标注入尚未接入时明确标记只验证到识别。
- [ ] 3.6 写入兼容接口配置、错误提示及 Phase 3 记录；运行 x64 build、HTTP contract 和状态机测试，使用真实服务器验证中文、取消、错误后重试及 UI 响应，凭据缺失时将真实服务验收标为未验证。

## 4. Phase 4 — Text Injection、原窗口、完整在线链路

- [ ] 4.1 完成 FocusContext 的 HWND / PID / TID / 可读进程创建时间与子焦点记录、目标身份校验及一次 SetForegroundWindow 恢复；用可替代平台边界测试无效句柄/复用/焦点变化的回退决策，手动验证窗口切换、内部焦点变化、最小化和目标关闭不会向可检测到的错误窗口发送。
- [ ] 4.2 实现进程 integrity 比较和安全桌面拒绝，把明确 elevated 和无法验证情况转为降级；测试错误分类与零发送分支，手动验证普通 VoxType 面向管理员目标提示手动粘贴，不自动提权。
- [ ] 4.3 封装 STA 剪贴板服务、有效 owner HWND、有限重试、支持文本格式快照与复杂格式结果、会话标记及排除 Windows 历史/同步标记；测试空/文本/HTML/RTF 快照、占用重试、复杂格式退化与提示，手动验证原剪贴板内容。
- [ ] 4.4 实现 Clipboard + Ctrl+V 的发送策略、修饰键释放等待和 x64 INPUT 正确布局、实际发送数校验及合成键清理；测试 0/部分/全部事件发送、2 秒等待边界、取消提交点与禁止全文重试，手动验证中文、多行及先松 Space 后仍按住 Alt。
- [ ] 4.5 实现约 750 毫秒后的剪贴板恢复，在同一次重新打开期间核对 sequence / 会话 ownership，仅恢复可安全快照内容；用受控时钟及 Clipboard fake 测试新复制不被覆盖、迟到恢复和下一会话写入不互相覆盖，手动验证普通文本及慢粘贴应用的实际表现。
- [ ] 4.6 实现 Dispatched / CopiedOnly / PendingCopy / Cancelled 和最近一个内存结果的设置复制入口；测试注入失败复制成功、复制持续失败、复杂格式无法恢复和更新后的提示准确性，验证不产生转录历史文件。
- [ ] 4.7 接入识别结果 → 注入 → Overlay 自动结束，在线完成用户记事本核心流程；连续 10 次说“你好，这是一个语音输入测试”，验证不抢焦点、真实波形、松开识别、中文输入、浮层隐藏和无需重启，并核对取消不注入。
- [ ] 4.8 写入自动输入限制、UIPI、剪贴板恢复说明与 Phase 4 记录；运行 x64 build、注入决策/剪贴板/状态机测试，再手动验收普通记事本、浏览器输入框、窗口切换、管理员目标、剪贴板占用与用户新复制，记录无法普遍确认目标接受文本的限制。

## 5. Phase 5 — Local Whisper

- [ ] 5.1 引入匹配稳定版本的 Whisper.net 与 Windows x64 CPU runtime，确认 CPU 指令集及 VC++ runtime 要求，锁定 native DLL 包含方式；验证 restore/build/MSIX payload 与普通 packaged 激活，缺 native DLL / runtime 时出现明确诊断，不引入 AllRuntimes、GPU 或 Python。
- [ ] 5.2 实现用户模型路径/默认目录与多语言 base 模型验证，缺失/损坏/英文模型不兼容时明确报错，不自动下载或切云；用临时路径测试路径校验，使用真实模型验证中文和显式 language / auto。
- [ ] 5.3 实现后台懒加载、单 factory/context 缓存、串行处理、Provider lease 与语言选项；验证同模型连续两次只加载一次，两个识别不会同时进入同一 native context，UI 不被首次加载阻塞。
- [ ] 5.4 实现取消后的结果守卫、模型切换/服务停用/5 分钟空闲/退出的安全卸载；用受控生命周期测试证明原生推理执行时不 dispose，真实取消后能重新录音，记录所选版本能否及时停止 native inference。
- [ ] 5.5 将本地 Provider 及 descriptor 接入统一设置页面与 Overlay，保存后下一次识别生效；断网执行记事本中文链路，验证无网络上传、不重启切换回在线、缺模型时仍可打开设置修复。
- [x] 5.6 写入手动模型配置、模型与依赖许可说明及 Phase 5 性能记录；运行 x64 build 与生命周期测试，记录本机硬件、冷加载/热识别时长、连续 10 次工作集、空闲卸载和可重试性，真实模型未提供时不宣称离线链路已通过。

## 6. Phase 6 — 错误处理、持久化、DPAPI、体验与最终验收

- [x] 6.1 实现当前用户 LocalApplicationData 中版本化 JSON 与临时文件原子替换，保存后发布新有效快照；测试写入失败保留旧有效配置、损坏文件安全本地恢复、版本错误及重启读取，不使用安装目录存储。
- [x] 6.2 接入 ProtectedData.CurrentUser 加密 API Key，密码字段清除/替换规则及解密错误提示；Windows 集成测试验证加密往返、篡改密文失败、磁盘无明文 Key，检查日志和错误消息不泄漏凭据，不声称 DPAPI 防同用户恶意软件。
- [ ] 6.3 完成设置保存校验、活动会话结束后应用 Hotkey / Provider / 模型变更，以及默认本地模式和两 Provider 各自配置保留；测试中途保存不改变当前录音、下一次立即使用新设置、失败保存不改变运行时配置，手动验证重启持久化。
- [ ] 6.4 整合麦克风、服务、空结果、焦点和复制错误的中文用户提示，成功/错误隐藏定时器与单个结果复制体验；验证故障后可再录音、旧计时器不隐藏新浮层、复制提示与真实状态一致，最新内存结果退出后消失。
- [ ] 6.5 完成关闭与退出资源清理、Hook / WinEvent / Tray 消息窗口订阅释放和最终启动检查；测试退出后迟到结果被拒绝，手动验证录音/识别中退出、取消、重复启动和 Explorer 重启无残留进程/录音。
- [ ] 6.6 完成主题、高对比度、透明关闭、缩放、状态可访问名称和键盘操作的体验修正；验证托盘结束按钮点击不激活，设置键盘导航可达所有可用配置，波形仅 Listening 显示且由真实数据驱动。
- [x] 6.7 完成运行说明、模型/在线配置、隐私/许可与六阶段验收记录；逐条核对文档中的命令、配置路径、服务名称与实际产物，记录已验证与未验证的硬件/权限/应用限制。
- [ ] 6.8 执行最终 x64 Debug / Release build、相关全部测试及 packaged 启动；分别使用真实在线与本地 Provider 完成完整记事本 16 步流程、连续录音、ESC、Provider 切换、设置关闭驻留、中文、多行、焦点变化、UIPI、剪贴板占用/恢复和锁屏恢复，交付逐项结果；只将实际完成的 task 标记完成。

## 7. 用户补充 — 程序内模型下载与管理

- [x] 7.1 列出支持的 base 模型及大小、语言、用途、许可、官方源与明确的第三方镜像；流式下载进度、取消、失败重试，大小/SHA-256/GGML 校验通过后原子安装，不覆盖已有文件；验证成功、截断、过大、哈希失败、网络错误、取消与并发互斥。
- [x] 7.2 接入 MVVM 模型卡片、导入/使用/打开目录/刷新/确认删除；导入保留外部源文件，使用保存 Provider 和路径，活跃 lease/推理期间拒绝删除，正常退出取消并等待清理；验证命令状态、保存失败、确认取消与资源保护。
- [x] 7.3 更新使用/来源/隐私文档，完成 x64 Debug/Release 构建、相关测试与实际下载/启动检查，记录结果和未验证边界。

规范已准备不代表本文件任务已执行。只勾选实际完成且满足验证条件的任务；真实验收通过且用户要求收尾后再执行 OpenSpec verify / archive。
