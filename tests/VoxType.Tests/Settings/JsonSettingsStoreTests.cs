using System.Text;
using System.Text.Json.Nodes;
using VoxType.Core.Hotkeys;
using VoxType.Core.Settings;
using VoxType.Infrastructure.Settings;

namespace VoxType.Tests.Settings;

[TestClass]
public sealed class JsonSettingsStoreTests
{
    private static readonly AppSettings Configured = new() { ProviderId = "openai-compatible", BaseUrl = "http://localhost:8000/v1", Model = "custom-model",
        Language = "zh", Theme = "dark", Hotkey = new(KeyModifiers.Control | KeyModifiers.Shift, 'Q'), LocalModelPath = "C:\\models\\ggml-base.bin", ApiKey = "VoxType-test-secret-只用于测试" };

    [TestMethod]
    public async Task LegacyDefault_UpgradesOnce_KeepingProviderPathAndEncryptedKey()
    {
        var file = new MemoryFile(); var store = new JsonSettingsStore(file, new DpapiSecretProtector());
        await store.SaveAsync(Configured with { Hotkey = new(KeyModifiers.Alt, 0x20) });
        var disk = JsonNode.Parse(file.Bytes!)!; disk["version"] = 1;
        string ciphertext = disk["encryptedApiKey"]!.GetValue<string>();
        file.Bytes = Encoding.UTF8.GetBytes(disk.ToJsonString());
        var loaded = await store.LoadAsync();
        Assert.AreEqual(Configured with { Hotkey = HotkeyBinding.Default }, loaded.Settings);
        Assert.IsNull(loaded.Warning);
        var upgraded = JsonNode.Parse(file.Bytes!)!;
        Assert.AreEqual(2, upgraded["version"]!.GetValue<int>());
        Assert.AreEqual(ciphertext, upgraded["encryptedApiKey"]!.GetValue<string>());
        Assert.AreEqual(2, file.Writes);
        Assert.AreEqual(loaded.Settings, (await store.LoadAsync()).Settings);
        Assert.AreEqual(2, file.Writes, "Migration must run only once.");
    }

    [TestMethod]
    public async Task LegacyCustomHotkey_IsPreservedDuringUpgrade()
    {
        var file = new MemoryFile(); var store = new JsonSettingsStore(file, new DpapiSecretProtector());
        await store.SaveAsync(Configured);
        var disk = JsonNode.Parse(file.Bytes!)!; disk["version"] = 1;
        file.Bytes = Encoding.UTF8.GetBytes(disk.ToJsonString());
        Assert.AreEqual(Configured, (await store.LoadAsync()).Settings);
        Assert.AreEqual(2, file.Writes);
    }

    [TestMethod]
    public async Task CurrentVersion_ExplicitAltSpace_RemainsUserChoice()
    {
        var file = new MemoryFile(); var store = new JsonSettingsStore(file, new DpapiSecretProtector());
        var custom = Configured with { Hotkey = new(KeyModifiers.Alt, 0x20) };
        await store.SaveAsync(custom);
        Assert.AreEqual(custom, (await store.LoadAsync()).Settings);
        Assert.AreEqual(1, file.Writes);
    }

