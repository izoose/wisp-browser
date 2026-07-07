using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.Core;

namespace Wisp;

/// <summary>Wraps one WebView2 download so the downloads panel can show progress and act on it.</summary>
public class DownloadItem : INotifyPropertyChanged
{
    private readonly CoreWebView2DownloadOperation _op;

    // Computed over the live path so a Windows de-dupe rename ("report (1).pdf") is reflected.
    public string FileName => SafeName(_op.ResultFilePath);

    public DownloadItem(CoreWebView2DownloadOperation op)
    {
        _op = op;
        op.BytesReceivedChanged += (_, _) => Refresh();
        op.StateChanged += (_, _) => Refresh();
        Refresh();
    }

    public double Progress { get; private set; }
    public string Status { get; private set; } = "";
    public bool IsComplete => _op.State == CoreWebView2DownloadState.Completed;
    public bool InProgress => _op.State == CoreWebView2DownloadState.InProgress;
    public string ResultFilePath => _op.ResultFilePath;

    private void Refresh()
    {
        ulong total = _op.TotalBytesToReceive ?? 0;
        Progress = total > 0 ? Math.Min(100.0, 100.0 * _op.BytesReceived / total) : (IsComplete ? 100 : 0);
        Status = _op.State switch
        {
            CoreWebView2DownloadState.Completed => "Done",
            CoreWebView2DownloadState.Interrupted => _op.InterruptReason switch
            {
                CoreWebView2DownloadInterruptReason.UserCanceled => "Canceled",
                CoreWebView2DownloadInterruptReason.UserShutdown => "Canceled",
                CoreWebView2DownloadInterruptReason.UserPaused => "Paused",
                _ => "Failed",
            },
            _ => total > 0 ? $"{Human(_op.BytesReceived)} / {Human((long)total)}" : Human(_op.BytesReceived),
        };
        OnChanged(nameof(Progress));
        OnChanged(nameof(Status));
        OnChanged(nameof(FileName));
        OnChanged(nameof(IsComplete));
        OnChanged(nameof(InProgress));
    }

    public void Open() { try { Process.Start(new ProcessStartInfo(ResultFilePath) { UseShellExecute = true }); } catch { } }
    public void ShowInFolder() { try { Process.Start("explorer.exe", $"/select,\"{ResultFilePath}\""); } catch { } }
    public void Cancel() { try { _op.Cancel(); } catch { } }

    private static string SafeName(string path) { try { return Path.GetFileName(path); } catch { return path; } }

    private static string Human(long b) =>
        b >= 1L << 20 ? $"{b / 1024.0 / 1024:0.0} MB" :
        b >= 1L << 10 ? $"{b / 1024.0:0.0} KB" : $"{b} B";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
