using System.Formats.Tar;
using System.IO;

namespace B_VIP.Desktop.Services.Samsung
{
    /// <summary>One partition image ready to be handed to the flasher.</summary>
    public sealed record OdinPartitionImage(string Partition, string FileName, string FilePath, string Slot);

    /// <summary>The result of unpacking the selected packages into a temporary working folder.</summary>
    public sealed record OdinExtractionResult(
        bool Succeeded,
        string Detail,
        IReadOnlyList<OdinPartitionImage> Images,
        string WorkingFolder,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<string> Log)
    {
        public static OdinExtractionResult Failed(string detail, string folder, IReadOnlyList<string> log)
            => new(false, detail, Array.Empty<OdinPartitionImage>(), folder, Array.Empty<string>(), log);
    }

    /// <summary>
    /// Turns the four Odin packages into the partition images Heimdall can actually write.
    ///
    /// Heimdall has no concept of a BL/AP/CP/CSC package: its <c>flash</c> action takes
    /// <c>--&lt;partition&gt; &lt;image&gt;</c> pairs, because a Samsung package is a tar archive of
    /// partition images. The extractor therefore unpacks each selected package into
    /// <c>%TEMP%\BVIP_ODIN\&lt;run&gt;</c>, maps every image to its partition name and returns the
    /// pairs; the caller deletes the folder when the flash ends.
    /// </summary>
    public static class OdinFirmwareExtractor
    {
        /// <summary>Extensions Heimdall can write as a raw partition image.</summary>
        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".img", ".bin", ".mbn", ".elf"
        };

