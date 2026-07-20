using System.Collections.Generic;

namespace SC4ModMigrationAssistant.Models;

/// <summary>
/// A file scanned from 075-my-plugins or 895-my-overrides for the purpose of looking it up
/// in the SC4 Prop Texture Catalog. Unlike <see cref="ScannedFile"/> (used for the Plugins-only
/// duplicate check), this keeps the file's TGIs around, since they are needed for the catalog
/// lookup that happens afterwards. This is fine memory-wise because only the two override
/// folders are scanned here - never the (much larger) main Plugins folder.
/// </summary>
public sealed class CatalogScanFile
{
    public required string FullPath { get; init; }
    public required string RelativePath { get; init; }
    public required SourceCategory Category { get; init; }
    public required HashSet<TgiKey> Tgis { get; init; }
}
