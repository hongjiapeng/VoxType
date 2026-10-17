using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoxType.Core.Dictation;

namespace VoxType_App.ViewModels;

public partial class OverlayViewModel : ObservableObject
{
    private long previewSession;
    private string transcriptPreview = "";
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowDetail))] public partial string Detail { get; set; } = "";
    [ObservableProperty] public partial string FullDetail { get; set; } = "";
    public bool ShowDetail => Detail.Length > 0;
    [ObservableProperty] public partial string Glyph { get; set; } = "\uE720";
    [ObservableProperty] public partial bool IsListening { get; set; }
    [ObservableProperty] public partial bool IsProcessing { get; set; }
    [ObservableProperty] public partial bool ShowStop { get; set; }
    [ObservableProperty] public partial bool ShowIcon { get; set; }
    [ObservableProperty] public partial bool IsStreamingPreview { get; set; }
    public event EventHandler? StopRequested;
    [RelayCommand] private void Stop() => StopRequested?.Invoke(this, EventArgs.Empty);
    public void Update(SessionUpdate update)
    {
        if (previewSession != update.SessionId)
        {
            previewSession = update.SessionId;
            transcriptPreview = "";
        }
        if (update.State is SessionState.Listening or SessionState.Processing && update.PartialText is { Length: > 0 } incoming)
            transcriptPreview = incoming;
        if (update.State is not (SessionState.Listening or SessionState.Stopping or SessionState.Processing or SessionState.Injecting or SessionState.Completed))
            transcriptPreview = "";
        IsListening = update.State == SessionState.Listening;
        IsStreamingPreview = transcriptPreview.Length > 0 && update.State is SessionState.Listening or SessionState.Stopping or SessionState.Processing;
        IsProcessing = update.State is SessionState.Starting or SessionState.Stopping or SessionState.Processing or SessionState.Injecting or SessionState.Cancelling;
        ShowStop = update.FromTray && IsListening;
        ShowIcon = !IsListening && !IsProcessing;
        Title = update.State switch
        {
            SessionState.Starting => "正在准备麦克风",
            SessionState.Listening => "正在聆听",
            SessionState.Stopping or SessionState.Processing => "正在识别",
            SessionState.Injecting => "正在发送文本",
            SessionState.Completed => "已发送文本",
            SessionState.Cancelling => "正在取消",
            SessionState.Failed when update.Message.Contains("没有检测到足够音量", StringComparison.Ordinal) => "没有听到声音",
            _ => "语音输入未完成"
        };
        Detail = update.State switch
        {
            SessionState.Listening => update.FromTray ? "Esc 取消" : "松开结束 · Esc 取消",
            SessionState.Stopping or SessionState.Processing => "Esc 取消",
            SessionState.Failed => update.Message,
            _ => ""
        };
        FullDetail = transcriptPreview.Length > 0 ? transcriptPreview : update.Message;
        if (IsStreamingPreview)
        {
            // Keep only the newest phrase visible; the tooltip retains the full transcript.
            int start = Math.Max(0, transcriptPreview.Length - (ShowStop ? 12 : 16));
            if (start > 0 && char.IsLowSurrogate(transcriptPreview[start])) start--;
            Detail = (start > 0 ? "…" + transcriptPreview[start..] : transcriptPreview)
                .Replace('\r', ' ').Replace('\n', ' ');
        }
        if (update.State == SessionState.Failed)
        {
            if (update.Message.Contains("没有检测到足够音量", StringComparison.Ordinal))
                Detail = "检查麦克风后重试";
            else if (update.Message.Contains("录音太短", StringComparison.Ordinal))
            {
                Title = "录音太短";
                Detail = update.FromTray ? "请再录久一点" : "按住快捷键多说一会儿";
            }
            else if (update.Message.Contains("文本已复制", StringComparison.Ordinal))
            {
                Title = "文本已复制";
                Detail = "请回到输入框手动粘贴";
            }
        }
        Glyph = update.State == SessionState.Completed ? "\uE73E" : update.State == SessionState.Failed ? "\uE783" : "\uE720";
    }
}
