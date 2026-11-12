using System.IO;
using System.Text.Json;

namespace B_VIP.Desktop.Services.Catalog
{
    /// <summary>
    /// One handset in <c>Data/devices.json</c>: what the technician calls it, the code the bootloader
    /// reports, the engine that actually services it and the loader file that engine wants.
    /// </summary>
    public sealed record CatalogDevice(
        string Brand,
        string MarketingName,
        string ModelCode,
        string Platform,
        string Chipset,
        string CustomLoaderPath)
    {
        /// <summary>What the picker shows: "Galaxy A06 · SM-A065F".</summary>
        public string DisplayName => string.IsNullOrWhiteSpace(ModelCode)
            ? MarketingName
            : $"{MarketingName} · {ModelCode}";

        /// <summary>What the console logs when a model is applied.</summary>
        public string Summary
        {
            get
            {
                // "Exynos 1380" already names its platform: do not print "Exynos Exynos 1380".
                string soc = Chipset.StartsWith(Platform, StringComparison.OrdinalIgnoreCase)
                    ? Chipset
                    : $"{Platform} {Chipset}".Trim();

                return $"{Brand} {MarketingName} ({ModelCode}) — {soc}";
            }
        }

        /// <summary>True when the catalog names a loader for this handset.</summary>
        public bool HasCustomLoader => !string.IsNullOrWhiteSpace(CustomLoaderPath);
    }

    /// <summary>
    /// Reads and searches the multi-brand handset catalog. The catalog is loaded once, asynchronously,
    /// and the parsed list is published as an immutable snapshot: readers never take a lock and the
    /// caller's thread is never blocked on the disk.
    /// </summary>
    public sealed class DeviceCatalogService
    {
        /// <summary>The order the brand dropdown presents; a brand absent from the file is hidden.</summary>
        public static readonly IReadOnlyList<string> KnownBrands = ["Samsung", "Xiaomi", "Infinix", "Tecno"];

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };

        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly string _path;
        private readonly string _loaderRoot;
        private IReadOnlyList<CatalogDevice>? _devices;

        /// <summary>
        /// <paramref name="path"/> defaults to <c>Data/devices.json</c> next to the executable;
        /// <paramref name="loaderRoot"/> is the folder relative loader paths resolve against.
        /// </summary>
        public DeviceCatalogService(string? path = null, string? loaderRoot = null)
        {
            _path = path ?? Path.Combine(AppContext.BaseDirectory, "Data", "devices.json");
            _loaderRoot = loaderRoot ?? AppContext.BaseDirectory;
        }

        /// <summary>Set when the catalog could not be read; the screen shows it instead of failing.</summary>
        public string? Warning { get; private set; }

        /// <summary>True once <see cref="LoadAsync"/> has produced a catalog.</summary>
        public bool IsLoaded => _devices is not null;

        public string FilePath => _path;

        /// <summary>
        /// Parses the catalog once. Concurrent callers share one load, and a second call after the
        /// first completed returns the cached snapshot without touching the disk again.
        /// </summary>
        public async Task<IReadOnlyList<CatalogDevice>> LoadAsync(CancellationToken ct = default)
        {
            if (_devices is { } cached)
            {
                return cached;
            }

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_devices is { } loaded)
                {
                    return loaded;
                }

                IReadOnlyList<CatalogDevice> devices = await ReadAsync(ct).ConfigureAwait(false);
                _devices = devices;
                return devices;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Exposes the already-loaded snapshot without any I/O; empty until loaded.</summary>
        public IReadOnlyList<CatalogDevice> Devices => _devices ?? Array.Empty<CatalogDevice>();