        /// <summary>
        /// Image names whose PIT partition is spelled differently. Anything not listed is used as-is
        /// (upper-cased), and Heimdall then resolves it against the handset's own PIT: an unknown
        /// partition aborts the flash before a single byte is written.
        /// </summary>
        private static readonly Dictionary<string, string> PartitionAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["boot"] = "BOOT",
            ["init_boot"] = "INIT_BOOT",
            ["vendor_boot"] = "VENDOR_BOOT",
            ["recovery"] = "RECOVERY",
            ["sboot"] = "SBOOT",
            ["bootloader"] = "BOOTLOADER",
            ["modem"] = "RADIO",
            ["radio"] = "RADIO",
            ["mdm"] = "MDM",
            ["tz"] = "TZ",
            ["tzsw"] = "TZSW",
            ["param"] = "PARAM",
            ["persist"] = "PERSIST",
            ["vbmeta"] = "VBMETA",
            ["vbmeta_system"] = "VBMETA_SYSTEM",
            ["dtbo"] = "DTBO",
            ["super"] = "SUPER",
            ["system"] = "SYSTEM",
            ["vendor"] = "VENDOR",
            ["product"] = "PRODUCT",
            ["userdata"] = "USERDATA",
            ["cache"] = "CACHE",
            ["efs"] = "EFS",
            ["keymaster"] = "KEYMASTER",
            ["keystore"] = "KEYMASTER",
            ["sec_efs"] = "SEC_EFS",
            ["modem_debug"] = "MODEM_DEBUG",
            ["cmnlib"] = "CMNLIB",
            ["cmnlib64"] = "CMNLIB64",
            ["abl"] = "ABL",
            ["xbl"] = "XBL",
            ["xbl_config"] = "XBL_CONFIG"
        };

        /// <summary>Archive members that are never partition images.</summary>
        private static bool IsCandidate(string entryName)
        {
            string name = Path.GetFileName(entryName);
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            string extension = Path.GetExtension(name);
            if (!ImageExtensions.Contains(extension))
            {
                return false;
            }

            // Some packages carry the same images compressed; Heimdall cannot decompress them.
            return !name.EndsWith(".lz4", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The partition a raw image belongs to, e.g. <c>sboot.bin</c> → SBOOT.</summary>
        public static string PartitionNameFor(string fileName)
        {
            string stem = Path.GetFileNameWithoutExtension(fileName);

            // Samsung images sometimes carry a trailing variant suffix (boot_samsung.img).
            int separator = stem.IndexOfAny(['-', '.']);
            if (separator > 0)
            {
                stem = stem[..separator];
            }

            if (PartitionAliases.TryGetValue(stem, out string? mapped))
            {
                return mapped;
            }

            return stem.ToUpperInvariant().Replace('-', '_').Replace(' ', '_');
        }

        /// <summary>
        /// Unpacks every selected package. A package that is already a raw image is taken as it is;
        /// a tar (with or without the trailing .md5) is walked entry by entry. Never throws: a bad
        /// archive comes back as a failed result with a readable reason.
        /// </summary>
        public static async Task<OdinExtractionResult> ExtractAsync(
            IReadOnlyList<OdinSlotFile> packages,
            string? workingRoot = null,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(packages);

            List<string> log = new();
            List<string> warnings = new();
            List<OdinPartitionImage> images = new();
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

            string root = workingRoot
                ?? Path.Combine(Path.GetTempPath(), "BVIP_ODIN", Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return OdinExtractionResult.Failed(
                    $"The temporary working folder could not be created: {ex.Message}", root, log);
            }

            foreach (OdinSlotFile package in packages)
            {
                ct.ThrowIfCancellationRequested();

                if (!File.Exists(package.FilePath))
                {
                    return OdinExtractionResult.Failed(
                        $"The package '{package.FileName}' is no longer on disk.", root, log);
                }

                log.Add($"{package.Slot}: {package.FileName}");

                bool rawImage = ImageExtensions.Contains(Path.GetExtension(package.FilePath));
                if (rawImage)
                {
                    // A bare image (rare, but a technician may flash just one partition).
                    Add(new OdinPartitionImage(PartitionNameFor(package.FilePath), package.FileName,
                        package.FilePath, package.Slot));
                    continue;
                }

                try
                {
                    await using FileStream stream = new(package.FilePath, FileMode.Open, FileAccess.Read,
                        FileShare.Read, 64 * 1024, useAsync: true);

                    using TarReader reader = new(stream, leaveOpen: false);
                    string folder = Path.Combine(root, package.Slot);

                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();

                        TarEntry? entry = await reader.GetNextEntryAsync(copyData: false, ct);
                        if (entry is null)
                        {
                            break;
                        }

                        if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                        {
                            continue;
                        }

                        if (!IsCandidate(entry.Name))
                        {
                            if (entry.Name.EndsWith(".lz4", StringComparison.OrdinalIgnoreCase))
                            {
                                warnings.Add($"{package.Slot}: '{Path.GetFileName(entry.Name)}' is compressed " +
                                             "(.lz4) and cannot be written by Heimdall; it was skipped.");
                            }

                            continue;
                        }

                        Directory.CreateDirectory(folder);
                        string fileName = Path.GetFileName(entry.Name);
                        string destination = Path.Combine(folder, fileName);

                        await entry.ExtractToFileAsync(destination, overwrite: true, ct);
                        Add(new OdinPartitionImage(PartitionNameFor(fileName), fileName, destination, package.Slot));
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                                               or EndOfStreamException or FormatException)
                {
                    return OdinExtractionResult.Failed(
                        $"'{package.FileName}' is not a readable Odin package: {ex.Message}", root, log);
                }
            }

            if (images.Count == 0)
            {
                return OdinExtractionResult.Failed(
                    "No partition images were found in the selected packages. Heimdall can only write raw " +
                    "partition images (boot.img, sboot.bin, super.img, ...); check that the packages are " +
                    "Samsung firmware archives.", root, log);
            }

            log.Add($"{images.Count} partition image(s) mapped: " +
                    string.Join(", ", images.Select(i => i.Partition)));

            return new OdinExtractionResult(true, "Packages unpacked.", images, root, warnings, log);

            void Add(OdinPartitionImage image)
            {
                // The same partition in two packages (BL and AP both carry TZ on some models) is
                // flashed once: writing it twice would double the transfer for no benefit.
                if (!seen.Add(image.Partition))
                {
                    warnings.Add($"{image.Slot}: '{image.Partition}' was already supplied by another package and was skipped.");
                    return;
                }

                images.Add(image);
            }
        }

        /// <summary>Removes a run's working folder; called once the flash ends, whatever its outcome.</summary>
        public static void Cleanup(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A locked file simply stays in %TEMP%; it is not worth failing a finished flash.
            }
        }
    }
}
