using System.Collections;
using System.IO;
using System.IO.Enumeration;

namespace SC4ModMigrationAssistant.Services;

internal sealed class DbpfFileSystemService : IEnumerable<string>
{
    private readonly string directory;
    private readonly bool excludeOverrideFolders;
    private readonly bool ignoreErrors;
    private readonly CancellationToken cancellationToken;
    private DbpfFileSystemEnumerator? enumerator;

    public DbpfFileSystemService(string directory,
                                 bool excludeOverrideFolders,
                                 bool ignoreErrors,
                                 CancellationToken cancellationToken)
    {
        this.directory = directory ?? throw new ArgumentNullException(nameof(directory));
        this.excludeOverrideFolders = excludeOverrideFolders;
        this.ignoreErrors = ignoreErrors;
        this.cancellationToken = cancellationToken;
        enumerator = new DbpfFileSystemEnumerator(directory, excludeOverrideFolders, ignoreErrors, cancellationToken);
    }

    public IEnumerator<string> GetEnumerator()
    {
        return Interlocked.Exchange(ref enumerator, null) ?? new DbpfFileSystemEnumerator(directory,
                                                                                          excludeOverrideFolders,
                                                                                          ignoreErrors,
                                                                                          cancellationToken);
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    private sealed class DbpfFileSystemEnumerator : FileSystemEnumerator<string>
    {
        private readonly bool excludeOverrideFolders;
        private readonly bool ignoreErrors;
        private readonly CancellationToken cancellationToken;

        public DbpfFileSystemEnumerator(string directory,
                                        bool excludeOverrideFolders,
                                        bool ignoreErrors,
                                        CancellationToken cancellationToken)
            : base(directory, new EnumerationOptions() { RecurseSubdirectories = true })
        {
            this.excludeOverrideFolders = excludeOverrideFolders;
            this.ignoreErrors = ignoreErrors;
            this.cancellationToken = cancellationToken;
        }

        protected override bool ContinueOnError(int error)
        {
            return ignoreErrors;
        }

        protected override void OnDirectoryFinished(ReadOnlySpan<char> directory)
        {
            base.OnDirectoryFinished(directory);

            cancellationToken.ThrowIfCancellationRequested();
        }

        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
        {
            if (entry.IsDirectory)
            {
                return false;
            }

            ReadOnlySpan<char> fileExtension = Path.GetExtension(entry.FileName);

            // Files without an extension are treated as potential .sc4* files, there are released
            // plugins that don't have a file extension. For example, Bosham Church by mintoes.
            //
            // The StartsWith comparison with a .sc4 file extension is an optimization to handle
            // the .sc4desc, .sc4lot, and .sc4model file extensions with a single comparison.

            return fileExtension.IsEmpty
                || fileExtension.Equals(".dat", StringComparison.OrdinalIgnoreCase)
                || fileExtension.StartsWith(".sc4", StringComparison.OrdinalIgnoreCase);
        }

        protected override bool ShouldRecurseIntoEntry(ref FileSystemEntry entry)
        {
            if (excludeOverrideFolders)
            {
                // The override folders only exist in the top-level plugin directory.

                if (entry.Directory.Equals(entry.OriginalRootDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    ReadOnlySpan<char> directoryName = entry.FileName;

                    // The sc4pac override subdirectories are handled separately from the other plugin folders.
                    // See DbpfScanService.cs

                    return !directoryName.Equals(DbpfScanService.Overrides075FolderName, StringComparison.OrdinalIgnoreCase)
                        && !directoryName.Equals(DbpfScanService.Overrides895FolderName, StringComparison.OrdinalIgnoreCase);
                }
            }

            return true;
        }

        protected override string TransformEntry(ref FileSystemEntry entry)
        {
            return entry.ToSpecifiedFullPath();
        }
    }
}
