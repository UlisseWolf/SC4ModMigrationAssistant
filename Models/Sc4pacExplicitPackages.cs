using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SC4ModMigrationAssistant.Models;

/// <summary>
/// Mirrors the JSON format sc4pac itself uses for an explicit-packages list:
/// <c>{ "explicit": ["group:package-id", ...] }</c>. Used to export the packages found by
/// "Check sc4pac Catalog" into a file sc4pac can consume directly, to ease migration.
/// </summary>
public sealed class Sc4pacExplicitPackages
{
    [JsonPropertyName("explicit")]
    public required List<string> Explicit { get; init; }
}
