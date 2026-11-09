using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace B_VIP.Desktop.Services.Loaders
{
    /// <summary>Lifecycle of one loader bundle on this computer.</summary>
    public enum LoaderStatus
    {
        /// <summary>Nothing has happened yet on this screen.</summary>
        Idle,

        /// <summary>The archive is being streamed from the release page.</summary>
        Downloading,

        /// <summary>The archive is on disk and its contents are being unpacked.</summary>
        Extracting,

        /// <summary>The bundle is on disk and the chipset screen can use it.</summary>
        Ready,

        /// <summary>The bundle could not be provisioned; the detail carries the reason.</summary>
        Error
    }

    /// <summary>Outcome of one provisioning attempt.</summary>
    public sealed record LoaderProvisionResult(bool Succeeded, LoaderStatus Status, string Directory, string Message);

    /// <summary>
    /// Downloads, unpacks and cleans up a loader bundle.
    ///
    /// The bundle lives under <c>Loaders/&lt;Chipset&gt;</c> next to the executable, so a portable
    /// copy of the tool carries its loaders with it. Nothing is downloaded twice: a folder that
    /// already holds files counts as provisioned, which keeps a workshop computer offline-capable
    /// after the first run.
    /// </summary>
    public sealed class LoaderProvisioner
    {
        private const int BufferSize = 81920;

        private readonly HttpClient _http;
        private readonly Action<string>? _log;

        public LoaderProvisioner(HttpClient http, string rootDirectory, Action<string>? log = null)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            RootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
                ? DefaultRootDirectory
                : Path.GetFullPath(rootDirectory);
            _log = log;
        }

        /// <summary>The <c>Loaders</c> folder beside the executable.</summary>
        public static string DefaultRootDirectory =>
            Path.Combine(AppContext.BaseDirectory, LoaderCatalog.RootFolderName);

        /// <summary>Root of every provisioned bundle on this computer.</summary>
        public string RootDirectory { get; }

        /// <summary>The folder a package unpacks into, e.g. <c>Loaders\MTK</c>.</summary>
        public string DirectoryFor(LoaderPackage package)
        {
            ArgumentNullException.ThrowIfNull(package);
            return Path.Combine(RootDirectory, package.FolderName);
        }

        /// <summary>
        /// True when the folder already holds at least one file. A folder that exists but is empty
        /// (an interrupted extraction) is treated as missing so the download happens again.
        /// </summary>
        public bool IsReady(LoaderPackage package)
        {
            string directory = DirectoryFor(package);

            try
            {
                return Directory.Exists(directory) &&
                       Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// Ensures the bundle is on disk: reuse what is there, otherwise stream the archive with
        /// <paramref name="progress"/> (0-100, or -1 when the server sends no length), unpack it
        /// into the chipset folder and delete the temporary archive.
        /// </summary>
        public async Task<LoaderProvisionResult> EnsureAsync(
            LoaderPackage package,
            IProgress<int>? progress = null,
            IProgress<LoaderStatus>? stages = null,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(package);

            string targetDirectory = DirectoryFor(package);

            if (IsReady(package))
            {
                stages?.Report(LoaderStatus.Ready);
                progress?.Report(100);
                return new LoaderProvisionResult(true, LoaderStatus.Ready, targetDirectory,
                    $"{package.DisplayName} are already installed.");
            }

            string archivePath = Path.Combine(Path.GetTempPath(),
                $"bvip-{package.Id}-{Guid.NewGuid():N}-{package.ArchiveFileName}");

            try
            {
                Directory.CreateDirectory(RootDirectory);

                _log?.Invoke($"Downloading {package.DisplayName}.");
                stages?.Report(LoaderStatus.Downloading);

                bool downloaded = await DownloadAsync(package, archivePath, progress, ct).ConfigureAwait(false);
                if (!downloaded)
                {
                    stages?.Report(LoaderStatus.Error);
                    return new LoaderProvisionResult(false, LoaderStatus.Error, targetDirectory,
                        "The loader archive could not be downloaded from the release page.");
                }

                _log?.Invoke($"Extracting {package.ArchiveFileName} into {targetDirectory}.");
                stages?.Report(LoaderStatus.Extracting);
                progress?.Report(-1);

                // Extracting over an older copy keeps the bundle consistent with the release.
                Directory.CreateDirectory(targetDirectory);
                await Task.Run(() => ZipFile.ExtractToDirectory(archivePath, targetDirectory, overwriteFiles: true), ct)
                    .ConfigureAwait(false);

                if (!IsReady(package))
                {
                    stages?.Report(LoaderStatus.Error);
                    return new LoaderProvisionResult(false, LoaderStatus.Error, targetDirectory,
                        "The loader archive was empty.");
                }

                stages?.Report(LoaderStatus.Ready);
                progress?.Report(100);

                return new LoaderProvisionResult(true, LoaderStatus.Ready, targetDirectory,
                    $"{package.DisplayName} are ready.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                stages?.Report(LoaderStatus.Idle);
                return new LoaderProvisionResult(false, LoaderStatus.Idle, targetDirectory, "Download cancelled.");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                or InvalidDataException or NotSupportedException)
            {
                _log?.Invoke($"Loader provisioning failed: {ex.Message}");
                stages?.Report(LoaderStatus.Error);
                return new LoaderProvisionResult(false, LoaderStatus.Error, targetDirectory, ex.Message);
            }
            finally
            {
                // The archive is only a delivery vehicle: the extracted folder is what is kept.
                TryDelete(archivePath);
            }
        }

        private async Task<bool> DownloadAsync(
            LoaderPackage package, string archivePath, IProgress<int>? progress, CancellationToken ct)
        {
            using HttpResponseMessage response = await _http
                .GetAsync(new Uri(package.DownloadUrl), HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _log?.Invoke($"Loader archive answered HTTP {(int)response.StatusCode}.");
                progress?.Report(-1);
                return false;
            }

            long? total = response.Content.Headers.ContentLength;
            long written = 0;
            int lastReported = -1;

            await using Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using FileStream destination = new(archivePath, FileMode.Create, FileAccess.Write, FileShare.None,
                BufferSize, useAsync: true);

            byte[] buffer = new byte[BufferSize];
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                written += read;

                if (total is > 0)
                {
                    int percent = (int)Math.Clamp(written * 100 / total.Value, 0, 100);
                    if (percent != lastReported)
                    {
                        lastReported = percent;
                        progress?.Report(percent);
                    }
                }
                else if (lastReported < 0)
                {
                    // No Content-Length (chunked reply): the bar shows indeterminate progress.
                    lastReported = -1;
                    progress?.Report(-1);
                }
            }

            progress?.Report(total is > 0 ? 100 : -1);
            return written > 0;
        }

        private void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.Invoke($"The temporary loader archive could not be removed: {ex.Message}");
            }
        }
    }
}
