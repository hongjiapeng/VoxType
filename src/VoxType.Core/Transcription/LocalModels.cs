namespace VoxType.Core.Transcription;

public sealed record LocalModelDefinition(string Id, string Name, string FileName, long SizeBytes,
    string Sha256, string Description, Uri SourcePage);

public sealed record ModelDownloadSource(string Name, Uri Url);
public enum ModelInstallationState { Missing, Installed, Invalid }
public sealed record ModelDownloadProgress(long BytesReceived, long TotalBytes, bool Verifying = false)
{
    public double Percent => TotalBytes == 0 ? 0 : Math.Min(100, BytesReceived * 100d / TotalBytes);
}

public interface ILocalModelManager
{
    LocalModelDefinition Model { get; }
    IReadOnlyList<ModelDownloadSource> Sources { get; }
    string ModelPath { get; }
    Task<ModelInstallationState> InspectAsync(CancellationToken token = default);
    Task DownloadAsync(ModelDownloadSource source, IProgress<ModelDownloadProgress>? progress, CancellationToken token);
    Task ImportAsync(string sourcePath, IProgress<ModelDownloadProgress>? progress, CancellationToken token);
    Task DeleteAsync(CancellationToken token = default);
}
