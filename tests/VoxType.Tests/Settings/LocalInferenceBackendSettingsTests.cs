using VoxType.Core.Settings;
using VoxType_App.ViewModels;

namespace VoxType.Tests.Settings;

[TestClass]
public sealed class LocalInferenceBackendSettingsTests
{
    [TestMethod]
    public void R2T2Modes_ShowLocalControlsOrServerFieldsAndNotifyBindings()
    {
        var model = new MainPageViewModel(new(new() { ProviderId = "r2t2-server" }), _ => Task.CompletedTask);
        Assert.IsTrue(model.HasR2T2);
        Assert.IsFalse(model.HasLocalR2T2);
        Assert.IsTrue(model.HasBaseUrl && model.HasModel && model.HasApiKey);
        var changed = new List<string?>();
        model.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        model.SelectedProvider = model.Providers.Single(p => p.Id == "r2t2-local");
        Assert.IsTrue(model.HasR2T2 && model.HasLocalR2T2);
        Assert.IsFalse(model.HasBaseUrl || model.HasModel || model.HasApiKey);
        Assert.Contains(nameof(model.HasLocalR2T2), changed);
        Assert.Contains(nameof(model.HasBaseUrl), changed);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task R2T2StreamingToggle_SavesExplicitChoice(bool enabled)
    {
        var session = new SettingsSession(new() { R2T2StreamOutput = !enabled });
        var model = new MainPageViewModel(session, settings => { session.Publish(settings); return Task.CompletedTask; });
        Assert.AreEqual(!enabled, model.R2T2StreamOutput);
        model.R2T2StreamOutput = enabled;
        await model.SaveCommand.ExecuteAsync(null);
        Assert.AreEqual(enabled, session.Current.R2T2StreamOutput);
    }

    [TestMethod]
    public async Task SaveCommand_PersistsWhisperVulkanSelectionAndExplainsRestart()
    {
        var session = new SettingsSession(new());
        AppSettings? saved = null;
        var viewModel = new MainPageViewModel(session, settings =>
        {
            saved = settings;
            session.Publish(settings);
            return Task.CompletedTask;
        });

        viewModel.LocalInferenceBackend = "vulkan";
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.IsNotNull(saved);
        Assert.AreEqual("whisper-local", saved.ProviderId);
        Assert.AreEqual("vulkan", saved.LocalInferenceBackend);
        Assert.Contains("重启", viewModel.Message);
    }

    [TestMethod]
    public async Task R2T2LocalLaunch_SelectsLocalModeAndPreservesServerCredentials()
    {
        var session = new SettingsSession(new() { BaseUrl = "https://speech.example.test/v1", Model = "remote-model", ApiKey = "saved-key" });
        string? launchedBackend = null;
        string? launchedDirectory = null;
        bool? preparing = null;
        var viewModel = new MainPageViewModel(session, settings =>
        {
            session.Publish(settings);
            return Task.CompletedTask;
        })
        {
            SelectedProvider = VoxType.Core.Transcription.ProviderDefinition.All.Single(p => p.Id == "r2t2-server"),
            R2T2InferenceBackend = "vulkan",
            R2T2DataDirectory = @"D:\R2T2 Models",
            RunLocalR2T2Async = (backend, directory, prepare, progress, token) =>
            {
                launchedBackend = backend;
                launchedDirectory = directory;
                preparing = prepare;
                return Task.CompletedTask;
            }
        };

        await viewModel.LaunchLocalR2T2Command.ExecuteAsync(null);

        Assert.AreEqual("r2t2-local", session.Current.ProviderId);
        Assert.AreEqual("https://speech.example.test/v1", session.Current.BaseUrl);
        Assert.AreEqual("remote-model", session.Current.Model);
        Assert.AreEqual("vulkan", session.Current.R2T2InferenceBackend);
        Assert.AreEqual("saved-key", session.Current.ApiKey);
        Assert.AreEqual("vulkan", launchedBackend);
        Assert.AreEqual(@"D:\R2T2 Models", launchedDirectory);
        Assert.IsNotNull(preparing);
        Assert.IsFalse(preparing.Value);
        Assert.IsTrue(viewModel.IsR2T2Running);
        Assert.IsFalse(viewModel.LaunchLocalR2T2Command.CanExecute(null));
        Assert.IsFalse(viewModel.PrepareLocalR2T2Command.CanExecute(null));
        Assert.IsTrue(viewModel.StopLocalR2T2Command.CanExecute(null));
    }

    [TestMethod]
    public async Task R2T2Preparation_CanCancelAndCannotLaunchConcurrently()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new MainPageViewModel(new(new()), _ => Task.CompletedTask)
        {
            RunLocalR2T2Async = async (_, _, _, _, token) => { started.SetResult(); await Task.Delay(Timeout.Infinite, token); }
        };
        var preparing = model.PrepareLocalR2T2Command.ExecuteAsync(null);
        await started.Task;
        Assert.IsTrue(model.IsR2T2Busy);
        Assert.IsFalse(model.LaunchLocalR2T2Command.CanExecute(null));
        Assert.IsFalse(model.PrepareLocalR2T2Command.CanExecute(null));
        await model.StopLocalR2T2Command.ExecuteAsync(null);
        await preparing;
        Assert.IsFalse(model.IsR2T2Busy);
        Assert.IsFalse(model.IsR2T2Running);
        Assert.Contains("已取消", model.R2T2Status);
        Assert.IsTrue(model.LaunchLocalR2T2Command.CanExecute(null));
    }

