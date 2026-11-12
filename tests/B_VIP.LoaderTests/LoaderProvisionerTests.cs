using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using B_VIP.Desktop.Services.Loaders;
using Xunit;

namespace B_VIP.LoaderTests
{
    /// <summary>
    /// Provisioning is exercised against a fake handler, so the test covers the real streaming,
    /// extraction and cleanup paths (and the "already installed => no second download" rule)
    /// without reaching the release page.
    /// </summary>
    public sealed class LoaderProvisionerTests : IDisposable
    {
        private readonly string _root;

        public LoaderProvisionerTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "bvip-loader-tests-" + Guid.NewGuid().ToString("N"));
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

        [Fact]
        public async Task A_missing_bundle_is_downloaded_extracted_and_the_archive_is_removed()
        {
            RecordingHandler handler = new(_ => Zip("mtkclient-main/loader.bin", new byte[] { 1, 2, 3, 4 }));
            LoaderProvisioner provisioner = new(new HttpClient(handler), _root);
            LoaderPackage package = LoaderCatalog.Mtk;

            // Another run on this machine may have left an archive behind: only the files this run
            // creates are the provisioner's responsibility.
            HashSet<string> archivesBefore = Directory
                .EnumerateFiles(Path.GetTempPath(), "bvip-mtk-*.zip")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            List<LoaderStatus> stages = new();
            List<int> percents = new();
            RecordingProgress<int> progress = new(percents);
            RecordingProgress<LoaderStatus> statuses = new(stages);

            LoaderProvisionResult result = await provisioner.EnsureAsync(package, progress, statuses);

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(LoaderStatus.Ready, result.Status);
            Assert.True(provisioner.IsReady(package));
            Assert.Contains(stages, s => s == LoaderStatus.Downloading);
            Assert.Contains(stages, s => s == LoaderStatus.Extracting);
            Assert.Equal(LoaderStatus.Ready, stages.Last());

            string extracted = Path.Combine(provisioner.DirectoryFor(package), "mtkclient-main", "loader.bin");
            Assert.True(File.Exists(extracted));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(extracted));

            // The archive is a delivery vehicle only: no *.zip survives in the temp folder for this run.
            Assert.DoesNotContain(
                Directory.EnumerateFiles(Path.GetTempPath(), "bvip-mtk-*.zip"),
                path => !archivesBefore.Contains(path));

