using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using SC4ModMigrationAssistant.Models;

namespace SC4ModMigrationAssistant.Services;

/// <summary>
/// A single log line produced during scanning or moving.
/// </summary>
public sealed record LogMessage(string Text, LogColor Color);

/// <summary>
/// Progress of a scanning phase, expressed as items processed out of an approximate total.
/// Reused for both the file-scanning phase and the (now very quick) TGI-comparison phase.
/// </summary>
public sealed record ScanProgress(int Processed, int Total);

/// <summary>
/// Full result of a scan: every file found inside 075-my-plugins / 895-my-overrides (the only
/// files we need to keep a record of - see remarks on <see cref="DbpfScanService"/>), plus
/// aggregate counters for the main Plugins folder.
/// </summary>
public sealed class ScanResult
{
    public List<ScannedFile> OverrideFiles { get; } = new();

    /// <summary>Number of files successfully read from the main Plugins folder (informational only).</summary>
    public int MainFilesScanned { get; set; }

    /// <summary>Number of distinct TGIs found across the main Plugins folder (informational only).</summary>
    public int MainUniqueTgiCount { get; set; }

    public int TotalFilesScanned => MainFilesScanned + OverrideFiles.Count;

    public List<ScannedFile> Duplicates => OverrideFiles.Where(f => f.IsDuplicate).ToList();
}

/// <summary>
/// Scans the Plugins folder (and the 075-my-plugins / 895-my-overrides subfolders), reads
/// the TGIs of every DBPF file via csDBPF, and flags genuine duplicates found inside the two
/// override folders.
/// </summary>
/// <remarks>
/// <para><b>Duplicate checking is scoped only to the main Plugins folder.</b> There is no
/// cross-comparison between 075-my-plugins and 895-my-overrides, and no comparison between
/// files within the same override folder either. Both override folders are meant to hold
/// files the user deliberately keeps or uses to override something - two files living there
/// sharing a TGI (or a name) with each other is not, by itself, evidence of a mistake. A match
/// against the main Plugins folder is what actually indicates a redundant copy, so that is the
/// only comparison performed.</para>
///
/// <para><b>Excluded (non-content) TGIs - the main source of false positives:</b> some DBPF
/// entries are structural/bookkeeping data whose TGI is, by design, identical (or drawn from a
/// tiny, near-universal set of default values) across a huge number of otherwise unrelated
/// files. Treating these as evidence of duplicated content produces large-scale false
/// positives. Two are known and excluded via <see cref="ExcludedTgis"/>:
/// <list type="bullet">
/// <item>The Directory subfile (csDBPF's <c>DBPFTGI.DIRECTORY</c>): Type=0xE86B1EEF,
/// Group=0xE86B1EEF, Instance=0x286B1F03 - present, with this exact TGI, in every compressed
/// DBPF file.</item>
/// <item>The common default LD (Lot Data) entry: Type=0x6BE74C60, Group=0x6BE74C60,
/// Instance=0x00000001 - a near-universal default LD instance shared across a huge number of
/// otherwise unrelated lot files.</item>
/// </list>
/// If further testing turns up other similarly universal, non-content TGIs causing false
/// positives, add them to that same set.</para>
///
/// <para><b>Memory design</b> (kept from the previous fix): the main Plugins folder is
/// collapsed into a single flat <c>HashSet&lt;TgiKey&gt;</c> of unique TGIs with no per-file
/// object retained; only the two override folders get lightweight per-file records; TGIs use
/// the boxing-free <see cref="TgiKey"/> type.</para>
/// </remarks>
public sealed class DbpfScanService
{
    public const string Overrides075FolderName = "075-my-plugins";
    public const string Overrides895FolderName = "895-my-overrides";

    /// <summary>
    /// How often (in files processed) a scan progress update is reported to the UI.
    /// </summary>
    public const int ProgressReportInterval = 100;

