# 开发验证记录

## 2026-10-03 轻声与停顿收音调整

用户反馈声音偏小、经常提示没有检测到语音。保留已验证可避免本机 APO 初始化故障的 RAW 录音路径，生产 `StopAsync` 接入 `WavConverter.ConvertForSpeech`：60 Hz 高通抑制直流偏置和低频轰鸣，按 20 ms 窗口累计至少 200 ms 的有效声音（约 -56 dBFS），不再让长停顿拉低整段平均而拒绝轻声。有效录音使用单个增益，目标有效窗口 RMS 0.1、上限 8 倍、峰值不超过 0.95。保留全部样本和时长，不裁剪停顿或尾音。能量检查不具备区分人声和持续环境噪声的能力，不声称完成了语音活动检测或完整降噪。

- `dotnet test tests/VoxType.Tests/VoxType.Tests.csproj -p:Platform=x64 --filter FullyQualifiedName~WavConverterTests --no-restore`：**Passed: 26, Failed: 0, Skipped: 0**。`Speech_QuietVoiceWithLongPauses_IsBoostedAndAccepted` 验证 60 秒录音中的轻声短句：旧检查拒绝，新检查接受，整段 RMS 仍低于旧阈值，声音增益受限且原缓冲区不变。其他回归检查静音/直流、低频轰鸣、低噪声、单次碰麦、200 ms 活动边界、250 ms 时长边界、峰值余量、正常音量目标、48 kHz 双声道转换及尾音保留。
- `dotnet test tests/VoxType.Tests/VoxType.Tests.csproj -p:Platform=x64 --no-restore`：**Passed: 205, Failed: 0, Skipped: 0**。
- `dotnet build VoxType.slnx -p:Platform=x64 --no-restore --no-incremental`：成功，**0 警告、0 错误**。
- 隔离原生探针使用生产 `WasapiAudioCaptureService` 连续 10 次真实麦克风启动，交替 5 次停止、5 次取消全部成功，停止返回的音频已经过新处理路径。麦克风录音仅在内存处理并丢弃，未保存或上传。
- 已知合成中文样本降低峰值至 0.012，前后各加 4 秒静音：旧检查拒绝，新检查接受；处理前整段 RMS 0.000554、处理后 0.004317，时长相同。真实 CPU Whisper base 推理仍包含预期的“你好”和“语音”。结果记录在忽略目录 `artifacts/runtime-smoke/audio-quality-result.json`；这不能替代用户本人真实语音的准确率验收。
- 确认旧实例空闲后通过原有退出入口释放资源，使用 `winapp run src/VoxType.App/VoxType.App.csproj --arch x64 --on local --no-build --detach --json` 更新并启动开发实例。更新后的包内 Infrastructure DLL 与构建输出 SHA-256 相同，诊断为 Idle、模型存在、浮层隐藏。沿用本地 Provider、模型和用户的 Ctrl + Shift + Space 快捷键。

尚未完成真实人声、不同环境/距离/麦克风、与 Win+H 同句对比；不能宣称收音或识别质量已达到 Windows 语音输入。

## 环境

2026-10-03，本机 Windows 11 家庭版 Insider Preview 10.0.26340，Intel Core Ultra 5 225H，14 核/14 逻辑处理器。项目目标 Windows 11 x64，最低版本 22000，目标 SDK 26100。本机 UI 截图按 125%（120 DPI）显示；未完成其他缩放/多屏验收。

- .NET SDK 10.0.301；WinApp CLI 0.7.0；OpenSpec 1.14.0（已核对 npm 最新稳定版本，`openspec update` 已执行）。
- Windows App SDK 2.5.1；Windows SDK BuildTools 10.0.28000.2705；WinApp BuildTools / WinUIAnalyzer 0.7.1；CommunityToolkit.Mvvm 8.4.2。
- NAudio.Wasapi 3.1.0；Whisper.net / Whisper.net.Runtime 1.9.1；ProtectedData 10.0.12。
- MSTest 4.0.2；TimeProvider.Testing 10.10.0。项目锁文件记录完整解析依赖。

## 六阶段状态

