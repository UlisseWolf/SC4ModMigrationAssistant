namespace SC4ModMigrationAssistant.Models;

/// <summary>
/// Indicates which area of the Plugins folder a scanned file came from.
/// </summary>
public enum SourceCategory
{
    /// <summary>
    /// File found inside Plugins (excluding 075-my-plugins and 895-my-overrides). Logged in black.
    /// </summary>
    PluginsMain,

    /// <summary>
    /// File found inside the 075-my-plugins folder. Logged in blue.
    /// </summary>
    Overrides075,

    /// <summary>
    /// File found inside the 895-my-overrides folder. Logged in blue.
    /// </summary>
    Overrides895
}
