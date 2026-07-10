using System;
using System.Collections.Generic;
using System.IO;
using SC4ModMigrationAssistant.Models;

namespace SC4ModMigrationAssistant.Services;

/// <summary>
/// Physically moves the files marked as duplicates to a destination folder, keeping the
/// relative path (075-my-plugins / 895-my-overrides subfolder) to avoid name collisions and
/// to make it easy to trace a file back to its origin.
/// </summary>
public sealed class DuplicateMoverService
{
    public void MoveDuplicates(
        IReadOnlyList<ScannedFile> duplicates,
        string destinationRoot,
        Action<LogMessage> log)
    {
        Directory.CreateDirectory(destinationRoot);

        foreach (ScannedFile duplicate in duplicates)
        {
            try
            {
                string destPath = Path.Combine(destinationRoot, duplicate.RelativePath);
                string? destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                // Avoid overwriting if a file with the same name already exists at destination
                destPath = GetUniqueDestinationPath(destPath);

                File.Move(duplicate.FullPath, destPath);

                log.Invoke(new LogMessage(
                    $"[MOVED] {duplicate.RelativePath} -> {destPath}",
                    LogColor.Red));
            }
            catch (Exception ex)
            {
                log.Invoke(new LogMessage(
                    $"[MOVE ERROR] {duplicate.RelativePath}: {ex.Message}",
                    LogColor.Orange));
            }
        }
    }

    private static string GetUniqueDestinationPath(string desiredPath)
    {
        if (!File.Exists(desiredPath))
        {
            return desiredPath;
        }

        string dir = Path.GetDirectoryName(desiredPath) ?? string.Empty;
        string nameNoExt = Path.GetFileNameWithoutExtension(desiredPath);
        string ext = Path.GetExtension(desiredPath);

        int counter = 1;
        string candidate;
        do
        {
            candidate = Path.Combine(dir, $"{nameNoExt}_dup{counter}{ext}");
            counter++;
        } while (File.Exists(candidate));

        return candidate;
    }
}
