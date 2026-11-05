using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using B_VIP.Desktop.Mvvm;
using B_VIP.Desktop.Services;

namespace B_VIP.Desktop.ViewModels
{
    /// <summary>
    /// Drivers &amp; Prerequisites: the USB drivers every vendor module needs, with the real state of
    /// the bundle. Nothing is downloaded behind the operator's back — an installer runs only when the
    /// administrator placed it in the bundle folder, otherwise the vendor page opens.
    /// </summary>
    public sealed class DriversViewModel : ViewModelBase
    {
        private readonly AppServices _services;
        private readonly DriverService _drivers;

        public DriversViewModel()
        {
            _services = AppServices.Current;
            _drivers = new DriverService(_services.Log);

            Packages = new ObservableCollection<DriverPackage>(_drivers.Catalog);

            RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
            InstallCommand = new AsyncRelayCommand(InstallAsync);
            OpenPageCommand = new RelayCommand(parameter =>
            {
                if (parameter is DriverPackage package)
                {
                    _drivers.OpenDownloadPage(package);
                }
            });
            OpenFolderCommand = new RelayCommand(_ => OpenBundleFolder());

            StatusMessage = _drivers.DescribeBundle();
        }

        public ObservableCollection<DriverPackage> Packages { get; }

        public string BundleFolder => _drivers.DriversDirectory ?? "Not created yet";

        public AsyncRelayCommand RefreshCommand { get; }

        public AsyncRelayCommand InstallCommand { get; }

        public RelayCommand OpenPageCommand { get; }

        public RelayCommand OpenFolderCommand { get; }

        public override async Task OnNavigatedToAsync() => await RefreshAsync();

        private async Task RefreshAsync()
        {
            IsBusy = true;
            ErrorMessage = null;
            try
            {
                await _drivers.RefreshAsync();
                StatusMessage = _drivers.DescribeBundle();
                OnPropertyChanged(nameof(BundleFolder));
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task InstallAsync(object? parameter)
        {
            if (parameter is not DriverPackage package)
            {
                return;
            }

            IsBusy = true;
            try
            {
                StatusMessage = _drivers.Install(package);
                await _drivers.RefreshAsync();
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void OpenBundleFolder()
        {
            string folder = _drivers.DriversDirectory ?? AppContext.BaseDirectory;
            try
            {
                if (Directory.Exists(folder))
                {
                    Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
                }
                else
                {
                    ErrorMessage = $"The bundle folder {folder} does not exist.";
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                ErrorMessage = ex.Message;
            }
        }
    }
}
