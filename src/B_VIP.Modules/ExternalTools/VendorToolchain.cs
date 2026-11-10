using System.IO;

namespace B_VIP.Modules.ExternalTools
{
    /// <summary>
    /// Where a chipset tool lives on this computer. <see cref="Executable"/> is what the process
    /// runner starts; <see cref="EntryScript"/> is the Python script for the tools that are scripts
    /// (mtkclient) and null for a native executable.
    /// </summary>
    public sealed record VendorToolResolution(bool Found, string Executable, string? EntryScript, string Detail)
    {
        public static VendorToolResolution Missing(string detail) => new(false, string.Empty, null, detail);

        /// <summary>For a script tool the tool runs from its own folder; that folder is the working directory.</summary>
        public string? WorkingDirectory
            => string.IsNullOrWhiteSpace(EntryScript) ? null : Path.GetDirectoryName(EntryScript);
    }

    /// <summary>Finds the chipset tools the FRP routines shell out to. Replaced in tests by a stub.</summary>
    public interface IVendorToolchain
    {
        VendorToolResolution ResolveMtkClient();

        VendorToolResolution ResolveFhLoader();

        VendorToolResolution ResolveSpdDump();

        /// <summary>The Samsung download agent shipped with the loader bundle, when it is present.</summary>
        string? FindSamsungDa();

        /// <summary>The Firehose programmer (prog_firehose_*.mbn / .elf) for fh_loader.</summary>
        string? FindFirehoseProgrammer();
    }

    /// <summary>
    /// Resolves the external tools from the provisioned loader bundles under <c>Loaders\</c> beside
    /// the executable (MediaTek / Qualcomm / SPD), and the Python interpreter from the machine's
    /// PATH. Nothing is downloaded here: the chipset screens provision their own bundle first, and a
    /// missing tool is reported as an actionable message instead of an exception.
    /// </summary>
    public sealed class VendorToolchain : IVendorToolchain
    {
        private readonly string _root;
        private readonly Func<string, string?> _locateOnPath;

        public VendorToolchain(string? rootDirectory = null, Func<string, string?>? locateOnPath = null)
        {
            _root = string.IsNullOrWhiteSpace(rootDirectory) ? DefaultRootDirectory : Path.GetFullPath(rootDirectory);
            _locateOnPath = locateOnPath ?? FindOnPath;
        }

        /// <summary><c>Loaders</c> beside the executable — the same folder the provisioner fills.</summary>
        public static string DefaultRootDirectory => Path.Combine(AppContext.BaseDirectory, "Loaders");

        public string RootDirectory => _root;

        /// <summary>mtkclient is a Python package: find <c>mtk.py</c> and an interpreter to run it.</summary>
        public VendorToolResolution ResolveMtkClient()
        {
            string? script = FindFile(["MTK", "MediaTek", "mtkclient"], ["mtk.py", "mtk_client.py"]);
            if (script is null)
            {
                return VendorToolResolution.Missing(
                    "mtkclient (mtk.py) was not found in the loader bundle. Open the MediaTek screen and install the loader package first.");
            }

            string? python = ResolvePython();
            if (python is null)
            {
                return VendorToolResolution.Missing(
                    "Python was not found on this computer, and mtkclient needs it. Install Python 3.8+ (with 'Add to PATH') and retry.");
            }

            return new VendorToolResolution(true, python, script, $"mtkclient: {script}");
        }

        public VendorToolResolution ResolveFhLoader()
        {
            string? exe = FindFile(["Qualcomm"], ["fh_loader.exe"]);
            return exe is null
                ? VendorToolResolution.Missing(
                    "fh_loader.exe was not found in the Qualcomm loader bundle. Install the Qualcomm loader package on the Qualcomm screen first.")
                : new VendorToolResolution(true, exe, null, $"fh_loader: {exe}");
        }

        public VendorToolResolution ResolveSpdDump()
        {
            string? exe = FindFile(["SPD", "Spreadtrum"], ["spd_dump.exe", "spd_dump"]);
            return exe is null
                ? VendorToolResolution.Missing(
                    "spd_dump was not found in the Unisoc loader bundle. Install the Spreadtrum loader package on the SPREADTRUM screen first.")
                : new VendorToolResolution(true, exe, null, $"spd_dump: {exe}");
        }

        public string? FindSamsungDa()
        {
            // The published bundle is named MTK; the documented Samsung layout is MediaTek.
            return FindFile(["MediaTek", "MTK"], ["samsung_da.bin", "samsung_da*.bin"]);
        }

        public string? FindFirehoseProgrammer()
        {
            string folder = Path.Combine(_root, "Qualcomm");
            try
            {
                if (!Directory.Exists(folder))
                {
                    return null;
                }

                return Directory.EnumerateFiles(folder, "prog_firehose_*", SearchOption.AllDirectories)
                    .Concat(Directory.EnumerateFiles(folder, "*.mbn", SearchOption.AllDirectories))
                    .Concat(Directory.EnumerateFiles(folder, "*.elf", SearchOption.AllDirectories))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// Looks for one of <paramref name="fileNames"/> in the named chipset folders first (the shape
        /// the bundles use), then anywhere under the loader root.
        /// </summary>
        private string? FindFile(IReadOnlyList<string> preferredFolders, IReadOnlyList<string> fileNames)
        {
            foreach (string folderName in preferredFolders)
            {
                string folder = Path.Combine(_root, folderName);
                string? found = Search(folder, fileNames, allDirectories: true);
                if (found is not null)
                {
                    return found;
                }
            }

            return Search(_root, fileNames, allDirectories: true);
        }

        private static string? Search(string folder, IReadOnlyList<string> fileNames, bool allDirectories)
        {
            try
            {
                if (!Directory.Exists(folder))
                {
                    return null;
                }

                foreach (string name in fileNames)
                {
                    string? match = Directory
                        .EnumerateFiles(folder, name, allDirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                        .FirstOrDefault();
                    if (match is not null)
                    {
                        return Path.GetFullPath(match);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A protected or unreadable folder simply means "not installed here".
            }

            return null;
        }

        /// <summary>An explicit override, then the py launcher, then the usual interpreter names.</summary>
        private string? ResolvePython()
        {
            string? configured = Environment.GetEnvironmentVariable("BVIP_PYTHON");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            {
                return Path.GetFullPath(configured);
            }

            foreach (string candidate in new[] { "python.exe", "python3.exe", "py.exe", "python", "python3" })
            {
                string? found = _locateOnPath(candidate);
                if (found is not null)
                {
                    return found;
                }
            }

            return null;
        }

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
                                 : [executable])
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
    }
}
