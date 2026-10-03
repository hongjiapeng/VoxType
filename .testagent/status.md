# 测试状态与自审

## 最新补充：本地模型下载管理

2026-10-03 新增 21 项用例。窄测试通过 21、失败 0、跳过 0；最终 `dotnet test tests/VoxType.Tests/VoxType.Tests.csproj -p:Platform=x64 --no-build -v:minimal` 通过 **190**、失败 **0**、跳过 **0**。下文 169/160 项是较早轮次的证据。

|要求|具体新增证据|
|---|---|
|列表、大小/语言/来源、下载进度、校验后发布|`Download_VerifiesPublishesAndReportsProgress_FromSelectedSourceWithoutCredentials`；目录常量和 XAML 模型卡片；真实 UI 报告的 Mirror is explicitly selected|
|取消、清理、失败重试与并发|`Cancellation_CleansPartialFile_AllowsRetry_AndRejectsConcurrentInstall`、`NetworkFailure_CanRetry_AndUnknownSourceIsRejectedBeforeRequest`、`CancelAndDispose_StopTransfer_RestoreRetryState_AndIgnoreLateProgress`|
|大小/哈希/类型错误、不覆盖已有文件|`InvalidDownloads_NeverInstall_AndRemoveTemporaryFiles`（short/large/hash/header）、`ExistingFile_IsNeverOverwritten_AndInvalidStateCanBeDeleted`|
|导入保留原文件、不联网|`Import_CopiesVerifiedModel_PreservesExternalSource_AndDoesNotUseNetwork`、`Import_UsesPickedFile_AndPickerCancellationLeavesModelMissing`|
|可用/未安装/无效命令状态、使用与保存失败|`Refresh_EnablesOnlyAppropriateCommands`、`Use_SavesInstalledPath_AndSaveFailureNeverReportsSuccess`、`UseInstalledModel_PreservesSavedSettingsAndUnrelatedUnsavedFormEdits`|
|删除确认、保护当前识别与缓存释放|`Delete_RequiresConfirmation_AndBusyFailureKeepsInstalledState`、`DeleteModel_RejectsActiveLease_ThenUnloadsCachedModelBeforeDeleting`、`DeleteModel_DuringInference_DoesNotDisposeOrDelete`|
|真实下载、关闭设置继续下载、文件选择器导入、目录启动、正常退出清理|`artifacts/model-download-smoke/evidence/20261003135407/result.json`、`ui-test-artifacts/model-manager-fbd6e4bfdcb34f289507ffb43a531af0/result.json`（9/9）、`artifacts/model-download-smoke/model-exit-result.json`|

自审确认断言能够发现提前发布、漏校验、覆盖已有文件、取消未清理、迟到进度、保存失败假成功及活跃模型提前删除。UI 首轮导入脚本误选 Open 的下拉按钮，保留失败报告；修正为从实时树选原生 SplitButton 主动作并固定主 HWND 后，整轮 9 项全通过，五张截图均已逐张复核。Debug/Release 构建 0 警告、0 错误。没有新采集覆盖率；官方源在此网络返回 HttpRequestException，镜像真实下载/取消/重试与哈希通过。该功能验证不替代原 MVP 的真实人声与完整系统验收。

2026-10-03，`dotnet test tests/VoxType.Tests/VoxType.Tests.csproj -p:Platform=x64 --nologo --no-restore` 实际执行 **169 项**，通过 169、失败 0、跳过 0。MSTest VSTest 模式，x64 Windows；Debug/Release 编译均已通过。当前 12 个测试文件、102 个 `[TestMethod]`，DataRow 展开形成 169 项。

## 快捷键复测增量

`HotkeyMatcherTests` / `JsonSettingsStoreTests` 的过滤命令通过 42 项；全套通过 169 项。需求及精确测试名见 `plan.md` 最后的复测表。直接检查新增断言：若取消吞键状态保护，持续按住和 ESC 配对测试会失败；若忽略修饰键丢失，取消和重新武装测试失败；若恢复旧默认或删去 Win 展示/匹配，新默认测试失败；迁移丢失配置、重加密、重复执行、覆盖自定义或写入失败回退整份默认都会违反设置测试断言。仅作静态 source-to-assertion 审查，没有执行突变。

隔离真实 Hook/F8 轮询探针复现旧取消并证明修复后保持，开发进程及设置 UIA 核对实际保存的 Ctrl + Shift + Space 和新增 Win 编辑选项。真实人的物理按键/人声/记事本仍待复测，不凭这些证据打勾完整系统任务。此前 94 个方法、396 个 assertion 的静态审查统计保留为上一轮记录，不作为新增测试数量。

## 需求到证据

