using System.IO;
using System.Text;

namespace B_VIP.Desktop.Services.Loaders
{
    /// <summary>One partition/firmware header inside a PAC packet.</summary>
    public sealed record PacEntry(string PartitionName, string FileName, long Size, long DataOffset)
    {
        public bool HasPayload => Size > 0;
    }

    /// <summary>The PAC header table, read without extracting anything.</summary>
    public sealed record PacInspection(
        bool Success,
        string? Error,
        string Version,
        string ProductName,
        string FirmwareName,
        IReadOnlyList<PacEntry> Entries);

    /// <summary>Outcome of pulling the FDL stages out of a PAC packet.</summary>
    public sealed record PacExtractionResult(
        bool Success,
        string? Error,
        string? Fdl1Path,
        string? Fdl2Path,
        string WorkDirectory,
        IReadOnlyList<PacEntry> Entries);

    /// <summary>
    /// Reads Spreadtrum/Unisoc <c>.pac</c> firmware directly.
    ///
    /// The packet is a fixed 2124-byte header (version string, product/firmware names, the number of
    /// partition headers and the offset of that table) followed by <c>partitionCount</c> 2580-byte
    /// partition headers. Each header names a payload and gives its size and absolute offset. Only
    /// the FDL1/FDL2 stages are copied out — they are what the boot-ROM upload needs — into
    /// <c>%TEMP%\BVIP_SPD</c>, and the PAC itself is never modified.
    /// </summary>
    public static class PacExtractor
    {
        /// <summary>Size of the packet header (<c>BP_R1</c> / <c>BP_R2</c>).</summary>
        public const int HeaderSize = 2124;

        /// <summary>Size of one partition header entry.</summary>
        public const int EntryHeaderSize = 2580;

        private const int MaxPartitions = 512;
        private const int CopyBufferSize = 81920;

        // Offsets inside the packet header.
        private const int VersionOffset = 0;
        private const int VersionLength = 44;
        private const int ProductNameOffset = 52;
        private const int ProductNameLength = 512;
        private const int FirmwareNameOffset = 564;
        private const int FirmwareNameLength = 512;
        private const int PartitionCountOffset = 1076;
        private const int PartitionsListOffset = 1080;
        private const int MagicOffset = 2116;

        // Offsets inside one partition header.
        private const int EntryLengthOffset = 0;
        private const int EntryPartitionNameOffset = 4;
        private const int EntryFileNameOffset = 516;
        private const int EntryNameLength = 512;
        private const int EntryHighSizeOffset = 1532;
        private const int EntryHighOffsetOffset = 1536;
        private const int EntryLowSizeOffset = 1540;
        private const int EntryLowOffsetOffset = 1552;

        /// <summary>Where the FDL stages are unpacked: <c>%TEMP%\BVIP_SPD</c>.</summary>
        public static string DefaultWorkDirectory => Path.Combine(Path.GetTempPath(), "BVIP_SPD");

        /// <summary>Reads the header table: version, product names and every partition entry.</summary>
        public static PacInspection Read(string pacPath)
        {
            if (string.IsNullOrWhiteSpace(pacPath) || !File.Exists(pacPath))
            {
                return new PacInspection(false, "The PAC firmware file was not found.", string.Empty,
                    string.Empty, string.Empty, Array.Empty<PacEntry>());
            }

            try
            {
                using FileStream stream = File.OpenRead(pacPath);
                return Read(stream);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new PacInspection(false, ex.Message, string.Empty, string.Empty, string.Empty,
                    Array.Empty<PacEntry>());
            }
        }

        /// <summary>
        /// Parses the PAC and writes FDL1/FDL2 into <paramref name="workDirectory"/>. Existing copies
        /// from an earlier run are replaced so a different firmware is never mixed with this one.
        /// </summary>
        public static PacExtractionResult ExtractLoaders(
            string pacPath, string? workDirectory = null, CancellationToken ct = default)
        {
            string work = string.IsNullOrWhiteSpace(workDirectory) ? DefaultWorkDirectory : workDirectory;

            if (string.IsNullOrWhiteSpace(pacPath) || !File.Exists(pacPath))
            {
                return new PacExtractionResult(false, "The PAC firmware file was not found.", null, null, work,
                    Array.Empty<PacEntry>());
            }

            try
            {
                using FileStream stream = File.OpenRead(pacPath);
                PacInspection inspection = Read(stream);

                if (!inspection.Success)
                {
                    return new PacExtractionResult(false, inspection.Error, null, null, work,
                        inspection.Entries);
                }

                PacEntry? fdl1 = FindFdl(inspection.Entries, firstStage: true);
                PacEntry? fdl2 = FindFdl(inspection.Entries, firstStage: false);

                if (fdl1 is null)
                {
                    return new PacExtractionResult(false,
                        "The PAC packet has no FDL1 entry (it is not a downloadable packet).", null, null, work,
                        inspection.Entries);
                }

                Directory.CreateDirectory(work);

                string path1 = WriteEntry(stream, fdl1, work, ct);
                string? path2 = fdl2 is null ? null : WriteEntry(stream, fdl2, work, ct);

                return new PacExtractionResult(true, null, path1, path2, work, inspection.Entries);
            }
            catch (OperationCanceledException)
            {
                return new PacExtractionResult(false, "Extraction cancelled.", null, null, work,
                    Array.Empty<PacEntry>());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                or ArgumentException)
            {
                return new PacExtractionResult(false, ex.Message, null, null, work, Array.Empty<PacEntry>());
            }
        }

