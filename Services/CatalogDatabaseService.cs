using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SC4ModMigrationAssistant.Models;

namespace SC4ModMigrationAssistant.Services;

/// <summary>
/// A sc4pac package as recorded in the catalog, with just the fields this app needs.
/// </summary>
public sealed record CatalogPackageInfo(string PackageId, string? Author, string? PageUrl);

/// <summary>
/// Downloads the community-run SC4 Prop Texture Catalog database
/// (<c>Catalog.db</c>, source: https://github.com/noah-severyn/SC4PropTextureCatalog) directly
/// from GitHub, caches it locally, and resolves TGIs to the sc4pac package(s) that provide them.
/// </summary>
/// <remarks>
/// <para>Schema (verified directly against a real copy of Catalog.db - not guessed):</para>
/// <list type="bullet">
/// <item><c>TGIs(FileId, TGI, Category, Name)</c> - <c>TGI</c> is a TEXT column formatted as
/// <c>"0xTTTTTTTT, 0xGGGGGGGG, 0xIIIIIIII"</c> (lowercase hex, comma-space separated). For
/// Props/Textures/etc. where the Type ID is implied by the category, the catalog sometimes
/// writes <c>"#"</c> instead of a Type value (about 0.5% of rows) - <see cref="TryParseCatalogTgi"/>
/// handles both forms, matching by Group+Instance only when Type is <c>"#"</c>.</item>
/// <item><c>PackageFiles(PackageId, FileId)</c> - many-to-many link between packages and files.
/// Some rows have <c>FileId = 0</c> (packages with no indexed TGI data); these are skipped.</item>
/// <item><c>Packages(Id, Name, Version, Subfolder, Websites, Author)</c> - <c>Name</c> is the
/// sc4pac package identifier itself, in <c>"group:package-name"</c> form. <c>Websites</c> is a
/// <c>;</c>-separated list of the mod's real download/info page URL(s).</item>
/// </list>
/// <para>This was confirmed by opening an actual copy of Catalog.db provided directly - not
/// inferred from documentation, which the upstream project does not publish for this database.
/// If a future version of the catalog changes its schema, the queries in
/// <see cref="LoadIndexesAsync"/> are the only place that needs updating.</para>
/// </remarks>
public sealed class CatalogDatabaseService
{
    /// <summary>Direct GitHub raw-content download URL for the catalog database.</summary>
    private const string CatalogDownloadUrl =
        "https://raw.githubusercontent.com/noah-severyn/SC4PropTextureCatalog/main/SC4PropTextureCatalogAPI/data/Catalog.db";