|要求|已执行测试（精确名称）|
|---|---|
|2.1 重复与松键|`Repeats_StartOnlyOnce_AndSwallowMatchingMainPair`、`ReleaseOrder_EndsOnce_AndRequiresWholeSequenceRelease`|
|2.1 额外修饰、主键先按、左右 Alt、ESC|`ExtraModifier_DoesNotStart`、`MainFirst_DoesNotRetroactivelyStart`、`LeftAndRightAlt_ReleaseOneSide_DoesNotEndCombination`、`Escape_CancelsOnce_AndSwallowsItsPair`|
|2.3 60 秒/忙碌；2.6 快速松键|`RecordingLimit_StopsAt60SecondsOnce`、`Busy_RequestDoesNotStartSecondRecording`、`FastRelease_DuringDelayedStart_StopsOnceAndLeavesNoCapture`|
|2.4 输出格式与尾部|`Float48kStereo_Produces16kMonoPcm16_AndPreservesTail`；其他 PCM16/extensible/边界场景见 `WavConverterTests`|
|3.1 会话配置快照|`SettingsSavedDuringRecording_OnlyNextSessionUsesNewProviderAndLanguage`|
|3.2 请求/结果|`Multipart_SendsExactAudioAndSettings_ParsesChinese`、`Endpoint_PreservesBasePath`|
|3.3 错误、安全、超时|`Endpoint_RejectsUnsafeAddresses`、`StatusErrors_AreSanitizedAndNeverRetried`、`InvalidResponse_IsRejected`、`OversizeResponse_RejectsDeclaredOrStreamingBody`、`Timeout_IncludesBodyAfterHeaders_Uses120SecondDeadline`、`NetworkException_DoesNotExposeUnderlyingSecrets`、`ProductionHttpClient_RejectsRealLoopbackRedirectWithoutFollowing`|
|3.4 迟到取消/退出|`Cancel_ProviderIgnoresToken_DiscardsLateResultAndKeepsLeaseUntilReturn`、`Dispose_DuringRecognition_PreventsLateInjectionAndNewSessions`|
|4.1 身份/恢复/焦点|`UnsafeTarget_IsRejected`、`IdentityChangedDuringRestore_IsRevalidated`、`ChangedForeground_TriesRestoreOnlyOnce`、`FinalValidation_DoesNotForceForegroundAgain`|
|4.2 安全桌面、权限|`SecureDesktop_SendsAndCopiesNothing`、`SessionHeader_OnlyKnownActiveUnlockedStateIsAccepted`、`UnsafeTarget_IsRejected` 的 elevated / unknown-integrity 分区|
|4.3–4.5 剪贴板|`SupportedSnapshot_RoundTripsExactFormats_ExcludesHistoryAndCloud`、`ComplexFormat_DoesNotPretendToRestore`、`BusyClipboard_RetriesWithinOneSecond_ThenStops`、`UserCopyDuringBusyRetry_IsPreserved`、`NewCopyOrLostMarker_DoesNotRestoreOrRewrite`、`LaterSessionOwnership_PreventsEarlierRestore`|
|4.3–4.5 原生剪贴板回归|`ClosePublishesFinalSequence_ReceiptOwnsTextAndRestoresOriginal`、`NewCopyAfterClose_PreservingMarker_StillPreventsRestore`、`OwnershipTemporarilyBusy_RetriesWithoutRewriting_ThenRecognizesReceipt`、`OwnershipPersistentlyBusy_StopsAtOneSecond_WithoutRewriting`、`PreviousVoxTypeText_RestoresAllFormats_WithoutRevivingOldReceipt`；`dotnet test tests/VoxType.Tests/VoxType.Tests.csproj -p:Platform=x64 --no-restore --nologo --filter FullyQualifiedName~ClipboardServiceTests` 通过 17 项，关闭序列/自身标记两例修复前实际失败、修复后通过|
|4.4 发送/取消|`FailedOrPartialSend_DoesNotRetryText_OnlyCleansSyntheticKeys`、`ModifiersHeldAtTwoSeconds_CopiesWithoutSendingOrReleasingUserKeys`、`CancellationBeforeCommit_RestoresOwnedClipboardAndDoesNotSend`、`CancellationAfterCommit_DoesNotClaimRollback`|
|4.6 准确结果|`UnsafeTarget_ReturnsAccurateCopyOutcome`、`RestoreOutcome_IsReflectedInResult`、`RestoreThrowsAfterCommit_StillReportsDispatchedWithoutRetry`|
|5.1 native / 5.2 校验|`PackagedCpuDependency_CanLoadAndInvokeRuntimeExport`、`ModelHeader_RejectsMissingCorruptEnglishAndOtherSizes`|
|5.3–5.4 生命周期|`RepeatedSessions_ReuseOneModel_KeepLanguagePerRequest`、`ParallelRequests_NeverEnterSameNativeModelConcurrently`、`CancelAndSwitchProvider_DoNotDisposeUntilNativeReturnsAndLeaseEnds`、`FiveMinuteIdle_UnloadsAndNextRequestReloads`、`IdleWhileLeaseHeld_DefersUnloadUntilRelease`、`ExitWaitsForNativeCallBeforeDisposal`|
|5.4 空闲计时提前回调|`IdleTimerFiresEarly_RearmsUntilDeadline_ThenUnloadsAndReloads` 核对提前 1 毫秒不卸载、到期卸载一次及下一次识别重新加载；`dotnet test tests/VoxType.Tests/VoxType.Tests.csproj -p:Platform=x64 --no-restore --nologo --filter FullyQualifiedName~LocalWhisperServiceTests` 通过 13 项|
|6.1 保存/恢复|`RealFile_AtomicReplacementAndCancellationLeaveNoTemporaryFiles`、`InvalidFile_ReturnsLocalDefaultsAndPreservesOriginalFile`、`FailedSave_LeavesOldDiskAndEffectiveSettings`、`OversizeSerializedSettings_AreRejectedBeforeDiskCommit`|
|6.2 DPAPI|`Dpapi_CurrentUserRoundTripAndTamperFailure`、`SaveAndRestart_PreservesBothProviders_WithEncryptedKeyOnly`、`DamagedCiphertext_ClearsKeyAndRequestsReentry_WithoutLosingOtherSettings`、`ClearingKey_PersistsEmptyEncryptedValue`|
|6.5 退出异常清理|`Dispose_WhenProviderReleaseFails_StillDisposesAudioAndUnsubscribes` 验证资源释放异常下音频清理、订阅归零及迟到结果零注入|
|2.6 原生快速停止；5.2 开始前模型校验|`StopOverwrittenByCaptureStartup_IsReissuedUntilStopped`、`CompletedStop_DoesNotRepeatRequests`、`UnavailableProvider_FailsBeforeOpeningMicrophone_AndCanRetry`|

