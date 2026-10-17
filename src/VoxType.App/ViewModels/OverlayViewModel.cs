using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoxType.Core.Dictation;

namespace VoxType_App.ViewModels;

public partial class OverlayViewModel : ObservableObject
{
    private long previewSession;
    private string transcriptPreview = "";
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Detail { get; set; } = "";
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
        IsStreamingPreview = transcriptPreview.Length > 0;
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
        Detail = IsListening ? update.FromTray ? "点击结束完成 · Esc 取消" : "松开快捷键完成 · Esc 取消"
            : update.State == SessionState.Failed && update.Message.Contains("没有检测到足够音量", StringComparison.Ordinal)
                ? "检查麦克风后，按住快捷键重试。" : update.Message;
        if (IsStreamingPreview)
        {
            if (IsListening) Title = "正在聆听 · 实时预览";
            else if (update.State == SessionState.Processing) Title = "正在识别 · Esc 取消";
            // Show the newest text within the compact two-line capsule.
            int start = Math.Max(0, transcriptPreview.Length - 64);
            if (start > 0 && char.IsLowSurrogate(transcriptPreview[start])) start--;
            Detail = start > 0 ? "…" + transcriptPreview[start..] : transcriptPreview;
        }
        if (update.State == SessionState.Failed)
            Detail = Detail.Replace("。文本已复制到剪贴板", "。\n文本已复制到剪贴板", StringComparison.Ordinal);
        Glyph = update.State == SessionState.Completed ? "\uE73E" : update.State == SessionState.Failed ? "\uE783" : "\uE720";
    }
}
