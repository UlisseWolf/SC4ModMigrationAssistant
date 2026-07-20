using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SC4ModMigrationAssistant.Models;

namespace SC4ModMigrationAssistant.Services;

/// <summary>
/// Orchestrates the "Check sc4pac Catalog" feature: scans 075-my-plugins / 895-my-overrides,
/// downloads/opens the SC4 Prop Texture Catalog (<see cref="CatalogDatabaseService"/>), looks
/// up every distinct TGI found, and aggregates the results by sc4pac package so the UI can show
/// "these local files are covered by package X" and let the user install it via sc4pac instead
/// of keeping a manual copy.
/// </summary>
public sealed class Sc4pacLookupService
{
    private readonly DbpfScanService _scanService;
    private readonly CatalogDatabaseService _catalogService = new();

    public Sc4pacLookupService(DbpfScanService scanService)
    {
        _scanService = scanService;
    }

    public async Task<List<Sc4pacMatch>> CheckCatalogAsync(
        string pluginsRoot,
        Action<LogMessage> log,
        IProgress<ScanProgress> scanProgress,
        CancellationToken token)
    {
        List<CatalogScanFile> files = await Task.Run(
            () => _scanService.ScanOverrideFoldersForCatalog(pluginsRoot, log, scanProgress, token),
            token).ConfigureAwait(false);

        // Map each distinct TGI to every file that contains it, so the catalog only needs to be
        // looked up once per distinct TGI even if several local files share it.
        var tgiOwners = new Dictionary<TgiKey, List<CatalogScanFile>>();
        foreach (CatalogScanFile file in files)
        {
            foreach (TgiKey tgi in file.Tgis)
            {
                if (!tgiOwners.TryGetValue(tgi, out List<CatalogScanFile>? owners))
                {
                    owners = new List<CatalogScanFile>();
                    tgiOwners[tgi] = owners;
                }
                owners.Add(file);
            }
        }

        if (tgiOwners.Count == 0)
        {
            log.Invoke(new LogMessage("[sc4pac] No TGIs found in 075-my-plugins / 895-my-overrides.", LogColor.Gray));
            return new List<Sc4pacMatch>();
        }

        string catalogPath = await _catalogService.EnsureCatalogDownloadedAsync(log, token).ConfigureAwait(false);
        await _catalogService.LoadIndexesAsync(catalogPath, log, token).ConfigureAwait(false);

        log.Invoke(new LogMessage($"[sc4pac] Looking up {tgiOwners.Count} distinct TGI(s) in the SC4 Prop Texture Catalog...", LogColor.Gray));

        Dictionary<TgiKey, List<CatalogPackageInfo>> matches = await Task.Run(
            () => _catalogService.LookupPackages(tgiOwners.Keys),
            token).ConfigureAwait(false);

        var matchesById = new Dictionary<string, Sc4pacMatch>(StringComparer.OrdinalIgnoreCase);

        foreach ((TgiKey tgi, List<CatalogPackageInfo> infos) in matches)
        {
            foreach (CatalogPackageInfo info in infos)
            {
                if (!matchesById.TryGetValue(info.PackageId, out Sc4pacMatch? match))
                {
                    match = new Sc4pacMatch
                    {
                        PackageId = info.PackageId,
                        DisplayName = ToFriendlyName(info.PackageId),
                        Author = info.Author,
                        PageUrl = info.PageUrl
                    };
                    matchesById[info.PackageId] = match;
                }

                match.MatchedTgiCount++;
                foreach (CatalogScanFile owner in tgiOwners[tgi])
                {
                    if (!match.MatchingFiles.Contains(owner.RelativePath))
                    {
                        match.MatchingFiles.Add(owner.RelativePath);
                    }
                }
            }
        }

        List<Sc4pacMatch> ordered = matchesById.Values.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

        foreach (Sc4pacMatch match in ordered)
        {
            log.Invoke(new LogMessage(
                $"[sc4pac] {match.DisplayName} ({match.PackageId}) - {match.MatchingFiles.Count} local file(s), {match.MatchedTgiCount} TGI(s)",
                LogColor.Red));
        }

        log.Invoke(new LogMessage($"[sc4pac] Catalog check complete: {ordered.Count} package(s) found for {files.Count} local file(s).", LogColor.Gray));

        return ordered;
    }

    /// <summary>
    /// Turns a sc4pac package id like "cyclone-boom:save-warning" into a friendlier display
    /// string like "Save Warning (cyclone-boom)" - the id itself remains available via
    /// <see cref="Sc4pacMatch.PackageId"/> for anything that needs the exact identifier.
    /// </summary>
    private static string ToFriendlyName(string packageId)
    {
        int colon = packageId.IndexOf(':');
        if (colon < 0 || colon == packageId.Length - 1)
        {
            return packageId;
        }

        string group = packageId[..colon];
        string name = packageId[(colon + 1)..];

        string[] words = name.Split('-', StringSplitOptions.RemoveEmptyEntries);
        string titleCased = string.Join(' ', words.Select(w =>
            w.Length == 0 ? w : char.ToUpper(w[0], CultureInfo.InvariantCulture) + w[1..]));

        return $"{titleCased} ({group})";
    }
}
