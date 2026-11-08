using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using B_VIP.Desktop.ViewModels;

namespace B_VIP.Desktop.Views
{
    public partial class LiveConsoleView : UserControl
    {
        private ConsoleViewModel? _viewModel;
        private INotifyCollectionChanged? _observed;
        private bool _scrollQueued;

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

        /// <summary>
        /// Schedules one scroll for the whole batch instead of scrolling per changed row.
        ///
        /// Scrolling forces the item container generator to run a layout pass. Doing that inside the
        /// CollectionChanged notification — while the collection is still being appended to — leaves
        /// the generator counting a different number of rows than the collection holds, and WPF then
        /// throws "An ItemsControl is inconsistent with its items source" on the next line of output.
        /// Posting the scroll lets the batch finish and the layout settle first.
        /// </summary>
        private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_viewModel?.AutoScroll != true || _scrollQueued)
            {
                return;
            }

            _scrollQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _scrollQueued = false;
                if (_viewModel?.AutoScroll == true)
                {
                    ScrollToNewest();
                }
            }));
        }

        private void ScrollToNewest()
        {
            ItemCollection items = LogList.Items;
            if (items.Count == 0)
            {
                return;
            }

            object? last = items[items.Count - 1];
            if (last is not null && LogList.Items.Contains(last))
            {
                LogList.ScrollIntoView(last);
            }
        }
    }
}
