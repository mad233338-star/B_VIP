using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using B_VIP.Desktop.Services.Loaders;
using B_VIP.Desktop.ViewModels;
using Xunit;

namespace B_VIP.LoaderTests
{
    /// <summary>
    /// The loader strip is only worth screen space while it has something to say. These tests pin
    /// that rule down: missing/downloading/failed keeps the card, an installed bundle collapses it
    /// and the header pill carries the state in one word.
    /// </summary>
    public sealed class LoaderPanelTests : IDisposable
    {
        private readonly string _root;

        public LoaderPanelTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "bvip-loader-panel-" + Guid.NewGuid().ToString("N"));
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
        public void A_missing_bundle_keeps_the_card_and_the_download_button_on_screen()
        {
            LoaderProvisioner provisioner = new(new HttpClient(new NotFoundHandler()), _root);
            LoaderPanelViewModel panel = new(new LoaderInstallation(LoaderCatalog.Qualcomm, provisioner), "q");

            Assert.True(panel.ShowCard);
            Assert.True(panel.ShowPrepareButton);
            Assert.False(panel.ShowProgressBar);
            Assert.Equal("● Missing", panel.HeaderStatusText);
        }

        [Fact]
        public void An_installed_bundle_collapses_the_card_and_turns_the_pill_green()
        {
            Install(LoaderCatalog.Mtk);

            LoaderProvisioner provisioner = new(new HttpClient(new NotFoundHandler()), _root);
            LoaderPanelViewModel panel = new(new LoaderInstallation(LoaderCatalog.Mtk, provisioner), "mtk");

            Assert.False(panel.ShowCard);
            Assert.Equal("Ready", panel.StatusText);
            Assert.Equal("● Ready", panel.HeaderStatusText);
            Assert.False(panel.ShowProgressBar);
        }

        [Fact]
        public async Task Finishing_the_first_download_collapses_the_card()
        {
            LoaderProvisioner provisioner = new(new HttpClient(new ZipHandler()), _root);
            LoaderPanelViewModel panel = new(new LoaderInstallation(LoaderCatalog.Mtk, provisioner), "mtk");

            Assert.True(panel.ShowCard);

            await panel.EnsureReadyAsync();

            Assert.True(panel.IsReady);
            Assert.False(panel.ShowCard);
            Assert.Equal("● Ready", panel.HeaderStatusText);
            Assert.False(panel.ShowProgressBar);
            Assert.True(panel.ShowPrepareButton);
        }

        private void Install(LoaderPackage package)
        {
            string directory = Path.Combine(_root, package.FolderName);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "payload.bin"), "installed");
        }

        private sealed class NotFoundHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private sealed class ZipHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                using MemoryStream buffer = new();
                using (ZipArchive archive = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
                {
                    ZipArchiveEntry entry = archive.CreateEntry("mtkclient-main/da.bin");
                    using Stream stream = entry.Open();
                    stream.Write(new byte[] { 1, 2, 3 });
                }

                ByteArrayContent body = new(buffer.ToArray());
                body.Headers.ContentLength = buffer.Length;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body });
            }
        }
    }
}
