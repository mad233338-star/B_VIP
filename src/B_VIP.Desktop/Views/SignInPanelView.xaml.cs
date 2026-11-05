using System.Windows;
using System.Windows.Controls;
using B_VIP.Desktop.ViewModels;

namespace B_VIP.Desktop.Views
{
    /// <summary>Sign-in panel: the password never leaves the PasswordBox as plain text binding.</summary>
    public partial class SignInPanelView : UserControl
    {
        public SignInPanelView()
        {
            InitializeComponent();
        }

        private void PasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox box && DataContext is SignInPanelViewModel vm)
            {
                vm.Password = box.Password;
            }
        }
    }
}