    /// <summary>
    /// TGIs that identify a structural/bookkeeping DBPF entry (or a near-universal default
    /// value) rather than actual mod content, and must never be treated as evidence that two
    /// files are duplicates. See the remarks on <see cref="DbpfScanService"/> for details on
    /// each one; add more here if testing turns up other similarly universal, non-content TGIs.
    /// </summary>
    private static readonly HashSet<TgiKey> ExcludedTgis = new()
    {
        new TgiKey(0xE86B1EEF, 0xE86B1EEF, 0x286B1F03), // csDBPF.DBPFTGI.DIRECTORY
        new TgiKey(0x6BE74C60, 0x6BE74C60, 0x00000001), // common default LD (Lot Data) entry
    };

    /// <summary>
    /// Scans the given Plugins root folder, in order:
    /// 1) Plugins, excluding 075-my-plugins and 895-my-overrides (logged in black/light) -
    ///    only a flat set of unique TGIs and a set of file names is kept for this folder, not
    ///    a per-file record.
    /// 2) 075-my-plugins (logged in blue/cyan) - each file is checked only against Plugins.
    /// 3) 895-my-overrides (logged in blue/cyan) - each file is checked only against Plugins,
    ///    for both TGI and file name.
    /// Genuine duplicates are logged in red/magenta - see <see cref="ScannedFile.IsDuplicate"/>.
    /// </summary>
    /// <param name="pluginsRoot">Full path of the Plugins folder to scan.</param>
    /// <param name="log">Callback invoked (from a background thread) for every log line produced.</param>
    /// <param name="scanProgress">Callback invoked at most every <see cref="ProgressReportInterval"/> files while scanning.</param>
    /// <param name="compareProgress">Callback invoked for the (now very quick) final comparison step.</param>
    /// <param name="token">Cancellation token, checked between files.</param>
    public ScanResult ScanPlugins(
        string pluginsRoot,
        Action<LogMessage> log,
        IProgress<ScanProgress> scanProgress,
        IProgress<ScanProgress> compareProgress,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(pluginsRoot) || !Directory.Exists(pluginsRoot))
        {
            throw new DirectoryNotFoundException($"Plugins folder not found: {pluginsRoot}");
        }

        string? overrides075Path = FindChildFolder(pluginsRoot, Overrides075FolderName);
        string? overrides895Path = FindChildFolder(pluginsRoot, Overrides895FolderName);

        var result = new ScanResult();

        var excludedRoots = new List<string>();
        if (overrides075Path != null) excludedRoots.Add(overrides075Path);
        if (overrides895Path != null) excludedRoots.Add(overrides895Path);

        // --- Pre-count files so the progress bar has a known maximum ---
        log.Invoke(new LogMessage("Counting files...", LogColor.Gray));
        int total = CountFiles(pluginsRoot, excludedRoots, token);
        if (overrides075Path != null) total += CountFiles(overrides075Path, new List<string>(), token);
        if (overrides895Path != null) total += CountFiles(overrides895Path, new List<string>(), token);

        int processed = 0;
        scanProgress.Report(new ScanProgress(0, total));

        void OnFileProcessed()
        {
            processed++;
            if (processed % ProgressReportInterval == 0 || processed == total)
            {
                scanProgress.Report(new ScanProgress(processed, total));
            }
        }

