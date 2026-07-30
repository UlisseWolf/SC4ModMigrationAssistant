using SC4ModMigrationAssistant.Models;
using System.Buffers.Binary;
using System.IO;

namespace SC4ModMigrationAssistant.Services;

internal static class DbpfParsingService
{
    /// <summary>
    /// The major version of the Sim City 4 DBPF format.
    /// </summary>
    private const uint SC4DbpfMajorVersion = 1;
    /// <summary>
    /// The minor version of the Sim City 4 DBPF format.
    /// </summary>
    private const uint SC4DbpfMinorVersion = 0;
    /// <summary>
    /// The index major version of the Sim City 4 DBPF format.
    /// </summary>
    private const uint SC4DbpfIndexMajorVersion = 7;
    /// <summary>
    /// The size in bytes of a single index entry in the Sim City 4 DBPF format.
    /// </summary>
    private const uint SC4DbpfIndexEntrySize = 20;

    public static IEnumerable<TgiKey> EnumerateTgis(string file)
    {
        using (FileStream stream = new(file, FileMode.Open, FileAccess.Read))
        {
            (uint entryCount, uint indexLocation) = ParseDbpfHeader(stream);

            if (entryCount > 0)
            {
                stream.Position = indexLocation;

                for (uint i = 0; i < entryCount; i++)
                {
                    // Each DBPF version 1.0 index entry consists of 5 UInt32 fields
                    // in the order: type id, group id, instance id, offset, size.

                    uint typeId = ReadUInt32LittleEndian(stream);
                    uint groupId = ReadUInt32LittleEndian(stream);
                    uint instanceId = ReadUInt32LittleEndian(stream);
                    stream.Position += 8; // skip the offset and size fields

                    yield return new TgiKey(typeId, groupId, instanceId);
                } 
            }
        }
    }

    private static (uint entryCount, uint indexLocation) ParseDbpfHeader(Stream stream)
    {
        // Parse the DBPF header while skipping the header fields we don't need.
        // See the following page for the full DBPF header layout:
        // https://wiki.sc4devotion.com/index.php?title=DBPF#Header

        Span<byte> signature = stackalloc byte[4];

        stream.ReadExactly(signature);

        if (!signature.SequenceEqual("DBPF"u8))
        {
            throw new FormatException("Invalid DBPF file signature.");
        }

        uint majorVersion = ReadUInt32LittleEndian(stream);
        uint minorVersion = ReadUInt32LittleEndian(stream);

        if (majorVersion != SC4DbpfMajorVersion || minorVersion != SC4DbpfMinorVersion)
        {
            throw new FormatException($"DBPF format version {majorVersion}.{minorVersion} is not supported.");
        }

        stream.Position += 20; // skip to the index major version field

        uint indexMajorVersion = ReadUInt32LittleEndian(stream);

        if (indexMajorVersion != SC4DbpfIndexMajorVersion)
        {
            throw new FormatException($"DBPF index version {indexMajorVersion} is not supported.");
        }

        uint entryCount = ReadUInt32LittleEndian(stream);
        uint indexLocation = ReadUInt32LittleEndian(stream);
        uint indexSize = ReadUInt32LittleEndian(stream);

        long expectedIndexSize = (long)entryCount * SC4DbpfIndexEntrySize;

        if (indexSize != expectedIndexSize)
        {
            throw new FormatException("The size of the DBPF index table is invalid.");
        }

        return (entryCount, indexLocation);
    }

    private static uint ReadUInt32LittleEndian(Stream stream)
    {
        Span<byte> bytes = stackalloc byte[4];

        stream.ReadExactly(bytes);

        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }
}
