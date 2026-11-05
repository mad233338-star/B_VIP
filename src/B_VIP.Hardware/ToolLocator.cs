using B_VIP.Contracts.Devices;

namespace B_VIP.Hardware
{
    public sealed record ToolLocatorOptions
    {
        /// <summary>Explicit directory containing adb(.exe) and fastboot(.exe).</summary>
        public string? PlatformToolsPath { get; set; }

        /// <summary>Optional additional search roots (for example configured integrations).</summary>
        public IReadOnlyList<string> AdditionalSearchPaths { get; set; } = Array.Empty<string>();

        /// <summary>
        /// How many parent directories above the application folder are searched for a development
        /// .tools or platform-tools folder. Zero keeps the search strictly local to the executable.
        /// Four levels reach from bin/Debug/net8.0-windows/win-x64 up to the solution root.
        /// </summary>
        public int ParentSearchDepth { get; set; } = 4;

        /// <summary>
        /// Base directory to start the search from. Defaults to AppDomain.CurrentDomain.BaseDirectory,
        /// so a shipped build finds tools next to its executable.
        /// </summary>
        public string? BaseDirectory { get; set; }
    }

    public interface IToolLocator
    {
        string? Find(string toolName);

        Task<ToolStatusDto> GetStatusAsync(string toolName, CancellationToken ct = default);

        Task<IReadOnlyList<ToolStatusDto>> GetAllStatusAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Finds the external Android tools in the order a portable installation needs: first the tool
    /// bundled next to the executable (<c>platform-tools\adb.exe</c>, then <c>adb.exe</c>),
    /// then the .tools / tools development folders (traversing up to 4 parent directories),
    /// then system PATH, and finally well-known SDK locations.
    /// A resolved tool is always returned as an absolute path.
    /// </summary>
    public sealed class ToolLocator : IToolLocator
    {
        public const string Adb = "adb";
        public const string Fastboot = "fastboot";

        /// <summary>Sub-folder a bundled copy of the Android platform-tools lives in.</summary>
        public const string PlatformToolsFolder = "platform-tools";

        private static readonly string[] WellKnownRoots =
        [
            "%LOCALAPPDATA%\\Android\\Sdk\\platform-tools",
            "%ANDROID_HOME%\\platform-tools",
            "%ANDROID_SDK_ROOT%\\platform-tools",
            "C:\\platform-tools",
            "C:\\adb"
        ];

        private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

        private readonly ToolLocatorOptions _options;
        private readonly IProcessRunner _runner;
        private readonly Dictionary<string, (ToolStatusDto Status, DateTime CachedUtc)> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _gate = new(1, 1);

        public ToolLocator(ToolLocatorOptions options, IProcessRunner runner)
        {
            _options = options;
            _runner = runner;
        }

        public string? Find(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName))
            {
                return null;
            }

            string fileName = toolName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? toolName : toolName + ".exe";

            foreach (string? candidate in CandidatePaths(fileName))
            {
                if (candidate is null)
                {
                    continue;
                }

                try
                {
                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // A malformed search root must not stop the resolution of the other candidates.
                }
            }

            return null;
        }