|阶段|已获得的证据|尚未验证|
|---|---|---|
|1 应用骨架|官方 WinUI MVVM 模板、三工程依赖方向、编译、API 查询；packaged project-mode 激活；启动隐藏、单实例、三个设置页面、统一表单的 UIA 检查；解锁桌面静态浮层显示、实际结束按钮点击及显示/点击/隐藏保持外部前台|Explorer 重启、真正键盘导航、多屏/高对比度、实际录音中的浮层交互|
|2 快捷键与音频|键序列、watchdog、快速松键/延迟开始、忙碌、60 秒上限、取消清理；已知 PCM/float/extensible WAV、16 kHz/mono/PCM16、全程 RMS 与尾部样本；生产 WASAPI RAW 真实设备连续 10 次打开/快速停止/取消|设备拔插/隐私权限、左右 Alt/AltGr、窗口菜单、真实人声波形、锁屏/休眠恢复与 10 次完整语音输入|
|3 在线识别|multipart 字段/原音频字节、路径、可选 Key、语言、中文/多行 JSON；状态错误、限流、网络/TLS 异常、HTML、空结果、超大响应、取消、120 秒读取超时；生产 HttpClient 的真实回环 302 不跟随|真实兼容服务器凭据、真实录音转录、费用与网络环境下的取消/重试|
|4 文字输入|身份/焦点/权限边界的替代平台测试；0/部分/完整发送、合成键清理、修饰键等待、提交点取消、恢复结果、剪贴板新复制/新会话保护；x64 INPUT 为 40 字节；真实模型结果两次通过 Clipboard + SendInput 进入普通记事本，UIA 核对内容及编辑焦点；之前的 VoxType 剪贴板标记实际恢复|浏览器/UIPI、更多原剪贴板格式/占用、慢粘贴应用、窗口切换、真实人声完整交付|
|5 本地 Whisper|多语言 base 文件头与路径校验；factory 复用、串行处理、取消期间保留资源、路径切换、5 分钟卸载、退出等待；真实 CPU 原生 runtime 加载与导出调用；MSIX 内四个 win-x64 CPU DLL；合成中文样本连续 10 次真实推理、工作集和耗时；真实取消后重试、五分钟空闲卸载、重新加载及退出释放；开发包进程内模型路径可见|真实人声中文离线记事本链路、录音中 Provider 切换及完整系统验收|
|6 保存与体验|版本化 JSON、Windows 实际 DPAPI 往返/篡改失败、真实临时目录原子替换、重启读取、无明文 Key、无效设置回退、保存失败保留有效快照；Debug/Release 构建与未签名 MSIX|干净机器安装、最终录音/识别中退出、真实在线/离线各 16 步完整验收|

自动测试已通过 169 项，失败 0、跳过 0。Debug 与 Release 构建通过。测试按真实行为断言；未采集代码覆盖率，不能宣称达到某个覆盖率。局部 pure/fake 测试不证明真实 ASR 或 Windows 输入链路通过。

## 快捷键闪退修复与默认组合更新（2026-10-03）

