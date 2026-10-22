using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;
using WanStudio.Services.Media;

namespace WanStudio.Services;

public enum SegmentGenState
{
    Idle,
    Queued,
    Running,
    Succeeded,
    Failed
}

/// <summary>One cut of the imported long video plus its generation state.</summary>
public sealed class LongVideoSegment : INotifyPropertyChanged
{
    private bool _isSelected;
    private double _startSeconds;
    private double _durationSeconds;
    private SegmentGenState _state = SegmentGenState.Idle;
    private string _statusText = "待生成";
    private string? _error;
    private ImageSource? _thumb;
    private string? _previewPath;

    public int Index { get; init; }
    public string Label => $"段 {Index:00}";

    public double StartSeconds
    {
        get => _startSeconds;
        set
        {
            if (Math.Abs(_startSeconds - value) < 0.001) return;
            _startSeconds = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RangeLabel));
            OnPropertyChanged(nameof(StartLabel));
        }
    }

    public double DurationSeconds
    {
        get => _durationSeconds;
        set
        {
            if (Math.Abs(_durationSeconds - value) < 0.001) return;
            _durationSeconds = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RangeLabel));
            OnPropertyChanged(nameof(DurationLabel));
            OnPropertyChanged(nameof(EndLabel));
        }
    }

    public double EndSeconds => _startSeconds + _durationSeconds;

    public string StartLabel => FormatTime(_startSeconds);
    public string EndLabel => FormatTime(EndSeconds);
    public string RangeLabel => $"{FormatTime(_startSeconds)} – {FormatTime(EndSeconds)}";
    public string DurationLabel => $"{_durationSeconds:0.0}s";

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); SelectionChanged?.Invoke(this, EventArgs.Empty); } }
    }

    public SegmentGenState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanRetry));
            OnPropertyChanged(nameof(RetryVisibility));
            OnPropertyChanged(nameof(StateBrushKey));
        }
    }

    public string StatusText
    {
        get => _statusText;
        set { if (_statusText != value) { _statusText = value; OnPropertyChanged(); } }
    }

    public string? Error
    {
        get => _error;
        set { if (_error != value) { _error = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasError)); OnPropertyChanged(nameof(ErrorVisibility)); } }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(_error);

    public ImageSource? Thumb
    {
        get => _thumb;
        set { if (!ReferenceEquals(_thumb, value)) { _thumb = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasThumb)); } }
    }

    public bool HasThumb => _thumb is not null;

    /// <summary>Local preview clip (cut with the media engine, never an API call).</summary>
    public string? PreviewPath
    {
        get => _previewPath;
        set { if (_previewPath != value) { _previewPath = value; OnPropertyChanged(); } }
    }

    public VideoTaskItem? Task { get; set; }

    public bool CanRetry => _state == SegmentGenState.Failed;

    public Microsoft.UI.Xaml.Visibility RetryVisibility
        => CanRetry ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility ErrorVisibility
        => HasError ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public string StateBrushKey => _state switch
    {
        SegmentGenState.Succeeded => "SystemFillColorSuccessBrush",
        SegmentGenState.Failed => "SystemFillColorCriticalBrush",
        SegmentGenState.Running => "AccentTextFillColorPrimaryBrush",
        _ => "TextFillColorSecondaryBrush"
    };

    public static string FormatTime(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 100:0}"
            : $"{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 100:0}";
    }

    public event EventHandler? SelectionChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Row shown in the pool list; wraps VideoPoolItem with UI-only state.</summary>
public sealed class PoolRow : INotifyPropertyChanged
{
    private bool _isSelected;
    private ImageSource? _thumb;
    private string _durationLabel = "";
    private double _durationSeconds;

    public PoolRow(VideoPoolItem item) => Item = item;

    public VideoPoolItem Item { get; }
    public string Title => string.IsNullOrWhiteSpace(Item.Title) ? "(无标题)" : Item.Title;
    public string SourceLabel => string.IsNullOrWhiteSpace(Item.SegmentLabel)
        ? Item.Source
        : $"{Item.Source} · {Item.SegmentLabel}";
    public string ContinueHint => Item.CanContinue ? "可作首片段" : Item.ContinueBlockReason;

    public string DurationLabel
    {
        get => _durationLabel;
        set { if (_durationLabel != value) { _durationLabel = value; OnPropertyChanged(); } }
    }

    /// <summary>探测到的真实时长（秒）。续接面板靠它校验「输出总时长必须大于首片段」。</summary>
    public double DurationSeconds
    {
        get => _durationSeconds;
        set
        {
            if (Math.Abs(_durationSeconds - value) < 0.001) return;
            _durationSeconds = value;
            OnPropertyChanged();
        }
    }

    public ImageSource? Thumb
    {
        get => _thumb;
        set { if (!ReferenceEquals(_thumb, value)) { _thumb = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasThumb)); } }
    }

    public bool HasThumb => _thumb is not null;

    private bool _showSelect;
    public bool ShowSelect
    {
        get => _showSelect;
        set { if (_showSelect != value) { _showSelect = value; OnPropertyChanged(); OnPropertyChanged(nameof(SelectVisibility)); } }
    }

    public Microsoft.UI.Xaml.Visibility SelectVisibility
        => _showSelect ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); SelectionChanged?.Invoke(this, EventArgs.Empty); } }
    }

    public event EventHandler? SelectionChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Page-scoped state that survives navigation (the Page itself is recreated).</summary>
public static class LongVideoWorkspace
{
    public static string? SourcePath { get; set; }
    public static MediaInfo? SourceInfo { get; set; }
    public static ObservableCollection<LongVideoSegment> Segments { get; } = new();
    public static string SplitMode { get; set; } = "fixed";
    public static double FixedSeconds { get; set; } = 5;
    public static string BatchPrompt { get; set; } = "保持原片主体与构图，镜头运动自然，画面稳定。";
    public static int ApiSubmitCount { get; set; }
    public static bool StitchEnabled { get; set; }
    public static string? LastStitchPath { get; set; }
    public static IReadOnlyList<double> LastStitchSeams { get; set; } = Array.Empty<double>();

    /// <summary>本批生成是否要衔接上一段（把已生成结果的尾巴当 first_clip）。</summary>
    public static bool AnchorEnabled { get; set; }

    /// <summary>锚点来源：池里哪一段。空表示自动取「最近一段已生成结果」。</summary>
    public static string? AnchorPoolItemId { get; set; }

    /// <summary>锚点尾巴取多少秒（会被夹到 2–10s）。</summary>
    public static double AnchorTailSeconds { get; set; } = 6.0;

    public static void Reset()
    {
        SourcePath = null;
        SourceInfo = null;
        Segments.Clear();
        LastStitchPath = null;
        LastStitchSeams = Array.Empty<double>();
        ApiSubmitCount = 0;
        StitchEnabled = false;
        AnchorEnabled = false;
        AnchorPoolItemId = null;
    }

    public static int SelectedCount
    {
        get
        {
            var n = 0;
            foreach (var s in Segments) if (s.IsSelected) n++;
            return n;
        }
    }
}
