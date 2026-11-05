using System.Windows;
using System.Windows.Controls;
using B_VIP.Desktop.ViewModels;

namespace B_VIP.Desktop.Views
{
    /// <summary>Registration panel: both password boxes feed the view model, which owns validation.</summary>
    public partial class RegisterPanelView : UserControl
    {
        public RegisterPanelView()
        {
            InitializeComponent();
        }

        private void Password_OnPasswordChanged(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox box && DataContext is RegisterPanelViewModel vm)
            {
                vm.Password = box.Password;
            }
        }

        private void ConfirmPassword_OnPasswordChanged(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox box && DataContext is RegisterPanelViewModel vm)
            {
                vm.ConfirmPassword = box.Password;
            }
        }
    }
}
