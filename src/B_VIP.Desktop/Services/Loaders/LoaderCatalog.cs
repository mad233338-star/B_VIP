namespace B_VIP.Desktop.Services.Loaders
{
    /// <summary>
    /// One downloadable loader bundle: where it is published, which folder it belongs in and the
    /// name of the archive that is thrown away once its contents are on disk.
    /// </summary>
    public sealed record LoaderPackage(
        string Id,
        string FolderName,
        string DisplayName,
        string DownloadUrl,
        string ArchiveFileName,
        string ExpectedContent);

    /// <summary>
    /// The single source of truth for the three chipset loader bundles. They are published as
    /// release assets of the B_VIP-Loaders repository, so the desktop client can provision a
    /// workshop computer without the operator hunting for the right archive.
    /// </summary>
    public static class LoaderCatalog
    {
        /// <summary>Sub-folder of the application directory that holds every provisioned bundle.</summary>
        public const string RootFolderName = "Loaders";

        /// <summary>Release the three archives come from (the direct fallback when no proxy answers).</summary>
        public const string ReleasePage = "https://github.com/mad233338-star/B_VIP-Loaders/releases/tag/v1.0";

        /// <summary>
        /// Route on the cloud service that streams a release archive to the client. The release CDN
        /// is blocked or throttled from some regions, so the service is tried first and the release
        /// URL only afterwards.
        /// </summary>
        public const string ProxyRoute = "/api/v1/loaders/download/";

        private const string ReleaseDownload = "https://github.com/mad233338-star/B_VIP-Loaders/releases/download/v1.0/";

        /// <summary>
        /// The proxy address for <paramref name="package"/>, or null when no cloud service is
        /// configured (the release asset is then used directly).
        /// </summary>
        public static string? ProxyUrl(string? baseUrl, LoaderPackage package)
        {
            ArgumentNullException.ThrowIfNull(package);

            string root = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            return root.Length == 0 ? null : root + ProxyRoute + package.Id;
        }

        /// <summary>Qualcomm programmers and Firehose loaders.</summary>
        public static LoaderPackage Qualcomm { get; } = new(
            "qualcomm",
            "Qualcomm",
            "Qualcomm loaders (Sahara / Firehose programmers)",
            ReleaseDownload + "Loaders-main.zip",
            "Loaders-main.zip",
            "Firehose programmer binaries");

        /// <summary>mtkclient: the MediaTek boot-ROM download agent and its loaders.</summary>
        public static LoaderPackage Mtk { get; } = new(
            "mtk",
            "MTK",
            "MediaTek loaders (mtkclient download agent)",
            ReleaseDownload + "mtkclient-main.zip",
            "mtkclient-main.zip",
            "mtkclient / BROM payloads");

        /// <summary>Unisoc/SPD loaders: SC9863A binaries and the spd_dump tool.</summary>
        public static LoaderPackage Spreadtrum { get; } = new(
            "spd",
            "SPD",
            "Spreadtrum loaders (SC9863A binaries, spd_dump)",
            ReleaseDownload + "SPD_Loaders.zip.zip",
            "SPD_Loaders.zip.zip",
            "spd_dump and SC9863A loader images");

        public static IReadOnlyList<LoaderPackage> All { get; } = [Qualcomm, Mtk, Spreadtrum];

        /// <summary>Looks a package up by its route id (<c>qualcomm</c>, <c>mtk</c>, <c>spd</c>).</summary>
        public static LoaderPackage? Find(string? id)
            => All.FirstOrDefault(package => string.Equals(package.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