        private static PacInspection Read(Stream stream)
        {
            long length = stream.Length;
            if (length < HeaderSize)
            {
                return Fail("The file is too small to be a PAC packet.");
            }

            byte[] header = new byte[HeaderSize];
            stream.Position = 0;
            stream.ReadExactly(header, 0, HeaderSize);

            string version = ReadUtf16(header, VersionOffset, VersionLength);
            if (!version.StartsWith("BP_", StringComparison.OrdinalIgnoreCase))
            {
                return Fail("The file is not a Spreadtrum PAC packet (unknown version header).");
            }

            int partitionCount = (int)BitConverter.ToUInt32(header, PartitionCountOffset);
            long listStart = BitConverter.ToUInt32(header, PartitionsListOffset);

            if (partitionCount <= 0 || partitionCount > MaxPartitions)
            {
                return Fail($"The PAC header lists an impossible number of partitions ({partitionCount}).");
            }

            if (listStart < HeaderSize || listStart >= length)
            {
                return Fail("The PAC header points its partition table outside the file.");
            }

            if (listStart + (long)partitionCount * EntryHeaderSize > length)
            {
                return Fail("The PAC partition table is truncated.");
            }

            string product = ReadUtf16(header, ProductNameOffset, ProductNameLength);
            string firmware = ReadUtf16(header, FirmwareNameOffset, FirmwareNameLength);

            List<PacEntry> entries = new(partitionCount);
            byte[] entryBuffer = new byte[EntryHeaderSize];
            stream.Position = listStart;

            for (int i = 0; i < partitionCount; i++)
            {
                if (stream.Position + EntryHeaderSize > length)
                {
                    return Fail("A PAC partition header runs past the end of the file.");
                }

                stream.ReadExactly(entryBuffer, 0, EntryHeaderSize);

                uint entryLength = BitConverter.ToUInt32(entryBuffer, EntryLengthOffset);
                if (entryLength != EntryHeaderSize)
                {
                    return new PacInspection(false,
                        $"Unknown PAC partition header format (expected {EntryHeaderSize} bytes, found {entryLength}).",
                        version, product, firmware, entries);
                }

                string partitionName = ReadUtf16(entryBuffer, EntryPartitionNameOffset, EntryNameLength);
                string fileName = ReadUtf16(entryBuffer, EntryFileNameOffset, EntryNameLength);

                long size = (long)BitConverter.ToUInt32(entryBuffer, EntryHighSizeOffset) << 32 |
                            BitConverter.ToUInt32(entryBuffer, EntryLowSizeOffset);
                long dataOffset = (long)BitConverter.ToUInt32(entryBuffer, EntryHighOffsetOffset) << 32 |
                                  BitConverter.ToUInt32(entryBuffer, EntryLowOffsetOffset);

                if (size < 0 || dataOffset < 0)
                {
                    return Fail("A PAC partition header carries a negative size or offset.");
                }

                if (size > 0 && (dataOffset < HeaderSize || dataOffset + size > length))
                {
                    return Fail($"The PAC entry '{Describe(partitionName, fileName)}' points outside the file.");
                }

                entries.Add(new PacEntry(partitionName, fileName, size, dataOffset));
            }

            if (BitConverter.ToUInt32(header, MagicOffset) != 0xFFFAFFFA)
            {
                // Not fatal: only newer packets carry the magic, and the table above already parsed.
                _ = entries;
            }

            return new PacInspection(true, null, version, product, firmware, entries);
        }

        private static PacEntry? FindFdl(IReadOnlyList<PacEntry> entries, bool firstStage)
        {
            string stage = firstStage ? "fdl1" : "fdl2";

            PacEntry? byName = entries.FirstOrDefault(entry =>
                entry.HasPayload &&
                (entry.FileName.Contains(stage, StringComparison.OrdinalIgnoreCase) ||
                 entry.PartitionName.Contains(stage, StringComparison.OrdinalIgnoreCase)));

            if (byName is not null)
            {
                return byName;
            }

            // Older packets label the first stage simply "FDL".
            return firstStage
                ? entries.FirstOrDefault(entry =>
                    entry.HasPayload &&
                    entry.PartitionName.Equals("FDL", StringComparison.OrdinalIgnoreCase))
                : null;
        }

        private static string WriteEntry(Stream stream, PacEntry entry, string workDirectory, CancellationToken ct)
        {
            // The name comes from the packet, so only the file name part is trusted.
            string name = Path.GetFileName(entry.FileName);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"{entry.PartitionName}.bin";
            }

            string target = Path.Combine(workDirectory, name);

            stream.Position = entry.DataOffset;
            using FileStream output = new(target, FileMode.Create, FileAccess.Write, FileShare.None,
                CopyBufferSize, useAsync: false);

            byte[] buffer = new byte[CopyBufferSize];
            long remaining = entry.Size;
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();

                int chunk = (int)Math.Min(buffer.Length, remaining);
                int read = stream.Read(buffer, 0, chunk);
                if (read <= 0)
                {
                    throw new InvalidDataException($"The PAC payload '{name}' ended early.");
                }

                output.Write(buffer, 0, read);
                remaining -= read;
            }

            return target;
        }

        private static string ReadUtf16(byte[] buffer, int offset, int length)
        {
            int end = offset + length;
            int stop = offset;
            while (stop + 1 < end && (buffer[stop] != 0 || buffer[stop + 1] != 0))
            {
                stop += 2;
            }

            return Encoding.Unicode.GetString(buffer, offset, stop - offset).Trim();
        }

        private static string Describe(string partitionName, string fileName)
            => string.IsNullOrWhiteSpace(partitionName) ? fileName : $"{partitionName} ({fileName})";

        private static PacInspection Fail(string error)
            => new(false, error, string.Empty, string.Empty, string.Empty, Array.Empty<PacEntry>());
    }
}
