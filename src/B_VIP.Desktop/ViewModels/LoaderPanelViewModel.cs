using B_VIP.Desktop.Mvvm;
using B_VIP.Desktop.Services.Loaders;

namespace B_VIP.Desktop.ViewModels
{
    /// <summary>
    /// The loader strip on a chipset screen: shows whether the bundle is installed, downloads it when
    /// it is missing, and mirrors the stage (Idle, Downloading, Extracting, Ready, Error) into the
    /// progress bar and the caption.
    /// </summary>
    public sealed class LoaderPanelViewModel : ViewModelBase
    {
        private readonly LoaderInstallation _installation;

        private CancellationTokenSource? _cancellation;
        private LoaderStatus _status;
        private int _progress = -1;
        private string _detail;

        public LoaderPanelViewModel(LoaderInstallation installation, string title)
        {
            _installation = installation ?? throw new ArgumentNullException(nameof(installation));
            Title = title;
            _status = installation.Status;
            _progress = installation.Progress;
            _detail = installation.Detail;

            PrepareCommand = new AsyncRelayCommand(_ => PrepareAsync(), _ => !IsProvisioning);
            CancelCommand = new RelayCommand(_ => Cancel(), _ => IsProvisioning);

            // The installation raises on whichever thread produced the change; the mirrored
            // properties are bound to controls, so they are only touched on the dispatcher.
            _installation.Changed += (_, _) => UiThread.Invoke(Sync);
        }

        public string Title { get; }

        public LoaderStatus Status
        {
            get => _status;
            private set
            {
                if (SetProperty(ref _status, value))
                {
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(IsProvisioning));
                    OnPropertyChanged(nameof(IsReady));
                    OnPropertyChanged(nameof(IsFailed));
                    OnPropertyChanged(nameof(IsIndeterminate));
                    OnPropertyChanged(nameof(IsIdle));
                    OnPropertyChanged(nameof(PrepareButtonText));
                    PrepareCommand.RaiseCanExecuteChanged();
                    CancelCommand.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>One word for the status pill: Idle / Downloading / Extracting / Ready / Error.</summary>
        public string StatusText => Status switch
        {
            LoaderStatus.Downloading => "Downloading",
            LoaderStatus.Extracting => "Extracting",
            LoaderStatus.Ready => "Ready",
            LoaderStatus.Error => "Error",
            _ => "Idle"
        };

        /// <summary>0-100 for the bar; a negative value means "total unknown".</summary>
        public int Progress
        {
            get => _progress;
            private set
            {
                if (SetProperty(ref _progress, value))
                {
                    OnPropertyChanged(nameof(IsIndeterminate));
                }
            }
        }

        public bool IsIndeterminate => IsProvisioning && Progress < 0;

        public string Detail
        {
            get => _detail;
            private set => SetProperty(ref _detail, value);
        }

        /// <summary>
        /// Where the bundle lives on this computer. Code-only: the screen never shows a folder, so
        /// an operator is not confronted with an absolute path.
        /// </summary>
        public string Directory => _installation.Directory;

        /// <summary>The action button: a first download, or the retry that follows a failure.</summary>
        public string PrepareButtonText => IsFailed ? "Retry" : "Download";

        public string PackageName => _installation.Package.DisplayName;

        public bool IsReady => Status == LoaderStatus.Ready;

        public bool IsFailed => Status == LoaderStatus.Error;

        public bool IsIdle => Status == LoaderStatus.Idle;

        /// <summary>A download or an extraction is in progress.</summary>
        public bool IsProvisioning => Status is LoaderStatus.Downloading or LoaderStatus.Extracting;

        /// <summary>Downloads the bundle (or retries a failed attempt).</summary>
        public AsyncRelayCommand PrepareCommand { get; }

        /// <summary>Stops a download that is already running.</summary>
        public RelayCommand CancelCommand { get; }

        /// <summary>
        /// Called when the screen opens: re-reads the folder so a bundle copied in by hand is picked
        /// up, and downloads it automatically when it is missing.
        /// </summary>
        public async Task EnsureReadyAsync()
        {
            Sync();

            if (IsReady || IsProvisioning)
            {
                return;
            }

            await PrepareAsync();
        }

        private async Task PrepareAsync()
        {
            if (IsProvisioning)
            {
                return;
            }

            using CancellationTokenSource cancellation = new();
            _cancellation = cancellation;

            try
            {
                await _installation.EnsureAsync(cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                // The cancellation path already reported Idle.
            }
            finally
            {
                _cancellation = null;
                Sync();
            }
        }

        private void Cancel()
        {
            try
            {
                _cancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The download finished between the click and the cancel.
            }
        }

        private void Sync()
        {
            Status = _installation.Status;
            Progress = _installation.Progress;
            Detail = _installation.Detail;
        }
    }
}
