using VoxType.Core.Settings;
using VoxType_App.ViewModels;

namespace VoxType.Tests.Settings;

[TestClass]
public sealed class LocalInferenceBackendSettingsTests
{
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
    public async Task R2T2LocalLaunch_SavesLoopbackAddressAndPassesSelectedBackend()
    {
        var session = new SettingsSession(new());
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
            RunLocalR2T2Async = (backend, directory, prepare) =>
            {
                launchedBackend = backend;
                launchedDirectory = directory;
                preparing = prepare;
                return Task.CompletedTask;
            }
        };

        await viewModel.LaunchLocalR2T2Command.ExecuteAsync(null);

        Assert.AreEqual("http://127.0.0.1:8272/v1", session.Current.BaseUrl);
        Assert.AreEqual("r2t2-q4", session.Current.Model);
        Assert.AreEqual("vulkan", session.Current.R2T2InferenceBackend);
        Assert.AreEqual("", session.Current.ApiKey);
        Assert.AreEqual("vulkan", launchedBackend);
        Assert.AreEqual(@"D:\R2T2 Models", launchedDirectory);
        Assert.IsNotNull(preparing);
        Assert.IsFalse(preparing.Value);
    }
}