        // 1) Plugins folder, excluding 075-my-plugins and 895-my-overrides.
        // Only a flat HashSet<TgiKey> of unique TGIs, and a set of file names, are kept - no
        // per-file object is retained for the main folder at all.
        log.Invoke(new LogMessage($"--- Scanning Plugins folder: {pluginsRoot} ---", LogColor.Gray));
        var mainTgiIndex = new HashSet<TgiKey>();
        var mainFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string filePath in EnumerateFilesExcluding(pluginsRoot, excludedRoots, token))
        {
            token.ThrowIfCancellationRequested();

            string relativePath = Path.GetRelativePath(pluginsRoot, filePath);
            int tgiCount = ReadTgisInto(filePath, mainTgiIndex, log, relativePath);
            if (tgiCount < 0)
            {
                OnFileProcessed();
                continue;
            }

            result.MainFilesScanned++;
            mainFileNames.Add(Path.GetFileName(filePath));
            log.Invoke(new LogMessage($"{relativePath}  ({tgiCount} TGI)", LogColor.Black));
            OnFileProcessed();
        }

        result.MainUniqueTgiCount = mainTgiIndex.Count;

        // 2) & 3) Override folders. Each file becomes a lightweight ScannedFile record; it is
        // only ever checked against mainTgiIndex / mainFileNames, never against the other
        // override folder or against other files in its own folder.
        if (overrides075Path != null)
        {
            log.Invoke(new LogMessage($"--- Scanning {Overrides075FolderName} folder ---", LogColor.Gray));
            ScanOverrideFolder(overrides075Path, pluginsRoot, SourceCategory.Overrides075, mainTgiIndex, mainFileNames, result, log, OnFileProcessed, token);
        }
        else
        {
            log.Invoke(new LogMessage($"{Overrides075FolderName} folder not found, skipped.", LogColor.Orange));
        }

        if (overrides895Path != null)
        {
            log.Invoke(new LogMessage($"--- Scanning {Overrides895FolderName} folder ---", LogColor.Gray));
            ScanOverrideFolder(overrides895Path, pluginsRoot, SourceCategory.Overrides895, mainTgiIndex, mainFileNames, result, log, OnFileProcessed, token);
        }
        else
        {
            log.Invoke(new LogMessage($"{Overrides895FolderName} folder not found, skipped.", LogColor.Orange));
        }

        scanProgress.Report(new ScanProgress(total, total));

        // Every override file was already checked against mainTgiIndex/mainFileNames while it
        // was being read, so there is no separate heavy comparison pass left to run - the
        // duplicate list is already fully determined. The compare progress bar is still
        // reported (briefly) for UI consistency.
        log.Invoke(new LogMessage("--- Comparing TGIs (against Plugins only) ---", LogColor.Gray));
        compareProgress.Report(new ScanProgress(0, 1));
        compareProgress.Report(new ScanProgress(1, 1));

        mainTgiIndex.Clear();
        mainFileNames.Clear();

        int duplicateCount = result.Duplicates.Count;
        foreach (ScannedFile duplicate in result.Duplicates)
        {
            string nameNote = duplicate.Category == SourceCategory.Overrides895
                ? ", same file name found in Plugins"
                : string.Empty;

            log.Invoke(new LogMessage(
                $"[DUPLICATE] {duplicate.RelativePath} -> {duplicate.MainMatchCount} TGI(s) also in Plugins{nameNote}",
                LogColor.Red));
        }

        log.Invoke(new LogMessage(
            $"--- Scan complete: {result.TotalFilesScanned} files read, {duplicateCount} duplicates found ---",
            LogColor.Gray));

        return result;
    }

    /// <summary>
    /// Scans only 075-my-plugins and 895-my-overrides (never the main Plugins folder) and
    /// returns every file found together with its full, deduplicated set of TGIs. Used by the
    /// "Check sc4pac Catalog" feature, which needs the actual TGIs (not just match counts) to
    /// look them up in the SC4 Prop Texture Catalog.
    /// </summary>
    /// <param name="pluginsRoot">Full path of the Plugins folder (must contain the two override subfolders).</param>
    /// <param name="log">Callback invoked (from a background thread) for every log line produced.</param>
    /// <param name="scanProgress">Callback invoked at most every <see cref="ProgressReportInterval"/> files.</param>
    /// <param name="token">Cancellation token, checked between files.</param>
    public List<CatalogScanFile> ScanOverrideFoldersForCatalog(
        string pluginsRoot,
        Action<LogMessage> log,
        IProgress<ScanProgress> scanProgress,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(pluginsRoot) || !Directory.Exists(pluginsRoot))
        {
            throw new DirectoryNotFoundException($"Plugins folder not found: {pluginsRoot}");
        }

        string? overrides075Path = FindChildFolder(pluginsRoot, Overrides075FolderName);
        string? overrides895Path = FindChildFolder(pluginsRoot, Overrides895FolderName);

        var results = new List<CatalogScanFile>();

        int total = 0;
        if (overrides075Path != null) total += CountFiles(overrides075Path, new List<string>(), token);
        if (overrides895Path != null) total += CountFiles(overrides895Path, new List<string>(), token);

        int processed = 0;
        scanProgress.Report(new ScanProgress(0, total));

        void OnFileProcessed()
        {
            processed++;
            if (processed % ProgressReportInterval == 0 || processed == total)
            {
                scanProgress.Report(new ScanProgress(processed, total));
            }
        }

        void ScanFolder(string folderPath, SourceCategory category)
        {
            foreach (string filePath in EnumerateFilesExcluding(folderPath, new List<string>(), token))
            {
                token.ThrowIfCancellationRequested();

                string relativePath = Path.GetRelativePath(pluginsRoot, filePath);

                HashSet<TgiKey> fileTgis;
                try
                {
                    fileTgis = new HashSet<TgiKey>();
                    foreach (TgiKey key in DbpfParsingService.EnumerateTgis(filePath))
                    {
                        if (!ExcludedTgis.Contains(key))
                        {
                            fileTgis.Add(key);
                        }
                    }
                }
                catch (Exception ex)
                {
                    log.Invoke(new LogMessage($"[SKIP] {relativePath} - could not be read as DBPF ({ex.Message})", LogColor.Orange));
                    OnFileProcessed();
                    continue;
                }

                results.Add(new CatalogScanFile
                {
                    FullPath = filePath,
                    RelativePath = relativePath,
                    Category = category,
                    Tgis = fileTgis
                });

                log.Invoke(new LogMessage($"{relativePath}  ({fileTgis.Count} TGI)", LogColor.Blue));
                OnFileProcessed();
            }
        }

        if (overrides075Path != null)
        {
            log.Invoke(new LogMessage($"--- Scanning {Overrides075FolderName} folder ---", LogColor.Gray));
            ScanFolder(overrides075Path, SourceCategory.Overrides075);
        }
        else
        {
            log.Invoke(new LogMessage($"{Overrides075FolderName} folder not found, skipped.", LogColor.Orange));
        }

        if (overrides895Path != null)
        {
            log.Invoke(new LogMessage($"--- Scanning {Overrides895FolderName} folder ---", LogColor.Gray));
            ScanFolder(overrides895Path, SourceCategory.Overrides895);
        }
        else
        {
            log.Invoke(new LogMessage($"{Overrides895FolderName} folder not found, skipped.", LogColor.Orange));
        }

        scanProgress.Report(new ScanProgress(total, total));
        return results;
    }

    /// <summary>
    /// Reads a single DBPF file's TGIs directly into <paramref name="destination"/> (no
    /// intermediate per-file collection is kept), returning how many (non-excluded) TGIs it
    /// contained, or -1 if the file could not be read as DBPF (in which case a warning is
    /// already logged).
    /// </summary>
    private static int ReadTgisInto(string filePath, HashSet<TgiKey> destination, Action<LogMessage> log, string relativePath)
    {
        try
        {
            int count = 0;
            foreach (TgiKey key in DbpfParsingService.EnumerateTgis(filePath))
            {
                if (ExcludedTgis.Contains(key))
                {
                    continue;
                }
                destination.Add(key);
                count++;
            }
            return count;
        }
        catch (Exception ex)
        {
            log.Invoke(new LogMessage($"[SKIP] {relativePath} - could not be read as DBPF ({ex.Message})", LogColor.Orange));
            return -1;
        }
    }

    private void ScanOverrideFolder(
        string folderToScan,
        string pluginsRoot,
        SourceCategory category,
        HashSet<TgiKey> mainTgiIndex,
        HashSet<string> mainFileNames,
        ScanResult result,
        Action<LogMessage> log,
        Action onFileProcessed,
        CancellationToken token)
    {
        foreach (string filePath in EnumerateFilesExcluding(folderToScan, new List<string>(), token))
        {
            token.ThrowIfCancellationRequested();

            string relativePath = Path.GetRelativePath(pluginsRoot, filePath);
            string fileName = Path.GetFileName(filePath);

            int mainMatches = 0;
            int tgiCount = 0;
            try
            {
                // Only this one file's TGIs are held at a time (deduplicated), and only for the
                // duration of this loop iteration - nothing is retained or cross-referenced
                // against other override files.
                var fileTgis = new HashSet<TgiKey>();
                foreach (TgiKey key in DbpfParsingService.EnumerateTgis(filePath))
                {
                    if (!ExcludedTgis.Contains(key))
                    {
                        fileTgis.Add(key);
                    }
                }

                tgiCount = fileTgis.Count;
                foreach (TgiKey key in fileTgis)
                {
                    if (mainTgiIndex.Contains(key))
                    {
                        mainMatches++;
                    }
                }
            }
            catch (Exception ex)
            {
                log.Invoke(new LogMessage($"[SKIP] {relativePath} - could not be read as DBPF ({ex.Message})", LogColor.Orange));
                onFileProcessed();
                continue;
            }

            var record = new ScannedFile
            {
                FullPath = filePath,
                RelativePath = relativePath,
                Category = category,
                MainMatchCount = mainMatches
            };

            if (category == SourceCategory.Overrides895)
            {
                record.NameMatchFound = mainFileNames.Contains(fileName);
            }

            result.OverrideFiles.Add(record);
            log.Invoke(new LogMessage($"{relativePath}  ({tgiCount} TGI)", LogColor.Blue));
            onFileProcessed();
        }
    }

    /// <summary>
    /// Finds, among the direct subfolders of <paramref name="parent"/>, the one whose name
    /// matches (case-insensitive) <paramref name="folderName"/>.
    /// </summary>
    private static string? FindChildFolder(string parent, string folderName)
    {
        string direct = Path.Combine(parent, folderName);
        if (Directory.Exists(direct))
        {
            return direct;
        }

        try
        {
            return Directory.EnumerateDirectories(parent)
                .FirstOrDefault(d => string.Equals(Path.GetFileName(d), folderName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Quickly counts the matching DBPF files under <paramref name="root"/>, without descending
    /// into any of the <paramref name="excludedRoots"/> subtrees (avoids wasted disk I/O).
    /// </summary>
    private int CountFiles(string root, List<string> excludedRoots, CancellationToken token)
    {
        int count = 0;
        foreach (string _ in EnumerateFilesExcluding(root, excludedRoots, token))
        {
            count++;
        }
        return count;
    }

    /// <summary>
    /// Recursively enumerates files matching known DBPF file extensions under <paramref name="root"/>,
    /// pruning any subtree listed in <paramref name="excludedRoots"/> instead of walking into it and
    /// filtering afterwards. This avoids needless disk I/O on large override folders and keeps a
    /// single bad subfolder (permissions, reparse points, etc.) from stopping the whole scan.
    /// </summary>
    private static IEnumerable<string> EnumerateFilesExcluding(string root, List<string> excludedRoots, CancellationToken token)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            string current = stack.Pop();

            IEnumerable<string> subDirs = Array.Empty<string>();
            IEnumerable<string> files = Array.Empty<string>();

            try
            {
                subDirs = Directory.EnumerateDirectories(current);
                files = Directory.EnumerateFiles(current);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (string file in files)
            {
                if (HasDBPFFileExtension(file))
                {
                    yield return file;
                }
            }

            foreach (string dir in subDirs)
            {
                bool excluded = excludedRoots.Any(ex => string.Equals(ex, dir, StringComparison.OrdinalIgnoreCase));
                if (!excluded)
                {
                    stack.Push(dir);
                }
            }
        }

        static bool HasDBPFFileExtension(ReadOnlySpan<char> path)
        {
            // Using ReadOnlySpan<char> allows the file extension to be compared without
            // allocating a new string.

            ReadOnlySpan<char> fileExtension = Path.GetExtension(path);

            // Files without an extension are treated as potential .sc4* files, there are released
            // plugins that don't have a file extension. For example, Bosham Church by mintoes.
            //
            // The StartsWith comparison with a .sc4 file extension is an optimization to handle
            // the .sc4desc, .sc4lot, and .sc4model file extensions with a single comparison.

            return fileExtension.IsEmpty
                || fileExtension.Equals(".dat", StringComparison.OrdinalIgnoreCase)
                || fileExtension.StartsWith(".sc4", StringComparison.OrdinalIgnoreCase);
        }
    }
}
