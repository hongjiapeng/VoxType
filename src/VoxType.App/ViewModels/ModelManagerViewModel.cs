using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoxType.Core.Transcription;

namespace VoxType_App.ViewModels;

public partial class ModelManagerViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ILocalModelManager manager;
    private readonly Func<string, Task> use;
    private readonly Action<Action> dispatch;
    private CancellationTokenSource? cancellation;
    private Task? active;
    private bool disposed;
    private long operation;
    public Func<CancellationToken, Task<string?>>? PickModelAsync { get; set; }
    public Func<CancellationToken, Task<bool>>? ConfirmDeleteAsync { get; set; }
    public Func<string, Task<bool>>? OpenFolderAsync { get; set; }
    public LocalModelDefinition Model => manager.Model;
    public string ModelName => Model.Name;
    public string ModelDescription => Model.Description;
    public Uri SourcePage => Model.SourcePage;
    public string SizeDescription => $"{Model.SizeBytes / 1_000_000d:F0} MB · 多语言 · CPU · MIT";
    public IReadOnlyList<ModelDownloadSource> Sources => manager.Sources;
    public string InstalledPath => manager.ModelPath;
    public string SourceUrl => SelectedSource.Url.AbsoluteUri;
    [ObservableProperty] public partial ModelDownloadSource SelectedSource { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsTransferring { get; set; }
    [ObservableProperty] public partial bool IsInstalled { get; set; }
    [ObservableProperty] public partial bool HasFile { get; set; }
    [ObservableProperty] public partial double Percent { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "正在检查本地模型…";
    [ObservableProperty] public partial string TransferStatus { get; set; } = "";
    public bool CanInstall => !disposed && !IsBusy && !HasFile;
    public bool CanUse => !disposed && !IsBusy && IsInstalled;
    public bool CanDelete => !disposed && !IsBusy && HasFile;
    public bool CanRefresh => !disposed && !IsBusy;
    public bool CanCancel => !disposed && IsBusy && IsTransferring;

    public ModelManagerViewModel(ILocalModelManager manager, Func<string, Task> use, Action<Action>? dispatch = null)
    {
        this.manager = manager; this.use = use; this.dispatch = dispatch ?? (action => action());
        SelectedSource = Sources[0];
    }
    partial void OnSelectedSourceChanged(ModelDownloadSource value) => OnPropertyChanged(nameof(SourceUrl));
    partial void OnIsBusyChanged(bool value) => UpdateCommands();
    partial void OnIsTransferringChanged(bool value) => UpdateCommands();
    partial void OnIsInstalledChanged(bool value) => UpdateCommands();
    partial void OnHasFileChanged(bool value) => UpdateCommands();
    private void UpdateCommands()
    {
        OnPropertyChanged(nameof(CanInstall)); OnPropertyChanged(nameof(CanUse));
        OnPropertyChanged(nameof(CanDelete)); OnPropertyChanged(nameof(CanRefresh)); OnPropertyChanged(nameof(CanCancel));
        DownloadCommand.NotifyCanExecuteChanged(); ImportCommand.NotifyCanExecuteChanged();
        UseCommand.NotifyCanExecuteChanged(); DeleteCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
    }

    private async Task InspectAsync(CancellationToken token)
    {
        var state = await manager.InspectAsync(token);
        HasFile = state != ModelInstallationState.Missing;
        IsInstalled = state == ModelInstallationState.Installed;
        Status = state switch
        {
            ModelInstallationState.Installed => "已安装 · 文件校验通过。点击“使用此模型”启用本地识别。",
            ModelInstallationState.Invalid => "文件无效或无法读取。请删除后重新下载，或继续使用手动指定的模型。",
            _ => "未安装 · 下载模型或导入已有的 ggml-base.bin。"
        };
    }
    private Task RunAsync(Func<CancellationToken, Task> action, bool transferring = false)
    {
        if (disposed || IsBusy) return Task.CompletedTask;
        IsBusy = true; IsTransferring = transferring; Percent = 0; TransferStatus = "正在准备…";
        cancellation = new(); operation++;
        active = ExecuteAsync(action, cancellation);
        return active;
    }
    private async Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationTokenSource source)
    {
        try { await action(source.Token); }
        catch (OperationCanceledException) { Status = source.IsCancellationRequested ? "已取消，可重新下载或导入。" : "下载超时，请重试或更换下载来源。"; }
        catch (TranscriptionException ex) { Status = ex.Message; }
        catch (HttpRequestException) { Status = "下载失败，请检查网络，或切换下载来源后重试。"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status = "文件操作失败，请检查磁盘空间和文件权限后重试。"; }
        catch { Status = "模型操作失败，请重试。"; }
        finally
        {
            source.Dispose(); cancellation = null;
            IsTransferring = false; IsBusy = false;
        }
    }
    private IProgress<ModelDownloadProgress> Progress()
    {
        long current = operation;
        return new CallbackProgress(value => dispatch(() =>
        {
            if (disposed || current != operation || !IsTransferring) return;
            Percent = value.Percent;
            TransferStatus = value.Verifying ? "正在校验模型文件…" : $"{value.Percent:F0}% · {value.BytesReceived / 1_000_000d:F1} / {value.TotalBytes / 1_000_000d:F0} MB";
        }));
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => RunAsync(InspectAsync);

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private Task DownloadAsync()
    {
        var source = SelectedSource;
        return RunAsync(async token =>
        {
            Status = "正在从 " + source.Name + " 下载模型…";
            await manager.DownloadAsync(source, Progress(), token);
            await InspectAsync(token);
        }, transferring: true);
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private Task ImportAsync() => RunAsync(async token =>
    {
        string? path = PickModelAsync is null ? null : await PickModelAsync(token);
        token.ThrowIfCancellationRequested();
        if (path is null) { await InspectAsync(token); return; }
        Status = "正在导入模型…";
        await manager.ImportAsync(path, Progress(), token);
        await InspectAsync(token);
    }, transferring: true);

    [RelayCommand(CanExecute = nameof(CanUse))]
    private Task UseAsync() => RunAsync(async token =>
    {
        await InspectAsync(token);
        if (!IsInstalled) return;
        token.ThrowIfCancellationRequested();
        await use(InstalledPath);
        Status = "已启用 Whisper base。下一次语音输入使用此模型。";
    });

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private Task DeleteAsync() => RunAsync(async token =>
    {
        if (ConfirmDeleteAsync is null || !await ConfirmDeleteAsync(token)) return;
        token.ThrowIfCancellationRequested();
        await manager.DeleteAsync(token);
        await InspectAsync(token);
        Status = "已删除本地模型。再次使用前请重新下载、导入或指定其他模型路径。";
    });

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task ShowFolderAsync() => RunAsync(async token =>
    {
        token.ThrowIfCancellationRequested();
        if (OpenFolderAsync is null || !await OpenFolderAsync(System.IO.Path.GetDirectoryName(InstalledPath)!))
            Status = "无法打开文件夹，可复制下方模型路径手动打开。";
    });

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() { cancellation?.Cancel(); TransferStatus = "正在取消…"; }

    public async ValueTask DisposeAsync()
    {
        disposed = true; cancellation?.Cancel(); UpdateCommands();
        if (active is not null) await active;
    }
    private sealed class CallbackProgress(Action<ModelDownloadProgress> callback) : IProgress<ModelDownloadProgress>
    { public void Report(ModelDownloadProgress value) => callback(value); }
}
