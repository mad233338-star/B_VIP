using System;
using System.Windows;
using B_VIP.Desktop.Services;
using B_VIP.Desktop.ViewModels;

namespace B_VIP.Desktop.Views
{
    /// <summary>
    /// The administrator's console, opened directly after sign-in. It is deliberately separate from
    /// the technician shell: there is no navigation between the two, and the technician workspace is
    /// never constructed for an administrator.
    /// </summary>
    public partial class AdminDashboardWindow : Window
    {
        public AdminDashboardWindow()
        {
            InitializeComponent();
            Model = new AdminViewModel();
            DataContext = this;

            Loaded += async (_, _) =>
            {
                try
                {
                    await Model.Inner.InitializeAsync();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"The administration data could not be loaded: {ex.Message}",
                        "B_VIP Tool", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
        }

        public AdminViewModel Model { get; }

        public string UserName => AppServices.Current.Session.DisplayName;

        private async void OnSignOut(object sender, RoutedEventArgs e)
        {
            try
            {
                await AppServices.Current.Api.LogoutAsync();
            }
            catch (Exception)
            {
                // Signing out locally must succeed even when the server cannot be reached.
            }

            AppServices.Current.Session.Clear();
            System.Windows.Application.Current.Shutdown();
        }
    }
}
