namespace SC4ModMigrationAssistant.Models;

/// <summary>
/// Semantic color of a log message. The UI (MainWindow) maps this to the actual
/// Brush to use, so the scanning services stay decoupled from WPF.
/// </summary>
public enum LogColor
{
    /// <summary>File inside Plugins (excluding 075-my-plugins and 895-my-overrides).</summary>
    Black,

    /// <summary>File inside 075-my-plugins or 895-my-overrides.</summary>
    Blue,

    /// <summary>Duplicate found (or moved).</summary>
    Red,

    /// <summary>Generic informational/status messages.</summary>
    Gray,

    /// <summary>Warnings or errors, e.g. files that could not be read as DBPF.</summary>
    Orange
}
