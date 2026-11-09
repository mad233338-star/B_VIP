using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace B_VIP.Desktop.Views
{
    /// <summary>E-mail verification panel; all state lives in <c>VerifyPanelViewModel</c>.</summary>
    public partial class VerifyPanelView : UserControl
    {
        public VerifyPanelView()
        {
            InitializeComponent();

            // Registration ends here, so the technician should be able to type the code without
            // clicking the field first.
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!IsVisible)
            {
                return;
            }

            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (!CodeBox.IsVisible)
                {
                    return;
                }

                CodeBox.Focus();
                CodeBox.CaretIndex = CodeBox.Text?.Length ?? 0;
            }));
        }
    }
}
