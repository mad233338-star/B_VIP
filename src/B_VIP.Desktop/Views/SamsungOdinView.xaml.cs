using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Threading;
using B_VIP.Desktop.ViewModels;

namespace B_VIP.Desktop.Views
{
    /// <summary>
    /// The flash console scrolls itself as lines arrive. The scroll is posted rather than run inside
    /// <c>CollectionChanged</c>: scrolling forces a layout pass, and doing that while an append is
    /// still in flight is what makes WPF report an inconsistent items source.
    /// </summary>
    public partial class SamsungOdinView : UserControl
    {
        private INotifyCollectionChanged? _observed;
        private bool _scrollQueued;

        public SamsungOdinView()
        {
            InitializeComponent();
            DataContextChanged += (_, e) => Attach(e.NewValue as SamsungOdinViewModel);
            Unloaded += (_, _) => Detach();
        }

        private void Attach(SamsungOdinViewModel? viewModel)
        {
            Detach();
            if (viewModel is null)
            {
                return;
            }

            _observed = viewModel.Log as INotifyCollectionChanged;
            if (_observed is not null)
            {
                _observed.CollectionChanged += OnLogChanged;
            }

            ScrollToNewest();
        }

        private void Detach()
        {
            if (_observed is not null)
            {
                _observed.CollectionChanged -= OnLogChanged;
                _observed = null;
            }
        }

        private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_scrollQueued)
            {
                return;
            }

            _scrollQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new System.Action(() =>
            {
                _scrollQueued = false;
                ScrollToNewest();
            }));
        }

        private void ScrollToNewest()
        {
            ItemCollection items = OdinLogList.Items;
            if (items.Count == 0)
            {
                return;
            }

            object? last = items[items.Count - 1];
            if (last is not null && OdinLogList.Items.Contains(last))
            {
                OdinLogList.ScrollIntoView(last);
            }
        }
    }
}