    /// <summary>
    /// Local cache location: <c>%LocalAppData%\SC4ModMigrationAssistant\Catalog.db</c>.
    /// Re-downloaded only if missing (delete the file to force a fresh download).
    /// </summary>
    public static string CacheFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SC4ModMigrationAssistant",
        "Catalog.db");

    // In-memory indexes, built once per app session (LoadIndexesAsync is a no-op if already
    // loaded) and reused across multiple "Check sc4pac Catalog" runs. Together these total a
    // few tens of MB at most for the current catalog size (~260k TGI rows, ~5k packages),
    // consistent with this app's low-memory design goals - nowhere near the size of the actual
    // Plugins-folder TGI sets this app scans separately.
    private Dictionary<TgiKey, List<int>>? _exactTgiToFileIds;
    private Dictionary<(uint Group, uint Instance), List<int>>? _wildcardTypeToFileIds;
    private Dictionary<int, List<int>>? _packageIdsByFileId;
    private Dictionary<int, CatalogPackageInfo>? _packagesById;

    /// <summary>
    /// Downloads Catalog.db from GitHub into <see cref="CacheFilePath"/> if it isn't already
    /// cached there, and returns that path.
    /// </summary>
    public async Task<string> EnsureCatalogDownloadedAsync(Action<LogMessage> log, CancellationToken token)
    {
        string path = CacheFilePath;
        FileInfo fileInfo = new(path);

        if (fileInfo.Exists && fileInfo.Length > 1_000_000)
        {
            double sizeMb = fileInfo.Length / 1024.0 / 1024.0;
            log.Invoke(new LogMessage($"[sc4pac] Using cached SC4 Prop Texture Catalog ({sizeMb:F1} MB): {path}", LogColor.Gray));
            return path;
        }
        
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        log.Invoke(new LogMessage("[sc4pac] Downloading SC4 Prop Texture Catalog (Catalog.db, ~22 MB) from GitHub...", LogColor.Gray));

        string tempPath = path + ".download";
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        using (var response = await http.GetAsync(CatalogDownloadUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();

            HttpContent content = response.Content;
            long contentLength = content.Headers.ContentLength ?? 0;

            FileStreamOptions fileStreamOptions = new()
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
                PreallocationSize = contentLength > 0 ? contentLength : 0
            };

            using Stream httpStream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using FileStream fileStream = new(tempPath, fileStreamOptions);
            await httpStream.CopyToAsync(fileStream, token).ConfigureAwait(false);
        }

        File.Move(tempPath, path, overwrite: true);
        log.Invoke(new LogMessage($"[sc4pac] Catalog downloaded to {path}.", LogColor.Gray));
        return path;
    }

    /// <summary>
    /// Loads the whole TGI -&gt; file -&gt; package chain into memory. Safe to call every time;
    /// does nothing after the first successful call in a given app session.
    /// </summary>
    public Task LoadIndexesAsync(string catalogPath, Action<LogMessage> log, CancellationToken token)
    {
        if (_exactTgiToFileIds != null)
        {
            return Task.CompletedTask;
        }

        return Task.Run(() =>
        {
            using var connection = new SqliteConnection($"Data Source={catalogPath};Mode=ReadOnly");
            connection.Open();

            var exact = new Dictionary<TgiKey, List<int>>();
            var wildcard = new Dictionary<(uint, uint), List<int>>();
            int skippedUnparsable = 0;

            using (SqliteCommand cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT FileId, TGI FROM TGIs";
                using SqliteDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();

                    int fileId = reader.GetInt32(0);
                    string tgiText = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);

                    if (!TryParseCatalogTgi(tgiText, out bool hasType, out uint type, out uint group, out uint instance))
                    {
                        skippedUnparsable++;
                        continue;
                    }

                    if (hasType)
                    {
                        AddToIndex(exact, new TgiKey(type, group, instance), fileId);
                    }
                    else
                    {
                        AddToIndex(wildcard, (group, instance), fileId);
                    }
                }
            }

            var packageIdsByFileId = new Dictionary<int, List<int>>();
            using (SqliteCommand cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT FileId, PackageId FROM PackageFiles WHERE FileId != 0";
                using SqliteDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    AddToIndex(packageIdsByFileId, reader.GetInt32(0), reader.GetInt32(1));
                }
            }

            var packagesById = new Dictionary<int, CatalogPackageInfo>();
            using (SqliteCommand cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT Id, Name, Author, Websites FROM Packages";
                using SqliteDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();

                    int id = reader.GetInt32(0);
                    string name = reader.GetString(1);
                    string? author = reader.IsDBNull(2) ? null : reader.GetString(2);
                    string? websites = reader.IsDBNull(3) ? null : reader.GetString(3);
                    string? firstUrl = string.IsNullOrWhiteSpace(websites)
                        ? null
                        : websites.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

                    packagesById[id] = new CatalogPackageInfo(name, author, firstUrl);
                }
            }

            _exactTgiToFileIds = exact;
            _wildcardTypeToFileIds = wildcard;
            _packageIdsByFileId = packageIdsByFileId;
            _packagesById = packagesById;

            string skipNote = skippedUnparsable > 0 ? $" ({skippedUnparsable} row(s) had an unparsable TGI and were skipped)" : string.Empty;
            log.Invoke(new LogMessage(
                $"[sc4pac] Catalog index ready: {exact.Count} exact + {wildcard.Count} type-agnostic TGI key(s), {packagesById.Count} package(s){skipNote}.",
                LogColor.Gray));
        }, token);
    }

    /// <summary>
    /// Resolves every TGI in <paramref name="tgis"/> to the sc4pac package(s) that provide it,
    /// if any. <see cref="LoadIndexesAsync"/> must have been called first. TGIs with no match
    /// are simply absent from the result - the normal, expected outcome for most files.
    /// </summary>
    public Dictionary<TgiKey, List<CatalogPackageInfo>> LookupPackages(IReadOnlyCollection<TgiKey> tgis)
    {
        var result = new Dictionary<TgiKey, List<CatalogPackageInfo>>();
        if (_exactTgiToFileIds == null || _wildcardTypeToFileIds == null || _packageIdsByFileId == null || _packagesById == null)
        {
            throw new InvalidOperationException($"{nameof(LoadIndexesAsync)} must be called before {nameof(LookupPackages)}.");
        }

        foreach (TgiKey tgi in tgis)
        {
            var fileIds = new List<int>();
            if (_exactTgiToFileIds.TryGetValue(tgi, out List<int>? exactMatches))
            {
                fileIds.AddRange(exactMatches);
            }
            if (_wildcardTypeToFileIds.TryGetValue((tgi.GroupId, tgi.InstanceId), out List<int>? wildcardMatches))
            {
                fileIds.AddRange(wildcardMatches);
            }
            if (fileIds.Count == 0)
            {
                continue;
            }

            var packageIds = new HashSet<int>();
            foreach (int fileId in fileIds)
            {
                if (_packageIdsByFileId.TryGetValue(fileId, out List<int>? pkgIds))
                {
                    foreach (int pkgId in pkgIds)
                    {
                        packageIds.Add(pkgId);
                    }
                }
            }
            if (packageIds.Count == 0)
            {
                continue;
            }

            var infos = new List<CatalogPackageInfo>();
            foreach (int pkgId in packageIds)
            {
                if (_packagesById.TryGetValue(pkgId, out CatalogPackageInfo? info))
                {
                    infos.Add(info);
                }
            }

            if (infos.Count > 0)
            {
                result[tgi] = infos;
            }
        }

        return result;
    }

    /// <summary>
    /// Parses a catalog TGI string such as <c>"0x6534284a, 0x016a6904, 0x02c59b5f"</c> or, when
    /// the Type is implied by category, <c>"#, 0x96a006b0, 0xe8724b4f"</c>.
    /// </summary>
    private static bool TryParseCatalogTgi(ReadOnlySpan<char> text, out bool hasType, out uint type, out uint group, out uint instance)
    {
        hasType = false;
        type = group = instance = 0;

        Span<Range> ranges = stackalloc Range[4];

        int rangesCount = text.Split(ranges, ", ", StringSplitOptions.None);
        if (rangesCount != 3)
        {
            return false;
        }

        if (!TryParseHex(text[ranges[1]], out group) || !TryParseHex(text[ranges[2]], out instance))
        {
            return false;
        }

        ReadOnlySpan<char> typePart = text[ranges[0]].Trim();
        if (typePart.Length == 1 && typePart[0] == '#')
        {
            hasType = false;
            return true;
        }

        if (!TryParseHex(typePart, out type))
        {
            return false;
        }

        hasType = true;
        return true;
    }

    private static bool TryParseHex(ReadOnlySpan<char> text, out uint value)
    {
        ReadOnlySpan<char> s = text.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            s = s[2..];
        }
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static void AddToIndex<TKey>(Dictionary<TKey, List<int>> index, TKey key, int value) where TKey : notnull
    {
        if (!index.TryGetValue(key, out List<int>? list))
        {
            list = new List<int>();
            index[key] = list;
        }
        list.Add(value);
    }
}
