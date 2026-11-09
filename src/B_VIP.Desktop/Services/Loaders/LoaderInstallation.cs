namespace B_VIP.Desktop.Services.Loaders
{
    /// <summary>
    /// One chipset bundle as this computer sees it: the current stage, the download percentage and a
    /// line of text for the screen. Every change raises <see cref="Changed"/>; the screen marshals
    /// that to the UI thread before touching its bound properties.
    /// </summary>
    public sealed class LoaderInstallation
    {
        private readonly LoaderProvisioner _provisioner;
        private readonly object _gate = new();

        private LoaderStatus _status;
        private int _progress = -1;
        private string _detail;

        public LoaderInstallation(LoaderPackage package, LoaderProvisioner provisioner)
        {
            Package = package ?? throw new ArgumentNullException(nameof(package));
            _provisioner = provisioner ?? throw new ArgumentNullException(nameof(provisioner));
            _status = provisioner.IsReady(package) ? LoaderStatus.Ready : LoaderStatus.Idle;
            _detail = _status == LoaderStatus.Ready
                ? $"{package.DisplayName} are installed."
                : "Not installed yet.";
        }

        public LoaderPackage Package { get; }

        /// <summary>Folder the bundle lives in (or will live in).</summary>
        public string Directory => _provisioner.DirectoryFor(Package);

        public LoaderStatus Status
        {
            get { lock (_gate) { return _status; } }
        }

        /// <summary>0-100, or -1 while the total size is unknown.</summary>
        public int Progress
        {
            get { lock (_gate) { return _progress; } }
        }

        public string Detail
        {
            get { lock (_gate) { return _detail; } }
        }

        public bool IsReady => Status == LoaderStatus.Ready;

        public bool IsBusy => Status is LoaderStatus.Downloading or LoaderStatus.Extracting;

        /// <summary>Raised after every state change, from whichever thread produced it.</summary>
        public event EventHandler? Changed;

        /// <summary>Re-reads the folder, so a bundle copied in by hand is picked up too.</summary>
        public void Refresh()
        {
            if (_provisioner.IsReady(Package))
            {
                Set(LoaderStatus.Ready, 100, $"{Package.DisplayName} are installed.");
            }
            else
            {
                Set(LoaderStatus.Idle, -1, "Not installed yet.");
            }
        }

        /// <summary>
        /// Provisions the bundle when it is missing and returns whether it is usable afterwards.
        /// A bundle already on disk is reported as ready without touching the network.
        /// </summary>
        public async Task<bool> EnsureAsync(CancellationToken ct = default)
        {
            if (_provisioner.IsReady(Package))
            {
                Set(LoaderStatus.Ready, 100, $"{Package.DisplayName} are installed.");
                return true;
            }

            LoaderProvisionResult result = await _provisioner.EnsureAsync(
                Package,
                new CallbackProgress<int>(percent =>
                {
                    if (Status == LoaderStatus.Downloading)
                    {
                        Set(LoaderStatus.Downloading, percent, DescribeDownload(percent));
                    }
                }),
                new CallbackProgress<LoaderStatus>(stage =>
                {
                    switch (stage)
                    {
                        case LoaderStatus.Downloading:
                            Set(LoaderStatus.Downloading, -1, "Downloading the loader archive…");
                            break;
                        case LoaderStatus.Extracting:
                            Set(LoaderStatus.Extracting, -1, "Extracting the loader archive…");
                            break;
                        case LoaderStatus.Ready:
                            Set(LoaderStatus.Ready, 100, $"{Package.DisplayName} are ready.");
                            break;
                        case LoaderStatus.Error:
                            Set(LoaderStatus.Error, -1, "The loader archive could not be installed.");
                            break;
                        default:
                            Set(LoaderStatus.Idle, -1, "Not installed yet.");
                            break;
                    }
                }),
                ct).ConfigureAwait(false);

            Set(result.Status, result.Status == LoaderStatus.Ready ? 100 : -1, result.Message);
            return result.Succeeded;
        }

        private string DescribeDownload(int percent)
            => percent >= 0
                ? $"Downloading the loader archive… {percent}%"
                : "Downloading the loader archive…";

        private void Set(LoaderStatus status, int progress, string detail)
        {
            lock (_gate)
            {
                if (_status == status && _progress == progress && _detail == detail)
                {
                    return;
                }

                _status = status;
                _progress = progress;
                _detail = detail;
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Minimal <see cref="IProgress{T}"/> that calls back inline. <see cref="Progress{T}"/> would
    /// post to the captured synchronisation context, which delays the progress bar behind the very
    /// download it reports.
    /// </summary>
    internal sealed class CallbackProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;

        public CallbackProgress(Action<T> report) => _report = report;

        public void Report(T value) => _report(value);
    }
}
