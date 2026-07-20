using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SC4ModMigrationAssistant.Models;
using SC4ModMigrationAssistant.Services;

namespace SC4ModMigrationAssistant;

public partial class MainWindow : Window
{
    private readonly DbpfScanService _scanService = new();
    private readonly DuplicateMoverService _moverService = new();
    private readonly Sc4pacLookupService _catalogLookupService;

    private readonly ObservableCollection<LogEntryView> _logEntries = new();
    private readonly ObservableCollection<Sc4pacMatch> _catalogMatches = new();
    private readonly ConcurrentQueue<LogMessage> _pendingLog = new();
    private readonly DispatcherTimer _logFlushTimer;

    // Synthwave neon palette used for log lines, matching the brushes defined in MainWindow.xaml.
    private static readonly Brush BrushPlugins = new SolidColorBrush(Color.FromRgb(0xF1, 0xEA, 0xFB));   // soft white/lavender
    private static readonly Brush BrushOverride = new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xEE));  // neon cyan
    private static readonly Brush BrushDuplicate = new SolidColorBrush(Color.FromRgb(0xFF, 0x3C, 0xAC)); // neon pink
    private static readonly Brush BrushWarning = new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x6C));   // neon amber
    private static readonly Brush BrushStatus = new SolidColorBrush(Color.FromRgb(0x9B, 0x8F, 0xC0));    // muted lavender-gray

    private string? _pluginsRoot;
    private ScanResult? _lastScanResult;
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
        _catalogLookupService = new Sc4pacLookupService(_scanService);
        LogListBox.ItemsSource = _logEntries;
        CatalogResultsListBox.ItemsSource = _catalogMatches;
        TryEnableDarkTitleBar();

        // Log lines are enqueued from the background scan thread (cheap, thread-safe, no UI
        // marshaling) and flushed to the UI in small batches on a timer. This is what keeps
        // the window responsive even with tens of thousands of files: without batching, a
        // WPF UI update per file is what causes the "freezing" during scans.
        _logFlushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _logFlushTimer.Tick += (_, _) => FlushLogQueue();
        _logFlushTimer.Start();
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select the Plugins folder"
        };

        if (dialog.ShowDialog() == true)
        {
            _pluginsRoot = dialog.FolderName;
            TxtPluginsPath.Text = _pluginsRoot;
            BtnScan.IsEnabled = true;
            BtnMoveDuplicates.IsEnabled = false;
            BtnCheckCatalog.IsEnabled = true;
            _lastScanResult = null;
            _logEntries.Clear();
            _catalogMatches.Clear();
            CatalogResultsPanel.Visibility = Visibility.Collapsed;
            ProgressBarScan.Value = 0;
            TxtProgressCount.Text = string.Empty;
            ResetCompareProgress();
        }
    }

    private async void BtnScan_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_pluginsRoot))
        {
            MessageBox.Show(this, "Please select the Plugins folder first.", "Warning",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _logEntries.Clear();
        ProgressBarScan.IsIndeterminate = true;
        ProgressBarScan.Value = 0;
        TxtProgressCount.Text = string.Empty;
        ResetCompareProgress();
        _cts = new CancellationTokenSource();
        SetBusy(true, "Scanning...");

        Action<LogMessage> log = msg => _pendingLog.Enqueue(msg);
        var scanProgress = new Progress<ScanProgress>(OnScanProgress);
        var compareProgress = new Progress<ScanProgress>(OnCompareProgress);

        try
        {
            ScanResult result = await Task.Run(() =>
                _scanService.ScanPlugins(_pluginsRoot!, log, scanProgress, compareProgress, _cts.Token), _cts.Token);

            FlushLogQueue();
            _lastScanResult = result;
            BtnMoveDuplicates.IsEnabled = result.Duplicates.Count > 0;

            TxtStatus.Text = $"Files read: {result.TotalFilesScanned} - Duplicates found: {result.Duplicates.Count}";
        }
        catch (OperationCanceledException)
        {
            FlushLogQueue();
            AppendLogImmediate(new LogMessage("Scan cancelled by the user.", LogColor.Orange));
            TxtStatus.Text = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            FlushLogQueue();
            AppendLogImmediate(new LogMessage($"Error during scan: {ex.Message}", LogColor.Orange));
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ProgressBarScan.IsIndeterminate = false;
            CompareProgressPanel.Visibility = Visibility.Collapsed;
            SetBusy(false, TxtStatus.Text);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async void BtnMoveDuplicates_Click(object sender, RoutedEventArgs e)
    {
        if (_lastScanResult == null || _lastScanResult.Duplicates.Count == 0)
        {
            MessageBox.Show(this, "No duplicates to move. Run a scan first.", "Warning",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Select (or create) the folder to move duplicates to"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string destination = dialog.FolderName;

        var confirm = MessageBox.Show(this,
            $"{_lastScanResult.Duplicates.Count} duplicate file(s) will be moved to:\n{destination}\n\nContinue?",
            "Confirm move", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        SetBusy(true, "Moving duplicates...");
        Action<LogMessage> log = msg => _pendingLog.Enqueue(msg);
        List<ScannedFile> duplicates = _lastScanResult.Duplicates;

        try
        {
            await Task.Run(() => _moverService.MoveDuplicates(duplicates, destination, log));
            FlushLogQueue();
            TxtStatus.Text = $"Move complete: {duplicates.Count} file(s) moved to {destination}";
            BtnMoveDuplicates.IsEnabled = false;
        }
        catch (Exception ex)
        {
            FlushLogQueue();
            AppendLogImmediate(new LogMessage($"Error while moving files: {ex.Message}", LogColor.Orange));
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, TxtStatus.Text);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        BtnCancel.IsEnabled = false;
    }

    private async void BtnCheckCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_pluginsRoot))
        {
            MessageBox.Show(this, "Please select the Plugins folder first.", "Warning",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _logEntries.Clear();
        _catalogMatches.Clear();
        CatalogResultsPanel.Visibility = Visibility.Collapsed;
        ProgressBarScan.IsIndeterminate = true;
        ProgressBarScan.Value = 0;
        TxtProgressCount.Text = string.Empty;
        ResetCompareProgress();
        _cts = new CancellationTokenSource();
        SetBusy(true, "Checking sc4pac catalog...");

        Action<LogMessage> log = msg => _pendingLog.Enqueue(msg);
        var scanProgress = new Progress<ScanProgress>(OnScanProgress);

        try
        {
            List<Sc4pacMatch> matches = await _catalogLookupService.CheckCatalogAsync(_pluginsRoot!, log, scanProgress, _cts.Token);

            FlushLogQueue();

            foreach (Sc4pacMatch match in matches)
            {
                _catalogMatches.Add(match);
            }
            CatalogResultsPanel.Visibility = matches.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            TxtStatus.Text = matches.Count > 0
                ? $"{matches.Count} sc4pac package(s) found for your override files."
                : "No matching sc4pac packages found.";
        }
        catch (OperationCanceledException)
        {
            FlushLogQueue();
            AppendLogImmediate(new LogMessage("Catalog check cancelled by the user.", LogColor.Orange));
            TxtStatus.Text = "Catalog check cancelled.";
        }
        catch (Exception ex)
        {
            FlushLogQueue();
            AppendLogImmediate(new LogMessage($"Error during catalog check: {ex.Message}", LogColor.Orange));
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ProgressBarScan.IsIndeterminate = false;
            SetBusy(false, TxtStatus.Text);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void BtnCopyPackageId_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Sc4pacMatch match })
        {
            TryCopyToClipboard(match.PackageId);
            TxtStatus.Text = $"Copied package ID: {match.PackageId}";
        }
    }

    private void BtnCopyAddCommand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Sc4pacMatch match })
        {
            TryCopyToClipboard(match.Sc4pacAddCommand);
            TxtStatus.Text = $"Copied: {match.Sc4pacAddCommand}";
        }
    }

    private void BtnOpenPage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Sc4pacMatch { PageUrl: { Length: > 0 } url } })
        {
            return;
        }

        try
        {
            // UseShellExecute=true opens the URL with the OS's default browser, same as
            // double-clicking a link - this project never launches a specific browser directly.
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open the page:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Clipboard.SetText can intermittently throw COMException if another process briefly holds
    /// the clipboard (common on Windows) - retrying once after a short delay, as recommended
    /// practice, instead of letting a transient failure surface as an error to the user.
    /// </summary>
    private static void TryCopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception)
        {
            try
            {
                System.Threading.Thread.Sleep(100);
                Clipboard.SetText(text);
            }
            catch
            {
                // Give up silently - copying to the clipboard is a convenience, not essential.
            }
        }
    }

    private void OnScanProgress(ScanProgress p)
    {
        ProgressBarScan.IsIndeterminate = false;
        ProgressBarScan.Maximum = Math.Max(p.Total, 1);
        ProgressBarScan.Value = p.Processed;
        TxtProgressCount.Text = p.Total > 0 ? $"{p.Processed} / {p.Total} files" : string.Empty;
    }

    /// <summary>
    /// Shows/updates the dedicated comparison progress bar. It only becomes visible once the
    /// comparison phase actually starts, and is hidden again as soon as a new scan begins.
    /// </summary>
    private void OnCompareProgress(ScanProgress p)
    {
        if (CompareProgressPanel.Visibility != Visibility.Visible)
        {
            CompareProgressPanel.Visibility = Visibility.Visible;
        }

        ProgressBarCompare.Maximum = Math.Max(p.Total, 1);
        ProgressBarCompare.Value = p.Processed;
        TxtCompareCount.Text = p.Total > 0 ? $"Analyzing duplicates: {p.Processed} / {p.Total}" : "Analyzing duplicates";
    }

    private void ResetCompareProgress()
    {
        CompareProgressPanel.Visibility = Visibility.Collapsed;
        ProgressBarCompare.Value = 0;
        TxtCompareCount.Text = string.Empty;
    }

    /// <summary>
    /// Drains all currently queued log messages and adds them to the bound collection in one
    /// batch. Runs on the UI thread (called from the DispatcherTimer tick or right after an
    /// awaited background task completes).
    /// </summary>
    private void FlushLogQueue()
    {
        if (_pendingLog.IsEmpty)
        {
            return;
        }

        while (_pendingLog.TryDequeue(out LogMessage? msg))
        {
            _logEntries.Add(ToView(msg));
        }

        if (_logEntries.Count > 0)
        {
            LogListBox.ScrollIntoView(_logEntries[^1]);
        }
    }

    private void AppendLogImmediate(LogMessage message)
    {
        _logEntries.Add(ToView(message));
        if (_logEntries.Count > 0)
        {
            LogListBox.ScrollIntoView(_logEntries[^1]);
        }
    }

    private static LogEntryView ToView(LogMessage message)
    {
        Brush brush = message.Color switch
        {
            LogColor.Black => BrushPlugins,
            LogColor.Blue => BrushOverride,
            LogColor.Red => BrushDuplicate,
            LogColor.Orange => BrushWarning,
            LogColor.Gray => BrushStatus,
            _ => BrushPlugins
        };

        return new LogEntryView { Text = message.Text, Brush = brush };
    }

    private void SetBusy(bool busy, string status)
    {
        BtnBrowse.IsEnabled = !busy;
        BtnScan.IsEnabled = !busy && !string.IsNullOrWhiteSpace(_pluginsRoot);
        BtnMoveDuplicates.IsEnabled = !busy && _lastScanResult is { } r && r.Duplicates.Count > 0;
        BtnCheckCatalog.IsEnabled = !busy && !string.IsNullOrWhiteSpace(_pluginsRoot);
        BtnCancel.IsEnabled = busy;
        TxtStatus.Text = status;
        Mouse.OverrideCursor = busy ? Cursors.Arrow : null;
    }

    /// <summary>
    /// Best-effort attempt to make the native window title bar follow the dark theme too
    /// (Windows 10 2004+ / Windows 11). Silently does nothing on older systems.
    /// </summary>
    private void TryEnableDarkTitleBar()
    {
        SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int useImmersiveDarkMode = 1;
                // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 20H1+ / 11). Falls back to the
                // older attribute id (19) used on early Windows 10 20H1 builds if that fails.
                if (DwmSetWindowAttribute(hwnd, 20, ref useImmersiveDarkMode, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(hwnd, 19, ref useImmersiveDarkMode, sizeof(int));
                }
            }
            catch
            {
                // Not critical - the rest of the UI is already themed regardless of the title bar.
            }
        };
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