        public async Task<ToolStatusDto> GetStatusAsync(string toolName, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct);
            try
            {
                if (_cache.TryGetValue(toolName, out (ToolStatusDto Status, DateTime CachedUtc) entry) &&
                    DateTime.UtcNow - entry.CachedUtc < CacheDuration)
                {
                    return entry.Status;
                }

                ToolStatusDto status = await ProbeAsync(toolName, ct);
                _cache[toolName] = (status, DateTime.UtcNow);
                return status;
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<ToolStatusDto> ProbeAsync(string toolName, CancellationToken ct)
        {
            string? path = Find(toolName);
            if (path is null)
            {
                return new ToolStatusDto(toolName, false, null, null,
                    $"The '{toolName}' executable was not found next to the application, in a bundled "
                    + $"\"{PlatformToolsFolder}\" folder, in a development .tools folder, on PATH "
                    + "or in the Android SDK locations.");
            }

            ProcessResult version = await _runner.RunAsync(new ProcessRequest(path, ["version"], TimeoutSeconds: 15), ct);
            string output = string.IsNullOrWhiteSpace(version.StandardOutput) ? version.StandardError : version.StandardOutput;
            string? versionLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(l => l.Contains(toolName, StringComparison.OrdinalIgnoreCase))
                ?? output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

            return new ToolStatusDto(toolName, true, path,
                version.Started ? Truncate(versionLine, 96) : null,
                version.Started ? null : version.FailureReason);
        }

        public async Task<IReadOnlyList<ToolStatusDto>> GetAllStatusAsync(CancellationToken ct = default)
        {
            List<ToolStatusDto> results = new(2);
            foreach (string tool in new[] { Adb, Fastboot })
            {
                results.Add(await GetStatusAsync(tool, ct));
            }

            return results;
        }

        /// <summary>
        /// Every location that is checked, in priority order for portable deployment:
        /// 1. Operator-chosen folder (PlatformToolsPath)
        /// 2. Additional search roots
        /// 3. Portable layout: platform-tools next to executable, then beside it,
        ///    and .tools / tools folders traversing up to 4 parent directories
        /// 4. System PATH
        /// 5. Well-known Android SDK locations
        /// </summary>
        private IEnumerable<string?> CandidatePaths(string fileName)
        {
            // 1. An operator-chosen folder always wins over automatic discovery.
            if (!string.IsNullOrWhiteSpace(_options.PlatformToolsPath))
            {
                yield return SafeCombine(_options.PlatformToolsPath, fileName);
            }

            foreach (string extra in _options.AdditionalSearchPaths)
            {
                if (!string.IsNullOrWhiteSpace(extra))
                {
                    yield return SafeCombine(extra, fileName);
                }
            }

            // 2. The portable layout: check BaseDirectory first, then traverse parents
            foreach (string? candidate in PortableCandidates(fileName))
            {
                yield return candidate;
            }

            // 3. The system PATH.
            string? pathVariable = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathVariable))
            {
                foreach (string dir in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    yield return SafeCombine(dir.Trim(), fileName);
                }
            }

            // 4. Well-known Android SDK locations.
            foreach (string root in WellKnownRoots)
            {
                string expanded = Environment.ExpandEnvironmentVariables(root);
                yield return SafeCombine(expanded, fileName);
            }

            string? localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
            {
                yield return SafeCombine(Path.Combine(localAppData, @"Android\Sdk\platform-tools"), fileName);
            }
        }

        /// <summary>
        /// Walks from the application directory upwards (up to ParentSearchDepth levels),
        /// so both a shipped build (platform-tools beside the exe) and a Visual Studio run
        /// (below the repository .tools folder) resolve.
        /// </summary>
        private IEnumerable<string?> PortableCandidates(string fileName)
        {
            string start;
            if (!string.IsNullOrWhiteSpace(_options.BaseDirectory))
            {
                start = _options.BaseDirectory;
            }
            else
            {
                start = AppDomain.CurrentDomain.BaseDirectory;
            }

            DirectoryInfo? current;
            try
            {
                current = new DirectoryInfo(Path.GetFullPath(start));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                yield break;
            }

            int depth = 0;
            int maxDepth = Math.Max(0, _options.ParentSearchDepth);

            while (current is not null && depth <= maxDepth)
            {
                string root = current.FullName;

                // Check platform-tools subfolder first (primary portable path)
                yield return SafeCombine(Path.Combine(root, PlatformToolsFolder), fileName);
                // Check direct executable beside the application directory
                yield return SafeCombine(root, fileName);
                // Check .tools/platform-tools
                yield return SafeCombine(Path.Combine(root, ".tools", PlatformToolsFolder), fileName);
                // Check .tools root
                yield return SafeCombine(Path.Combine(root, ".tools"), fileName);
                // Check tools/platform-tools
                yield return SafeCombine(Path.Combine(root, "tools", PlatformToolsFolder), fileName);
                // Check tools root
                yield return SafeCombine(Path.Combine(root, "tools"), fileName);

                // Stop at the repository root: the traversal exists to find the development .tools
                // folder, not to pick up unrelated tool folders higher up on the machine.
                if (IsRepositoryRoot(root))
                {
                    yield break;
                }

                current = current.Parent;
                depth++;
            }
        }

        private static bool IsRepositoryRoot(string directory)
        {
            try
            {
                if (Directory.Exists(Path.Combine(directory, ".git")))
                {
                    return true;
                }

                return Directory.EnumerateFiles(directory, "*.sln", SearchOption.TopDirectoryOnly).Any();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return false;
            }
        }

        private static string? SafeCombine(string directory, string fileName)
        {
            try
            {
                return Path.Combine(directory, fileName);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private static string? Truncate(string? value, int max)
            => string.IsNullOrEmpty(value) ? value : value.Length <= max ? value : value[..max];
    }
}