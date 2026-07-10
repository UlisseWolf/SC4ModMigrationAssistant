using System.Windows.Media;

namespace SC4ModMigrationAssistant;

/// <summary>
/// Lightweight, immutable item shown in the virtualized log list. Kept intentionally simple
/// (no INotifyPropertyChanged) since entries are never mutated after being added.
/// </summary>
public sealed class LogEntryView
{
    public required string Text { get; init; }
    public required Brush Brush { get; init; }
}