        /// <summary>The brands present in the catalog, in <see cref="KnownBrands"/> order.</summary>
        public IReadOnlyList<string> Brands
        {
            get
            {
                IReadOnlyList<CatalogDevice> devices = Devices;
                List<string> present = KnownBrands
                    .Where(b => devices.Any(d => string.Equals(d.Brand, b, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                // A brand that only exists in a future file version still gets a dropdown entry.
                present.AddRange(devices
                    .Select(d => d.Brand)
                    .Where(b => !present.Contains(b, StringComparer.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(b => b, StringComparer.OrdinalIgnoreCase));

                return present;
            }
        }

        /// <summary>
        /// Filters by brand and by a free-text query against the marketing name, model code and
        /// chipset. An empty query returns every model of the brand, alphabetically by name.
        /// The limit is high enough that a whole brand lineup is never truncated: Samsung alone
        /// lists more than 150 handsets.
        /// </summary>
        public IReadOnlyList<CatalogDevice> Search(string? brand, string? query, int limit = 500)
        {
            IEnumerable<CatalogDevice> matches = Devices;

            // "All" / "All brands" stand for "no brand filter"; only a real brand name scopes the list.
            if (!string.IsNullOrWhiteSpace(brand) &&
                !brand.Trim().StartsWith("all", StringComparison.OrdinalIgnoreCase))
            {
                matches = matches.Where(d => string.Equals(d.Brand, brand.Trim(), StringComparison.OrdinalIgnoreCase));
            }

            string term = (query ?? string.Empty).Trim();
            if (term.Length > 0)
            {
                matches = matches.Where(d =>
                    d.MarketingName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    d.ModelCode.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    d.Chipset.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    d.Platform.Contains(term, StringComparison.OrdinalIgnoreCase));
            }

            return matches
                .OrderBy(d => d.MarketingName, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, limit))
                .ToList();
        }

        /// <summary>
        /// Absolute path of the loader the catalog names for this handset, or null when the file is
        /// not on this computer. The caller falls back to the engine's own default in that case.
        /// </summary>
        public string? ResolveLoader(CatalogDevice? device)
        {
            if (device is null || !device.HasCustomLoader)
            {
                return null;
            }

            try
            {
                string candidate = Path.IsPathRooted(device.CustomLoaderPath)
                    ? device.CustomLoaderPath
                    : Path.GetFullPath(Path.Combine(_loaderRoot, device.CustomLoaderPath));

                return File.Exists(candidate) ? candidate : null;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        private async Task<IReadOnlyList<CatalogDevice>> ReadAsync(CancellationToken ct)
        {
            Warning = null;

            try
            {
                if (!File.Exists(_path))
                {
                    Warning = $"The device catalog is missing ({Path.GetFileName(_path)}).";
                    return Array.Empty<CatalogDevice>();
                }

                await using FileStream stream = File.OpenRead(_path);
                List<CatalogEntry>? entries = await JsonSerializer
                    .DeserializeAsync<List<CatalogEntry>>(stream, JsonOptions, ct)
                    .ConfigureAwait(false);

                if (entries is null)
                {
                    Warning = "The device catalog is empty.";
                    return Array.Empty<CatalogDevice>();
                }

                return Normalise(entries);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Warning = $"The device catalog could not be read: {ex.Message}";
                return Array.Empty<CatalogDevice>();
            }
        }

        /// <summary>
        /// Drops incomplete entries and fixes the platform spelling, so a hand-edited catalog can
        /// never put a blank row in the picker or an unknown engine in the routing parameters.
        /// </summary>
        internal static IReadOnlyList<CatalogDevice> Normalise(IEnumerable<CatalogEntry> entries)
        {
            List<CatalogDevice> devices = new();

            foreach (CatalogEntry entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Brand) || string.IsNullOrWhiteSpace(entry.MarketingName))
                {
                    continue;
                }

                devices.Add(new CatalogDevice(
                    entry.Brand.Trim(),
                    entry.MarketingName.Trim(),
                    (entry.ModelCode ?? string.Empty).Trim(),
                    NormalisePlatform(entry.Platform),
                    (entry.Chipset ?? string.Empty).Trim(),
                    (entry.CustomLoaderPath ?? string.Empty).Trim()));
            }

            return devices;
        }

        /// <summary>
        /// Maps any spelling of the platforms onto the routing vocabulary the modules use. Exynos is
        /// its own label (so the technician sees the real SoC family) but no separate engine: the
        /// screens route it through the same bootloader erase an unknown platform gets.
        /// </summary>
        public static string NormalisePlatform(string? platform)
        {
            string value = (platform ?? string.Empty).Trim();

            if (value.Contains("exynos", StringComparison.OrdinalIgnoreCase))
            {
                return "Exynos";
            }

            if (value.Contains("mediatek", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("mt", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("dimensity", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("helio", StringComparison.OrdinalIgnoreCase))
            {
                return "MediaTek";
            }

            if (value.Contains("qualcomm", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("snapdragon", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("sm", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("msm", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("sdm", StringComparison.OrdinalIgnoreCase))
            {
                return "Qualcomm";
            }

            if (value.Contains("unisoc", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("spreadtrum", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("spd", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("sc", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("ums", StringComparison.OrdinalIgnoreCase))
            {
                return "Spreadtrum";
            }

            return "Fastboot";
        }

        /// <summary>The on-disk shape of one catalog row; kept separate from the immutable record.</summary>
        internal sealed record CatalogEntry(
            string Brand,
            string MarketingName,
            string? ModelCode,
            string? Platform,
            string? Chipset,
            string? CustomLoaderPath);
    }
}
