using System.Windows;
using System.Windows.Controls;

namespace B_VIP.Desktop.Views
{
    /// <summary>
    /// Password entry with an eye toggle. The secret is exposed as the bindable
    /// <see cref="Password"/> property, so a view model never has to reach into a PasswordBox,
    /// and revealing the text swaps the masked renderer for a TextBox that carries the same
    /// characters, the same caret position and the keyboard focus.
    /// </summary>
    public partial class PasswordField : UserControl
    {
        public static readonly DependencyProperty PasswordProperty =
            DependencyProperty.Register(
                nameof(Password),
                typeof(string),
                typeof(PasswordField),
                new FrameworkPropertyMetadata(
                    string.Empty,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    OnPasswordPropertyChanged));

        private bool _syncing;

        public PasswordField()
        {
            InitializeComponent();
        }

        /// <summary>The entered password, in clear text, kept in step with both renderers.</summary>
        public string Password
        {
            get => (string)GetValue(PasswordProperty);
            set => SetValue(PasswordProperty, value);
        }

        private static void OnPasswordPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var field = (PasswordField)d;
            field.WriteToEditors(e.NewValue as string ?? string.Empty);
        }

        /// <summary>Pushes a value coming from the binding into the editors without echoing back.</summary>
        private void WriteToEditors(string value)
        {
            if (_syncing || Masked is null || Revealed is null)
            {
                return;
            }

            _syncing = true;
            try
            {
                if (Masked.Password != value)
                {
                    Masked.Password = value;
                }

                if (Revealed.Text != value)
                {
                    Revealed.Text = value;
                }
            }
            finally
            {
                _syncing = false;
            }
        }

        private void OnMaskedPasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_syncing || Revealed is null)
            {
                return;
            }

            _syncing = true;
            try
            {
                if (Revealed.Text != Masked.Password)
                {
                    Revealed.Text = Masked.Password;
                }

                Password = Masked.Password;
            }
            finally
            {
                _syncing = false;
            }
        }

        private void OnRevealedTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_syncing || Masked is null)
            {
                return;
            }

            _syncing = true;
            try
            {
                if (Masked.Password != Revealed.Text)
                {
                    Masked.Password = Revealed.Text;
                }

                Password = Revealed.Text;
            }
            finally
            {
                _syncing = false;
            }
        }

        private void OnRevealChanged(object sender, RoutedEventArgs e)
        {
            if (RevealToggle.IsChecked == true)
            {
                Revealed.Text = Masked.Password;
                Revealed.Visibility = Visibility.Visible;
                Masked.Visibility = Visibility.Collapsed;
                Revealed.Focus();
                Revealed.CaretIndex = Revealed.Text.Length;
            }
            else
            {
                Masked.Password = Revealed.Text;
                Masked.Visibility = Visibility.Visible;
                Revealed.Visibility = Visibility.Collapsed;
                Masked.Focus();
            }
        }
    }
}
