using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SC4ModMigrationAssistant.Models;

namespace SC4ModMigrationAssistant.Services;

/// <summary>
/// Locates and reads sc4pac's own profile files, so the "Check sc4pac Catalog" feature can
/// tell which packages the user already has installed and exclude them from its suggestions.
/// </summary>
/// <remarks>
/// <para>Per sc4pac's own documentation (https://memo33.github.io/sc4pac/#/, "Details" section),
/// the CLI keeps state in two files, both living in the same per-profile folder under
/// <c>%AppData%\io.github.memo33\sc4pac\config\profiles\&lt;profile&gt;\</c>:</para>
/// <list type="bullet">
/// <item><c>sc4pac-plugins.json</c> - its <c>"explicit"</c> array lists only the packages the
/// user explicitly added themselves, <i>excluding</i> dependencies.</item>
/// <item><c>sc4pac-plugins-lock.json</c> - its <c>"installed"</c> array lists every package
/// actually installed, <i>including</i> every dependency pulled in automatically (e.g.
/// <c>nam:bridges</c>, installed as a dependency of NAM rather than added directly).</item>
/// </list>
/// <para>Since a package only needs to be excluded here if it's genuinely already present in
/// the Plugins folder, <c>sc4pac-plugins-lock.json</c>'s "installed" list is the authoritative,
/// complete source, and is read whenever available. The narrower "explicit" list from
/// sc4pac-plugins.json is still included too (a strict subset in practice), so filtering still
/// works even if only that file can be found.</para>
/// </remarks>
public sealed class Sc4pacPluginsFileService
{
    private const string PluginsFileName = "sc4pac-plugins.json";
    private const string LockFileName = "sc4pac-plugins-lock.json";

    /// <summary>Root folder sc4pac stores its per-profile config under, on Windows.</summary>
    public static string DefaultProfilesRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "io.github.memo33", "sc4pac", "config", "profiles");

    /// <summary>
    /// Searches <see cref="DefaultProfilesRoot"/> for every <c>sc4pac-plugins.json</c> file
    /// (one per sc4pac profile) and returns the most recently modified one, or null if none
    /// were found (e.g. sc4pac isn't installed, or uses a non-default config location).
    /// </summary>
    public string? TryFindDefaultFile()
    {
        if (!Directory.Exists(DefaultProfilesRoot))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(DefaultProfilesRoot, PluginsFileName, SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Loads every package ID that counts as "already installed" for filtering purposes:
    /// <c>sc4pac-plugins.json</c>'s "explicit" list plus, more importantly,
    /// <c>sc4pac-plugins-lock.json</c>'s "installed" list (which also covers dependencies).
    /// </summary>
    /// <param name="selectedFilePath">
    /// Path to either sc4pac-plugins.json or sc4pac-plugins-lock.json (both are looked for in
    /// the same folder regardless of which one was actually selected/detected).
    /// </param>
    /// <param name="log">Optional callback used to report whether the lock file was found.</param>
    public HashSet<string> LoadInstalledPackageIds(string selectedFilePath, Action<LogMessage>? log = null)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string? directory = Path.GetDirectoryName(selectedFilePath);
        string fileName = Path.GetFileName(selectedFilePath);

        string pluginsJsonPath = string.Equals(fileName, PluginsFileName, StringComparison.OrdinalIgnoreCase)
            ? selectedFilePath
            : Path.Combine(directory ?? string.Empty, PluginsFileName);

        string lockJsonPath = string.Equals(fileName, LockFileName, StringComparison.OrdinalIgnoreCase)
            ? selectedFilePath
            : Path.Combine(directory ?? string.Empty, LockFileName);

        if (File.Exists(pluginsJsonPath))
        {
            AddPackageIdsFromArray(pluginsJsonPath, "explicit", result, isLockFileFormat: false);
        }

        if (File.Exists(lockJsonPath))
        {
            AddPackageIdsFromArray(lockJsonPath, "installed", result, isLockFileFormat: true);
        }
        else
        {
            log?.Invoke(new LogMessage(
                $"[sc4pac] {LockFileName} not found next to {selectedFilePath} - only explicitly-added " +
                "packages will be excluded, so packages installed only as a dependency (e.g. nam:bridges, " +
                "pulled in by NAM) may still be suggested. Make sure both sc4pac files are in the same folder.",
                LogColor.Orange));
        }

        return result;
    }

    /// <summary>
    /// Reads package IDs out of one of sc4pac's JSON arrays into <paramref name="destination"/>.
    /// sc4pac-plugins.json's "explicit" array is a plain array of "group:name" strings;
    /// sc4pac-plugins-lock.json's "installed" array is an array of objects with separate
    /// "group" and "name" fields that need combining into the same "group:name" form.
    /// </summary>
    private static void AddPackageIdsFromArray(string filePath, string propertyName, HashSet<string> destination, bool isLockFileFormat)
    {
        using FileStream stream = File.OpenRead(filePath);
        using JsonDocument document = JsonDocument.Parse(stream);

        if (!document.RootElement.TryGetProperty(propertyName, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement item in array.EnumerateArray())
        {
            if (!isLockFileFormat)
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    string? id = item.GetString();
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        destination.Add(id);
                    }
                }
                continue;
            }

            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("group", out JsonElement groupElement)
                && item.TryGetProperty("name", out JsonElement nameElement)
                && groupElement.ValueKind == JsonValueKind.String
                && nameElement.ValueKind == JsonValueKind.String)
            {
                string? group = groupElement.GetString();
                string? name = nameElement.GetString();
                if (!string.IsNullOrWhiteSpace(group) && !string.IsNullOrWhiteSpace(name))
                {
                    destination.Add($"{group}:{name}");
                }
            }
        }
    }
}
