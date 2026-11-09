using System.IO;

namespace B_VIP.Desktop.Services.Loaders
{
    /// <summary>
    /// MediaTek side of the loader pipeline: keeps <see cref="LoaderCatalog.Mtk"/> provisioned in
    /// <c>Loaders\MTK</c> and exposes the mtkclient assets found there.
    /// </summary>
    public sealed class MtkLoaderManager
    {
        public MtkLoaderManager(LoaderProvisioner provisioner)
        {
            Loaders = new LoaderInstallation(LoaderCatalog.Mtk, provisioner);
        }

        /// <summary>Download state of this chipset's bundle.</summary>
        public LoaderInstallation Loaders { get; }

        /// <summary><c>Loaders\MTK</c>.</summary>
        public string Directory => Loaders.Directory;

        /// <summary>
        /// Loader payloads (preloader / DA images) found in the bundle. They are listed for the
        /// operator; the BROM handshake itself is done by the module.
        /// </summary>
        public IReadOnlyList<string> FindPayloads()
        {
            try
            {
                if (!System.IO.Directory.Exists(Directory))
                {
                    return Array.Empty<string>();
                }

                return System.IO.Directory
                    .EnumerateFiles(Directory, "*.bin", SearchOption.AllDirectories)
                    .Take(50)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
        }
    }
}
