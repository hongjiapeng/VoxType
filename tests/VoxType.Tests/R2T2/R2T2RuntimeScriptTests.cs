using System.Diagnostics;

namespace VoxType.Tests.R2T2;

[TestClass]
public sealed class R2T2RuntimeScriptTests
{
    [TestMethod]
    [DataRow("reuse")]
    [DataRow("missing")]
    [DataRow("modified")]
    public async Task RuntimePreparation_ReusesLockedValidFilesAndRepairsOnlyChangedFiles(string scenario)
    {
        await RunAsync("""
            Expand-R2T2Runtime $archive $destination
            if ([IO.File]::ReadAllText($executable) -ne 'server-v1') { throw 'Initial executable missing.' }
            $stamp = [datetime]::new(2020, 1, 2, 0, 0, 0, [DateTimeKind]::Utc)
            [IO.File]::SetLastWriteTimeUtc($executable, $stamp)
            if ($Scenario -eq 'missing') { [IO.File]::Delete($dll) }
            if ($Scenario -eq 'modified') { [IO.File]::WriteAllText($dll, 'broken') }
            $held = [IO.File]::Open($executable, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            try { Expand-R2T2Runtime $archive $destination }
            finally { $held.Dispose() }
            if ([IO.File]::ReadAllText($dll) -ne 'vulkan-v1') { throw 'Runtime DLL not repaired.' }
            if ([IO.File]::GetLastWriteTimeUtc($executable) -ne $stamp) { throw 'Valid loaded executable was overwritten.' }
            """, scenario);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RuntimePreparation_LockedModifiedFileFailsBeforeChangingOtherFiles(bool exclusive)
    {
        await RunAsync("""
            Expand-R2T2Runtime $archive $destination
            [IO.File]::WriteAllText($executable, 'damaged-executable')
            [IO.File]::WriteAllText($dll, 'broken-v1')
            $sharing = if ($Scenario -eq 'exclusive') { [IO.FileShare]::None } else { [IO.FileShare]::Read }
            $held = [IO.File]::Open($dll, [IO.FileMode]::Open, [IO.FileAccess]::Read, $sharing)
            try {
                $message = ''
                try { Expand-R2T2Runtime $archive $destination } catch { $message = $_.Exception.Message }
                if (-not $message.Contains('Ctrl+C') -or -not $message.Contains($dll)) { throw "Expected actionable lock error, got: $message" }
                if ([IO.File]::ReadAllText($executable) -ne 'damaged-executable') { throw 'Files changed before lock detection.' }
            } finally { $held.Dispose() }
            if ([IO.File]::ReadAllText($dll) -ne 'broken-v1') { throw 'Locked original was changed.' }
            if (@(Get-ChildItem -LiteralPath $destination -Filter '*.partial').Count) { throw 'Temporary files leaked.' }
            """, exclusive ? "exclusive" : "shared");
    }

    [TestMethod]
    [DataRow("traversal")]
    [DataRow("duplicate")]
    [DataRow("missing-executable")]
    public async Task RuntimePreparation_InvalidArchiveIsRejectedBeforeAnyWrite(string scenario)
    {
        await RunAsync("""
            $zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Update)
            try {
                if ($Scenario -eq 'missing-executable') { $zip.GetEntry('llama-server.exe').Delete() }
                else { $entry = $zip.CreateEntry($(if ($Scenario -eq 'traversal') { '../escaped.txt' } else { 'llama-server.exe' })); $entry.Open().Dispose() }
            } finally { $zip.Dispose() }
            $rejected = $false
            try { Expand-R2T2Runtime $archive $destination } catch { $rejected = $true }
            if (-not $rejected) { throw 'Unsafe archive accepted.' }
            if (Test-Path -LiteralPath $executable) { throw 'Archive was partially extracted before rejection.' }
            if (Test-Path -LiteralPath (Join-Path $Root 'escaped.txt')) { throw 'Archive escaped runtime root.' }
            """, scenario);
    }

    [TestMethod]
    public async Task PortProbe_ReportsOccupiedPortAndPid_AndReleasesItsOwnProbe()
    {
        await RunAsync("""
            $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
            $listener.Start()
            $port = $listener.LocalEndpoint.Port
            try {
                $message = ''
                try { Assert-R2T2PortAvailable -Address ([Net.IPAddress]::Loopback) -Port $port } catch { $message = $_.Exception.Message }
                if (-not $message.Contains("端口 $port") -or -not $message.Contains("PID $PID") -or -not $message.Contains('Ctrl+C')) { throw "Unhelpful port error: $message" }
            } finally { $listener.Stop() }
            Assert-R2T2PortAvailable -Address ([Net.IPAddress]::Loopback) -Port $port
            $replacement = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $port)
            try { $replacement.Start() } finally { $replacement.Stop() }
            """);
    }

    private static async Task RunAsync(string body, string scenario = "")
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "scripts", "r2t2-common.ps1"))) directory = directory.Parent;
        Assert.IsNotNull(directory, "Cannot find repository script.");
        string root = Path.Combine(Path.GetTempPath(), "VoxType runtime checks " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string script = Path.Combine(root, "check.ps1");
        const string setup = """
            param([string]$CommonScript, [string]$Root, [string]$Scenario)
            $ErrorActionPreference = 'Stop'
            . $CommonScript
            $source = Join-Path $Root 'source'
            $destination = Join-Path $Root 'runtime'
            $null = [IO.Directory]::CreateDirectory($source)
            [IO.File]::WriteAllText((Join-Path $source 'llama-server.exe'), 'server-v1')
            [IO.File]::WriteAllText((Join-Path $source 'ggml-vulkan.dll'), 'vulkan-v1')
            $archive = Join-Path $Root 'runtime.zip'
            [IO.Compression.ZipFile]::CreateFromDirectory($source, $archive)
            $executable = Join-Path $destination 'llama-server.exe'
            $dll = Join-Path $destination 'ggml-vulkan.dll'
            """;
        try
        {
            await File.WriteAllTextAsync(script, setup + Environment.NewLine + body);
            var info = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "-NoProfile", "-File", script, "-CommonScript", Path.Combine(directory.FullName, "scripts", "r2t2-common.ps1"), "-Root", root, "-Scenario", scenario }) info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
            Assert.AreEqual(0, process.ExitCode, await output + await error);
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(root));
            Directory.Delete(root, recursive: true);
        }
    }
}
