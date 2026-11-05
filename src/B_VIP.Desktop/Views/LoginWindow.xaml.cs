using System.Windows;
using B_VIP.Desktop.ViewModels;

namespace B_VIP.Desktop.Views
{
    /// <summary>
    /// The onboarding container: sign-in, registration, e-mail verification and plan selection are
    /// states of this single Cyber Blue window, not separate dialogs.
    /// </summary>
    public partial class LoginWindow : Window
    {
        public LoginWindow()
        {
            InitializeComponent();
            ViewModel = new AuthFlowViewModel();
            DataContext = ViewModel;
        }

        public AuthFlowViewModel ViewModel { get; }
    }
}
