using System.IO;

namespace B_VIP.Desktop.Services.Loaders
{
    /// <summary>
    /// Unisoc/SPD side of the loader pipeline. It has two independent sources for the FDL stages:
    /// the <c>Loaders\SPD</c> bundle published with the release (SC9863A binaries and spd_dump) and
    /// a firmware <c>.pac</c> the operator selects, whose own FDL1/FDL2 are extracted on the fly.
    /// </summary>
    public sealed class SpreadtrumService
    {
        public SpreadtrumService(LoaderProvisioner provisioner)
        {
            Loaders = new LoaderInstallation(LoaderCatalog.Spreadtrum, provisioner);
        }

        /// <summary>Download state of the SPD bundle.</summary>
        public LoaderInstallation Loaders { get; }

        /// <summary><c>Loaders\SPD</c>.</summary>
        public string Directory => Loaders.Directory;

        /// <summary><c>%TEMP%\BVIP_SPD</c>, where PAC payloads are unpacked.</summary>
        public string WorkDirectory => PacExtractor.DefaultWorkDirectory;

        /// <summary>
        /// Parses the selected PAC and writes its FDL1/FDL2 into <see cref="WorkDirectory"/>. The
        /// returned paths are handed straight to the boot-ROM upload routine.
        /// </summary>
        public Task<PacExtractionResult> ExtractLoadersAsync(string pacPath, CancellationToken ct = default)
        {
            // Parsing a multi-hundred-megabyte packet reads the header table and two payloads:
            // keep that off the UI thread.
            return Task.Run(() => PacExtractor.ExtractLoaders(pacPath, WorkDirectory, ct), ct);
        }

        /// <summary>The bundler's <c>spd_dump</c> executable, when the release shipped one.</summary>
        public string? FindSpdDump()
        {
            try
            {
                if (!System.IO.Directory.Exists(Directory))
                {
                    return null;
                }

                return System.IO.Directory
                    .EnumerateFiles(Directory, "spd_dump*", SearchOption.AllDirectories)
                    .FirstOrDefault();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