用户报告按住组合后浮层偶尔闪一下就结束，并要求默认键避开 Alt + Space。原 watchdog 将被 Hook 吞掉的主键也纳入 `GetAsyncKeyState` 核对，但 Windows 不会更新被吞掉事件的异步状态。微软 [PowerToys 键盘处理文档](https://microsoft.github.io/PowerToys/modules/keyboardmanager/keyboardeventhandlers/) 明确描述这一行为。

隔离原生探针 `RuntimeSmoke --hotkey-polling` 安装真实低级键盘 Hook，仅处理自身标记的合成 F8 down/repeat/up。Ctrl / Win 仅在探针内存状态机中，不向桌面发送。主键已收到 down 的 1.5 秒内，15 次真实轮询都未报告按下；旧状态机中断一次，修复后零中断，最终 key-up 正常结束一次，三个主键事件全部成对吞掉。结果保存在忽略目录 `artifacts/runtime-smoke/hotkey-polling-result.json`。这是 Windows Hook/轮询行为的验证，不能当作人的物理键盘录音验收；生产服务依然忽略注入事件。

生产轮询改用 `ReconcileAsyncState`，保留被吞掉的主键和 ESC，依靠 Hook key-up 结束/清理配对；必要修饰键丢失仍中断。锁屏/休眠、取消和 60 秒上限保留。被吞主键的松开不能仅凭异步轮询可靠恢复；若整个 Hook 不再报告，修饰键丢失及会话通知、时长上限仍是兜底，不宣称完整解决 Windows 静默移除 Hook。

原默认曾改为 Ctrl + Win + F8，因为 Ctrl + Win + Space 可能与 [Windows 输入选项快捷键](https://support.microsoft.com/en-us/windows/keyboard-shortcuts-in-windows-dcc61a57-8ff0-cffe-9796-cb9706c75eec)冲突。2026-10-17 按用户要求先改为 Ctrl + Win + Space，最终改为 Ctrl + Shift + Space。配置版本 1 中的 Alt + Space 只迁移一次；其他自定义组合及版本 2 明确选回的 Alt + Space 保留。迁移原子写入并保持原 DPAPI 密文、Provider、模型路径。

`HotkeyMatcherTests` 和 `JsonSettingsStoreTests` 窄测试共 42 项通过，全套 169 项通过。新增四个快捷键测试方法（左右修饰参数展开为五项）和四个设置迁移测试方法。Debug 构建 0 警告、0 错误；Release MSIX 已重新生成。普通开发实例 PID 8600 已启动；进程内诊断确认版本 2、真实模型可见、空闲且浮层隐藏。用户已有 Ctrl + Shift + Space 作为自定义组合，实际保存并在设置 UIA 显示 Ctrl/Shift on、Alt/Win off、主键 Space，新默认提示及可用 Win 选项已核对。当前真实人的按键、说话、记事本交付结果仍待用户复测。

退出路径新增释放失败回归测试：Provider lease 释放异常仍会解除音频事件订阅并释放麦克风，迟到结果不会注入。App 也会在协调器释放异常时继续尝试卸载本地模型，然后执行原有 Windows 服务清理。

## UI 证据与修正

证据目录 `ui-test-artifacts/940d2b749a3a4bc998cc0ddf8d973a3b`：七项表单/进程生命周期检查通过；浮层结束按钮实际点击没有出现预期状态，记为 FAIL；隐藏焦点检查因 foreground HWND 为 0 记为 NOT_VERIFIED。Windows 工具曾明确返回 `no_interactive_desktop`，尚未取得可靠的可交互外部输入窗口。

早期检查曾只比较显示前后 `GetForegroundWindow` 的值，两个值均为 0，这不是焦点保持通过的证据。脚本现要求非零、可见的外部目标；前台为空或只有 VoxType 自身时不标通过。更新脚本后的结果另行追加，旧失败记录保留。

UI 自动化用 Debug 独立 profile 保留测试配置，避免替换正常用户设置。静态 `--check-overlay` 仅用于布局，无音频、ASR 或文字注入。当前未启动真实 Notepad 语音验收，也没有使用假转录替代真实结果。

最终从干净实例运行的证据目录 `ui-test-artifacts/403fa12401744c2abc7edcf5371a03df`：**8 PASS、0 FAIL、2 NOT_VERIFIED**。新增检查确认 Hook、焦点监听和剪贴板 STA 服务初始化成功，未打开麦克风。两项物理焦点检查在执行前因 WTS 明确报告锁定而停止；不是测试通过。独立查询返回 WTSINFOEX level 1、sessionFlags 0（锁定），生产目标采样/自动输入现也检查该状态。

此前在非干净实例上重跑脚本的目录 `41805936d1f34123be00a5eeeefb0616` 无效作为回归基线：静态浮层仍可见，且开发包文件已更新，重复激活遇到旧进程占用。保留该原始失败结果；退出旧测试实例并以 project-mode 重新构建/启动后得到上面的干净结果。`f1c60bd53e034bd396f4d2a4d17d3e0e` 也保留了锁定检查补强前的物理点击失败。

编译后期还按 WinUI code-review 检查了 partial ObservableProperty、动态 x:Bind、MVVM 边界、命令和访问名称：修正初始标题栏主题、修饰键 AutomationId、装饰图标/进度状态以及已保存快捷键的动态使用说明。单语言中文和固定浮层尺寸是本 MVP 的明确范围。服务启动的短同步等待只等待独立原生线程注册完成，不依赖 UI Dispatcher 回调；模型加载/推理、音频转换、HTTP 与文件写入在后台/异步路径执行。

最终正常 profile 已用 project-mode 启动设置窗口。UIA 可读取设置导航、就绪提示和保存按钮；`ui-test-artifacts/final-settings.png` 可见暗色标题栏，但锁定桌面下客户区截图为黑色，不能据此确认最终页面渲染或主题验收通过。解锁后仍需现场复查。

## 2026-10-03 用户问题复查

- 用户报告按住 Alt + Space 后系统显示麦克风占用，但没有输入或可见状态。原实例在取消/退出后仍有原生录音线程；线程报告保留于本次工具输出，未采集包含录音或密钥的进程 dump。
- NAudio `WasapiRecorder` 的异步启动会无条件写入 Capturing，可能覆盖早于启动完成的 Stop。新增 `RecordingStopWaiter` 重发停止请求直到 RecordingStopped；受控测试覆盖覆盖后的恢复及已停止不重试。
- 默认麦克风常规流重复打开时真实出现 `0x80040154`；换 STA 仍在第三次失败。RAW 流连续 10 次成功。生产服务已优先 RAW，并在明确 RAW 不支持时回退常规流；未修改系统增强/驱动/注册表。
- 使用生产 `WasapiAudioCaptureService` 连续 10 次真实打开、立即停止/取消通过，首轮约 1.25 秒，后续约 0.56–0.94 秒；录音仅内存保存。这不是连续 10 次完整语音输入验收。
- 手动下载多语言 base 模型，官方端点连接超时后从镜像取得，并比对 whisper.cpp 官方 SHA-1 `465707469ff3a37a2b9b8d8f89f2f99de7299dac`；SHA-256 为 `60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe`。模型未打入 MSIX，应用无自动下载行为。
- 真实 CPU 模型识别 SAPI 合成中文样本，输出“你好,这是一个语音收入测试。”，约 1.2–1.3 秒；输入/收入存在识别误差。这证明原生模型推理可用，不替代真实人声验收。
- MSIX 下根 AppData 文件在进程内不可见，诊断明确 ModelExists=false。默认模型改用 `ApplicationData.Current.LocalCacheFolder`，设置显示可复制的实际文件路径；模型已移到当前开发包默认目录并保存该路径，进程内诊断现为 ModelExists=true、IsBusy=false。
- `ui-test-artifacts/overlay-before-fix.png` 与 `overlay-visibility-probe.png` 是窗口 PrintWindow 捕获的黑色客户区；同一解锁桌面/窗口的 `overlay-screen-probe.png` 用实际屏幕区域捕获得到正常浮层。不能仅由前两张判断 WinUI 没有渲染。
- 未做最小化/恢复处理的新浮层首次显示也已用实际屏幕捕获核对：`overlay-fresh-window-screen.png` 正常显示。静态布局参数不打开麦克风，不替代真实波形。
- 当前正常实例的静态浮层在解锁桌面通过实际结束按钮点击，显示/点击/隐藏均保持同一个非零外部前台 HWND；核对 WS_EX_NOACTIVATE、WS_EX_TOOLWINDOW 且无 WS_EX_APPWINDOW。结果在 `artifacts/runtime-smoke/overlay-physical-result.json`，检查结束已隐藏布局浮层。该检查不打开麦克风，不证明物理快捷键及真实录音波形。
- 正常实例通过 Debug 同一录音入口实际打开麦克风后，UIA 可读“正在聆听”，实际区域截图 `overlay-real-recording-screen.png` 可见浮层，显示时前台仍为原外部窗口。随后前台变为 LockApp，脚本的取消后焦点比较失败；不能据此确认取消焦点保持，也不能归因为 VoxType 抢焦点。已执行取消，随后诊断 IsBusy=false、OverlayVisible=false。这不替代物理快捷键和真实人声验收。
- 真实本地模型空闲计时的第一轮在 322.9 秒仍未卸载，记录为失败。检查发现一次性 timer 提前回调时，单纯比较五分钟截止时间会退出且不再调度。生产代码现按剩余时间重新调度；`IdleTimerFiresEarly_RearmsUntilDeadline_ThenUnloadsAndReloads` 覆盖提前 1 毫秒回调、到期卸载及再次加载，相关 13 项生命周期/模型测试与完整 155 项均通过。真实计时重验结果另行记录。
- 专用空白 `voxtype-acceptance.txt` 记事本已打开。Windows 拒绝后台 SetForegroundWindow，UI 工具也拒绝向非前台目标发点击/聚焦。测试程序核对目标 PID 后拒绝注入；未在错误窗口粘贴。完整按键/人声/记事本交付仍待用户聚焦并试录。

上述模型安装和人工诊断发生在开发验收过程中，没有添加运行时模型下载或记录私人音频的功能。

## 真实本地模型生命周期复测

使用上述硬件、同一多语言 base 模型及已知 SAPI 合成样本，通过生产 `LocalWhisperService` 和真实 `WhisperBackend` 执行；观察包装器仅计数加载/释放及计时，不替代识别结果或五分钟时钟。修复后结果在 `artifacts/runtime-smoke/lifecycle-result.json`。

|次数|语言|识别耗时 ms（首轮包含模型加载）|工作集 MiB|
|---|---|---:|---:|
|1|zh|1086|187.6|
|2|auto|1574|193.6|
|3|zh|872|196.2|
|4|auto|1553|198.6|
|5|zh|870|198.7|
|6|auto|1576|199.0|
|7|zh|825|200.3|
|8|auto|1524|202.4|
|9|zh|834|203.5|
|10|auto|1524|203.6|

独立进程初始工作集 30.4 MiB。首次模型加载 203 ms（进程内首次加载，系统文件缓存未清空），十次识别只加载一次。第十次之后又在真实推理进入 100 ms 后取消，等待原生完成约 767 ms，未返回识别结果、未提前释放模型；下一次识别复用模型成功。

释放所有 lease 后使用真实时钟等待：300087 ms 时模型释放，工作集 55.3 MiB。下一次 auto 识别成功并重新加载，加载 332 ms；退出后总加载 2 次、释放 2 次，没有活跃原生调用。第一轮未卸载的失败记录保留于上节；此复测证明修复后生命周期通过。没有断开系统网络，合成样本及独立进程不等同于真实人声、物理快捷键、WinUI 输入和在线服务的完整链路。

## 原生文字输入与剪贴板复查

测试记事本 PID 29244 成为前台后，`winapp ui focus 'Text editor'` 已成功恢复并聚焦可见的 RichEdit 编辑区。真实多语言 base 模型识别已知合成样本，生产 `TextInjectionService` 经真实 `NativeClipboardHost` / `NativeInputTargetPlatform` 首次返回 PendingCopy，未发送粘贴。

原生诊断记录 Replace 序列 44 → 50、Close 50 → 53；GUID 标记长度 16、内容完全匹配，但 receipt 仍是 50，所以 IsOwned=false。官方说明序列号随剪贴板内容变更/清空递增，延迟呈现也会影响何时递增；本机进一步实测到了关闭时的序列变化。[GetClipboardSequenceNumber 文档](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getclipboardsequencenumber)

receipt 改为 Close 后采样最终序列。下一次原生检查还遇到刚关闭后 OpenClipboard 暂时失败，后来打开时序列和标记均相符；IsOwned 因此新增只针对占用的 25 ms 间隔、最长 1 秒重试。序列/标记不匹配立即拒绝，不重写剪贴板。修复后原生检查 receipt=71、实际序列=71、标记匹配、IsOwned=true。

真实模型输出“你好,这是一个语音收入测试。”随后通过 Ctrl+V 进入记事本；UIA 核对编辑区内容和焦点。第一次因原剪贴板含此前 VoxType 自身标记而报告复杂格式，未恢复；Snapshot 已将已知自身标记纳入可保存格式。第二次完整发送返回 Dispatched、ClipboardRestored=true，原 GUID 标记也实际恢复。最终编辑区长度 28，已知转录出现两次，编辑区仍有焦点；输出仍包含上节记录的“输入/收入”识别误差。

`artifacts/runtime-smoke/notepad-injection-result.json` 保存 PID、发送状态、恢复布尔值，不含私人录音/密钥。新增五项回归覆盖关闭序列、保留标记的新复制、短暂/持续占用和恢复上一段 VoxType 文本而不复活旧 receipt。关闭序列和自身标记两例在修复前实际失败，修复后相关 17 项、完整 160 项通过。

这些检查证明真实模型结果到普通记事本的生产注入和一次剪贴板恢复通过，尚不证明物理全局键、人声麦克风、浏览器、UIPI、其他剪贴板格式/占用及完整在线流程。在线有效配置仍未提供；正常 profile 为本地，在线 URL 为默认 OpenAI 地址且没有已保存 Key。已重新构建并启动修复后的正常实例，未修改这些 Provider 配置。

随后尝试追加真实剪贴板更多格式的检查，初始 OpenClipboard 在有限重试内始终未成功，检查在第一项写入前退出，没有执行 Replace。复查 foreground HWND=0；再用 WinApp UI 聚焦同一记事本，工具明确返回 `no_interactive_desktop`。这次追加检查记为未验证，不算格式恢复通过。正常 VoxType 实例仍为 PID 29064；最后诊断 IsBusy=false、ModelExists=true、OverlayVisible=false。单实例诊断启动的次实例 PID 37904 已自行退出，没有终止正常实例。

## 最新产物

### 2026-10-03 程序内本地模型下载管理

已接入语音识别页的多语言 Whisper base 模型卡片（148 MB）、官方源/明确的第三方镜像选择、下载/取消/重试、导入、使用、打开目录、状态刷新与确认删除。下载安装采用 147951465 字节、SHA-256 和 GGML base 头校验，同目录临时文件原子发布且不覆盖已有文件。使用按钮只保存本地 Provider/路径，不混入其他未保存表单编辑；活跃 lease/原生推理时拒绝删除。DEBUG 测试配置同时隔离设置和程序模型目录，验证期间未更改用户原模型或自定义快捷键。

- `dotnet build VoxType.slnx -p:Platform=x64 --no-restore -v:minimal` 与 Release 同命令加 `-c Release`：0 警告、0 错误。
- 新增窄测试 21/21；最终完整套件 190/190、失败 0、跳过 0。具体要求/方法映射见 `.testagent/status.md` 的最新补充。
- 生产 LocalModelManager 真实联网：官方源返回 HttpRequestException；HF-Mirror 完成取消清理和重新下载，完整文件 147951465 字节，SHA-256 与上游相同，64 次进度报告（含校验阶段）。这两次镜像请求约 12.3 秒，不能作为所有网络速度的保证。证据 `artifacts/model-download-smoke/evidence/20261003135407/result.json`。
- `scripts/test-model-manager.ps1` 在独立配置 `97f96a92bdc54c21b253420d3b79cf9a` 的真实 packaged 进程运行：9 项全通过。验证未安装命令、主动选镜像、下载取消清理、关闭设置继续下载、完成校验后使用、删除确认取消、确认删除、系统文件选择器导入且保留源、打开模型目录。报告 `ui-test-artifacts/model-manager-fbd6e4bfdcb34f289507ffb43a531af0/result.json`；五张截图均已逐张检查。首轮脚本误选文件选择器 Open 下拉按钮的失败报告保留，修正测试选择器后重跑通过。
- 实际下载中显式退出：进程结束，既无最终模型也无 `.download` 残留，证据 `artifacts/model-download-smoke/model-exit-result.json`。已恢复普通配置并打开新版设置，默认目录模型校验通过。
- 最新未签名 MSIX 位于 `artifacts/VoxType.App_1.0.0.0_x64.msix`；SHA-256 `A5F0AE83B332CA40CF564F164335A5DF6BBBE487093533266A9FBAA5367C9F1D`。重新检查包含三个生产程序集和更新后的 ThirdPartyNotices，未将模型放入安装包。

此轮检查模型安装管理功能；未重做真实人声录音/记事本完整链路、亮色/高对比度、其他 DPI 和键盘导航全量验收。OpenSpec 新增第 7 阶段已完成，原来缺少完整验收证据的任务继续保留未完成。

`winapp package src/VoxType.App/VoxType.App.csproj --arch x64 --no-sign` 已成功生成 `VoxType.App_1.0.0.0_x64.msix`。已读取包内清单及 native 文件列表，CPU 文件仅 win-x64 四个 DLL。包依赖 Windows App Runtime 2.5.1，并保留 Win11 最低版本、桌面权限及麦克风 capability。未签名、未安装生产证书，未做 Store 发布。

OpenSpec 只勾选满足完整验证条件的任务；代码已经接入但缺少任务指定的真实系统验收时仍保留未完成状态。变更没有归档。

### 2026-10-17 Whisper 本地 CPU/Vulkan 选择

语音识别页新增本地 Whisper 的 `cpu` / `vulkan` 选择，旧设置文件缺少该字段时默认 CPU。模型缓存按模型路径和推理设备区分；Whisper.net 原生库一旦在进程内加载，切换设备需要重启应用。R2T2 的后端继续由服务端脚本选择。

Release/x64 WinUI 应用构建成功，0 警告、0 错误。新增设置持久化、ViewModel 保存和模型缓存切换测试；完整套件在 Windows 上通过 323/323。清理后重新构建的应用目录包含 Windows x64 CPU/Vulkan 原生库，不包含 CPU 的 win-x86/win-arm64 或 Vulkan 的 linux-x64 文件。本机 `global.json` 要求 SDK 10.0.301，但可用版本为 10.0.203；验证命令从仓库外启动，以该已安装 SDK 构建，NuGet 缓存设在 D 盘。

真实模型烟测使用官方 `ggml-base.bin`（147951465 字节，SHA-256 `60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe`）和本机参考项目 `Whisper/SampleClips/jfk.wav`。测试通过生产 `WhisperBackend` 在独立进程分别加载后端，NVIDIA GeForce RTX 4080 Laptop GPU 上 `LoadedLibrary=Vulkan`，CPU 进程为 `LoadedLibrary=Cpu`；两者都输出相同的 JFK 句子。一次冷启动 Vulkan 推理约 5.8 秒，随后再次独立进程测试时 Vulkan 两次约 0.82/0.73 秒，CPU 两次约 1.07/1.07 秒。这是短样本和单台设备的观察值，不代表所有设备、录音长度或每次首次运行的加速比；未测试无 Vulkan 显卡的机器和打包后的 UI 操作。

### 2026-10-17 R2T2 本机 CPU/Vulkan 选择与启动

语音识别页在 R2T2 Provider 下增加设备、数据目录、资源准备和本机服务启动入口。旧设置缺少新字段时默认 CPU；启动入口保存 `http://127.0.0.1:8272/v1` 与 `r2t2-q4`，并在可见的 PowerShell 7 窗口运行现有脚本。安装输出包含两个脚本、公共脚本、资源清单、聊天模板及适配服务源码。适配服务的构建中间文件写入数据目录，避免在安装目录写入。

Release/x64 WinUI 应用构建成功，0 警告、0 错误；完整测试 326/326 通过。PowerShell 解析器检查准备、启动、公共脚本均无语法错误。`git diff --check` 通过。使用当前 `winapp package` 从 Release/x64 应用输出创建临时未签名 MSIX，读取包内 ZIP 清单确认两个启动脚本、公共脚本、资源清单、适配服务源码/项目文件、聊天模板和 Whisper CPU/Vulkan Windows x64 原生库均已打入；未安装该包。

在本机 NVIDIA GeForce RTX 4080 Laptop GPU 上，资源准备脚本复用已校验的两个 Q4 模型并取得 llama.cpp b10950 Vulkan 运行时。改动后的启动脚本成功构建适配服务、加载模型并通过健康检查。官方测试 WAV 发送至 `POST /v1/audio/transcriptions` 后得到中文识别结果“之前有顾客自己带酒水，也没加收钱或者不让喝。”。Ctrl+C 停止后，8272/8273 端口及两个服务子进程均退出。此烟测证明服务端 Vulkan 路径在这台设备上可用；尚未实测打包后 UI 的按钮操作及无 GPU 设备上的 CPU 路径。

### 2026-10-17 R2T2 开发版默认目录复用

用户启动 Debug MSIX 时，应用默认指向包的 LocalCache，和上午已准备好的仓库 `r2t2-data` 不同。LocalCache 当时只有一个 51,075,758 字节的 `.partial` 文件；完整模型在仓库目录，大小分别为 1,107,404,736 和 641,773,984 字节。使用 `prepare-r2t2.ps1 -ModelsOnly -DataDirectory r2t2-data` 重新验证，两模型均通过官方大小、GGUF 与 SHA-256 校验。CPU 和 Vulkan 运行时也都已在该目录准备好。

开发版现在会从应用目录向上查找仓库 `r2t2-data` 并复用；MSIX 安装版继续使用 LocalCache。Debug/x64 构建成功，0 警告、0 错误。现有运行实例仍可在数据目录输入框显式填入仓库路径继续使用，无需重新下载模型。

### 2026-10-17 R2T2 流式输出预览

原有本地推理设备和启动入口先保存为 `6034ae6`，流式功能在 `codex/r2t2-streaming-output` 分支实现。服务将完整 WAV 发送给本机 llama-server，启用其 Chat Completions SSE 输出，过滤可能跨 token 拆开的语言前缀和结束标记，再通过 `transcript.text.delta`／`transcript.text.done` 转发给客户端。麦克风仍在松开快捷键后提交完整录音。

R2T2 设置新增默认开启的“流式显示识别结果”，可以关闭并保存来比较效果。客户端将增量文字作为累积预览传给会话；预览最多每 50ms 更新一次，最终结果强制刷新。悬浮层固定预览宽度，显示最近 64 个 UTF-16 代码单元附近的文字，避免切开代理对。目标输入框只接收验证完成后的完整结果；中途错误、取消和未收到完成事件均丢弃未完成结果。Whisper 和 OpenAI Compatible 继续使用原有 JSON 流程。

验证命令：

```powershell
dotnet test tests/VoxType.Tests/VoxType.Tests.csproj -p:Platform=x64 --no-restore --filter 'FullyQualifiedName~Streaming|FullyQualifiedName~R2T2HttpContractTests' --nologo
dotnet test tests/VoxType.Tests/VoxType.Tests.csproj -p:Platform=x64 --no-restore --nologo --logger 'trx;LogFileName=r2t2-streaming-regression.trx'
& 'C:/Users/OMEN/.codex/skills/winui-dev-workflow/BuildAndRun.ps1' 'src/VoxType.App/VoxType.App.csproj' -SkipRun /p:Configuration=Release /p:RuntimeIdentifier=win-x64 /t:Rebuild
git diff --check
```

窄范围检查 **Passed: 40, Failed: 0, Skipped: 0**；之后补充悬浮层和开关检查，最终完整回归 **Passed: 354, Failed: 0, Skipped: 0**。TRX XML 已解析，确认 354 项全部执行且成功，并核对关键测试名称。Release/x64 非增量重建成功，无编译或 WinUI analyzer 诊断。构建脚本最初没有指定 RuntimeIdentifier，误选 win-x86 而出现 NETSDK1032；显式设置 win-x64 后完成重建，脚本临时 analyzer 文件已清理，构建带来的无关锁文件变化已恢复。

| Requirement | Evidence |
|---|---|
| 识别完成前可看到增量文字 | `StreamingHttp_PreviewArrivesBeforeUpstreamCompletes` 通过真实 Kestrel 和独立适配器进程，阻塞上游的后半段响应，确认客户端先收到“你好”且最终任务仍未完成 |
| 协议标记、中文 UTF-8 和 SSE 跨读取边界 | `LlamaStreaming_SplitProtocolAndUtf8ProducesCleanDeltas` 使用单字节、7 字节和 4096 字节读取，断言首段和最终拼接文字，语言／结束标记不会泄露到预览 |
| 断流、截断、错误与响应大小上限 | `LlamaStreaming_UnfinishedOrInvalidOutputNeverSucceeds`、`LlamaStreaming_ResponseBudgetChecksExactBoundary`、`R2T2Streaming_FailedStreamCannotBecomeSuccessfulResult`、`StreamingHttp_ErrorAfterPreviewDoesNotReturnFinalResult` |
| 超时和取消后释放推理资源 | `LlamaStreaming_CancellationOrTimeoutReleasesInferenceGate` 验证首段输出后取消或超时，下一次识别可以取得同一 gate |
| 预览不提前输入、取消后迟到结果被丢弃 | `StreamingPreview_IsPublishedBeforeFinal_AndOnlyFinalIsInjected`、`StreamingPreview_AfterCancelIsIgnored_AndNothingIsInjected` |
| 悬浮层可读、比较开关和旧协议兼容 | `StreamingPreview_LongTextShowsTailWithoutBreakingSurrogatePair`、`StreamingPreview_DisplaysRecognizedTextAndClearsItOnNextState`、`R2T2StreamingToggle_SavesExplicitChoice`、`StreamingSetting_DefaultsOnForOldFiles_AndPersistsDisabledChoice`、`StreamingDisabledOrOtherProviderKeepsJsonRequest` |

本次未完成真实 Q4 流式延迟和原生 UI 体验验收。机器上已有 Vulkan llama-server 与旧适配器运行，计划以公开样例和临时适配器验证真实模型；自动审批拒绝了包含下载样例和启动临时适配器的命令，只返回 `blocked by policy`，未提供具体原因。没有据自动化上游替身的成功宣称真实模型首字延迟或加速效果。未停止已有进程、修改用户现有配置或安装新 MSIX。
