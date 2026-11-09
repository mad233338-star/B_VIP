using System;
using System.IO;
using System.Linq;
using System.Text;
using B_VIP.Desktop.Services.Loaders;
using Xunit;

namespace B_VIP.LoaderTests
{
    /// <summary>
    /// The PAC reader is exercised against packets built with the documented layout (2124-byte
    /// packet header, 2580-byte partition headers, UTF-16 names, absolute data offsets), so a
    /// regression in the offsets or the name decoding fails here instead of on a shop bench.
    /// </summary>
    public sealed class PacExtractorTests : IDisposable
    {
        private readonly string _root;

        public PacExtractorTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "bvip-pac-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: the assertion already ran.
            }
        }

        [Fact]
        public void ExtractLoaders_writes_fdl1_and_fdl2_from_the_packet()
        {
            string pac = PacBuilder.Write(
                Path.Combine(_root, "firmware.pac"),
                ("FDL1", "fdl1-sign.bin", new byte[] { 1, 2, 3, 4, 5 }),
                ("FDL2", "fdl2-sign.bin", new byte[] { 9, 8, 7, 6 }),
                ("system", "system.img", new byte[] { 0xAA, 0xBB }));

            string work = Path.Combine(_root, "work");
            PacExtractionResult result = PacExtractor.ExtractLoaders(pac, work);

            Assert.True(result.Success, result.Error);
            Assert.Equal(3, result.Entries.Count);
            Assert.NotNull(result.Fdl1Path);
            Assert.NotNull(result.Fdl2Path);
            Assert.Equal("fdl1-sign.bin", Path.GetFileName(result.Fdl1Path));
            Assert.Equal("fdl2-sign.bin", Path.GetFileName(result.Fdl2Path));
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, File.ReadAllBytes(result.Fdl1Path!));
            Assert.Equal(new byte[] { 9, 8, 7, 6 }, File.ReadAllBytes(result.Fdl2Path!));

            // Only the two loader stages are unpacked; partition images stay inside the packet.
            Assert.False(File.Exists(Path.Combine(work, "system.img")));
        }

        [Fact]
        public void Read_reports_the_packet_header()
        {
            string pac = PacBuilder.Write(
                Path.Combine(_root, "firmware.pac"),
                ("FDL1", "fdl1.bin", new byte[] { 1 }),
                ("FDL2", "fdl2.bin", new byte[] { 2 }));

            PacInspection inspection = PacExtractor.Read(pac);

            Assert.True(inspection.Success, inspection.Error);
            Assert.Equal("BP_R1.0.0", inspection.Version);
            Assert.Equal("SC9863A", inspection.ProductName);
            Assert.Equal("B_VIP Test Firmware", inspection.FirmwareName);
            Assert.Equal(2, inspection.Entries.Count);
            Assert.Equal("FDL1", inspection.Entries[0].PartitionName);
            Assert.Equal(1, inspection.Entries[0].Size);
        }

        [Fact]
        public void A_file_that_is_not_a_packet_is_refused()
        {
            string path = Path.Combine(_root, "not-a-pac.bin");
            File.WriteAllText(path, "this is not a spreadtrum firmware packet");

            PacExtractionResult result = PacExtractor.ExtractLoaders(path, Path.Combine(_root, "work"));

            Assert.False(result.Success);
            Assert.Contains("PAC", result.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_packet_without_an_fdl1_entry_is_refused()
        {
            string pac = PacBuilder.Write(
                Path.Combine(_root, "firmware.pac"),
                ("system", "system.img", new byte[] { 0x11, 0x22 }));

            PacExtractionResult result = PacExtractor.ExtractLoaders(pac, Path.Combine(_root, "work"));

            Assert.False(result.Success);
            Assert.Contains("FDL1", result.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void An_entry_pointing_outside_the_file_is_refused()
        {
            string pac = PacBuilder.Write(
                Path.Combine(_root, "firmware.pac"),
                ("FDL1", "fdl1.bin", new byte[] { 1, 2, 3 }));

            // Claim a payload far past the end of the packet.
            using (FileStream stream = new(pac, FileMode.Open, FileAccess.Write))
            {
                stream.Position = PacExtractor.HeaderSize + 1540; // loPartitionSize of entry 0
                stream.Write(BitConverter.GetBytes(1024u));
            }

            PacInspection inspection = PacExtractor.Read(pac);

            Assert.False(inspection.Success);
            Assert.Contains("outside", inspection.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_older_FDL_partition_name_is_accepted_as_the_first_stage()
        {
            string pac = PacBuilder.Write(
                Path.Combine(_root, "firmware.pac"),
                ("FDL", "splloader.bin", new byte[] { 4, 5, 6 }));

            PacExtractionResult result = PacExtractor.ExtractLoaders(pac, Path.Combine(_root, "work"));

            Assert.True(result.Success, result.Error);
            Assert.Equal("splloader.bin", Path.GetFileName(result.Fdl1Path));
            Assert.Null(result.Fdl2Path);
        }

        [Fact]
        public void A_missing_file_is_refused_without_throwing()
        {
            PacExtractionResult result = PacExtractor.ExtractLoaders(
                Path.Combine(_root, "missing.pac"), Path.Combine(_root, "work"));

            Assert.False(result.Success);
            Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Builds a packet that follows the real header layout.</summary>
        private static class PacBuilder
        {
            public static string Write(string path, params (string Partition, string File, byte[] Payload)[] entries)
            {
                int tableSize = PacExtractor.EntryHeaderSize * entries.Length;
                int dataStart = PacExtractor.HeaderSize + tableSize;

                byte[] header = new byte[PacExtractor.HeaderSize];
                WriteUtf16(header, 0, 44, "BP_R1.0.0");
                WriteUtf16(header, 52, 512, "SC9863A");
                WriteUtf16(header, 564, 512, "B_VIP Test Firmware");
                BitConverter.GetBytes((uint)entries.Length).CopyTo(header, 1076);
                BitConverter.GetBytes((uint)PacExtractor.HeaderSize).CopyTo(header, 1080);
                BitConverter.GetBytes(0xFFFAFFFAu).CopyTo(header, 2116);

                byte[] table = new byte[tableSize];
                int offset = dataStart;

                for (int i = 0; i < entries.Length; i++)
                {
                    int at = i * PacExtractor.EntryHeaderSize;
                    (string partition, string file, byte[] payload) = entries[i];

                    BitConverter.GetBytes((uint)PacExtractor.EntryHeaderSize).CopyTo(table, at);
                    WriteUtf16(table, at + 4, 512, partition);
                    WriteUtf16(table, at + 516, 512, file);
                    BitConverter.GetBytes(0u).CopyTo(table, at + 1532);                // high size
                    BitConverter.GetBytes(0u).CopyTo(table, at + 1536);                // high offset
                    BitConverter.GetBytes((uint)payload.Length).CopyTo(table, at + 1540); // low size
                    BitConverter.GetBytes(1u).CopyTo(table, at + 1544);                // nFileFlag
                    BitConverter.GetBytes((uint)offset).CopyTo(table, at + 1552);       // low offset

                    offset += payload.Length;
                }

                using FileStream stream = File.Create(path);
                stream.Write(header);
                stream.Write(table);

                foreach ((string _, string _, byte[] payload) in entries)
                {
                    stream.Write(payload);
                }

                return path;
            }

            private static void WriteUtf16(byte[] buffer, int offset, int length, string value)
            {
                byte[] bytes = Encoding.Unicode.GetBytes(value ?? string.Empty);
                int copy = Math.Min(bytes.Length, length - 2);
                Array.Copy(bytes, 0, buffer, offset, copy);
            }
        }
    }
}
