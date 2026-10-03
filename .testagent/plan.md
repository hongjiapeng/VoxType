# 测试实施计划

## 模型下载管理新增验证

先验证下载成功/来源/进度/最终文件、大小/哈希/类型拒绝、已有文件保护、取消清理和重试、重复安装互斥、离线导入与删除路径。再验证 VM 状态、切换源、进度与取消、使用成功/保存失败、导入选择、删除确认、退出清理和保存模型时保留其他设置。补充本地资源 lease/缓存删除保护。运行新增测试及完整套件，再 Debug/Release build 与真实下载/包内启动；逐条检查断言是否能发现发布过早、缺少哈希、覆盖文件或提前删除的回归。

|阶段/要求|计划证据|
|---|---|
|2.1 长按重复|`Repeats_StartOnlyOnce_AndSwallowMatchingMainPair`|
|2.1 先松 Alt / Space|`ReleaseOrder_EndsOnce_AndRequiresWholeSequenceRelease`|
|2.1 左右修饰|`LeftAndRightAlt_ReleaseOneSide_DoesNotEndCombination`|
|2.1 额外修饰 / 主键先按|`ExtraModifier_DoesNotStart` / `MainFirst_DoesNotRetroactivelyStart`|
|2.1 ESC|`Escape_CancelsOnce_AndSwallowsItsPair`|
|2.1 无效组合|`InvalidBinding_IsRejected`|
|2.2 注入事件|`InjectedSequence_IsIgnored`|
|2.3 中断、遗漏和重新武装|`Interrupt_RequiresReleaseBeforeNewPress` / `MissedRelease_WatchdogCancelsAndRearms`|
|6.3 快捷键配置延迟|`Configure_DuringSequence_AppliesOnlyAfterOldKeysRelease`|
|后续阶段|现已实现会话/音频/HTTP/注入/模型/设置测试，精确方法与证据映射见 `status.md`；真实系统验收仍独立记录。|

先运行窄测试工程，再验证 `VoxType.slnx` 能发现相同测试。完成本批后核对每个 assertion 是否能阻止实际回归，记录 `.testagent/status.md`。不追求无意义的 DTO 存取测试数量。

|快捷键缺陷复测要求|测试|
|---|---|
|持续按住时不能被异步状态误取消|`SwallowedMainAbsentFromAsyncState_DoesNotCancelHold_AndKeyUpStillEnds`|
|修饰键遗漏仍中断并保留主键配对|`AsyncStateLosesRequiredModifier_CancelsOnce_AndPreservesMainPairUntilUp`|
|ESC 配对不被轮询清掉|`SwallowedEscapeAbsentFromAsyncState_StillSwallowsRepeatAndKeyUp`|
|新默认与左右/松键顺序|`Default_CtrlWinF8_StartsOnceAndReleasesOnEitherModifier`|
|迁移一次且保留原加密密钥/配置|`LegacyDefault_UpgradesOnce_KeepingProviderPathAndEncryptedKey`|
|保留已有及明确选择的自定义键|`LegacyCustomHotkey_IsPreservedDuringUpgrade`、`CurrentVersion_ExplicitAltSpace_RemainsUserChoice`|
|迁移写入失败不丢旧设置|`MigrationWriteFailure_UsesNewDefaultWithoutLosingExistingConfiguration`|