            Assert.Contains(100, percents);
            Assert.Equal(1, handler.Requests);
        }

        [Fact]
        public async Task A_bundle_that_is_already_there_is_not_downloaded_again()
        {
            RecordingHandler handler = new(_ => Zip("payload.bin", new byte[] { 7 }));
            LoaderProvisioner provisioner = new(new HttpClient(handler), _root);

            await provisioner.EnsureAsync(LoaderCatalog.Qualcomm);
            await provisioner.EnsureAsync(LoaderCatalog.Qualcomm);

            Assert.Equal(1, handler.Requests);
        }

        [Fact]
        public async Task A_failed_download_reports_error_and_leaves_nothing_ready()
        {
            RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
            LoaderProvisioner provisioner = new(new HttpClient(handler), _root);
            LoaderPackage package = LoaderCatalog.Spreadtrum;

            LoaderProvisionResult result = await provisioner.EnsureAsync(package);

            Assert.False(result.Succeeded);
            Assert.Equal(LoaderStatus.Error, result.Status);
            Assert.False(provisioner.IsReady(package));

            // What the operator reads is a status line, never a folder or a release URL.
            Assert.DoesNotContain(_root, result.Message);
            Assert.DoesNotContain("http", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".zip", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task The_cloud_service_is_asked_before_the_release_asset()
        {
            List<Uri> seen = new();
            RecordingHandler handler = new(request =>
            {
                seen.Add(request.RequestUri!);
                return Zip("mtkclient-main/da.bin", new byte[] { 9 });
            });

            LoaderProvisioner provisioner = new(new HttpClient(handler), _root, null, "https://b-vip.test/");
            LoaderProvisionResult result = await provisioner.EnsureAsync(LoaderCatalog.Mtk);

            Assert.True(result.Succeeded, result.Message);
            Assert.Single(seen);
            Assert.Equal("https://b-vip.test/api/v1/loaders/download/mtk", seen[0].ToString());
        }

        [Fact]
        public async Task A_proxy_that_cannot_deliver_falls_back_to_the_release_asset()
        {
            List<Uri> seen = new();
            RecordingHandler handler = new(request =>
            {
                Uri uri = request.RequestUri!;
                seen.Add(uri);

                return uri.Host == "b-vip.test"
                    ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                    : Zip("Loaders-main/programmer.elf", new byte[] { 3, 3 });
            });

            LoaderProvisioner provisioner = new(new HttpClient(handler), _root, null, "https://b-vip.test");
            LoaderProvisionResult result = await provisioner.EnsureAsync(LoaderCatalog.Qualcomm);

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(2, seen.Count);
            Assert.Equal("https://b-vip.test/api/v1/loaders/download/qualcomm", seen[0].ToString());
            Assert.Contains("/B_VIP-Loaders/releases/download/", seen[1].ToString());
            Assert.True(provisioner.IsReady(LoaderCatalog.Qualcomm));
        }

        [Fact]
        public void The_proxy_address_is_omitted_when_no_service_is_configured()
        {
            Assert.Null(LoaderCatalog.ProxyUrl(null, LoaderCatalog.Mtk));
            Assert.Null(LoaderCatalog.ProxyUrl("  ", LoaderCatalog.Mtk));
            Assert.Equal("https://b-vip.test/api/v1/loaders/download/spd",
                LoaderCatalog.ProxyUrl("https://b-vip.test/", LoaderCatalog.Spreadtrum));
            Assert.Same(LoaderCatalog.Qualcomm, LoaderCatalog.Find("QUALCOMM"));
            Assert.Null(LoaderCatalog.Find("unknown"));
        }

        [Fact]
        public async Task An_empty_archive_is_not_mistaken_for_an_installed_bundle()
        {
            RecordingHandler handler = new(_ => Zip("readme.txt", Array.Empty<byte>(), omitFile: true));
            LoaderProvisioner provisioner = new(new HttpClient(handler), _root);

            LoaderProvisionResult result = await provisioner.EnsureAsync(LoaderCatalog.Mtk);

            Assert.False(result.Succeeded);
            Assert.False(provisioner.IsReady(LoaderCatalog.Mtk));
        }

        [Fact]
        public async Task The_installation_mirrors_the_stages_and_ends_ready()
        {
            RecordingHandler handler = new(_ => Zip("mtkclient-main/da.bin", new byte[] { 5, 5, 5 }));
            LoaderProvisioner provisioner = new(new HttpClient(handler), _root);
            LoaderInstallation installation = new(LoaderCatalog.Mtk, provisioner);

            Assert.Equal(LoaderStatus.Idle, installation.Status);

            List<LoaderStatus> seen = new();
            installation.Changed += (_, _) => seen.Add(installation.Status);

            bool ready = await installation.EnsureAsync();

            Assert.True(ready);
            Assert.True(installation.IsReady);
            Assert.Equal(100, installation.Progress);
            Assert.Contains(LoaderStatus.Downloading, seen);
            Assert.Equal(LoaderStatus.Ready, installation.Status);
        }

        [Fact]
        public void The_catalog_points_every_platform_at_its_own_folder_and_release_asset()
        {
            Assert.Equal(3, LoaderCatalog.All.Count);
            Assert.Equal(3, LoaderCatalog.All.Select(p => p.FolderName).Distinct().Count());

            Assert.Equal("Qualcomm", LoaderCatalog.Qualcomm.FolderName);
            Assert.Equal("MTK", LoaderCatalog.Mtk.FolderName);
            Assert.Equal("SPD", LoaderCatalog.Spreadtrum.FolderName);

            Assert.EndsWith("/Loaders-main.zip", LoaderCatalog.Qualcomm.DownloadUrl);
            Assert.EndsWith("/mtkclient-main.zip", LoaderCatalog.Mtk.DownloadUrl);
            Assert.EndsWith("/SPD_Loaders.zip.zip", LoaderCatalog.Spreadtrum.DownloadUrl);

            Assert.All(LoaderCatalog.All, package =>
            {
                Assert.StartsWith("https://", package.DownloadUrl);
                Assert.Contains("/B_VIP-Loaders/releases/download/", package.DownloadUrl);
            });
        }

        [Fact]
        public async Task A_slow_payload_is_not_cut_off_by_the_connect_timeout()
        {
            // Headers arrive immediately; the 154 MB archive then streams for minutes. A connect
            // timeout of a few hundred milliseconds must not abort the transfer.
            LoaderTimeoutPolicy timeouts = new(
                Connect: TimeSpan.FromMilliseconds(150),
                Download: TimeSpan.FromSeconds(30),
                Idle: TimeSpan.FromSeconds(5));

            byte[] archive = ZipBytes("Loaders-main/programmer.elf", new byte[96]);
            RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ChunkedContent(archive, chunkSize: 24, delay: TimeSpan.FromMilliseconds(250))
            });

            LoaderProvisioner provisioner = new(new HttpClient(handler), _root, null, null, timeouts);
            LoaderProvisionResult result = await provisioner.EnsureAsync(LoaderCatalog.Qualcomm);

            Assert.True(result.Succeeded, result.Message);
            Assert.True(provisioner.IsReady(LoaderCatalog.Qualcomm));
        }

        [Fact]
        public async Task A_stalled_payload_times_out_and_reports_a_retryable_error()
        {
            LoaderTimeoutPolicy timeouts = new(
                Connect: TimeSpan.FromSeconds(5),
                Download: TimeSpan.FromMinutes(5),
                Idle: TimeSpan.FromMilliseconds(150));

            RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ChunkedContent(ZipBytes("mtkclient-main/da.bin", new byte[16]),
                    chunkSize: 8, delay: TimeSpan.Zero, startDelay: TimeSpan.FromSeconds(5))
            });

            LoaderProvisioner provisioner = new(new HttpClient(handler), _root, null, null, timeouts);
            LoaderProvisionResult result = await provisioner.EnsureAsync(LoaderCatalog.Mtk);

            Assert.False(result.Succeeded);
            Assert.Equal(LoaderStatus.Error, result.Status);
            Assert.Contains("timed out", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(provisioner.IsReady(LoaderCatalog.Mtk));
        }

        [Fact]
        public async Task A_host_that_never_answers_the_headers_fails_on_the_connect_timeout()
        {
            LoaderTimeoutPolicy timeouts = new(
                Connect: TimeSpan.FromMilliseconds(120),
                Download: TimeSpan.FromMinutes(5),
                Idle: TimeSpan.FromSeconds(5));

            SlowHeaderHandler handler = new(TimeSpan.FromSeconds(5));
            LoaderProvisioner provisioner = new(new HttpClient(handler), _root, null, null, timeouts);

            LoaderProvisionResult result = await provisioner.EnsureAsync(LoaderCatalog.Spreadtrum);

            Assert.False(result.Succeeded);
            Assert.Equal(LoaderStatus.Error, result.Status);
            Assert.Contains("timed out", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        private static HttpResponseMessage Zip(string entryName, byte[] content, bool omitFile = false)
        {
            byte[] bytes = omitFile ? ZipBytesEmpty() : ZipBytes(entryName, content);

            ByteArrayContent body = new(bytes);
            body.Headers.ContentLength = bytes.Length;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = body };
        }

        private static byte[] ZipBytes(string entryName, byte[] content)
        {
            using MemoryStream buffer = new();
            using (ZipArchive archive = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                ZipArchiveEntry entry = archive.CreateEntry(entryName);
                using Stream stream = entry.Open();
                stream.Write(content);
            }

            return buffer.ToArray();
        }

        private static byte[] ZipBytesEmpty()
        {
            using MemoryStream buffer = new();
            using (new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
            }

            return buffer.ToArray();
        }

        private sealed class RecordingHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

            public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

            public int Requests { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests++;
                return Task.FromResult(_responder(request));
            }
        }

        /// <summary>Answers after a delay, honouring the caller's cancellation token.</summary>
        private sealed class SlowHeaderHandler : HttpMessageHandler
        {
            private readonly TimeSpan _delay;

            public SlowHeaderHandler(TimeSpan delay) => _delay = delay;

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                await Task.Delay(_delay, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
            }
        }

        /// <summary>
        /// A response body that hands out small chunks with a pause between them, so a test can show
        /// that streaming is governed by the inactivity window instead of the connect timeout.
        /// </summary>
        private sealed class ChunkedContent : HttpContent
        {
            private readonly byte[] _payload;
            private readonly int _chunkSize;
            private readonly TimeSpan _delay;
            private readonly TimeSpan _startDelay;

            public ChunkedContent(byte[] payload, int chunkSize, TimeSpan delay, TimeSpan startDelay = default)
            {
                _payload = payload;
                _chunkSize = chunkSize;
                _delay = delay;
                _startDelay = startDelay;
            }

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
                => throw new NotSupportedException();

            protected override bool TryComputeLength(out long length)
            {
                length = _payload.Length;
                return true;
            }

            protected override Task<Stream> CreateContentReadStreamAsync()
                => Task.FromResult<Stream>(new ChunkedStream(_payload, _chunkSize, _delay, _startDelay));
        }

        private sealed class ChunkedStream : Stream
        {
            private readonly byte[] _payload;
            private readonly int _chunkSize;
            private readonly TimeSpan _delay;
            private readonly TimeSpan _startDelay;
            private int _sent;

            public ChunkedStream(byte[] payload, int chunkSize, TimeSpan delay, TimeSpan startDelay)
            {
                _payload = payload;
                _chunkSize = chunkSize;
                _delay = delay;
                _startDelay = startDelay;
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => _payload.Length;

            public override long Position
            {
                get => _sent;
                set => throw new NotSupportedException();
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_sent >= _payload.Length)
                {
                    return 0;
                }

                if (_sent == 0 && _startDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_startDelay, cancellationToken);
                }

                if (_delay > TimeSpan.Zero)
                {
                    await Task.Delay(_delay, cancellationToken);
                }

                int count = Math.Min(Math.Min(buffer.Length, _chunkSize), _payload.Length - _sent);
                _payload.AsSpan(_sent, count).CopyTo(buffer.Span);
                _sent += count;
                return count;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override int Read(byte[] buffer, int offset, int count)
                => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private sealed class RecordingProgress<T> : IProgress<T>
        {
            private readonly List<T> _values;

            public RecordingProgress(List<T> values) => _values = values;

            public void Report(T value) => _values.Add(value);
        }
    }
}
