# 语音浮窗

快捷键录音只显示波形和一行文字，没有额外手柄、结束按钮或更多菜单；尚无转录时提示「松开结束」，实时转录到达后直接用文字替换提示。点击托盘或常驻入口开始的录音保留结束和更多按钮。

松开快捷键立即收起录音条；识别超过约 350 毫秒再显示紧凑的「识别中」提示，期间不重复展示临时转录。文本写入前隐藏浮窗和菜单，成功后直接隐藏或回到可选常驻入口。失败提示保留，可点击「重试」，或通过更多菜单进入设置、收起提示。

浮窗采用单行内容和随文字平滑调整的宽度，活动状态宽 180–420 DIP、高至少 52 DIP；系统文字缩放可增加高度。失败也只显示简短原因和重试，不把完整错误铺成第二行；完整错误保留在无障碍说明、重试按钮的悬停提示及设置页中。五根音量柱在完整的 44 DIP 区域内绘制，无随机跳动，静音回落。长转录保留最新片段，按完整文本元素截取，完整内容保留在提示中。交互布局参考 [VoiceInput](https://github.com/yetone/voice-input-dist) 的「波形＋单行文字」设计，在 WinUI 中独立实现。

## 位置与常驻

- 按住波形或状态文字直接拖动录音浮窗，松开后保存位置。采用与 SlidePace 浮动 HUD 相同的 `Window.SetTitleBar` 系统拖动区域；结束、重试和更多按钮保留点击功能。待机入口保留左侧手柄。
- 直接拖拽即可调整位置，不再保留四方向「微调位置」菜单。
- 每次新录音出现时检查输入位置，尽量避让输入框或光标附近。一次录音内的位置保持稳定；用户仍可主动拖动。
- 「设置 → 常规 → 语音浮窗 → 固定在桌面」立即生效并保存，也可在浮窗更多菜单中切换。
- 固定后，完成或取消会收成小麦克风入口；**入口可见不表示正在收音**。点击麦克风才开始录音，点击「结束」提交识别。
- 「恢复自动位置」清除手动位置，不改变是否常驻。

浮窗保持不激活窗口的行为。点击更多菜单中的「设置」后，等待菜单关闭再把主窗口激活到前台；主窗口已最小化时先恢复。位置按显示器和工作区内的比例保存，以适应缩放、任务栏和显示器配置变化。原显示器不可用时，使用目标窗口所在的可用显示器。浮窗偏好独立保存在设置目录下的 `overlay.json`，不覆盖识别服务配置。

## 外观与图标

浮窗使用透明系统背景，只有 XAML 胶囊绘制底色、圆角和边框。浅色采用柔和浅灰渐变，不绘制外沿描边；结束、重试和麦克风按钮使用同色系底色，不再使用默认白底描边。高对比模式保留系统按钮色和轮廓。录音条和常驻入口均使用这一规则。移除原生矩形底布、DWM 边框和二值 HRGN 裁剪，由 XAML 提供透明度抗锯齿。主题切换时收起已有菜单与悬停提示，并重新填满 WinUI 内容子窗口，避免顶部留下原生标题栏间隙。透明背景独立处理主题和激活变化，避免默认背景控制器在反复切换时访问不匹配的目标。

浅色浮窗隐藏后再次显示时，WinUI 的原生 `WM_ERASEBKGND` 会按主题绘制不透明白底，使圆角外露出矩形，并可能残留到深色主题。浮窗现在拦截该消息，用 DWM 全客户区扩展所需的零值背景清除原生底层，圆角和抗锯齿仍由 XAML 绘制。依据：[WinUI 原生背景绘制实现](https://github.com/microsoft/microsoft-ui-xaml/blob/main/dxaml/xcp/dxaml/lib/DesktopWindowImpl.cpp) 与 [DWM 自定义窗口框架说明](https://learn.microsoft.com/en-us/windows/win32/dwm/customframe)。

应用、托盘、任务栏和安装包共用青绿色「麦克风 + 文本光标」图标；48 像素及以下版本简化为居中的麦克风。标题栏即使只显示 16–24 像素，也会加载 48 像素图标，因此该源尺寸同样需要简化。可编辑原稿在 `src/VoxType.App/Assets/VoxType.svg`，运行 `python scripts/generate-app-icon.py`（需要 Pillow）可重新生成 ICO 和安装包各尺寸图片。

## 避让范围

优先读取系统暴露的、范围明确的输入子窗口；没有合适输入窗口时读取原生文本光标并留出行间和水平空间。部分浏览器、自绘编辑器或权限不同的应用可能不暴露这些位置，此时沿用记忆位置或默认位置，由拖动兜底。输入区域占满可用屏幕、没有可用避让空间时也保留手动调整能力。

## 验证

### 精简交互验收（2026-10-18）

WinUI Debug x64 构建通过。失败提示收紧后，Release 完整回归 396 项通过、0 失败、0 跳过，其中浮窗显示逻辑专项 17 项。`scripts/test-overlay-minimal.ps1` 的 9 项界面检查全部通过，已查看当前 150% 显示缩放下的深浅主题、短句、长转录、点击录音、处理中和失败状态截图。浅色配色调整后，外观脚本 7 项全部通过；新增验收先通过设置下拉框选择 dark → light → dark，确认实际主题、尺寸、圆角、透明四角和浅灰底色，再执行其他静态主题场景。测试不点击保存，结束时恢复原预览主题、常驻状态并退出静态检查模式。

| 行为 | 当前证据 |
| --- | --- |
| 快捷键录音单行显示、隐藏结束和更多按钮 | `ShortcutRecording_HidesActionsAndUsesSingleLine`；界面检查 `Shortcut recording has one line and no action buttons` |
| 点击录音保留按钮，切回快捷键不会残留按钮 | `TriggerOrigin_SwitchesFromClickToShortcutWithoutLeavingActions`；界面检查 `Click recording retains working stop and more controls` 验证结束命令 |
| 转录直接替换提示，五根波形均在布局边界内 | `LivePreview_KeepsListeningWaveform_AndCollapsesDetailDuringDelivery`；界面检查 `Live preview is the only text line and all five waveform bars fit` |
| 松手立即收起，慢识别延后提示，快速完成不会弹出迟到提示 | `Processing_DelaysShortcutFeedbackAndKeepsClickControls`；界面检查 `Release hides immediately then shows compact processing feedback`、`Fast completion cancels delayed feedback and delivery hides the bar` |
| 长文字保留末尾，不拆开 emoji、旗帜或组合字符 | `StreamingPreview_LongTextKeepsNewestGraphemes` 的 4 个数据用例；界面检查 `Long preview remains one line and bounded` |
| 失败保留恢复操作，完成时连同菜单收起 | `Failure_KeepsRecoveryActionAndFullReason`；界面检查 `Failure retains retry and options`、`Completion closes click recording options along with the bar` |
| 长错误不会撑成两行横幅，失败状态仍保持胶囊形状 | `Failure_KeepsRecoveryActionAndFullReason` 的 6 个数据用例；界面检查 `Rejected request stays a compact capsule with round ends in both themes` 验证单行、宽度不超过 300 DIP、半圆两端和截图四角透明 |
| 从设置切换深浅主题不会改变尺寸 | `scripts/test-overlay-polish.ps1` 的 `Settings selector changes dark to light and back without distorting the capsule`，先于其他主题预览场景执行，截图保存在 `settings-light.png` 与 `settings-dark.png` |
| 浅色隐藏后显示、常驻入口重现、切回深色均无矩形底布 | `scripts/test-overlay-screen-corners.ps1` 的 10 个屏幕实拍场景，直接校验四角透出已知背景色，并确认胶囊中心已绘制 |

结果与原始截图在忽略目录 `ui-test-artifacts/minimal-*/`、`ui-test-artifacts/polish-*/`，回归 TRX 在 `artifacts/overlay-refinement-tests/`。截图检查要求胶囊中心确实绘制，避免截图工具在合成帧更新期间返回空白图却被当作有效证据。以上为静态显示与状态切换验证，不代表本次重新完成了真实麦克风、物理拖拽或跨应用粘贴验收。

原生矩形底布修复另有屏幕回归证据：修复前，隐藏后切换浅色再显示的四角为白色，切回深色仍残留；修复后，`screen-corners-17887008bba04b2eac3acaed58cb8154` 中 10 项全部通过，Release 完整回归仍为 396 项通过、0 失败、0 跳过。此前窗口离屏截图的 Alpha 检查未捕获实际桌面上的原生白底，因此不能单独证明窗口透明。新增脚本在浮窗背后放置小块紫色测试底色，保持窗口不激活，截取实际屏幕像素；结果保存在 `ui-test-artifacts/screen-corners-*/`。脚本结束时恢复原主题预览、常驻状态并退出静态场景。

### 测试退出后首次录音的浮窗恢复（2026-10-18）

此前 Debug 验收的 `resume` 与延时处理场景沿用了真实更新入口，却带有独立增长的测试会话编号。测试退出后，较小编号的真实录音被浮窗误判为过期消息，造成麦克风已开启、输入仍正常但浮窗不显示。现在验收更新显式走 `UpdatePreview`，真实更新走 `Update`；跨来源时重置浮窗的排序编号，同一来源内仍过滤过期会话与修订。延时回调保留当前来源，并由取消令牌与会话/修订检查约束，不根据服务显示名称判断来源。

`scripts/test-overlay-session-handoff.ps1` 回放真实入口的状态更新，不开启麦克风、不请求识别、不注入文字。修复前 4 项中 3 项失败，证据在 `session-handoff-458a610259ea4879a5fb1941d9f1cd6b`；修复后 `session-handoff-897c21c43a9444488e2d757c8591bf43` 的 4 项全部通过：反复预览后首次快捷键会话显示、错误预览转真实会话、旧延时提示不覆盖新会话、真实会话仍拒绝旧消息。此专项验证覆盖浮窗状态交接，不等同于物理键盘与真实麦克风验收。

修复后 Debug、Release x64 均构建通过；精简交互 9 项通过（`minimal-82020fb2ac804a24a548cd6a5a4e079f`），屏幕圆角 10 项通过（`screen-corners-5cd3b7567b954488a9cb6cf83fcf5b87`）。菜单关闭验收改为跟踪实际 PopupHost 窗口及菜单项 ID，避免应用级模糊查询选中前台设置窗口中的 `OverlayPinnedSetting`。

### 此前验收记录

单元及回归测试：

```powershell
dotnet test tests/VoxType.Tests/VoxType.Tests.csproj --logger 'trx;LogFileName=overlay-feature-regression.trx'
```

此前版本的完整回归结果：387 项通过，0 失败，0 跳过。以下旧版验收记录保留用于追溯，当前精简交互另见下方的专项验收。

此前 UI Automation 与原生窗口验收：11 项通过，0 失败，3 项未验证。已验证系统 Caption 区域覆盖手柄、波形和文字，排除操作按钮；通过原生移动开始、位置变更与移动结束消息验证松手后保存位置，以及后续状态切换不回跳。旧版曾在菜单打开时延后收起；当前行为改为写入／完成时一并关闭菜单。已检查暗色录音、待机入口、失败重试和亮色录音状态截图。

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
./scripts/test-overlay-minimal.ps1 -AppPid <进程ID> -AppUserModelId <开发包AUMID>
./scripts/test-overlay-screen-corners.ps1 -AppPid <进程ID> -AppUserModelId <开发包AUMID>
./scripts/test-overlay-session-handoff.ps1 -AppPid <进程ID> -AppUserModelId <开发包AUMID>
```

当前桌面无法把测试编辑器置于前台，真实输入框避让、手柄鼠标拖动、波形/文字鼠标拖动三项标为 `NOT_VERIFIED`；系统实际注册的拖动区域、原生移动消息处理、位置算法和移动后的保存、恢复已分别验证。可使用 `-SkipPhysicalInput` 只运行 UI Automation 与原生窗口验收。验收脚本把结果和截图写入 `ui-test-artifacts/interaction-*/`，不会把环境受限项计入通过数。
