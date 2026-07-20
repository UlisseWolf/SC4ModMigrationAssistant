using System.Collections.Generic;

namespace SC4ModMigrationAssistant.Models;

/// <summary>
/// A distinct sc4pac package identified via the SC4 Prop Texture Catalog while scanning
/// 075-my-plugins / 895-my-overrides. Aggregates every local file that matched it, since the
/// same package can obviously provide more than one TGI/file.
/// </summary>
public sealed class Sc4pacMatch
{
    /// <summary>Package identifier in sc4pac's own "group:package-name" format, e.g. "cyclone-boom:save-warning".</summary>
    public required string PackageId { get; init; }

    /// <summary>Human-readable pack/mod name from the catalog, if available; falls back to <see cref="PackageId"/>.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Mod author, from the catalog, if known.</summary>
    public string? Author { get; init; }

    /// <summary>
    /// Real download/info page URL for this package (from the catalog's own <c>Websites</c>
    /// field), if known. Opening this lets the user confirm the pack before installing it, and
    /// is a genuine, verifiable link - unlike a fabricated "open sc4pac directly" URL scheme,
    /// which sc4pac does not publicly document. See <see cref="Sc4pacAddCommand"/> for the
    /// actually-supported way to install the package.
    /// </summary>
    public string? PageUrl { get; init; }

    /// <summary>True if <see cref="PageUrl"/> is available - used to show/hide the "Open Page" button.</summary>
    public bool HasPageUrl => !string.IsNullOrWhiteSpace(PageUrl);

    /// <summary>Relative paths of every local file (within 075/895) that matched this package.</summary>
    public List<string> MatchingFiles { get; } = new();

    /// <summary>Total number of TGIs across all matching files that resolved to this package.</summary>
    public int MatchedTgiCount { get; set; }

    /// <summary>Ready-to-copy CLI command that installs this package via sc4pac.</summary>
    public string Sc4pacAddCommand => $"sc4pac add {PackageId}";
}
