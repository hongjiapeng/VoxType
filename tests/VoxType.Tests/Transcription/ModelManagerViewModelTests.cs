using VoxType.Core.Settings;
using VoxType.Core.Transcription;
using VoxType.Infrastructure.Transcription;
using VoxType_App.ViewModels;

namespace VoxType.Tests.Transcription;

[TestClass]
public sealed class ModelManagerViewModelTests
{
    [TestMethod]
    [DataRow(ModelInstallationState.Missing, true, false, false)]
    [DataRow(ModelInstallationState.Installed, false, true, true)]
    [DataRow(ModelInstallationState.Invalid, false, false, true)]
    public async Task Refresh_EnablesOnlyAppropriateCommands(ModelInstallationState state, bool install, bool use, bool delete)
    {
        var manager = new Manager { State = state }; await using var vm = new ModelManagerViewModel(manager, _ => Task.CompletedTask);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.AreEqual(install, vm.DownloadCommand.CanExecute(null)); Assert.AreEqual(install, vm.ImportCommand.CanExecute(null));
        Assert.AreEqual(use, vm.UseCommand.CanExecute(null)); Assert.AreEqual(delete, vm.DeleteCommand.CanExecute(null));
        Assert.IsFalse(vm.IsBusy); Assert.IsFalse(vm.CancelCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task Download_UsesSelectedSource_ReportsProgress_AndEnablesUseAfterVerification()
    {
        var manager = new Manager { Block = true }; await using var vm = new ModelManagerViewModel(manager, _ => Task.CompletedTask);
        vm.SelectedSource = vm.Sources[1]; Assert.AreEqual(vm.Sources[1].Url.AbsoluteUri, vm.SourceUrl);
        var task = vm.DownloadCommand.ExecuteAsync(null); await manager.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreSame(vm.Sources[1], manager.SelectedSource); Assert.AreEqual(50d, vm.Percent);
        Assert.IsTrue(vm.CancelCommand.CanExecute(null)); Assert.IsFalse(vm.UseCommand.CanExecute(null)); Assert.IsFalse(vm.DeleteCommand.CanExecute(null));
        Assert.IsFalse(vm.RefreshCommand.CanExecute(null)); Assert.IsFalse(vm.DownloadCommand.CanExecute(null));
        manager.Release.TrySetResult(); await task;
        Assert.IsTrue(vm.IsInstalled); Assert.IsTrue(vm.UseCommand.CanExecute(null)); Assert.Contains("已安装", vm.Status);
        Assert.IsFalse(vm.IsTransferring); Assert.IsFalse(vm.CancelCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task CancelAndDispose_StopTransfer_RestoreRetryState_AndIgnoreLateProgress()
    {
        var manager = new Manager { Block = true }; var callbacks = new List<Action>();
        var vm = new ModelManagerViewModel(manager, _ => Task.CompletedTask, callbacks.Add);
        var task = vm.DownloadCommand.ExecuteAsync(null); await manager.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.CancelCommand.Execute(null); await task;
        Assert.IsTrue(manager.Cancelled); Assert.IsTrue(vm.CanInstall); Assert.Contains("已取消", vm.Status);
        foreach (var callback in callbacks) callback(); Assert.AreEqual(0d, vm.Percent);
        manager.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        task = vm.DownloadCommand.ExecuteAsync(null); await manager.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await vm.DisposeAsync(); await task;
        Assert.IsFalse(vm.IsBusy); Assert.IsFalse(vm.CanInstall); Assert.IsFalse(vm.CanRefresh);
    }

    [TestMethod]
    public async Task NetworkFailure_ShowsRetryMessage_ThenSuccessfulRetryInstalls()
    {
        var manager = new Manager { Error = new HttpRequestException("private network detail") };
        await using var vm = new ModelManagerViewModel(manager, _ => Task.CompletedTask);
        await vm.DownloadCommand.ExecuteAsync(null);
        Assert.Contains("下载失败", vm.Status); Assert.DoesNotContain("private", vm.Status); Assert.IsTrue(vm.CanInstall);
        manager.Error = null; await vm.DownloadCommand.ExecuteAsync(null);
        Assert.IsTrue(vm.IsInstalled); Assert.AreEqual(2, manager.Downloads);
    }

    [TestMethod]
    public async Task Import_UsesPickedFile_AndPickerCancellationLeavesModelMissing()
    {
        var manager = new Manager(); await using var vm = new ModelManagerViewModel(manager, _ => Task.CompletedTask);
        vm.PickModelAsync = _ => Task.FromResult<string?>(null);
        await vm.ImportCommand.ExecuteAsync(null); Assert.IsNull(manager.Imported); Assert.IsFalse(vm.IsInstalled);
        vm.PickModelAsync = _ => Task.FromResult<string?>("external.bin");
        await vm.ImportCommand.ExecuteAsync(null);
        Assert.AreEqual("external.bin", manager.Imported); Assert.IsTrue(vm.IsInstalled);
    }

    [TestMethod]
    public async Task Use_SavesInstalledPath_AndSaveFailureNeverReportsSuccess()
    {
        var manager = new Manager { State = ModelInstallationState.Installed }; string? saved = null; bool fail = false;
        await using var vm = new ModelManagerViewModel(manager, path => { if (fail) throw new IOException("disk"); saved = path; return Task.CompletedTask; });
        await vm.RefreshCommand.ExecuteAsync(null); await vm.UseCommand.ExecuteAsync(null);
        Assert.AreEqual(manager.ModelPath, saved); Assert.Contains("已启用", vm.Status);
        fail = true; await vm.UseCommand.ExecuteAsync(null);
        Assert.Contains("失败", vm.Status); Assert.DoesNotContain("已启用", vm.Status); Assert.IsTrue(vm.CanUse);
    }

    [TestMethod]
    public async Task Delete_RequiresConfirmation_AndBusyFailureKeepsInstalledState()
    {
        var manager = new Manager { State = ModelInstallationState.Installed };
        await using var vm = new ModelManagerViewModel(manager, _ => Task.CompletedTask);
        await vm.RefreshCommand.ExecuteAsync(null); vm.ConfirmDeleteAsync = _ => Task.FromResult(false);
        await vm.DeleteCommand.ExecuteAsync(null); Assert.AreEqual(0, manager.Deletes); Assert.IsTrue(vm.IsInstalled);
        vm.ConfirmDeleteAsync = _ => Task.FromResult(true); manager.Error = new TranscriptionException("正在识别，请稍后删除。", "model-busy");
        await vm.DeleteCommand.ExecuteAsync(null); Assert.IsTrue(vm.IsInstalled); Assert.Contains("正在识别", vm.Status);
        manager.Error = null; await vm.DeleteCommand.ExecuteAsync(null);
        Assert.IsFalse(vm.IsInstalled); Assert.IsTrue(vm.CanInstall); Assert.Contains("已删除", vm.Status);
    }

    [TestMethod]
    public async Task UseInstalledModel_PreservesSavedSettingsAndUnrelatedUnsavedFormEdits()
    {
        var settings = new SettingsSession(new() { ProviderId = "openai-compatible", Language = "ja", ApiKey = "saved-key", Theme = "dark" });
        var vm = new MainPageViewModel(settings, updated => { settings.Publish(updated); return Task.CompletedTask; })
        { Language = "zh", ApiKey = "draft-key", BaseUrl = "https://draft.example/v1" };
        await vm.UseInstalledModelAsync("C:\\models\\ggml-base.bin");
        Assert.AreEqual("whisper-local", settings.Current.ProviderId); Assert.AreEqual("C:\\models\\ggml-base.bin", settings.Current.LocalModelPath);
        Assert.AreEqual("saved-key", settings.Current.ApiKey); Assert.AreEqual("ja", settings.Current.Language); Assert.AreEqual("dark", settings.Current.Theme);
        Assert.AreEqual("draft-key", vm.ApiKey); Assert.AreEqual("zh", vm.Language); Assert.AreEqual("https://draft.example/v1", vm.BaseUrl);
        Assert.AreEqual("whisper-local", vm.SelectedProvider.Id);
    }

    private sealed class Manager : ILocalModelManager
    {
        public LocalModelDefinition Model => LocalModelManager.BaseModel;
        public IReadOnlyList<ModelDownloadSource> Sources { get; } = [new("official", new("https://example.com/base")), new("mirror", new("https://mirror.example.com/base"))];
        public string ModelPath => "C:\\models\\ggml-base.bin";
        public ModelInstallationState State { get; set; }
        public Exception? Error { get; set; }
        public bool Block { get; set; }
        public bool Cancelled { get; private set; }
        public int Downloads { get; private set; }
        public int Deletes { get; private set; }
        public string? Imported { get; private set; }
        public ModelDownloadSource? SelectedSource { get; private set; }
        public TaskCompletionSource Entered { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ModelInstallationState> InspectAsync(CancellationToken token = default) => Task.FromResult(State);
        public async Task DownloadAsync(ModelDownloadSource source, IProgress<ModelDownloadProgress>? progress, CancellationToken token)
        {
            Downloads++; SelectedSource = source; if (Error is not null) throw Error;
            progress?.Report(new(50, 100)); Entered.TrySetResult();
            try { if (Block) await Release.Task.WaitAsync(token); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            State = ModelInstallationState.Installed;
        }
        public Task ImportAsync(string sourcePath, IProgress<ModelDownloadProgress>? progress, CancellationToken token)
        { Imported = sourcePath; State = ModelInstallationState.Installed; return Task.CompletedTask; }
        public Task DeleteAsync(CancellationToken token = default)
        { if (Error is not null) throw Error; Deletes++; State = ModelInstallationState.Missing; return Task.CompletedTask; }
    }
}
