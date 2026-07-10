namespace SC4ModMigrationAssistant.Models;

/// <summary>
/// Represents a file scanned inside 075-my-plugins or 895-my-overrides.
/// Deliberately lightweight - see remarks on <see cref="Services.DbpfScanService"/> for why.
/// </summary>
public sealed class ScannedFile
{
    public required string FullPath { get; init; }

    public required string RelativePath { get; init; }

    public required SourceCategory Category { get; init; }

    /// <summary>Number of this file's TGIs that are also present somewhere in the main Plugins folder.</summary>
    public int MainMatchCount { get; set; }

    /// <summary>
    /// Only relevant for files in <see cref="SourceCategory.Overrides895"/>: true if a file with
    /// the same name (case-insensitive) was found in the main Plugins folder.
    /// </summary>
    public bool NameMatchFound { get; set; }

    /// <summary>
    /// Duplicate checking is performed only against the main Plugins folder - never between
    /// 075-my-plugins and 895-my-overrides, nor between files within the same override folder.
    /// Both of those folders are meant to hold files a user deliberately keeps/overrides, so two
    /// files living there sharing a TGI (or even a name) with each other is not, by itself,
    /// evidence of a mistake; a match against the main Plugins folder is.
    ///
    /// For 075-my-plugins a TGI match against Plugins is enough. For 895-my-overrides, a TGI
    /// match against Plugins is expected by design (that folder exists to override existing
    /// files while keeping their TGI) - so a file name match against Plugins is additionally
    /// required there to flag a genuine accidental duplicate.
    /// </summary>
    public bool IsDuplicate => Category == SourceCategory.Overrides895
        ? MainMatchCount > 0 && NameMatchFound
        : MainMatchCount > 0;
}
