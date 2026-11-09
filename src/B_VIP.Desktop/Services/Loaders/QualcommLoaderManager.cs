using System.IO;

namespace B_VIP.Desktop.Services.Loaders
{
    /// <summary>
    /// Qualcomm side of the loader pipeline: keeps <see cref="LoaderCatalog.Qualcomm"/> provisioned in
    /// <c>Loaders\Qualcomm</c> and lists the programmer binaries a Firehose session can use.
    /// </summary>
    public sealed class QualcommLoaderManager
    {
        private static readonly string[] ProgrammerExtensions = [".elf", ".mbn", ".bin"];

        public QualcommLoaderManager(LoaderProvisioner provisioner)
        {
            Loaders = new LoaderInstallation(LoaderCatalog.Qualcomm, provisioner);
        }

        /// <summary>Download state of this chipset's bundle.</summary>
        public LoaderInstallation Loaders { get; }

        /// <summary><c>Loaders\Qualcomm</c>.</summary>
        public string Directory => Loaders.Directory;

        /// <summary>
        /// Programmer images the bundle ships (e.g. <c>prog_firehose_*.elf</c>), newest first. The
        /// screen offers the first one so the operator does not have to hunt for it.
        /// </summary>
        public IReadOnlyList<string> FindProgrammers()
        {
            try
            {
                if (!System.IO.Directory.Exists(Directory))
                {
                    return Array.Empty<string>();
                }

                return System.IO.Directory
                    .EnumerateFiles(Directory, "*", SearchOption.AllDirectories)
                    .Where(path => ProgrammerExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
        }
    }
}
