using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using B_VIP.Desktop.Services.Catalog;
using Xunit;

namespace B_VIP.LoaderTests
{
    /// <summary>
    /// The catalog decides which engine a vendor tab drives, so the parsing, filtering and loader
    /// resolution are exercised against real files instead of the shipped Data/devices.json.
    /// </summary>
    public sealed class DeviceCatalogTests : IDisposable
    {
        private readonly string _root;

        public DeviceCatalogTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "bvip-catalog-tests-" + Guid.NewGuid().ToString("N"));
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
                // Best effort.
            }
        }

        private string WriteCatalog(string json)
        {
            string path = Path.Combine(_root, "devices.json");
            File.WriteAllText(path, json);
            return path;
        }

        [Fact]
        public async Task LoadAsync_ParsesTheCatalogAndExposesBrandsInKnownOrder()
        {
            string path = WriteCatalog("""
            [
              { "brand": "Tecno", "marketingName": "Spark 20", "modelCode": "KJ5", "platform": "MediaTek", "chipset": "MT6769", "customLoaderPath": "" },
              { "brand": "Samsung", "marketingName": "Galaxy A06", "modelCode": "SM-A065F", "platform": "mediatek", "chipset": "MT6769", "customLoaderPath": "Loaders/MediaTek/samsung_da.bin" },
              { "brand": "Xiaomi", "marketingName": "Redmi Note 13", "modelCode": "23124RA7EO", "platform": "MTK", "chipset": "MT6833", "customLoaderPath": "" }
            ]
            """);

            DeviceCatalogService catalog = new(path, _root);
            var devices = await catalog.LoadAsync();

            Assert.Equal(3, devices.Count);
            Assert.Equal(new[] { "Samsung", "Xiaomi", "Tecno" }, catalog.Brands);
            Assert.Equal("MediaTek", devices.Single(d => d.Brand == "Samsung").Platform);
            Assert.Equal("MediaTek", devices.Single(d => d.Brand == "Xiaomi").Platform);
            Assert.Null(catalog.Warning);
        }

        [Fact]
        public async Task Search_MatchesMarketingNameModelCodeAndChipset()
        {
            string path = WriteCatalog("""
            [
              { "brand": "Samsung", "marketingName": "Galaxy A06", "modelCode": "SM-A065F", "platform": "MediaTek", "chipset": "MT6769" },
              { "brand": "Samsung", "marketingName": "Galaxy A34 5G", "modelCode": "SM-A346B", "platform": "MediaTek", "chipset": "MT6877" },
              { "brand": "Infinix", "marketingName": "Hot 30", "modelCode": "X6831", "platform": "MediaTek", "chipset": "MT6769" }
            ]
            """);

            DeviceCatalogService catalog = new(path, _root);
            await catalog.LoadAsync();

            Assert.Equal("Galaxy A06", Assert.Single(catalog.Search("Samsung", "a06")).MarketingName);
            Assert.Equal("Galaxy A34 5G", Assert.Single(catalog.Search(null, "A346B")).MarketingName);
            Assert.Equal(2, catalog.Search("Samsung", null).Count);
            Assert.Equal(2, catalog.Search("All brands", "MT6769").Count);
            Assert.Empty(catalog.Search("Samsung", "spark"));
        }

        [Fact]
        public async Task LoadAsync_MissingFile_ReportsAWarningInsteadOfThrowing()
        {
            DeviceCatalogService catalog = new(Path.Combine(_root, "absent.json"), _root);

            var devices = await catalog.LoadAsync();

            Assert.Empty(devices);
            Assert.NotNull(catalog.Warning);
        }

        [Fact]
        public async Task LoadAsync_DropsIncompleteRows()
        {
            string path = WriteCatalog("""
            [
              { "brand": "Samsung", "marketingName": "Galaxy A05", "modelCode": "SM-A055F", "platform": "MediaTek" },
              { "brand": "", "marketingName": "No brand", "modelCode": "X" },
              { "brand": "Tecno", "marketingName": "" }
            ]
            """);

            DeviceCatalogService catalog = new(path, _root);
            var devices = await catalog.LoadAsync();

            Assert.Equal("Galaxy A05", Assert.Single(devices).MarketingName);
        }

        [Fact]
        public async Task ShippedCatalog_IsCompleteForEveryVendorTab()
        {
            // The catalog is copied next to the test assembly exactly as it is copied next to the app.
            DeviceCatalogService catalog = new(Path.Combine(AppContext.BaseDirectory, "Data", "devices.json"));
            var devices = await catalog.LoadAsync();

            Assert.Null(catalog.Warning);
            Assert.True(devices.Count >= 300, $"Only {devices.Count} models in the shipped catalog.");
            Assert.All(devices, d => Assert.False(string.IsNullOrWhiteSpace(d.ModelCode), $"{d.MarketingName} has no model code."));
            Assert.All(devices, d => Assert.Contains(d.Platform, new[] { "MediaTek", "Qualcomm", "Spreadtrum", "Fastboot", "Exynos" }));

            // Every vendor tab must have a real lineup, not a couple of placeholder handsets.
            foreach (string brand in DeviceCatalogService.KnownBrands)
            {
                Assert.True(devices.Count(d => d.Brand == brand) >= 20, $"{brand} has only {devices.Count(d => d.Brand == brand)} models.");
            }

            // The catalog is deduplicated on the bootloader model code.
            Assert.Equal(devices.Count, devices.Select(d => d.ModelCode.ToUpperInvariant()).Distinct().Count());
        }

        [Theory]
        [InlineData("MediaTek", "MediaTek")]
        [InlineData("mtk", "MediaTek")]
        [InlineData("Dimensity 1080", "MediaTek")]
        [InlineData("Qualcomm", "Qualcomm")]
        [InlineData("SM6225", "Qualcomm")]
        [InlineData("Spreadtrum", "Spreadtrum")]
        [InlineData("SC9863A", "Spreadtrum")]
        [InlineData("Fastboot", "Fastboot")]
        [InlineData("Exynos", "Exynos")]
        [InlineData("Exynos 1380", "Exynos")]
        [InlineData("", "Fastboot")]
        public void NormalisePlatform_MapsEverySpellingOntoTheRoutingVocabulary(string input, string expected)
            => Assert.Equal(expected, DeviceCatalogService.NormalisePlatform(input));

        [Fact]
        public async Task ResolveLoader_ReturnsOnlyFilesThatExist()
        {
            string loader = Path.Combine(_root, "Loaders", "MediaTek");
            Directory.CreateDirectory(loader);
            File.WriteAllText(Path.Combine(loader, "samsung_da.bin"), "da");

            string path = WriteCatalog("""
            [
              { "brand": "Samsung", "marketingName": "Galaxy A04", "modelCode": "SM-A045F", "platform": "MediaTek", "chipset": "MT6765", "customLoaderPath": "Loaders/MediaTek/samsung_da.bin" },
              { "brand": "Samsung", "marketingName": "Galaxy A05s", "modelCode": "SM-A057F", "platform": "Qualcomm", "chipset": "SM6225", "customLoaderPath": "Loaders/Qualcomm/prog_firehose_sm6225.elf" }
            ]
            """);

            DeviceCatalogService catalog = new(path, _root);
            var devices = await catalog.LoadAsync();

            Assert.Equal(Path.Combine(loader, "samsung_da.bin"), catalog.ResolveLoader(devices.Single(d => d.ModelCode == "SM-A045F")));
            Assert.Null(catalog.ResolveLoader(devices.Single(d => d.ModelCode == "SM-A057F")));
            Assert.Null(catalog.ResolveLoader(null));
        }
    }
}
