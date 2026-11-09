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

        /// <summary>
        /// A stalled release CDN must surface as an error instead of an endless progress bar, so
        /// every attempt is abandoned after this long.
        /// </summary>
        private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(30);

        private readonly HttpClient _http;
        private readonly Action<string>? _log;
        private readonly string? _downloadBaseUrl;

        public LoaderProvisioner(
            HttpClient http,
            string rootDirectory,
            Action<string>? log = null,
            string? downloadBaseUrl = null)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            RootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
                ? DefaultRootDirectory
                : Path.GetFullPath(rootDirectory);
            _log = log;
            _downloadBaseUrl = string.IsNullOrWhiteSpace(downloadBaseUrl) ? null : downloadBaseUrl.Trim();
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

                DownloadOutcome download = await DownloadAsync(package, archivePath, progress, ct).ConfigureAwait(false);
                if (download != DownloadOutcome.Downloaded)
                {
                    stages?.Report(LoaderStatus.Error);
                    return new LoaderProvisionResult(false, LoaderStatus.Error, targetDirectory,
                        download == DownloadOutcome.TimedOut
                            ? "The loader download timed out. Retry to start it again."
                            : "The loader archive could not be fetched. Check the connection and retry.");
                }

                _log?.Invoke($"Extracting {package.ArchiveFileName}.");
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
                // The raw message can name a host or a folder, neither of which belongs on screen.
                _log?.Invoke("Loader provisioning failed.");
                stages?.Report(LoaderStatus.Error);
                return new LoaderProvisionResult(false, LoaderStatus.Error, targetDirectory,
                    "The loader archive could not be installed. Retry to try again.");
            }
            finally
            {
                // The archive is only a delivery vehicle: the extracted folder is what is kept.
                TryDelete(archivePath);
            }
        }

        /// <summary>How one provisioning download ended.</summary>
        private enum DownloadOutcome
        {
            Downloaded,
            Failed,
            TimedOut
        }

        /// <summary>
        /// Streams the archive, trying the cloud service first and the published release asset only
        /// when the service cannot deliver it. Every attempt is bounded by <see cref="AttemptTimeout"/>,
        /// so an ISP block on the release CDN becomes a retryable error instead of a frozen bar.
        /// </summary>
        private async Task<DownloadOutcome> DownloadAsync(
            LoaderPackage package, string archivePath, IProgress<int>? progress, CancellationToken ct)
        {
            bool timedOut = false;

            foreach (Uri source in SourcesFor(package))
            {
                progress?.Report(-1);

                try
                {
                    if (await DownloadFromAsync(source, archivePath, progress, ct).ConfigureAwait(false))
                    {
                        return DownloadOutcome.Downloaded;
                    }

                    _log?.Invoke("A loader download source did not answer; trying the next one.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    // The linked timeout fired, not the operator's cancel.
                    timedOut = true;
                    _log?.Invoke("A loader download timed out; trying the next source.");
                }
                catch (HttpRequestException)
                {
                    _log?.Invoke("A loader download source could not be reached; trying the next one.");
                }
                catch (IOException)
                {
                    _log?.Invoke("A loader download stopped early; trying the next source.");
                }
            }

            return timedOut ? DownloadOutcome.TimedOut : DownloadOutcome.Failed;
        }

        /// <summary>The cloud service first, then the release asset it mirrors.</summary>
        private IEnumerable<Uri> SourcesFor(LoaderPackage package)
        {
            string? proxyUrl = LoaderCatalog.ProxyUrl(_downloadBaseUrl, package);
            if (proxyUrl is not null && Uri.TryCreate(proxyUrl, UriKind.Absolute, out Uri? proxy))
            {
                yield return proxy;
            }

            if (Uri.TryCreate(package.DownloadUrl, UriKind.Absolute, out Uri? direct))
            {
                yield return direct;
            }
        }

        private async Task<bool> DownloadFromAsync(
            Uri source, string archivePath, IProgress<int>? progress, CancellationToken ct)
        {
            using CancellationTokenSource attempt =
                CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(AttemptTimeout);

            using HttpResponseMessage response = await _http
                .GetAsync(source, HttpCompletionOption.ResponseHeadersRead, attempt.Token)
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

            await using Stream body = await response.Content.ReadAsStreamAsync(attempt.Token).ConfigureAwait(false);
            await using FileStream destination = new(archivePath, FileMode.Create, FileAccess.Write, FileShare.None,
                BufferSize, useAsync: true);

            byte[] buffer = new byte[BufferSize];
            int read;
            while ((read = await body.ReadAsync(buffer, attempt.Token).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), attempt.Token).ConfigureAwait(false);
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