## Assertion quality

静态扫描包含测试及 fixture 内的防并发/锁状态断言，共 396 个调用：值相等/不等 237，Boolean 状态 75，Null/非 Null 25，异常 19，字符串 12，集合/计数 28。类别只表示断言形态；例如发送计数同时验证外部副作用，不能由形态推断覆盖率。

逐个检查 94 个 test method，无 assertion-free 方法。重点检查了结果字段、请求原字节、状态、负向零调用、资源释放、重复发送、持有 lease、文件内容、时间边界及新复制不被覆盖。运行结果未使用 Ignore/Skip 放过失败，也未降低既有断言。纯拒绝路径的非空错误断言同时核对没有额外 foreground 恢复；native runtime 测试核对真实导出可调用且加载 CPU 分支。

## Test-gap review

仅做 source-to-assertion 静态映射，未执行突变，因此没有“突变全部杀死”的结论。对去掉迟到取消守卫、将发送数错误判断为成功、将恢复延迟提前、覆盖用户新复制、忽略配置版本、保存明文 Key、取消时 dispose 活跃 native context 等候选变化，对应现有断言已列于上表。

自审发现并补上：双 Alt watchdog 不应在只丢失一侧时取消；响应超时需覆盖 body；提交后恢复异常不能报告成没有发送；序列改变时不应粘贴后来复制的内容；序列化后的配置可能超过读取上限；锁定状态不能仅由前台 HWND 判定。

剩余证据缺口：设备拔插/隐私权限、Hook/AltGr/系统菜单、实际 INPUT/剪贴板时序、真实人声模型推理、实际录音 UI 交互/主题/其他缩放、Explorer/电源事件及完整在线/离线记事本流程。HTTP body 的 IOException 分支和本地 native 加载/推理异常的重试尚无专门分区测试。没有采集行/分支覆盖率，静态 pairing 也不代表覆盖率。

真实 WASAPI RAW 开关、已校验模型的合成中文样本推理、解锁桌面静态浮层实际点击及前台保持、真实识别结果自动输入记事本及恢复之前的 VoxType 文本/标记现已实测，完整实际人声/按键/记事本交付仍待验收，不能凭 160 项通过宣布整个 MVP 已验收。详细系统限制及 UI 失败原始记录见 `docs/verification.md`。

生产 `LocalWhisperService` / `WhisperBackend` 的真实模型复测也已通过：10 次 zh/auto 推理只加载一次，原生推理中取消后等待完成、丢弃结果且可重试，真实 300087 ms 空闲时卸载，工作集降至 55.3 MiB；下次重新加载识别、退出释放，总加载/释放各 2 次。原五分钟计时未卸载的失败保留，修复及性能逐轮数据见 verification.md；这不替代实际人声记事本链路。
