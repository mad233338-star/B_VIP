using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace B_VIP.Modules.Mtk
{
    /// <summary>One partition line of a MediaTek scatter table.</summary>
    public sealed record ScatterPartition(
        string Name,
        long StartAddress,
        long SizeBytes,
        string? Region,
        string? FileName)
    {
        public long EndAddress => StartAddress + SizeBytes;

        /// <summary>Partitions that hold radio calibration data and must never be erased.</summary>
        public bool IsCalibration => MtkScatterTable.IsProtected(Name);

        public string Summary =>
            $"{Name,-20} 0x{StartAddress:X8}  0x{SizeBytes:X8} ({(SizeBytes / 1024d / 1024d):0.##} MB){(Region is null ? string.Empty : "  " + Region)}";
    }

    /// <summary>
    /// Reads the two scatter-table formats MediaTek ships: the XML table used by newer SoCs and the
    /// classic dash-prefixed text table. Parsing is pure so the boundaries can be asserted in tests —
    /// the table is the only authority the engine has for where a partition starts and how long it is.
    /// </summary>
    public static class MtkScatterTable
    {
        /// <summary>Partitions that carry per-unit calibration or identifiers; erasing them bricks reception.</summary>
        private static readonly string[] Protected =
        [
            "nvram", "nvdata", "nvcfg", "protect1", "protect2", "persist", "radio", "modem",
            "metadata", "seccfg", "otp", "boot0", "boot1"
        ];

        public static bool IsProtected(string partitionName)
        {
            string name = partitionName.Trim().ToLowerInvariant();
            return Protected.Contains(name) ||
                   name.StartsWith("proinfo", StringComparison.Ordinal) ||
                   name.StartsWith("nvram", StringComparison.Ordinal) ||
                   name.StartsWith("protect", StringComparison.Ordinal);
        }

        /// <summary>Parses either scatter format. Throws <see cref="FormatException"/> when nothing is found.</summary>
        public static IReadOnlyList<ScatterPartition> Parse(string text)
        {
            ArgumentNullException.ThrowIfNull(text);

            if (string.IsNullOrWhiteSpace(text))
            {
                throw new FormatException("The scatter file is empty.");
            }

            IReadOnlyList<ScatterPartition> partitions =
                text.TrimStart().StartsWith('<') ? ParseXml(text) : ParseText(text);

            if (partitions.Count == 0)
            {
                throw new FormatException("No partition entries were found in the scatter file.");
            }

            return partitions;
        }

        /// <summary>Locates the factory reset protection partition, if the table declares one.</summary>
        public static ScatterPartition? FindFrp(IReadOnlyList<ScatterPartition> partitions)
            => partitions.FirstOrDefault(p => p.Name.Equals("frp", StringComparison.OrdinalIgnoreCase));

        private static IReadOnlyList<ScatterPartition> ParseXml(string text)
        {
            XDocument document;
            try
            {
                document = XDocument.Parse(text);
            }
            catch (System.Xml.XmlException ex)
            {
                throw new FormatException($"The scatter XML could not be read: {ex.Message}", ex);
            }

            List<ScatterPartition> partitions = new();
            foreach (XElement element in document.Descendants()
                .Where(e => e.Name.LocalName.Equals("partition", StringComparison.OrdinalIgnoreCase)))
            {
                string? name = Value(element, "partition_name") ?? Value(element, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    // The <partition> element also wraps the general table settings, which have no name.
                    continue;
                }

                partitions.Add(new ScatterPartition(
                    name.Trim(),
                    ParseNumber(Value(element, "partition_addr") ?? Value(element, "start_addr")) ?? 0,
                    ParseNumber(Value(element, "partition_size") ?? Value(element, "size")) ?? 0,
                    Value(element, "storage") ?? Value(element, "region"),
                    Value(element, "file_name")));
            }

            return partitions;
        }

        private static IReadOnlyList<ScatterPartition> ParseText(string text)
        {
            // A text scatter entry is a block of "- key: value" lines separated by blank lines.
            Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase);
            List<ScatterPartition> partitions = new();

            void Flush()
            {
                if (fields.TryGetValue("partition_name", out string? name) && !string.IsNullOrWhiteSpace(name))
                {
                    partitions.Add(new ScatterPartition(
                        name.Trim(),
                        ParseNumber(fields.GetValueOrDefault("partition_addr")) ?? 0,
                        ParseNumber(fields.GetValueOrDefault("partition_size")) ?? 0,
                        fields.GetValueOrDefault("storage"),
                        fields.GetValueOrDefault("file_name")));
                }

                fields.Clear();
            }

            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                {
                    Flush();
                    continue;
                }

                Match match = Regex.Match(line, @"^-\s*([a-z_]+)\s*:\s*(.*)$", RegexOptions.IgnoreCase);
                if (!match.Success)
                {
                    continue;
                }

                fields[match.Groups[1].Value.Trim()] = match.Groups[2].Value.Trim();
            }

            Flush();
            return partitions;
        }

        private static string? Value(XElement element, string name)
            => element.Elements()
                .FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?.Value.Trim();

        /// <summary>Scatter tables mix decimal and 0x-prefixed hexadecimal values.</summary>
        public static long? ParseNumber(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            string text = value.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return long.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex)
                    ? hex
                    : null;
            }

            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long dec) ? dec : null;
        }
    }
}