    [TestMethod]
    public async Task MigrationWriteFailure_UsesNewDefaultWithoutLosingExistingConfiguration()
    {
        var file = new MemoryFile(); var store = new JsonSettingsStore(file, new DpapiSecretProtector());
        await store.SaveAsync(Configured with { Hotkey = new(KeyModifiers.Alt, 0x20) });
        var disk = JsonNode.Parse(file.Bytes!)!; disk["version"] = 1;
        file.Bytes = Encoding.UTF8.GetBytes(disk.ToJsonString());
        byte[] original = file.Bytes.ToArray(); file.FailWrite = true;
        var result = await store.LoadAsync();
        Assert.AreEqual(Configured with { Hotkey = HotkeyBinding.Default }, result.Settings);
        Assert.IsNotNull(result.Warning); Assert.Contains("重新保存", result.Warning);
        CollectionAssert.AreEqual(original, file.Bytes);
        Assert.AreEqual(1, file.Writes);
    }
    [TestMethod]
    public async Task SaveAndRestart_PreservesBothProviders_WithEncryptedKeyOnly()
    {
        var file = new MemoryFile(); var protector = new DpapiSecretProtector();
        await new JsonSettingsStore(file, protector).SaveAsync(Configured);
        var json = Encoding.UTF8.GetString(file.Bytes!);
        Assert.DoesNotContain(Configured.ApiKey, json); Assert.DoesNotContain("\"apiKey\"", json);
        var loaded = await new JsonSettingsStore(file, protector).LoadAsync();
        Assert.AreEqual(Configured, loaded.Settings); Assert.IsNull(loaded.Warning);
    }
    [TestMethod]
    [DataRow("corrupt")]
    [DataRow("version")]
    [DataRow("provider")]
    [DataRow("hotkey")]
    [DataRow("missing-field")]
    public async Task InvalidFile_ReturnsLocalDefaultsAndPreservesOriginalFile(string kind)
    {
        var file = new MemoryFile(); var store = new JsonSettingsStore(file, new DpapiSecretProtector()); await store.SaveAsync(Configured);
        var json = JsonNode.Parse(file.Bytes!)!;
        switch (kind)
        {
            case "version": json["version"] = 999; break;
            case "provider": json["providerId"] = "unknown-provider"; break;
            case "hotkey": json["hotkey"]!["key"] = 0x7B; break;
            case "missing-field": json.AsObject().Remove("providerId"); break;
        }
        file.Bytes = kind == "corrupt" ? Encoding.UTF8.GetBytes("{broken") : Encoding.UTF8.GetBytes(json.ToJsonString());
        byte[] original = file.Bytes.ToArray();
        var result = await store.LoadAsync();
        Assert.AreEqual(new AppSettings(), result.Settings); Assert.IsNotNull(result.Warning);
        CollectionAssert.AreEqual(original, file.Bytes); Assert.AreEqual(1, file.Writes);
    }
    [TestMethod]
    public async Task DamagedCiphertext_ClearsKeyAndRequestsReentry_WithoutLosingOtherSettings()
    {
        var file = new MemoryFile(); var store = new JsonSettingsStore(file, new DpapiSecretProtector()); await store.SaveAsync(Configured);
        var json = JsonNode.Parse(file.Bytes!)!; json["encryptedApiKey"] = "invalid encrypted value"; file.Bytes = Encoding.UTF8.GetBytes(json.ToJsonString());
        var result = await store.LoadAsync();
        Assert.AreEqual(Configured with { ApiKey = "" }, result.Settings); Assert.IsNotNull(result.Warning); Assert.Contains("重新输入", result.Warning);
        Assert.DoesNotContain(Configured.ApiKey, result.Warning);
    }
    [TestMethod]
    public async Task FailedSave_LeavesOldDiskAndEffectiveSettings()
    {
        var file = new MemoryFile(); var store = new JsonSettingsStore(file, new DpapiSecretProtector()); await store.SaveAsync(Configured);
        var session = new SettingsSession(Configured); byte[] original = file.Bytes!.ToArray(); file.FailWrite = true;
        async Task SaveAndPublish() { var next = Configured with { Model = "new-model" }; await store.SaveAsync(next); session.Publish(next); }
        await Assert.ThrowsExactlyAsync<IOException>(SaveAndPublish);
        Assert.AreEqual(Configured, session.Current); CollectionAssert.AreEqual(original, file.Bytes);
    }
    [TestMethod]
    public async Task ClearingKey_PersistsEmptyEncryptedValue()
    {
        var file = new MemoryFile(); var store = new JsonSettingsStore(file, new DpapiSecretProtector());
        await store.SaveAsync(Configured); await store.SaveAsync(Configured with { ApiKey = "" });
        Assert.AreEqual("", JsonNode.Parse(file.Bytes!)!["encryptedApiKey"]!.GetValue<string>());
        Assert.AreEqual("", (await store.LoadAsync()).Settings.ApiKey);
    }
    [TestMethod]
    public void Dpapi_CurrentUserRoundTripAndTamperFailure()
    {
        var protector = new DpapiSecretProtector(); string encrypted = protector.Protect(Configured.ApiKey);
        Assert.AreNotEqual(Configured.ApiKey, encrypted); Assert.AreEqual(Configured.ApiKey, protector.Unprotect(encrypted));
        byte[] changed = Convert.FromBase64String(encrypted); changed[^1] ^= 0xFF;
        Assert.ThrowsExactly<System.Security.Cryptography.CryptographicException>(() => protector.Unprotect(Convert.ToBase64String(changed)));
    }
    [TestMethod]
    public async Task RealFile_AtomicReplacementAndCancellationLeaveNoTemporaryFiles()
    {
        string directory = Path.Combine(Path.GetTempPath(), "VoxType-settings-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new JsonSettingsStore(new AtomicSettingsFile(path), new DpapiSecretProtector());
            await store.SaveAsync(Configured); await store.SaveAsync(Configured with { Model = "replacement-model" });
            Assert.AreEqual("replacement-model", (await store.LoadAsync()).Settings.Model);
            Assert.DoesNotContain(Configured.ApiKey, await File.ReadAllTextAsync(path));
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => store.SaveAsync(Configured, cancellation.Token));
            Assert.HasCount(1, Directory.GetFiles(directory));
            Assert.AreEqual("replacement-model", (await store.LoadAsync()).Settings.Model);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory); // non-recursive; only the dedicated empty test folder
        }
    }
    [TestMethod]
    public async Task OversizeSerializedSettings_AreRejectedBeforeDiskCommit()
    {
        var file = new MemoryFile(); var store = new JsonSettingsStore(file, new DpapiSecretProtector());
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.SaveAsync(Configured with { ApiKey = new string('密', 16384) }));
        Assert.AreEqual(0, file.Writes); Assert.IsNull(file.Bytes);
    }
    private sealed class MemoryFile : ISettingsFile
    {
        public byte[]? Bytes; public int Writes; public bool FailWrite;
        public Task<byte[]?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Bytes?.ToArray());
        public Task WriteAtomicAsync(byte[] bytes, CancellationToken cancellationToken) { if (FailWrite) throw new IOException("write failure"); Bytes = bytes.ToArray(); Writes++; return Task.CompletedTask; }
    }
}
