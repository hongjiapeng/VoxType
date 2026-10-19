using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Globalization;
using VoxType.Core.Dictation;

namespace VoxType_App.ViewModels;

public partial class OverlayViewModel : ObservableObject
{
    private long previewSession;
    private string transcriptPreview = "";
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string FullDetail { get; set; } = "";
    [ObservableProperty] public partial string Glyph { get; set; } = "\uE720";
    [ObservableProperty] public partial bool IsListening { get; set; }
    [ObservableProperty] public partial bool IsProcessing { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowActions))] public partial bool ShowStop { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowActions))] public partial bool ShowMore { get; set; }
    [ObservableProperty] public partial bool DelayProcessing { get; set; }
    public bool ShowActions => ShowStop || ShowRetry || ShowMore;
    [ObservableProperty] public partial bool ShowIcon { get; set; }
    [ObservableProperty] public partial bool IsStreamingPreview { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowSession))] public partial bool IsStandby { get; set; }
    public bool ShowSession => !IsStandby;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowActions))] public partial bool ShowRetry { get; set; }
    [ObservableProperty] public partial bool CanStart { get; set; } = true;
    public event EventHandler? StopRequested;
    [RelayCommand] private void Stop() => StopRequested?.Invoke(this, EventArgs.Empty);
    public void Update(SessionUpdate update)
    {
        IsStandby = update.State == SessionState.Idle;
        ShowRetry = update.State == SessionState.Failed;
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
        ShowMore = update.FromTray || update.State == SessionState.Failed;
        DelayProcessing = !update.FromTray && update.State is SessionState.Stopping or SessionState.Processing;
        ShowIcon = !IsListening && !IsProcessing;
        Title = update.State switch
        {
            SessionState.Idle => "麦克风未开启",
            SessionState.Starting => "正在准备麦克风",
            SessionState.Listening => update.FromTray ? "开始说话…" : "松开结束",
            SessionState.Stopping or SessionState.Processing => "识别中…",
            SessionState.Injecting => "正在发送文本",
            SessionState.Completed => "已发送文本",
            SessionState.Cancelling => "正在取消",
            SessionState.Failed when update.Message.Contains("没有检测到足够音量", StringComparison.Ordinal) => "没有听到声音",
            SessionState.Failed when update.Message.Contains("录音太短", StringComparison.Ordinal) => "录音太短",
            SessionState.Failed when update.Message.Contains("文本已复制", StringComparison.Ordinal) => "请手动粘贴",
            SessionState.Failed when update.Message.Contains("请求被拒绝", StringComparison.Ordinal) => "请求被拒绝",
            SessionState.Failed when update.Message.Contains("连接失败", StringComparison.Ordinal) => "连接失败",
            _ => "输入未完成"
        };
        FullDetail = transcriptPreview.Length > 0 ? transcriptPreview : update.Message;
        if (IsListening && !IsStreamingPreview)
            FullDetail = update.FromTray ? "正在聆听 · 点击结束 · Esc 取消" : "正在聆听 · 松开结束 · Esc 取消";
        if (update.State is SessionState.Starting or SessionState.Stopping or SessionState.Processing)
            FullDetail = transcriptPreview.Length > 0 ? transcriptPreview : Title + " · Esc 取消";
        if (IsStreamingPreview && IsListening)
        {
            // One line of content, without splitting emoji or combining characters.
            string line = transcriptPreview.Replace('\r', ' ').Replace('\n', ' ');
            int[] elements = StringInfo.ParseCombiningCharacters(line);
            int limit = ShowStop ? 14 : 20;
            Title = elements.Length > limit ? "…" + line[elements[^limit]..] : line;
        }
        Glyph = update.State == SessionState.Completed ? "\uE73E" : update.State == SessionState.Failed ? "\uE783" : "\uE720";
    }
}
