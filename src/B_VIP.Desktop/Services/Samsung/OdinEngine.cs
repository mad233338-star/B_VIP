using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace B_VIP.Desktop.Services.Samsung
{
    /// <summary>Which command-line flasher the Samsung Odin screen will drive.</summary>
    public enum OdinEngineKind
    {
        /// <summary>Nothing usable was found in <c>Tools\Samsung</c>.</summary>
        None,

        /// <summary>odin4, the CLI cousin of Odin3: takes the four tar packages directly.</summary>
        Odin4,

        /// <summary>Heimdall, the open-source Odin-compatible flasher: takes partition images.</summary>
        Heimdall
    }

    /// <summary>One firmware slot the flasher accepts: BL, AP, CP or CSC.</summary>
    public sealed record OdinSlotFile(string Slot, string FilePath)
    {
        public string FileName => Path.GetFileName(FilePath);
    }

    /// <summary>
    /// Where the Samsung flashing engine lives and how a flash is spelled for it. The engine is
    /// bundled beside the executable under <c>Tools\Samsung</c>; nothing is downloaded at runtime,
    /// so a release that ships the folder works on a machine with no internet at all.
    /// </summary>
    public sealed record OdinEngineInfo(
        bool Found,
        OdinEngineKind Kind,
        string Executable,
        string Folder,
        string Detail)
    {
        public static OdinEngineInfo Missing(string folder, string detail)
            => new(false, OdinEngineKind.None, string.Empty, folder, detail);

        public string Name => Kind switch
        {
            OdinEngineKind.Odin4 => "odin4",
            OdinEngineKind.Heimdall => "heimdall",
            _ => "not installed"
        };

        /// <summary>
        /// True for the engines that accept a command line. Both bundled engines run headless: the
        /// screen starts them hidden and streams their output, so no vendor window ever appears.
        /// </summary>
        public bool SupportsCli => Kind is OdinEngineKind.Odin4 or OdinEngineKind.Heimdall;
    }

    /// <summary>
    /// Resolves the bundled Samsung CLI flasher and translates the technician's firmware picks into
    /// that engine's command line. The folder is fixed (<c>Tools\Samsung</c> beside the executable)
    /// so the installer and the portable build both end up with one predictable layout; a
    /// machine-wide PATH install is accepted as a fallback for workshops that already have one.
    /// </summary>
    public static class OdinEngine
    {
        /// <summary>The bundled engine folder, always relative to the running executable.</summary>
        public static string DefaultFolder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "Samsung");

        /// <summary>File names each CLI engine is known by, in preference order.</summary>
        private static readonly (string Name, OdinEngineKind Kind)[] Candidates =
        [
            ("odin4.exe", OdinEngineKind.Odin4),
            ("odin4", OdinEngineKind.Odin4),
            ("heimdall.exe", OdinEngineKind.Heimdall),
            ("heimdall", OdinEngineKind.Heimdall)
        ];

        /// <summary>Finds the engine in <paramref name="folder"/>, then on PATH. Never throws.</summary>
        public static OdinEngineInfo Resolve(string? folder = null, Func<string, string?>? locateOnPath = null)
        {
            string root = string.IsNullOrWhiteSpace(folder) ? DefaultFolder : Path.GetFullPath(folder);
            Func<string, string?> locate = locateOnPath ?? FindOnPath;

            try
            {
                if (Directory.Exists(root))
                {
                    foreach ((string name, OdinEngineKind kind) in Candidates)
                    {
                        string candidate = Path.Combine(root, name);
                        if (!File.Exists(candidate))
                        {
                            continue;
                        }

                        // The engine's own folder is the working directory so its runtime DLLs
                        // (heimdall needs libusb-1.0.dll) resolve no matter where the app started.
                        string? work = Path.GetDirectoryName(Path.GetFullPath(candidate));
                        return new OdinEngineInfo(true, kind, Path.GetFullPath(candidate), work ?? root,
                            $"{Name(kind)} · {Path.GetFileName(candidate)} · {root}");
                    }
                }

                foreach ((string name, OdinEngineKind kind) in Candidates)
                {
                    string? onPath = locate(name);
                    if (onPath is null)
                    {
                        continue;
                    }

                    return new OdinEngineInfo(true, kind, onPath, Path.GetDirectoryName(onPath) ?? root,
                        $"{Name(kind)} · {onPath} (found on PATH)");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return OdinEngineInfo.Missing(root, $"The engine folder could not be read: {ex.Message}");
            }

            return OdinEngineInfo.Missing(root,
                $"No flashing engine in {root}. The release ships heimdall.exe with libusb-1.0.dll there; " +
                "an odin4.exe dropped in the same folder is preferred when present.");
        }

        private static string Name(OdinEngineKind kind) => kind switch
        {
            OdinEngineKind.Odin4 => "Odin4 CLI",
            OdinEngineKind.Heimdall => "Heimdall CLI",
            _ => "engine"
        };

        /// <summary>
        /// The odin4 command line for one flash. odin4 takes the packages themselves, so only the
        /// slots the technician filled are passed: an AP-only flash is legitimate, and a CP-only
        /// flash re-writes the modem without touching the system.
        /// </summary>
        public static IReadOnlyList<string> BuildArguments(
            OdinEngineKind kind,
            IReadOnlyList<OdinSlotFile> slots,
            string? pitPath,
            bool autoReboot,
            bool resetTime)
        {
            ArgumentNullException.ThrowIfNull(slots);

            if (kind != OdinEngineKind.Odin4)
            {
                // Heimdall takes partition images, not packages: see BuildHeimdallArguments.
                return Array.Empty<string>();
            }

            List<string> arguments = new();

            string? Find(string slot) => slots
                .FirstOrDefault(s => string.Equals(s.Slot, slot, StringComparison.OrdinalIgnoreCase))?.FilePath;

            Append(arguments, "-b", Find("BL"));
            Append(arguments, "-a", Find("AP"));
            Append(arguments, "-c", Find("CP"));
            Append(arguments, "-s", Find("CSC"));

            if (!string.IsNullOrWhiteSpace(pitPath))
            {
                Append(arguments, "--pit", pitPath);
            }

            // odin4 reboots the handset itself unless told otherwise; "F. Reset Time" has no CLI
            // switch and is accepted as a no-op so the two Odin options stay switchable.
            if (!autoReboot)
            {
                arguments.Add("--no-reboot");
            }

            _ = resetTime;
            return arguments;
        }

        /// <summary>
        /// The Heimdall command line for a set of partition images:
        /// <c>heimdall flash --BOOT boot.img --RECOVERY recovery.img [--pit p.pit] [--no-reboot]</c>.
        /// <c>--stdout-errors</c> keeps the error text on the stream the console already reads, and
        /// <c>--verbose</c> makes the engine report every partition it writes.
        /// </summary>
        public static IReadOnlyList<string> BuildHeimdallArguments(
            IReadOnlyList<OdinPartitionImage> images,
            string? pitPath,
            bool autoReboot)
        {
            ArgumentNullException.ThrowIfNull(images);

            List<string> arguments = ["flash"];

            foreach (OdinPartitionImage image in images)
            {
                arguments.Add("--" + image.Partition);
                arguments.Add(image.FilePath);
            }

            if (!string.IsNullOrWhiteSpace(pitPath))
            {
                arguments.Add("--pit");
                arguments.Add(pitPath);
            }

            if (!autoReboot)
            {
                arguments.Add("--no-reboot");
            }

            arguments.Add("--verbose");
            arguments.Add("--stdout-errors");
            return arguments;
        }

        private static void Append(List<string> arguments, string flag, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            arguments.Add(flag);
            arguments.Add(value);
        }

        /// <summary>
        /// Reads a percentage out of an engine line ("Flashing BOOTLOADER 42%"). Returns null when
        /// the line carries no percentage, which is most of them.
        /// </summary>
        public static int? ParsePercent(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            Match match = PercentPattern().Match(line);
            if (!match.Success || !int.TryParse(match.Groups["pct"].Value, out int percent))
            {
                return null;
            }

            return Math.Clamp(percent, 0, 100);
        }

        /// <summary>
        /// Which of the four slots a line belongs to, so a percentage can be placed on the overall
        /// bar instead of restarting it for every stage.
        /// </summary>
        public static int StageIndex(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return -1;
            }

            string text = line.ToUpperInvariant();
            if (text.Contains("CSC") || text.Contains("USERDATA"))
            {
                return 3;
            }

            if (text.Contains("CP ") || text.Contains("MODEM") || text.Contains("RADIO"))
            {
                return 2;
            }

            if (text.Contains("AP ") || text.Contains("SYSTEM") || text.Contains("SUPER") || text.Contains("RECOVERY"))
            {
                return 1;
            }

            if (text.Contains("BL ") || text.Contains("BOOTLOADER") || text.Contains("PIT"))
            {
                return 0;
            }

            return -1;
        }

        /// <summary>True when the engine reported success in its own output.</summary>
        public static bool LooksSuccessful(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            string text = line.Trim().ToUpperInvariant();
            return text.Contains("PASS") || text.Contains("RST_OK") || text.Contains("FLASH COMPLETED")
                || text.Contains("FLASHING COMPLETED") || text.Contains("REBOOTING DEVICE");
        }

        /// <summary>True when the engine reported a failure in its own output.</summary>
        public static bool LooksFailed(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            string text = line.Trim().ToUpperInvariant();
            return text.Contains("FAIL") || text.Contains("ERROR") || text.Contains("INVALID")
                || text.Contains("CANNOT") || text.Contains("NOT FOUND");
        }

        private static Regex? _percent;

        private static Regex PercentPattern() => _percent ??= new Regex(
            @"(?<pct>\d{1,3})\s*%",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static string? FindOnPath(string executable)
        {
            string? path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            bool needsExtension = Path.GetExtension(executable).Length == 0;
            foreach (string entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string directory = entry.Trim().Trim('"');
                if (directory.Length == 0)
                {
                    continue;
                }

                try
                {
                    foreach (string name in needsExtension
                                 ? new[] { executable, executable + ".exe" }
                                 : new[] { executable })
                    {
                        string candidate = Path.Combine(directory, name);
                        if (File.Exists(candidate))
                        {
                            return Path.GetFullPath(candidate);
                        }
                    }
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry must not stop the search.
                }
            }

            return null;
        }

        /// <summary>Opens the engine folder in Explorer; used by the "engine missing" hint.</summary>
        public static bool TryOpenFolder(string folder)
        {
            try
            {
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{folder}\"",
                    UseShellExecute = true
                });

                return true;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }
    }
}
