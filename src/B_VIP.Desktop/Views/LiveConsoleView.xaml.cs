using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using B_VIP.Desktop.ViewModels;

namespace B_VIP.Desktop.Views
{
    public partial class LiveConsoleView : UserControl
    {
        private ConsoleViewModel? _viewModel;
        private INotifyCollectionChanged? _observed;

        public LiveConsoleView()
        {
            InitializeComponent();
            DataContextChanged += (_, e) => Attach(e.NewValue as ConsoleViewModel);
            Unloaded += (_, _) => Detach();
        }

        private void Attach(ConsoleViewModel? viewModel)
        {
            Detach();

            _viewModel = viewModel;
            if (viewModel is null)
            {
                return;
            }

            _observed = viewModel.Entries as INotifyCollectionChanged;
            if (_observed is not null)
            {
                _observed.CollectionChanged += OnEntriesChanged;
            }

            ScrollToNewest();
        }

        private void Detach()
        {
            if (_observed is not null)
            {
                _observed.CollectionChanged -= OnEntriesChanged;
                _observed = null;
            }

            _viewModel = null;
        }

        private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_viewModel?.AutoScroll != true)
            {
                return;
            }

            ScrollToNewest();
        }

        private void ScrollToNewest()
        {
            int count = LogList.Items.Count;
            if (count == 0)
            {
                return;
            }

            object last = LogList.Items[count - 1];
            if (last is not null)
            {
                LogList.ScrollIntoView(last);
            }
        }
    }
}
