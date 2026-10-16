using System.Net;
using System.Text;
using VoxType.Core.Audio;
using VoxType.Core.Settings;
using VoxType.Core.Transcription;
using VoxType.Infrastructure.Transcription;
using VoxType_App.ViewModels;

namespace VoxType.Tests.R2T2;

[TestClass]
public sealed class ServerAddressPolicyTests
{
    [TestMethod]
    [DataRow("http://10.0.0.1:8272/v1")]
    [DataRow("http://172.16.0.1:8272/v1")]
    [DataRow("http://172.31.255.255:8272/v1")]
    [DataRow("http://192.168.1.1:8272/v1")]
    [DataRow("http://[fd00::1]:8272/v1")]
    [DataRow("http://[fc00::1]:8272/v1")]
    [DataRow("http://[::ffff:192.168.1.1]:8272/v1")]
    public void R2T2_PrivateIpHttpAllowed_OriginalProviderStillRejects(string url)
    {
        Assert.AreEqual(url, ServerAddressPolicy.Validate(url, "r2t2-server").OriginalString);
        Assert.ThrowsExactly<ArgumentException>(() => ServerAddressPolicy.Validate(url, "openai-compatible"));
        Assert.AreEqual(url + "/audio/transcriptions", OpenAiCompatibleProvider.Endpoint(url, "r2t2-server").OriginalString);
    }

    [TestMethod]
    [DataRow("http://172.15.255.255:8272/v1")]
    [DataRow("http://172.32.0.0:8272/v1")]
    [DataRow("http://192.169.0.1:8272/v1")]
    [DataRow("http://8.8.8.8:8272/v1")]
    [DataRow("http://169.254.1.1:8272/v1")]
    [DataRow("http://[fe80::1]:8272/v1")]
    [DataRow("http://[2001:db8::1]:8272/v1")]
    [DataRow("http://nas.local:8272/v1")]
    [DataRow("http://192.168.1.1.example.org:8272/v1")]
    [DataRow("ftp://127.0.0.1:8272/v1")]
    [DataRow("https://user:secret@example.org/v1")]
    [DataRow("http://192.168.1.1/v1?key=secret")]
    [DataRow("http://192.168.1.1/v1#secret")]
    public void R2T2_PublicOrAmbiguousHttpAndEmbeddedSecrets_Rejected(string url)
    {
        var error = Assert.ThrowsExactly<ArgumentException>(() => ServerAddressPolicy.Validate(url, "r2t2-server"));
        Assert.DoesNotContain("secret", error.Message);
    }

    [TestMethod]
    [DataRow("http://127.0.0.1:8272/v1")]
    [DataRow("http://localhost:8272/v1")]
    [DataRow("http://[::1]:8272/v1")]
    [DataRow("https://speech.example.org/v1")]
    public void SharedLoopbackAndHttpsRules_AcceptedForBothProviders(string url)
    {
        Assert.AreEqual(url, ServerAddressPolicy.Validate(url, "r2t2-server").OriginalString);
        Assert.AreEqual(url, ServerAddressPolicy.Validate(url, "openai-compatible").OriginalString);
    }

    [TestMethod]
    public async Task R2T2Provider_ForwardsExactAudioKeyModelLanguageAndIdentity()
    {
        var bytes = WavValidatorTests.CreateWav();
        var handler = new LlamaTranscriberTests.Handler(async (request, token) =>
        {
            Assert.AreEqual("http://192.168.1.5:8272/v1/audio/transcriptions", request.RequestUri!.AbsoluteUri);
            Assert.AreEqual("Bearer lan-key", request.Headers.Authorization!.ToString());
            var form = Assert.IsInstanceOfType<MultipartFormDataContent>(request.Content!);
            var parts = form.ToDictionary(part => part.Headers.ContentDisposition!.Name!.Trim('"'));
            Assert.HasCount(4, parts);
            Assert.AreEqual("r2t2-q4", await parts["model"].ReadAsStringAsync(token));
            Assert.AreEqual("zh", await parts["language"].ReadAsStringAsync(token));
            Assert.AreEqual("json", await parts["response_format"].ReadAsStringAsync(token));
            CollectionAssert.AreEqual(bytes, await parts["file"].ReadAsByteArrayAsync(token));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"text\":\"  你好。  \"}", Encoding.UTF8, "application/json") };
        });
        using var client = new HttpClient(handler);
        var settings = new AppSettings { ProviderId = "r2t2-server", BaseUrl = "http://192.168.1.5:8272/v1", Model = "r2t2-q4", ApiKey = "lan-key" };
        var provider = new OpenAiCompatibleProvider(client, settings);
        var result = await provider.TranscribeAsync(new AudioData(bytes, TimeSpan.FromMilliseconds(250), .1f), new("zh"), default);
        Assert.AreEqual("r2t2-server", provider.Id); Assert.AreEqual("r2t2-server", result.ProviderId);
        Assert.AreEqual("zh", result.Language); Assert.AreEqual("你好。", result.Text); Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task SettingsAndViewModel_R2T2LanSave_SharesPolicyAndPreservesKey()
    {
        var session = new SettingsSession(new()); AppSettings? saved = null;
        var vm = new MainPageViewModel(session, value => { saved = value; return Task.CompletedTask; })
        {
            SelectedProvider = ProviderDefinition.All.Single(p => p.Id == "r2t2-server"),
            BaseUrl = " http://192.168.1.5:8272/v1 ", Model = " r2t2-q4 ", ApiKey = " lan-key ", Language = "zh"
        };
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.IsNotNull(saved); Assert.AreEqual("r2t2-server", saved.ProviderId);
        Assert.AreEqual("http://192.168.1.5:8272/v1", saved.BaseUrl); Assert.AreEqual("r2t2-q4", saved.Model);
        Assert.AreEqual("lan-key", saved.ApiKey); Assert.AreEqual("zh", saved.Language);
        Assert.IsTrue(vm.HasBaseUrl); Assert.IsTrue(vm.HasApiKey); Assert.IsTrue(vm.HasModel); Assert.IsFalse(vm.HasModelPath);
        Assert.IsFalse(vm.SelectedProvider.IsLocal); Assert.Contains("HTTP", vm.ProviderDescription);
        saved = null; vm.SelectedProvider = ProviderDefinition.All.Single(p => p.Id == "openai-compatible");
        await vm.SaveCommand.ExecuteAsync(null); Assert.IsNull(saved); Assert.DoesNotContain("已保存", vm.Message);
        Assert.ThrowsExactly<ArgumentException>(() => SettingsValidator.Validate(session.Current with { ProviderId = "r2t2-server", BaseUrl = "http://8.8.8.8/v1" }));
    }
}