    [TestMethod]
    public async Task R2T2LaunchFailure_ShowsErrorAndAllowsRetry()
    {
        var model = new MainPageViewModel(new(new()), _ => Task.CompletedTask)
        {
            RunLocalR2T2Async = (_, _, _, _, _) => throw new IOException("安装缺少 R2T2 服务")
        };
        await model.LaunchLocalR2T2Command.ExecuteAsync(null);
        Assert.AreEqual("安装缺少 R2T2 服务", model.R2T2Status);
        Assert.IsFalse(model.IsR2T2Running);
        Assert.IsFalse(model.IsR2T2Busy);
        Assert.IsTrue(model.LaunchLocalR2T2Command.CanExecute(null));
    }

    [TestMethod]
    public async Task R2T2Stop_ReleasesRunningStateAndAllowsBackendChange()
    {
        int stopped = 0;
        var model = new MainPageViewModel(new(new()), _ => Task.CompletedTask)
        {
            RunLocalR2T2Async = (_, _, _, _, _) => Task.CompletedTask,
            StopR2T2ServiceAsync = () => { stopped++; return Task.CompletedTask; }
        };
        await model.LaunchLocalR2T2Command.ExecuteAsync(null);
        await model.StopLocalR2T2Command.ExecuteAsync(null);
        Assert.AreEqual(1, stopped);
        Assert.IsFalse(model.IsR2T2Running);
        Assert.IsTrue(model.PrepareLocalR2T2Command.CanExecute(null));
    }

    [TestMethod]
    public async Task R2T2Launch_DoesNotOverwriteFailureReceivedBeforeCompletion()
    {
        MainPageViewModel model = null!;
        model = new(new(new()), _ => Task.CompletedTask)
        {
            RunLocalR2T2Async = (_, _, _, _, _) =>
            {
                model.ReportR2T2Status("本机服务已退出。", false);
                return Task.CompletedTask;
            }
        };
        await model.LaunchLocalR2T2Command.ExecuteAsync(null);
        Assert.IsFalse(model.IsR2T2Running);
        Assert.AreEqual("本机服务已退出。", model.R2T2Status);
        Assert.IsTrue(model.LaunchLocalR2T2Command.CanExecute(null));
    }
}
