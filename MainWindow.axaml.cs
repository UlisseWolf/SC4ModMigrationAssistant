using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SC4ModMigrationAssistant.Dialogs;
using SC4ModMigrationAssistant.Models;
using SC4ModMigrationAssistant.Services;

namespace SC4ModMigrationAssistant;

public partial class MainWindow : Window
{
    private readonly DbpfScanService _scanService = new();
    private readonly DuplicateMoverService _moverService = new();
    private readonly Sc4pacLookupService _catalogLookupService;
    private readonly Sc4pacPluginsFileService _pluginsFileService = new();

    private readonly ObservableCollection<LogEntryView> _logEntries = new();
    private readonly ObservableCollection<Sc4pacMatch> _catalogMatches = new();
    private readonly ConcurrentQueue<LogMessage> _pendingLog = new();
    private readonly DispatcherTimer _logFlushTimer;

    // Synthwave neon palette used for log lines, matching the brushes defined in App.axaml.
    private static readonly IBrush BrushPlugins = new SolidColorBrush(Color.FromRgb(0xF1, 0xEA, 0xFB));   // soft white/lavender
    private static readonly IBrush BrushOverride = new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xEE));  // neon cyan
    private static readonly IBrush BrushDuplicate = new SolidColorBrush(Color.FromRgb(0xFF, 0x3C, 0xAC)); // neon pink
    private static readonly IBrush BrushWarning = new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x6C));   // neon amber
    private static readonly IBrush BrushStatus = new SolidColorBrush(Color.FromRgb(0x9B, 0x8F, 0xC0));    // muted lavender-gray

    // System.Text.Json's indented writer defaults to 2 spaces per level, matching sc4pac's own
    // "explicit": [ ... ] file formatting.
    private static readonly JsonSerializerOptions Sc4pacJsonOptions = new() { WriteIndented = true };

    private string? _pluginsRoot;
    private ScanResult? _lastScanResult;
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
        _catalogLookupService = new Sc4pacLookupService(_scanService);
        LogListBox.ItemsSource = _logEntries;
        CatalogResultsListBox.ItemsSource = _catalogMatches;

        // Best-effort native dark title bar on Windows; harmless no-op elsewhere.
        Opened += (_, _) => TryEnableDarkTitleBar();

        string? detectedPluginsFile = _pluginsFileService.TryFindDefaultFile();
        if (detectedPluginsFile != null)
        {
            TxtSc4pacPluginsPath.Text = detectedPluginsFile;
        }

        // Log lines are enqueued from the background scan thread (cheap, thread-safe, no UI
        // marshaling) and flushed to the UI in small batches on a timer. This is what keeps
        // the window responsive even with tens of thousands of files: without batching, a
        // UI update per file is what causes the "freezing" during scans.
        _logFlushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _logFlushTimer.Tick += (_, _) => FlushLogQueue();
        _logFlushTimer.Start();
    }

    private async void BtnBrowse_Click(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the Plugins folder",
            AllowMultiple = false
        });

        if (folders.Count == 0)
        {
            return;
        }

        string? path = folders[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        _pluginsRoot = path;
        TxtPluginsPath.Text = _pluginsRoot;
        BtnScan.IsEnabled = true;
        BtnMoveDuplicates.IsEnabled = false;
        BtnCheckCatalog.IsEnabled = true;
        _lastScanResult = null;
        _logEntries.Clear();
        _catalogMatches.Clear();
        CatalogResultsPanel.IsVisible = false;
        ProgressBarScan.Value = 0;
        TxtProgressCount.Text = string.Empty;
        ResetCompareProgress();
    }

    private async void BtnBrowseSc4pacPlugins_Click(object? sender, RoutedEventArgs e)
    {
        string defaultDir = Sc4pacPluginsFileService.DefaultProfilesRoot;
        IStorageFolder? startLocation = Directory.Exists(defaultDir)
            ? await StorageProvider.TryGetFolderFromPathAsync(defaultDir)
            : null;

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select sc4pac-plugins.json",
            AllowMultiple = false,
            SuggestedStartLocation = startLocation,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("sc4pac plugins file") { Patterns = new[] { "sc4pac-plugins.json" } },
                new FilePickerFileType("JSON files") { Patterns = new[] { "*.json" } },
                FilePickerFileTypes.All
            }
        });

        if (files.Count == 0)
        {
            return;
        }

        string? path = files[0].TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
        {
            TxtSc4pacPluginsPath.Text = path;
        }
    }

    private async void BtnScan_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_pluginsRoot))
        {
            await MessageDialog.ShowInfo(this, "Warning", "Please select the Plugins folder first.");
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
            await MessageDialog.ShowInfo(this, "Error", ex.Message);
        }
        finally
        {
            ProgressBarScan.IsIndeterminate = false;
            CompareProgressPanel.IsVisible = false;
            SetBusy(false, TxtStatus.Text);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async void BtnMoveDuplicates_Click(object? sender, RoutedEventArgs e)
    {
        if (_lastScanResult == null || _lastScanResult.Duplicates.Count == 0)
        {
            await MessageDialog.ShowInfo(this, "Warning", "No duplicates to move. Run a scan first.");
            return;
        }

        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select (or create) the folder to move duplicates to",
            AllowMultiple = false
        });

        if (folders.Count == 0)
        {
            return;
        }

        string? destination = folders[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(destination))
        {
            return;
        }

        bool confirm = await MessageDialog.ShowConfirm(this, "Confirm move",
            $"{_lastScanResult.Duplicates.Count} duplicate file(s) will be moved to:\n{destination}\n\nContinue?");

        if (!confirm)
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
            await MessageDialog.ShowInfo(this, "Error", ex.Message);
        }
        finally
        {
            SetBusy(false, TxtStatus.Text);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        BtnCancel.IsEnabled = false;
    }

    private async void BtnCheckCatalog_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_pluginsRoot))
        {
            await MessageDialog.ShowInfo(this, "Warning", "Please select the Plugins folder first.");
            return;
        }

        _logEntries.Clear();
        _catalogMatches.Clear();
        CatalogResultsPanel.IsVisible = false;
        ProgressBarScan.IsIndeterminate = true;
        ProgressBarScan.Value = 0;
        TxtProgressCount.Text = string.Empty;
        ResetCompareProgress();
        _cts = new CancellationTokenSource();
        SetBusy(true, "Checking sc4pac catalog...");

        Action<LogMessage> log = msg => _pendingLog.Enqueue(msg);
        var scanProgress = new Progress<ScanProgress>(OnScanProgress);

        HashSet<string>? installedPackageIds = null;
        string sc4pacPluginsPath = TxtSc4pacPluginsPath.Text?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(sc4pacPluginsPath))
        {
            if (File.Exists(sc4pacPluginsPath))
            {
                try
                {
                    installedPackageIds = _pluginsFileService.LoadInstalledPackageIds(sc4pacPluginsPath, log);
                    log.Invoke(new LogMessage(
                        $"[sc4pac] Loaded {installedPackageIds.Count} already-installed package ID(s) (explicit + dependencies) via {sc4pacPluginsPath}",
                        LogColor.Gray));
                }
                catch (Exception ex)
                {
                    log.Invoke(new LogMessage(
                        $"[sc4pac] Could not read sc4pac-plugins.json ({ex.Message}) - results may include already-installed packages.",
                        LogColor.Orange));
                }
            }
            else
            {
                log.Invoke(new LogMessage(
                    $"[sc4pac] sc4pac-plugins.json not found at {sc4pacPluginsPath} - results may include already-installed packages.",
                    LogColor.Orange));
            }
        }
        else
        {
            log.Invoke(new LogMessage(
                "[sc4pac] No sc4pac-plugins.json set - results may include packages you already installed via sc4pac.",
                LogColor.Orange));
        }

        try
        {
            List<Sc4pacMatch> matches = await _catalogLookupService.CheckCatalogAsync(
                _pluginsRoot!, log, scanProgress, _cts.Token, installedPackageIds);

            FlushLogQueue();

            foreach (Sc4pacMatch match in matches)
            {
                _catalogMatches.Add(match);
            }
            CatalogResultsPanel.IsVisible = matches.Count > 0;

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
            await MessageDialog.ShowInfo(this, "Error", ex.Message);
        }
        finally
        {
            ProgressBarScan.IsIndeterminate = false;
            SetBusy(false, TxtStatus.Text);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async void BtnCopyPackageId_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Sc4pacMatch match })
        {
            await TryCopyToClipboard(match.PackageId);
            TxtStatus.Text = $"Copied package ID: {match.PackageId}";
        }
    }

    private async void BtnCopyAddCommand_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Sc4pacMatch match })
        {
            await TryCopyToClipboard(match.Sc4pacAddCommand);
            TxtStatus.Text = $"Copied: {match.Sc4pacAddCommand}";
        }
    }

    private async void BtnOpenPage_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Sc4pacMatch { PageUrl: { Length: > 0 } url } })
        {
            return;
        }

        try
        {
            // UseShellExecute=true opens the URL with the OS's default handler (xdg-open on
            // Linux, "open" on macOS, the default browser association on Windows) - this
            // project never launches a specific browser directly.
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            await MessageDialog.ShowInfo(this, "Error", $"Could not open the page:\n{ex.Message}");
        }
    }

    /// <summary>
    /// Exports every package currently listed in the catalog results as a sc4pac-compatible
    /// JSON file: <c>{ "explicit": ["group:package-id", ...] }</c>, sorted and de-duplicated.
    /// This matches the format sc4pac itself uses for its explicit-packages list, so the file
    /// can be handed straight to sc4pac to install everything at once, easing migration to it.
    /// </summary>
    private async void BtnExportCatalogJson_Click(object? sender, RoutedEventArgs e)
    {
        if (_catalogMatches.Count == 0)
        {
            await MessageDialog.ShowInfo(this, "Warning", "No sc4pac packages to export yet. Run \"Check sc4pac Catalog\" first.");
            return;
        }

        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export sc4pac package list",
            SuggestedFileName = "sc4pac-import.json",
            DefaultExtension = "json",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("sc4pac JSON file") { Patterns = new[] { "*.json" } },
                FilePickerFileTypes.All
            }
        });

        if (file == null)
        {
            return;
        }

        string? filePath = file.TryGetLocalPath();
        if (string.IsNullOrEmpty(filePath))
        {
            return;
        }

        List<string> packageIds = _catalogMatches
            .Select(m => m.PackageId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var payload = new Sc4pacExplicitPackages { Explicit = packageIds };

        try
        {
            string json = JsonSerializer.Serialize(payload, Sc4pacJsonOptions);
            await File.WriteAllTextAsync(filePath, json);

            AppendLogImmediate(new LogMessage(
                $"[sc4pac] Exported {packageIds.Count} package(s) to {filePath}", LogColor.Gray));
            TxtStatus.Text = $"Exported {packageIds.Count} package(s) to {filePath}";
        }
        catch (Exception ex)
        {
            await MessageDialog.ShowInfo(this, "Error", $"Could not save the file:\n{ex.Message}");
        }
    }

    /// <summary>
    /// Copies text to the clipboard, retrying once after a short delay if the first attempt
    /// fails - clipboard access can transiently fail if another process briefly holds it.
    /// </summary>
    private async Task TryCopyToClipboard(string text)
    {
        IClipboard? clipboard = Clipboard;
        if (clipboard == null)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(text);
        }
        catch (Exception)
        {
            try
            {
                await Task.Delay(100);
                await clipboard.SetTextAsync(text);
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
        if (!CompareProgressPanel.IsVisible)
        {
            CompareProgressPanel.IsVisible = true;
        }

        ProgressBarCompare.Maximum = Math.Max(p.Total, 1);
        ProgressBarCompare.Value = p.Processed;
        TxtCompareCount.Text = p.Total > 0 ? $"Analyzing duplicates: {p.Processed} / {p.Total}" : "Analyzing duplicates";
    }

    private void ResetCompareProgress()
    {
        CompareProgressPanel.IsVisible = false;
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
        IBrush brush = message.Color switch
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

    private void SetBusy(bool busy, string? status)
    {
        BtnBrowse.IsEnabled = !busy;
        BtnScan.IsEnabled = !busy && !string.IsNullOrWhiteSpace(_pluginsRoot);
        BtnMoveDuplicates.IsEnabled = !busy && _lastScanResult is { } r && r.Duplicates.Count > 0;
        BtnCheckCatalog.IsEnabled = !busy && !string.IsNullOrWhiteSpace(_pluginsRoot);
        BtnCancel.IsEnabled = busy;
        TxtStatus.Text = status;
        Cursor = busy ? new Cursor(StandardCursorType.Wait) : Cursor.Default;
    }

    /// <summary>
    /// Best-effort attempt to make the native window title bar follow the dark theme too
    /// (Windows 10 2004+ / Windows 11). No-op on Linux and macOS, where Avalonia's own window
    /// chrome / theme handling already applies consistently.
    /// </summary>
    private void TryEnableDarkTitleBar()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            IntPtr handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            int useImmersiveDarkMode = 1;
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 20H1+ / 11). Falls back to the
            // older attribute id (19) used on early Windows 10 20H1 builds if that fails.
            if (DwmSetWindowAttribute(handle, 20, ref useImmersiveDarkMode, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(handle, 19, ref useImmersiveDarkMode, sizeof(int));
            }
        }
        catch
        {
            // Not critical - the rest of the UI is already themed regardless of the title bar.
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
