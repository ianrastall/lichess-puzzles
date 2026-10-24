using CommunityToolkit.Mvvm.ComponentModel;
using Jupiter.LichessPuzzles.Messages;
using Microsoft.UI.Xaml.Controls;

namespace Jupiter.LichessPuzzles.App.ViewModels;

/// <summary>The status bar, the empty-state panel and the database summary.</summary>
public sealed partial class StatusViewModel : ObservableObject
{
    public Status? Status { get; private set; }

    // Database summary ----------------------------------------------------------------------------
    [ObservableProperty] public partial string DatabaseText { get; set; } = "";
    [ObservableProperty] public partial bool HasStore { get; set; }

    // Running operation ---------------------------------------------------------------------------
    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial string OperationText { get; set; } = "";
    [ObservableProperty] public partial double OperationPercent { get; set; }
    [ObservableProperty] public partial bool IsIndeterminate { get; set; }
    [ObservableProperty] public partial bool CanPause { get; set; }
    [ObservableProperty] public partial bool CanCancel { get; set; }
    public OperationKind RunningKind { get; private set; }

    // Notice: the latest event, or an available update ------------------------------------------------
    [ObservableProperty] public partial bool HasNotice { get; set; }
    [ObservableProperty] public partial string NoticeText { get; set; } = "";
    [ObservableProperty] public partial InfoBarSeverity NoticeSeverity { get; set; }
    [ObservableProperty] public partial string NoticeActionText { get; set; } = "";
    [ObservableProperty] public partial bool HasNoticeAction { get; set; }
    public EventAction NoticeAction { get; private set; }
    private bool _eventNotice;

    // Empty state ---------------------------------------------------------------------------------
    [ObservableProperty] public partial bool ShowEmptyState { get; set; }
    [ObservableProperty] public partial string EmptyTitle { get; set; } = "";
    [ObservableProperty] public partial string EmptyMessage { get; set; } = "";
    [ObservableProperty] public partial string EmptyPrimaryText { get; set; } = "";
    [ObservableProperty] public partial bool ShowEmptyActions { get; set; }
    /// <summary>The empty-state primary action resumes a paused download rather than starting one.</summary>
    public bool EmptyPrimaryResumes { get; private set; }

    public void Apply(Status status)
    {
        Status = status;
        HasStore = status.Store is not null;
        if (status.Store is { } store)
        {
            var source = store.SourceVersion is { LastModified.Length: > 0 } v
                ? $"Lichess database of {Format.HttpDate(v.LastModified)}"
                : $"built from {store.SourceFile}";
            DatabaseText = $"{Format.Count(store.PuzzleCount)} puzzles · {source}";
        }
        else
        {
            DatabaseText = "No puzzle database";
        }

        var op = status.Operation;
        IsRunning = op is not null;
        RunningKind = op?.Kind ?? OperationKind.Unspecified;
        CanPause = op is { Kind: OperationKind.Download, Cancelling: false };
        CanCancel = op is { Cancelling: false } && op.Kind != OperationKind.CheckUpdate;
        if (op is not null)
        {
            var known = op.Total > 0;
            IsIndeterminate = !known;
            OperationPercent = known ? 100.0 * op.Done / op.Total : 0;
            OperationText = op switch
            {
                { Cancelling: true } => "Stopping…",
                { Kind: OperationKind.CheckUpdate } => "Checking for an update…",
                { Kind: OperationKind.Download, Done: 0 } => "Connecting to database.lichess.org…",
                { Kind: OperationKind.Download } => DownloadText(op),
                { Kind: OperationKind.Build } => $"Building the puzzle store · {Format.Count(op.Rows)} puzzles read" + (known ? $" · {OperationPercent:0}%" : ""),
                _ => "Working…",
            };
        }

        if (!_eventNotice)
        {
            if (status.UpdateReady)
            {
                SetNotice("The puzzle update is ready.", InfoBarSeverity.Success, EventAction.SwitchStore, fromEvent: false);
            }
            else if (status.UpdateAvailable is { } update && op is null)
            {
                var date = update.LastModified.Length > 0 ? Format.HttpDate(update.LastModified) : "a newer date";
                var size = update.Length > 0 ? $", {Format.Megabytes(update.Length)}" : "";
                SetNotice($"Update available ({date}{size})", InfoBarSeverity.Informational, EventAction.RetryDownload, fromEvent: false, actionText: "Download update");
            }
            else
            {
                HasNotice = false;
            }
        }

        ShowEmptyState = status.Store is null;
        ShowEmptyActions = status.Store is null && op is null;
        EmptyPrimaryResumes = status.PartialBytes > 0;
        if (status.Store is null)
        {
            if (op is { Kind: OperationKind.Build })
            {
                EmptyTitle = "Building the puzzle store";
                EmptyMessage = "Each puzzle is checked as it is added. This takes about half a minute.";
            }
            else if (op is { Kind: OperationKind.Download })
            {
                EmptyTitle = "Downloading puzzles";
                EmptyMessage = "The download can be paused and resumed later.";
            }
            else if (status.PartialBytes > 0)
            {
                var total = status.PartialTotal > 0 ? $" of {Format.Megabytes(status.PartialTotal)}" : "";
                EmptyTitle = "Download paused";
                EmptyMessage = $"Download paused at {Format.Megabytes(status.PartialBytes)}{total}.";
                EmptyPrimaryText = "Resume download";
            }
            else if (status.StoreProblem.StartsWith("No puzzles", StringComparison.Ordinal))
            {
                EmptyTitle = "No puzzles installed";
                EmptyMessage = "The Lichess puzzle database is about 300 MB to download and 500 MB once installed.";
                EmptyPrimaryText = "Download puzzles";
            }
            else
            {
                EmptyTitle = "The puzzle store can't be read";
                EmptyMessage = status.StoreProblem;
                EmptyPrimaryText = status.Download is not null ? "Rebuild store" : "Download puzzles";
            }
        }
    }

    private static string DownloadText(Operation op)
    {
        var text = $"Downloading {Format.Megabytes(op.Done)}";
        if (op.Total > 0)
        {
            text += $" of {Format.Megabytes(op.Total)}";
        }
        if (op.Rate > 0)
        {
            text += $" · {op.Rate / 1048576.0:0.0} MB/s";
            if (op.Total > op.Done)
            {
                text += $" · {Format.Duration((op.Total - op.Done) / op.Rate)} left";
            }
        }
        return text;
    }

    public void AddEvent(Event e)
    {
        var severity = e.Level switch
        {
            EventLevel.Success => InfoBarSeverity.Success,
            EventLevel.Warning => InfoBarSeverity.Warning,
            EventLevel.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        };
        SetNotice(e.Message, severity, e.Action, fromEvent: true);
    }

    public void Report(string message) => SetNotice(message, InfoBarSeverity.Error, EventAction.None, fromEvent: true);

    public void DismissNotice()
    {
        _eventNotice = false;
        HasNotice = false;
        if (Status is not null)
        {
            Apply(Status);
        }
    }

    private void SetNotice(string text, InfoBarSeverity severity, EventAction action, bool fromEvent, string? actionText = null)
    {
        _eventNotice = fromEvent;
        NoticeText = text;
        NoticeSeverity = severity;
        NoticeAction = action;
        NoticeActionText = actionText ?? action switch
        {
            EventAction.RetryDownload => "Retry",
            EventAction.RetryBuild => "Retry",
            EventAction.RetryCheck => "Retry",
            EventAction.SwitchStore => "Switch to new puzzles",
            _ => "",
        };
        HasNoticeAction = NoticeActionText.Length > 0;
        HasNotice = true;
    }
}
