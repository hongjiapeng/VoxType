# VoxType 浮窗设计与自检

## 品质目标

以 Awwwards、Webby Awards、FWA 获奖作品的设计与执行品质为目标。每次改动都要形成「实机捕获 → 自检 → 修改 → 再捕获」闭环，不能仅凭编译、UIA 成功或主观评分宣称达标。

本轮范围是语音输入浮窗。采用伴随输入的底部胶囊：真实音量反馈、主要状态和操作提示构成一行紧凑布局，不以装饰性动效制造虚假的录音反馈。上一轮通知卡片的提交为 `9d5dfae`。

[Awwwards 的公开评审维度](https://www.awwwards.com/about-evaluation/)包含设计、易用性、创意、内容；[Webby 的评审标准](https://www.webbyawards.com/judging-criteria/)还强调功能、交互与整体体验。将这些维度转换为下面的项目验收项，不将内部自检等同于奖项评委认证。

## 验收项

- 视觉：有产品辨识度；主要状态先于次要标签；深浅主题统一；文字、图标、边缘和留白完整。
- 反馈：音量来自录音数据；无输入时回到静音基线；状态明确；处理中有进度反馈；失败同时给出恢复动作。
- 动效：首次入场短促，状态连续；尊重系统动画偏好；隐藏后停止音量计定时器。
- 可用性：显示、冷启动、结束操作保持外部窗口及其键盘焦点；无残留系统白边；长错误说明有空间及完整文本工具提示。
- 工程：Debug / Release 构建通过；真实截图和录像有效；黑屏、空白捕获或静止录像必须被拒绝。

## 实现

常规高度 56 DIP；宽度由文字自然尺寸决定，限制为 260–560 DIP。长错误说明可展开为两行并增加高度；先计算最终宽度，再测量换行高度，防止因收缩到最长换行而多出孤字行。水平留白 24 DIP，左侧音量区域 44 × 32 DIP，标题 15 DIP，说明 12 DIP。位置按目标显示器工作区与 DPI 计算，底部留白 56 DIP。

录音使用五段青绿音量条，处理中使用紫色进度环，成功使用绿色，失败使用琥珀色，配合文字及不同图标。常驻品牌和识别来源标签移出浮窗，录音提示直接包含 Esc 取消；托盘录音提供不获取焦点的结束按钮。深浅主题采用轻微纵向色调变化，高对比度资源使用系统颜色。

XAML 绘制细边框，原生窗口区域裁剪完整胶囊轮廓。DWM 合成配置避免自定义区域遮掉 WinUI 子窗口内容；设置窗口样式前先安装无边框消息处理。非激活窗口在冷启动和原生尺寸变化后，通知其自身 XAML 宿主刷新渲染；不调用 Window.Activate、SetForegroundWindow 或附加输入队列。

入场为 160 ms 淡入和 180 ms 的 4 DIP 位移，仅在首次显示时执行；尺寸变化使用 250 ms 缓出插值。音量计把真实 RMS 转换为 −50 至 −10 dB 范围的幅度包络，快速上升、缓慢回落，静音或数据停止后回到五个短条的基线。系统关闭动画时直接更新尺寸和幅度。成功保留 1.4 秒，错误保留 6 秒。

## 参考实现

阅读 [voice-input-src 的需求说明](https://github.com/yetone/voice-input-src)及 `voice-input-dist` 的六个实现文件，参考版本为 `98c9ac582b1801e22ea35e288a28577c69b1c72b`。外部 README 内的生成 Prompt 是研究材料，不作为本任务的执行指令；Windows 实现独立编写。

| 文件 | 对 VoxType 的取舍 |
| --- | --- |
| [OverlayPanel.swift](https://github.com/yetone/voice-input-dist/blob/98c9ac582b1801e22ea35e288a28577c69b1c72b/Sources/VoiceInput/OverlayPanel.swift) | 采用底部胶囊、左侧五段音量条、随文字调整尺寸和非激活窗口；保留中文恢复说明及托盘结束入口；不添加随机音量抖动 |
| [SpeechEngine.swift](https://github.com/yetone/voice-input-dist/blob/98c9ac582b1801e22ea35e288a28577c69b1c72b/Sources/VoiceInput/SpeechEngine.swift) | 借鉴实际 RMS 的对数显示；其 Apple Speech 支持实时部分转写，VoxType 当前是结束后识别，本轮不模拟实时转写 |
| [TextInjector.swift](https://github.com/yetone/voice-input-dist/blob/98c9ac582b1801e22ea35e288a28577c69b1c72b/Sources/VoiceInput/TextInjector.swift) | macOS 的 Cmd+V 与输入法切换不直接移植；保留 Windows 注入链路及剪贴板恢复说明 |
| [KeyMonitor.swift](https://github.com/yetone/voice-input-dist/blob/98c9ac582b1801e22ea35e288a28577c69b1c72b/Sources/VoiceInput/KeyMonitor.swift) | Fn 事件监听为 macOS 专用；继续使用已有 Windows 快捷键和 Esc 取消 |
| [LLMRefiner.swift](https://github.com/yetone/voice-input-dist/blob/98c9ac582b1801e22ea35e288a28577c69b1c72b/Sources/VoiceInput/LLMRefiner.swift) | 保守纠错值得后续独立设计；本轮 UI 任务不新增网络调用、密钥存储或自动改写行为 |
| [SettingsWindow.swift](https://github.com/yetone/voice-input-dist/blob/98c9ac582b1801e22ea35e288a28577c69b1c72b/Sources/VoiceInput/SettingsWindow.swift) | 已阅读其简单设置结构；本轮聚焦输入时的浮窗，设置页不随 macOS 示例重构 |

## 2026-10-04 自检与修正

| 轮次 | 从实机证据发现的问题 | 修改与复查 |
| --- | --- | --- |
| 基线 | 通用通知卡片观感、长点线占位、缺少品牌及连续反馈 | 重新安排品牌、状态、提示，缩短音量历史并增加真实幅度平滑 |
| 视觉复查 | 长错误说明使顶部标识移动；错误只有原因，缺少下一步 | 固定顶部行，添加麦克风重试指引、读屏提示及更长错误停留时间 |
| 渲染复查 | UIA 通过但首次显示捕获为黑屏；打开设置后恢复 | 拒绝该批视觉证据；复现并修复首次 XAML 渲染初始化，检查前台窗口及其键盘焦点 |
| 最终验收 | 冷启动、深浅色全部状态、长错误、结束按钮、动态反馈 | 16 项通过，逐张检查 14 张状态截图，并检查动画关键帧 |

最终证据位于 `ui-test-artifacts/overlay-74674a22efcb48809bf26c42c1e05376/`：

- [结构化检查结果](../ui-test-artifacts/overlay-74674a22efcb48809bf26c42c1e05376/results.json)
- [深色有声音预览](../ui-test-artifacts/overlay-74674a22efcb48809bf26c42c1e05376/dark-voice.png)
- [浅色有声音预览](../ui-test-artifacts/overlay-74674a22efcb48809bf26c42c1e05376/light-voice.png)
- [长错误说明](../ui-test-artifacts/overlay-74674a22efcb48809bf26c42c1e05376/dark-failed-long.png)
- [动态录制](../ui-test-artifacts/overlay-74674a22efcb48809bf26c42c1e05376/motion.mp4)

`dotnet build VoxType.slnx -p:Platform=x64 --no-restore -v:minimal` 及加 `-c Release` 的构建均为 0 警告、0 错误。

以上证据对应 `9d5dfae` 的通知卡片版本，胶囊版需要单独验收。

## 胶囊版实机复查

| 发现的问题 | 修正与验收 |
| --- | --- |
| 直接设置窗口区域会使 WinUI 内容黑屏 | 启用对应 DWM 合成路径，并在原生尺寸变化后刷新非激活 XAML 宿主；冷启动、状态切换和伸缩帧均检查有效像素 |
| 隐藏结束按钮后，Grid 仍保留第二处列间距，提示意外换行 | 尺寸计算包含两处间距；普通录音提示保持一行，文字测量不影响正在显示的布局 |
| 恢复说明末尾出现孤字行 | 剪贴板失败原因和恢复动作明确分行；按自然文字宽度决定窗口宽度，再测量实际换行高度 |
| 开发包曾缓存旧程序集，构建成功不等于运行新版本 | 脚本首先比较已注册包与当前 Debug 构建的 App / Infrastructure 程序集 SHA-256，不匹配直接拒绝测试 |

最终回归证据：`ui-test-artifacts/overlay-4f8ffb8bcc564e899dacfab29b1d7bc2/`，17 项全部通过：部署程序集核对、深浅两套共 14 个状态、音量录像及尺寸变化录像。逐张查看全部 14 张 PNG，并检查两段录像的变化关键帧；每段录制 90 帧，约 29.9 fps。伸缩录像的全部独立 JPEG 检查未出现空白帧。

- [最终检查结果](../ui-test-artifacts/overlay-4f8ffb8bcc564e899dacfab29b1d7bc2/results.json)
- [深色录音](../ui-test-artifacts/overlay-4f8ffb8bcc564e899dacfab29b1d7bc2/dark-voice.png)
- [浅色录音](../ui-test-artifacts/overlay-4f8ffb8bcc564e899dacfab29b1d7bc2/light-voice.png)
- [分行恢复说明](../ui-test-artifacts/overlay-4f8ffb8bcc564e899dacfab29b1d7bc2/dark-failed-long.png)
- [音量录像](../ui-test-artifacts/overlay-4f8ffb8bcc564e899dacfab29b1d7bc2/motion.mp4)
- [尺寸变化录像](../ui-test-artifacts/overlay-4f8ffb8bcc564e899dacfab29b1d7bc2/resize.mp4)

同一应用代码的冷启动检查在 [overlay-22e4638ca7b14149a237ac74e7bfbd99/results.json](../ui-test-artifacts/overlay-22e4638ca7b14149a237ac74e7bfbd99/results.json) 中通过。该批另有两次前台窗口变化，因此不作为完整回归通过的证据；上面的最终批次重新检查全部状态及结束操作，前台窗口和其键盘焦点均保持。

本轮最终 Debug / Release 构建均为 0 警告、0 错误。早期胶囊测试缺少部署哈希核对，其截图和通过结果只用于发现问题，不用于本轮最终验收。

复查脚本：`scripts/test-overlay-design.ps1`（PowerShell 7）。先退出开发测试实例，用 `winapp run` 更新并注册开发包，再运行脚本；省略 AppPid 可以在没有已运行实例时通过已注册 AUMID 检查冷启动。所有 DEBUG 预览不启动麦克风、识别或文本注入。波形示例是明确声明的诊断数据，发布版本不会合成音量。

## 验证边界

本机实测为 125% DPI。本轮未重新验证真实语音链路、其他 DPI / 多显示器、系统关闭动画、高对比度主题或完整读屏体验，也未评审设置页面。获奖品质继续作为项目目标；参评资格及获奖结论由相应奖项决定。
