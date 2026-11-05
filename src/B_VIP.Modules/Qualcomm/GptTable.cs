using System.Buffers.Binary;
using System.Text;

namespace B_VIP.Modules.Qualcomm
{
    /// <summary>One GPT entry: the partition's identity, boundaries and name.</summary>
    public sealed record GptPartition(
        int Index,
        Guid TypeGuid,
        Guid UniqueGuid,
        long FirstLba,
        long LastLba,
        long Attributes,
        string Name)
    {
        /// <summary>Number of sectors the partition spans, inclusive of both ends.</summary>
        public long SectorCount => Math.Max(0, LastLba - FirstLba + 1);

        public long StartByte(int sectorSize) => FirstLba * sectorSize;

        public long SizeBytes(int sectorSize) => SectorCount * sectorSize;

        public string SizeText(int sectorSize) => FormatSize(SizeBytes(sectorSize));

        public string Summary => $"{Name} (LBA {FirstLba}..{LastLba}, {SectorCount} sector(s))";

        /// <summary>One aligned line for the operator's log, sized with the table's sector size.</summary>
        public string Describe(int sectorSize)
            => $"{Name,-24} LBA {FirstLba,12}..{LastLba,-12} {SizeText(sectorSize),10}";

        internal static string FormatSize(long bytes)
        {
            string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return $"{value:0.#} {units[unit]}";
        }
    }

    /// <summary>
    /// Parser for a GUID partition table read off the device. The header's CRC32 is verified, so a
    /// block of zeroes or a half-read sector is rejected instead of producing a plausible-looking
    /// partition list that would then be erased by mistake.
    /// </summary>
    public sealed class GptTable
    {
        /// <summary>Sector sizes an Android UFS/eMMC GPT can use.</summary>
        public static readonly int[] SupportedSectorSizes = [4096, 512];

        /// <summary>
        /// Partitions that carry radio calibration, persistent storage or trust anchors. They are never
        /// erased by this module, whichever operation asks for them.
        /// </summary>
        public static readonly string[] ProtectedPartitionNames =
        [
            "modemst1",
            "modemst2",
            "fsg",
            "fsc",
            "persist",
            "ssd",
            "ddr",
            "modem",
            "nvram",
            "nvdata",
            "nvcfg",
            "protect1",
            "protect2",
            "devinfo",
            "xbl",
            "abl",
            "tz",
            "hyp"
        ];

        private const string Signature = "EFI PART";

        private GptTable(Guid diskGuid, long firstUsableLba, long lastUsableLba, int sectorSize,
            IReadOnlyList<GptPartition> partitions)
        {
            DiskGuid = diskGuid;
            FirstUsableLba = firstUsableLba;
            LastUsableLba = lastUsableLba;
            SectorSize = sectorSize;
            Partitions = partitions;
        }

        public Guid DiskGuid { get; }

        public long FirstUsableLba { get; }

        public long LastUsableLba { get; }

        /// <summary>The sector size this table was read with, in bytes.</summary>
        public int SectorSize { get; }

        public IReadOnlyList<GptPartition> Partitions { get; }

        /// <summary>Finds a partition by name (case-insensitive), matching the usual <c>frp</c>/<c>config</c> aliases.</summary>
        public GptPartition? Find(params string[] names)
        {
            foreach (string name in names)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                GptPartition? match = Partitions.FirstOrDefault(p =>
                    string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    return match;
                }
            }

            return null;
        }

        /// <summary>True when erasing this partition name is refused outright.</summary>
        public static bool IsProtected(string? partitionName)
            => !string.IsNullOrWhiteSpace(partitionName) &&
               ProtectedPartitionNames.Contains(partitionName.Trim(), StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Parses a block that starts at LBA 0 and contains at least the header plus the entry array.
        /// </summary>
        /// <exception cref="FormatException">The signature, the header CRC or the bounds do not hold.</exception>
        public static GptTable Parse(ReadOnlySpan<byte> data, int sectorSize = 4096)
        {
            if (sectorSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sectorSize), "The sector size must be positive.");
            }

            int headerOffset = LocateHeader(data, sectorSize);
            if (headerOffset < 0)
            {
                throw new FormatException(
                    $"No GPT header ('{Signature}') was found at LBA 1 with a {sectorSize} byte sector size.");
            }

            ReadOnlySpan<byte> header = data[headerOffset..];
            if (header.Length < 92)
            {
                throw new FormatException("The GPT header is truncated.");
            }

            uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header[12..16]);
            if (headerSize < 92 || headerSize > (uint)sectorSize)
            {
                throw new FormatException($"The GPT header declares an impossible size of {headerSize} byte(s).");
            }

            uint declaredCrc = BinaryPrimitives.ReadUInt32LittleEndian(header[16..20]);

            // The header CRC is computed with the CRC field itself zeroed.
            byte[] headerCopy = header[..(int)headerSize].ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(headerCopy.AsSpan(16, 4), 0);
            uint actualCrc = GptCrc32.Compute(headerCopy);
            if (declaredCrc != actualCrc)
            {
                throw new FormatException(
                    $"The GPT header checksum does not match (header declares 0x{declaredCrc:X8}, computed 0x{actualCrc:X8}).");
            }

            long firstUsable = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[40..48]);
            long lastUsable = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[48..56]);
            Guid diskGuid = new(header.Slice(56, 16));

            long entryLba = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[72..80]);
            uint entryCount = BinaryPrimitives.ReadUInt32LittleEndian(header[80..84]);
            uint entrySize = BinaryPrimitives.ReadUInt32LittleEndian(header[84..88]);

            if (entryCount == 0 || entryCount > 1024)
            {
                throw new FormatException($"The GPT declares {entryCount} partition entries.");
            }

            if (entrySize < 128 || entrySize > 4096)
            {
                throw new FormatException($"The GPT declares an entry size of {entrySize} byte(s).");
            }

            long arrayOffset = entryLba * sectorSize;
            long arrayLength = (long)entryCount * entrySize;
            if (arrayOffset < 0 || arrayOffset + arrayLength > data.Length)
            {
                throw new FormatException(
                    $"The partition entry array ({entryCount} × {entrySize} bytes at LBA {entryLba}) is outside the {data.Length} byte block that was read.");
            }

            ReadOnlySpan<byte> entries = data.Slice((int)arrayOffset, (int)arrayLength);

            List<GptPartition> partitions = new();
            for (int index = 0; index < entryCount; index++)
            {
                ReadOnlySpan<byte> entry = entries.Slice(index * (int)entrySize, (int)entrySize);
                Guid typeGuid = new(entry[..16]);
                if (typeGuid == Guid.Empty)
                {
                    continue; // An unused slot.
                }

                Guid uniqueGuid = new(entry.Slice(16, 16));
                long firstLba = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(32, 8));
                long lastLba = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(40, 8));
                long attributes = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(48, 8));
                string name = ReadName(entry.Slice(56, 72));

                partitions.Add(new GptPartition(index, typeGuid, uniqueGuid, firstLba, lastLba, attributes, name));
            }

            if (partitions.Count == 0)
            {
                throw new FormatException("The GPT contains no primary partition entries.");
            }

            return new GptTable(diskGuid, firstUsable, lastUsable, sectorSize, partitions);
        }

        /// <summary>
        /// Tries both supported sector sizes: the header must sit at LBA 1, so its offset is the sector
        /// size itself. That makes the sector size verifiable rather than a guess.
        /// </summary>
        public static int DetectSectorSize(ReadOnlySpan<byte> data)
        {
            foreach (int candidate in SupportedSectorSizes)
            {
                if (LocateHeader(data, candidate) >= 0)
                {
                    return candidate;
                }
            }

            throw new FormatException(
                "No GPT header was found with a 4096 or 512 byte sector size; the block does not contain partition metadata.");
        }

        private static int LocateHeader(ReadOnlySpan<byte> data, int sectorSize)
        {
            int offset = sectorSize;
            if (data.Length < offset + 92)
            {
                return -1;
            }

            return data.Slice(offset, 8).SequenceEqual(Encoding.ASCII.GetBytes(Signature)) ? offset : -1;
        }

        private static string ReadName(ReadOnlySpan<byte> utf16)
        {
            string name = Encoding.Unicode.GetString(utf16);
            int terminator = name.IndexOf('\0');
            if (terminator >= 0)
            {
                name = name[..terminator];
            }

            return name.Trim();
        }
    }

    /// <summary>CRC-32 (IEEE 802.3), the checksum GPT headers and entry arrays are validated with.</summary>
    public static class GptCrc32
    {
        private static readonly uint[] Table = BuildTable();

        public static uint Compute(ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (byte value in data)
            {
                crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
            }

            return crc ^ 0xFFFFFFFFu;
        }

        private static uint[] BuildTable()
        {
            uint[] table = new uint[256];
            for (uint index = 0; index < 256; index++)
            {
                uint value = index;
                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                }

                table[index] = value;
            }

            return table;
        }
    }
}
