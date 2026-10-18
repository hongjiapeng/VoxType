# 语音浮窗

默认按快捷键出现录音胶囊，录音、识别和发送文本时持续显示状态。发送完成后约 600 毫秒收起。失败提示保留，可点击「重试」，或通过更多菜单进入设置、收起提示。

## 位置与常驻

- 按住波形、状态文字或左侧手柄直接拖动浮窗，松开后保存位置。采用与 SlidePace 浮动 HUD 相同的 `Window.SetTitleBar` 系统拖动区域；结束、重试和更多按钮保留点击功能。待机入口从左侧手柄拖动。
- 直接拖拽即可调整位置，不再保留四方向「微调位置」菜单。
- 每次新录音出现时检查输入位置，尽量避让输入框或光标附近。一次录音内的位置保持稳定；用户仍可主动拖动。
- 「设置 → 常规 → 语音浮窗 → 固定在桌面」立即生效并保存，也可在浮窗更多菜单中切换。
- 固定后，完成或取消会收成小麦克风入口；**入口可见不表示正在收音**。点击麦克风才开始录音，点击「结束」提交识别。
- 「恢复自动位置」清除手动位置，不改变是否常驻。

浮窗保持不激活窗口的行为。点击更多菜单中的「设置」后，等待菜单关闭再把主窗口激活到前台；主窗口已最小化时先恢复。位置按显示器和工作区内的比例保存，以适应缩放、任务栏和显示器配置变化。原显示器不可用时，使用目标窗口所在的可用显示器。浮窗偏好独立保存在设置目录下的 `overlay.json`，不覆盖识别服务配置。

## 外观与图标

浮窗使用透明系统背景，只有 XAML 胶囊绘制底色、圆角和边框。移除原生矩形底布、DWM 边框和二值 HRGN 裁剪，由 XAML 提供透明度抗锯齿。主题切换时重新填满 WinUI 内容子窗口，避免顶部留下原生标题栏间隙。透明背景独立处理主题和激活变化，避免默认背景控制器在反复切换时访问不匹配的目标。

应用、托盘、任务栏和安装包共用青绿色「麦克风 + 文本光标」图标；16–24 像素版本简化为居中的麦克风。可编辑原稿在 `src/VoxType.App/Assets/VoxType.svg`，运行 `python scripts/generate-app-icon.py`（需要 Pillow）可重新生成 ICO 和安装包各尺寸图片。

## 避让范围

优先读取系统暴露的、范围明确的输入子窗口；没有合适输入窗口时读取原生文本光标并留出行间和水平空间。部分浏览器、自绘编辑器或权限不同的应用可能不暴露这些位置，此时沿用记忆位置或默认位置，由拖动兜底。输入区域占满可用屏幕、没有可用避让空间时也保留手动调整能力。

## 验证

单元及回归测试：

```powershell
dotnet test tests/VoxType.Tests/VoxType.Tests.csproj --logger 'trx;LogFileName=overlay-feature-regression.trx'
```

本次完整回归结果：387 项通过，0 失败，0 跳过。WinUI Debug x64 构建通过。

本次 UI Automation 与原生窗口验收：11 项通过，0 失败，3 项未验证。已验证系统 Caption 区域覆盖手柄、波形和文字，排除操作按钮；通过原生移动开始、位置变更与移动结束消息验证松手后保存位置，以及后续状态切换不回跳。还包括完成状态不会在更多菜单仍打开时收起浮窗。已检查暗色录音、待机入口、失败重试和亮色录音状态截图。

| 行为 | 自动验证证据 |
| --- | --- |
| 输入区域避让、优先保留安全位置 | `OverlayInteractionTests.AutomaticPlacement_BottomInputMovesOverlayAboveProtectedLine`、`SavedPosition_StaysPutWhenInputDoesNotOverlap`、`SavedPosition_IsTemporarilyAvoidedWithoutChangingAnchor` |
| 屏幕边界、负坐标、DPI 与位置恢复 | `OverlayInteractionTests.Placement_ClampsOversizedOverlayToSmallMonitor`、`Drag_ClampsAllEdgesOnNegativeCoordinateMonitor`、`DraggedPosition_RestoresProportionAfterDpiAndWorkAreaChange` |
| 待机不呈现录音、失败后允许重试 | `OverlayInteractionTests.Standby_ClearsTranscriptAndNeverLooksLikeRecording`、`Failure_OffersRetryAndNewRecordingClearsIt` |
| 常驻、位置与重置保存，异常配置回退 | `OverlayInteractionTests.Preferences_RoundTripPinnedAndManualPosition_AndResetRetainsPin`、`Preferences_InvalidAnchorKeepsPinAndFallsBackToAutomaticPosition`、`Preferences_MissingOrCorruptFileDefaultsToTemporaryMode` |
| 菜单简化、前台焦点、常驻/临时切换、完成收起、失败重试、主题 | `scripts/test-overlay-interaction.ps1` 的 UI Automation 验收与截图 |
| 原生系统拖动区域、按钮排除、原生移动结束后保存位置 | `Native caption region includes waveform and text but excludes buttons`、`Native move completion saves actual position and survives state updates`（界面验收脚本） |
| 透明底布、抗锯齿、内容填满窗口、主题切换无顶线、设置激活与最小化恢复、常驻入口反复切换主题 | `scripts/test-overlay-polish.ps1`，6 项通过；截图检查四角 Alpha 为 0、圆角含半透明边缘、深浅主题往返后顶部像素一致 |

UI 验收使用 Debug 静态场景和独立 `--test-profile=<32位GUID>`，不录音、不识别、不向其他程序输入文字。对运行中的开发实例运行：

```powershell
./scripts/test-overlay-interaction.ps1 -AppPid <进程ID> -AppUserModelId <开发包AUMID> -ProfileId <启动时使用的GUID>
./scripts/test-overlay-polish.ps1 -AppPid <进程ID> -AppUserModelId <开发包AUMID>
```

当前桌面无法把测试编辑器置于前台，真实输入框避让、手柄鼠标拖动、波形/文字鼠标拖动三项标为 `NOT_VERIFIED`；系统实际注册的拖动区域、原生移动消息处理、位置算法和移动后的保存、恢复已分别验证。可使用 `-SkipPhysicalInput` 只运行 UI Automation 与原生窗口验收。验收脚本把结果和截图写入 `ui-test-artifacts/interaction-*/`，不会把环境受限项计入通过数。
